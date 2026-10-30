using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Sales;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Opportunities;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Quotes;

/// <summary>
/// Teklif tarihçesine işaret satırı yazar ve okur. İşaretler "bu yan etki yapıldı" kaydıdır (ör. takip aktivitesi açıldı); tekrar teslimde
/// ya da zamanlanmış işin yeniden çalışmasında aynı yan etkinin ikinci kez yapılmasını önler. Teklif değerleri değişmez; yazma teklif
/// güncellemesi olarak tarihçe satırı bırakır.
/// </summary>
internal sealed class QuoteMarkers(IRecordPipeline pipeline, QuoteWorkflowGate gate, QuoteReader reader)
{
    public Task<bool> HasAsync(Guid quoteId, string kind, CancellationToken cancellationToken) => reader.HasEventAsync(quoteId, kind, cancellationToken);

    public async Task MarkAsync(Guid quoteId, string kind, string? note, IReadOnlyList<string> permissions, CancellationToken cancellationToken)
    {
        if (await reader.QuoteAsync(quoteId, cancellationToken) is not { } quote)
        {
            return;
        }

        var text = note is { Length: > Quote.ReasonMaxLength } ? note[..Quote.ReasonMaxLength] : note;
        using (gate.Open(permissions, kind, text))
        {
            await pipeline.UpdateAsync(new RecordCommand(CrmModule.Entities.Quote, quoteId, new JsonObject(), quote.Version) { Reason = $"quote.{kind}" }, cancellationToken);
        }
    }
}

/// <summary>Teklif olaylarının yan etkilerinin tarihçe işaretleri.</summary>
public static class QuoteMarkerKinds
{
    public const string OpportunityWon = "OpportunityWon";
    public const string OpportunityWinSkipped = "OpportunityWinSkipped";
    public const string FollowUpScheduled = "FollowUpScheduled";
    public const string ExpiryReminderCreated = "ExpiryReminderCreated";
    public const string Expired = "Expired";
}

/// <summary>
/// Teklif yaşam döngüsü aboneliği (<c>crm.quote-lifecycle</c>, ADR-0019 dilim 7): teklif güncelleme olayından, kaydın <b>güncel durumuna</b> bakarak iki
/// ayarlı yan etki üretir; ayarlar kapalıyken hiçbir şey yapmaz. Teslim en az bir kezdir; işaret satırı ve durum denetimi idempotent yapar.
/// <list type="bullet">
///   <item><b>Kabul</b> (<c>quote.Accepted</c>/<c>quote.CustomerAccepted</c>): ayar açıksa bağlı <i>açık</i> fırsat kazanıldı olur (kazanılan tutar teklifin net
///   toplamı). Fırsat zaten kapalı ya da beklemedeyse dokunulmaz. Başarısızlıkta kabul bozulmaz; neden teklif tarihçesine yazılır.</item>
///   <item><b>Sunum</b> (<c>quote.Presented</c>): ayar &gt; 0 ise sorumlu için takip aktivitesi açılır.</item>
/// </list>
/// </summary>
internal sealed class QuoteLifecycleSubscriber(
    IRecordPipeline pipeline, IOpportunityWorkflow opportunities, QuoteMarkers markers, IQuotePolicyProvider? policyProvider = null) : IEventSubscriber
{
    private static readonly string[] MarkPermissions = [CrmModule.Permissions.QuotesRead];

    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Reason is not { } reason || !reason.StartsWith("quote.", StringComparison.Ordinal))
        {
            return;
        }

        var record = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Quote, envelope.RecordId), cancellationToken);
        if (!record.IsSuccess)
        {
            return;
        }

        var policy = policyProvider is null ? new QuotePolicySettings() : await policyProvider.GetAsync(cancellationToken);
        var status = Text(record.Value!, "status");
        if (status == QuoteStatus.Accepted && reason is "quote.Accepted" or "quote.CustomerAccepted" && policy.OnAcceptWinOpportunity)
        {
            await WinOpportunityAsync(record.Value!, cancellationToken);
        }
        else if (status == QuoteStatus.Presented && reason == "quote.Presented" && policy.FollowUpDays > 0)
        {
            await ScheduleFollowUpAsync(record.Value!, policy.FollowUpDays, cancellationToken);
        }
    }

    private async Task WinOpportunityAsync(RecordData quote, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(Text(quote, "opportunityId"), out var opportunityId)
            || await markers.HasAsync(quote.Id, QuoteMarkerKinds.OpportunityWon, cancellationToken)
            || await markers.HasAsync(quote.Id, QuoteMarkerKinds.OpportunityWinSkipped, cancellationToken))
        {
            return;
        }

        var label = $"Teklif {Text(quote, "quoteNumber")} V{Number(quote, "revisionNo")}";
        var opportunity = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Opportunity, opportunityId), cancellationToken);
        if (!opportunity.IsSuccess)
        {
            await markers.MarkAsync(quote.Id, QuoteMarkerKinds.OpportunityWinSkipped, "Fırsat bulunamadı ya da erişilemedi; fırsat kazanıldı yapılmadı.", MarkPermissions, cancellationToken);
            return;
        }

        if (Text(opportunity.Value!, "status") != OpportunityStatus.Open)
        {
            await markers.MarkAsync(quote.Id, QuoteMarkerKinds.OpportunityWinSkipped, "Fırsat açık değil; olduğu gibi bırakıldı.", MarkPermissions, cancellationToken);
            return;
        }

        var win = await opportunities.WinAsync(opportunity.Value!.Id, opportunity.Value.Version,
            new WinOpportunityRequest(null, Decimal(quote, "netTotal"), Text(quote, "currency"), $"{label} kabul edildi", null, null), cancellationToken);
        await markers.MarkAsync(
            quote.Id,
            win.IsSuccess ? QuoteMarkerKinds.OpportunityWon : QuoteMarkerKinds.OpportunityWinSkipped,
            win.IsSuccess ? "Bağlı fırsat kazanıldı olarak kapandı." : $"Fırsat kazanıldı yapılamadı: {win.Error!.Message}",
            MarkPermissions, cancellationToken);
    }

    private async Task ScheduleFollowUpAsync(RecordData quote, int days, CancellationToken cancellationToken)
    {
        if (await markers.HasAsync(quote.Id, QuoteMarkerKinds.FollowUpScheduled, cancellationToken))
        {
            return;
        }

        var presented = DateTimeOffset.TryParse(Text(quote, "presentedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.UtcNow;
        var due = presented.UtcDateTime.Date.AddDays(days).AddHours(9);
        var values = QuoteActivities.Values($"Teklif takibi: {Text(quote, "title")} ({Text(quote, "quoteNumber")})", due, quote);
        var created = await pipeline.CreateAsync(new RecordCommand(CrmModule.Entities.Activity, null, values), cancellationToken);
        if (created.IsSuccess)
        {
            await markers.MarkAsync(quote.Id, QuoteMarkerKinds.FollowUpScheduled, $"Takip aktivitesi {due:dd.MM.yyyy} için açıldı.", MarkPermissions, cancellationToken);
        }
    }

    private static string? Text(RecordData record, string name) =>
        record.Values[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static decimal? Decimal(RecordData record, string name) =>
        record.Values[name] is JsonValue value && value.TryGetValue<decimal>(out var number) ? number : null;

    private static int Number(RecordData record, string name) =>
        record.Values[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 1;
}

/// <summary>Tekliften açılan aktivitelerin ortak değerleri: sorumlu teklifin sorumlusudur; fırsat varsa aynı firmaya bağlıdır.</summary>
internal static class QuoteActivities
{
    public static JsonObject Values(string subject, DateTime dueUtc, RecordData quote)
    {
        string? Text(string name) => quote.Values[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
        return new JsonObject
        {
            ["type"] = "FollowUp",
            ["subject"] = subject.Length > 200 ? subject[..200] : subject,
            ["dueAt"] = new DateTimeOffset(DateTime.SpecifyKind(dueUtc, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture),
            ["companyId"] = Text("companyId"),
            ["opportunityId"] = Text("opportunityId"),
            ["ownerId"] = Text("ownerId"),
        };
    }
}

/// <summary>Zamanlanmış işin bir turunun özeti.</summary>
public sealed record QuoteExpiryRun(int Expired, int RemindersCreated, string? Warning = null);

/// <summary>
/// Teklif süre dolumu (ADR-0019 dilim 7): geçerliliği bitmiş ama hâlâ "müşteriye sunuldu" teklifleri <c>Expired</c> yapar; ayar açıksa bitmeden önce
/// sorumlu için hatırlatma aktivitesi açar. Kabul anındaki süre denetimi bu işe bağlı değildir (iş geç kalsa da kabul reddedilir). Tekrar çalıştırmak
/// güvenlidir: durum geçişi sürüm denetimlidir, hatırlatma tarihçe işaretiyle tek kez açılır. Sistem kimliğiyle (<c>crm.quote-expiry</c>) çalışır.
/// </summary>
public interface IQuoteExpiry
{
    Task<QuoteExpiryRun> RunAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed class QuoteExpiry(
    IRecordPipeline pipeline, QuoteWorkflowGate gate, QuoteReader reader, QuoteMarkers markers, IQuotePolicyProvider? policyProvider = null) : IQuoteExpiry
{
    private const int BatchSize = 200;
    private static readonly string[] DecisionPermissions = [CrmModule.Permissions.QuotesRecordDecision];
    private static readonly string[] MarkPermissions = [CrmModule.Permissions.QuotesRead];

    public async Task<QuoteExpiryRun> RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expired = 0;
        foreach (var quote in await reader.DueForExpiryAsync(now, BatchSize, cancellationToken))
        {
            RecordResult<RecordData> result;
            using (gate.Open(DecisionPermissions, QuoteMarkerKinds.Expired, "Geçerlilik süresi doldu."))
            {
                result = await pipeline.UpdateAsync(new RecordCommand(CrmModule.Entities.Quote, quote.Id, new JsonObject
                {
                    ["status"] = QuoteStatus.Expired,
                    ["decidedAt"] = now.ToString("O", CultureInfo.InvariantCulture),
                    ["statusReason"] = "Geçerlilik süresi doldu.",
                }, quote.Version) { Reason = $"quote.{QuoteMarkerKinds.Expired}" }, cancellationToken);
            }

            if (result.IsSuccess)
            {
                expired++;
            }

            // Sürüm çakışması (aynı anda kabul/iptal/revizyon) hata değildir: o teklif zaten başka bir kararla sonuçlanmıştır.
        }

        var policy = policyProvider is null ? new QuotePolicySettings() : await policyProvider.GetAsync(cancellationToken);
        var reminders = 0;
        string? warning = null;
        if (policy.ExpiryReminderDays > 0)
        {
            foreach (var quote in await reader.DueForReminderAsync(now, now.AddDays(policy.ExpiryReminderDays), BatchSize, cancellationToken))
            {
                var created = await pipeline.RunAtomicAsync(async ct =>
                {
                    var record = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Quote, quote.Id), ct);
                    if (!record.IsSuccess || await markers.HasAsync(quote.Id, QuoteMarkerKinds.ExpiryReminderCreated, ct))
                    {
                        return RecordResult<bool>.Ok(false);
                    }

                    var due = quote.ValidUntil!.Value.UtcDateTime.Date.AddDays(-1).AddHours(9);
                    if (due < now.UtcDateTime)
                    {
                        due = now.UtcDateTime;
                    }

                    var activity = await pipeline.CreateAsync(new RecordCommand(CrmModule.Entities.Activity, null,
                        QuoteActivities.Values($"Teklif süresi doluyor: {quote.Title} ({quote.QuoteNumber})", due, record.Value!)), ct);
                    if (!activity.IsSuccess)
                    {
                        return RecordResult<bool>.Fail(activity.Error!.Code, activity.Error.Message, activity.Error.FieldErrors);
                    }

                    await markers.MarkAsync(quote.Id, QuoteMarkerKinds.ExpiryReminderCreated,
                        $"Hatırlatma aktivitesi açıldı (geçerlilik {quote.ValidUntil:dd.MM.yyyy}).", MarkPermissions, ct);
                    return RecordResult<bool>.Ok(true);
                }, cancellationToken);
                if (created is { IsSuccess: true, Value: true })
                {
                    reminders++;
                }
                else if (!created.IsSuccess)
                {
                    warning = $"Teklif {quote.QuoteNumber} hatırlatması açılamadı: {created.Error!.Message} {string.Join("; ", created.Error.FieldErrors?.Select(f => $"{f.Field}: {f.Message}") ?? [])}";
                }
            }
        }

        return new QuoteExpiryRun(expired, reminders, warning);
    }
}

/// <summary>"crm.quote-expiry" kimliğinin izin çapası (zamanlanmış iş sistem adına çalışır); olay işlemez.</summary>
internal sealed class QuoteExpiryAnchor : IEventSubscriber
{
    public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Kabul edilmiş teklifi sonraki modüllere veren sözleşmenin uygulaması (<see cref="IAcceptedQuoteSource"/>).</summary>
internal sealed class AcceptedQuoteSource(IRecordPipeline pipeline, QuoteReader reader) : IAcceptedQuoteSource
{
    public async Task<AcceptedQuote?> GetAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        // Görünürlük (izin ve veri kapsamı) kayıt boru hattında doğrulanır; kapsam dışı ya da kabul edilmemiş teklif "yok" sayılır.
        if (!(await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Quote, quoteId), cancellationToken)).IsSuccess
            || await reader.QuoteAsync(quoteId, cancellationToken) is not { Status: QuoteStatus.Accepted } quote)
        {
            return null;
        }

        var decision = await reader.LastDecisionAsync(quoteId, cancellationToken);
        var document = (await reader.DocumentsAsync(quoteId, cancellationToken)).FirstOrDefault();
        var lines = (await reader.LinesAsync(quoteId, cancellationToken)).Where(l => l.Included)
            .Select(l => new AcceptedQuoteLine(
                l.StableKey, l.Position, l.ProductCode, l.Name, l.Description, l.Quantity, l.UnitName, l.UnitPrice, l.TaxRatePercent, l.Net, l.Tax, l.Gross, l.RecurringPeriod))
            .ToList();
        return new AcceptedQuote(
            quote.Id, quote.QuoteNumber, quote.RevisionNo, quote.Title, quote.CompanyId, quote.ContactId, quote.OpportunityId, quote.Currency, quote.PriceIncludesTax,
            quote.NetTotal, quote.TaxTotal, quote.GrossTotal, quote.DecidedAt ?? quote.UpdatedAt, decision?.SignerName, decision?.DocumentSha256 ?? document?.Sha256,
            quote.PaymentTerms, quote.DeliveryTerms, quote.CommercialTerms, lines);
    }
}
