using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Sales;
using Modulix.Modules.Crm.Activities;
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
    public const string FollowUpsClosed = "FollowUpsClosed";
    public const string OpportunitySynced = "OpportunitySynced";
    public const string ExpiryReminderCreated = "ExpiryReminderCreated";
    public const string ApprovalReminderSent = "ApprovalReminderSent";
    public const string Expired = "Expired";
}

/// <summary>
/// Teklif yaşam döngüsü aboneliği (<c>crm.quote-lifecycle</c>, ADR-0019 dilim 7): teklif güncelleme olayından, kaydın <b>güncel durumuna</b> bakarak
/// ayarlı yan etkiler üretir; ayarlar kapalıyken hiçbir şey yapmaz. Teslim en az bir kezdir; işaret satırı ve durum denetimi idempotent yapar.
/// <list type="bullet">
///   <item><b>Kabul</b> (<c>quote.Accepted</c>/<c>quote.CustomerAccepted</c>): ayar açıksa bağlı <i>açık</i> fırsat kazanıldı olur (kazanılan tutar teklifin net
///   toplamı). Fırsat zaten kapalı ya da beklemedeyse dokunulmaz. Başarısızlıkta kabul bozulmaz; neden teklif tarihçesine yazılır.</item>
///   <item><b>Sunum</b> (<c>quote.Presented</c>): ayar &gt; 0 ise sorumlu için takip aktivitesi açılır (teklife bağlı).</item>
///   <item><b>Sonuçlanma</b> (kabul, ret, iptal, süre dolumu): teklife bağlı, tekliften açılmış ve hâlâ açık takip/hatırlatma aktiviteleri
///   gerekçeyle iptal edilir. Kullanıcının elle açıp teklife bağladığı aktivitelere dokunulmaz.</item>
/// </list>
/// </summary>
internal sealed class QuoteLifecycleSubscriber(
    IRecordPipeline pipeline, IOpportunityWorkflow opportunities, IActivityWorkflow activities, QuoteMarkers markers, QuoteReader reader, IQuoteOpportunitySync opportunitySync,
    IQuotePolicyProvider? policyProvider = null) : IEventSubscriber
{
    private static readonly string[] MarkPermissions = [CrmModule.Permissions.QuotesRead];

    private static readonly string[] FinalReasons =
        ["quote.Accepted", "quote.CustomerAccepted", "quote.Rejected", "quote.CustomerRejected", "quote.Cancelled", "quote.Expired"];

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
        else if (status == QuoteStatus.Presented && reason == "quote.Presented")
        {
            // Fırsatın esas teklifi yoksa ilk sunulan teklif esas olur; ayar açıksa fırsat tutarı eşitlenir (işaretle tek kez).
            if (!await markers.HasAsync(record.Value!.Id, QuoteMarkerKinds.OpportunitySynced, cancellationToken))
            {
                var note = await opportunitySync.OnPresentedAsync(record.Value!, policy.SyncOpportunityAmount, cancellationToken);
                if (note is not null)
                {
                    await markers.MarkAsync(record.Value!.Id, QuoteMarkerKinds.OpportunitySynced, note, MarkPermissions, cancellationToken);
                }
            }

            if (policy.FollowUpDays > 0)
            {
                await ScheduleFollowUpAsync(record.Value!, policy.FollowUpDays, cancellationToken);
            }
        }

        if (FinalReasons.Contains(reason) && status != QuoteStatus.Draft && status != QuoteStatus.Presented)
        {
            await CloseFollowUpsAsync(record.Value!, status!, cancellationToken);
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
        var created = await QuoteActivities.CreateAsync(pipeline, $"{QuoteActivities.FollowUpPrefix}{Text(quote, "title")} ({Text(quote, "quoteNumber")})", due, quote, cancellationToken);
        if (created.IsSuccess)
        {
            await markers.MarkAsync(quote.Id, QuoteMarkerKinds.FollowUpScheduled, $"Takip aktivitesi {due:dd.MM.yyyy} için açıldı.", MarkPermissions, cancellationToken);
        }
    }

    /// <summary>Tekliften açılan (konu öneki bizim) açık aktiviteleri iptal eder; işaret satırı sayıyı ve nedeni tutar.</summary>
    private async Task CloseFollowUpsAsync(RecordData quote, string status, CancellationToken cancellationToken)
    {
        if (await markers.HasAsync(quote.Id, QuoteMarkerKinds.FollowUpsClosed, cancellationToken))
        {
            return;
        }

        var open = (await reader.LinkedOpenActivitiesAsync(quote.Id, cancellationToken)).Where(a => QuoteActivities.IsOurs(a)).ToList();
        if (open.Count == 0)
        {
            return;
        }

        var reason = $"Teklif sonuçlandı ({QuoteStatusLabel(status)}); takip otomatik kapatıldı.";
        var closed = 0;
        foreach (var activity in open)
        {
            if ((await activities.CancelAsync(activity.Id, activity.Version, reason, cancellationToken)).IsSuccess)
            {
                closed++;
            }
        }

        await markers.MarkAsync(quote.Id, QuoteMarkerKinds.FollowUpsClosed, $"{closed} açık takip/hatırlatma aktivitesi kapatıldı.", MarkPermissions, cancellationToken);
    }

    private static string QuoteStatusLabel(string status) => status switch
    {
        QuoteStatus.Accepted => "kabul edildi",
        QuoteStatus.Rejected => "reddedildi",
        QuoteStatus.Cancelled => "iptal edildi",
        QuoteStatus.Expired => "süresi doldu",
        _ => status,
    };

    private static string? Text(RecordData record, string name) =>
        record.Values[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static decimal? Decimal(RecordData record, string name) =>
        record.Values[name] is JsonValue value && value.TryGetValue<decimal>(out var number) ? number : null;

    private static int Number(RecordData record, string name) =>
        record.Values[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 1;
}

/// <summary>
/// Tekliften açılan aktiviteler: sorumlu teklifin sorumlusudur, firma ve varsa fırsata bağlıdır, ayrıca teklife <see cref="ActivityLink"/> ile bağlanır.
/// Konu öneki, otomatik kapatmada "bizim açtığımız" aktiviteyi kullanıcının elle bağladığından ayırır.
/// </summary>
internal static class QuoteActivities
{
    public const string FollowUpPrefix = "Teklif takibi: ";
    public const string ReminderPrefix = "Teklif süresi doluyor: ";

    public static bool IsOurs(Activity activity) =>
        activity.Type == "FollowUp" && (activity.Subject.StartsWith(FollowUpPrefix, StringComparison.Ordinal) || activity.Subject.StartsWith(ReminderPrefix, StringComparison.Ordinal));

    /// <summary>Aktiviteyi ve teklif bağını oluşturur. Bağ oluşturulamazsa aktivite yine kalır (otomatik kapatma o aktiviteyi bulamaz).</summary>
    public static async Task<RecordResult<RecordData>> CreateAsync(IRecordPipeline pipeline, string subject, DateTime dueUtc, RecordData quote, CancellationToken cancellationToken)
    {
        string? Text(string name) => quote.Values[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
        var created = await pipeline.CreateAsync(new RecordCommand(CrmModule.Entities.Activity, null, new JsonObject
        {
            ["type"] = "FollowUp",
            ["subject"] = subject.Length > 200 ? subject[..200] : subject,
            ["dueAt"] = new DateTimeOffset(DateTime.SpecifyKind(dueUtc, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture),
            ["companyId"] = Text("companyId"),
            ["opportunityId"] = Text("opportunityId"),
            ["ownerId"] = Text("ownerId"),
        }), cancellationToken);
        if (!created.IsSuccess)
        {
            return created;
        }

        await pipeline.CreateAsync(new RecordCommand(CrmModule.Entities.ActivityLink, null, new JsonObject
        {
            ["activityId"] = created.Value!.Id.ToString(),
            ["entityKey"] = CrmModule.Entities.Quote,
            ["recordId"] = quote.Id.ToString(),
            ["isPrimary"] = false,
        }), cancellationToken);
        return created;
    }
}

/// <summary>Tekliflerle ilgili sorumlu bildirimleri (e-posta). Kullanıcı girdisi HTML'e kodlanır; içerik yalnızca başlık, numara ve tarihtir.</summary>
internal static class QuoteOwnerEmails
{
    public const string ReminderReference = "crm.quote-reminder:";
    public const string ExpiredReference = "crm.quote-expired:";
    public const string ApprovalReminderReference = "crm.quote-approval-reminder:";

    public static EmailMessage ApprovalReminder(NotificationRecipient to, Quote quote, string? link) => Build(
        to, quote, $"Onayınızı bekleyen teklif: {OneLine(quote.QuoteNumber)}",
        $"\"{OneLine(quote.Title)}\" başlıklı {quote.QuoteNumber} numaralı teklif iç onayınızı bekliyor; henüz karar verilmedi.",
        link, ApprovalReminderReference + quote.Id.ToString("N"));

    public static EmailMessage ExpiryReminder(NotificationRecipient to, Quote quote, string? link) => Build(
        to, quote, $"Teklif süresi doluyor: {OneLine(quote.QuoteNumber)}",
        $"\"{OneLine(quote.Title)}\" başlıklı {quote.QuoteNumber} numaralı teklifin geçerliliği {Date(quote.ValidUntil)} tarihinde sona eriyor; müşteri henüz karar vermedi.",
        link, ReminderReference + quote.Id.ToString("N"));

    public static EmailMessage Expired(NotificationRecipient to, Quote quote, string? link) => Build(
        to, quote, $"Teklif süresi doldu: {OneLine(quote.QuoteNumber)}",
        $"\"{OneLine(quote.Title)}\" başlıklı {quote.QuoteNumber} numaralı teklifin geçerliliği {Date(quote.ValidUntil)} tarihinde doldu ve teklif \"süresi doldu\" durumuna alındı. Gerekirse yeni revizyon açabilirsiniz.",
        link, ExpiredReference + quote.Id.ToString("N"));

    private static EmailMessage Build(NotificationRecipient to, Quote quote, string subject, string body, string? link, string reference)
    {
        var greeting = $"Merhaba {to.DisplayName},";
        var html = $"<p>{Encode(greeting)}</p><p>{Encode(body)}</p>" + (link is null ? string.Empty : $"<p><a href=\"{Encode(link)}\">Teklifi aç</a></p>");
        var text = string.Join("\n\n", new[] { greeting, body, link is null ? null : $"Teklifi aç: {link}" }.Where(p => p is not null));
        return new EmailMessage(to.Email, subject, html) { ToName = to.DisplayName, TextBody = text, Reference = reference };
    }

    private static string Date(DateTimeOffset? value) => value is { } v ? v.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "—";

    private static string OneLine(string value) => string.Join(' ', value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

/// <summary>Zamanlanmış işin bir turunun özeti. <see cref="Skipped"/>: başka bir örnek aynı anda çalıştığı için tur atlandı.</summary>
public sealed record QuoteExpiryRun(int Expired, int RemindersCreated, string? Warning = null, bool Skipped = false, int ApprovalRemindersSent = 0);

/// <summary>
/// Teklif süre dolumu (ADR-0019 dilim 7): geçerliliği bitmiş ama hâlâ "müşteriye sunuldu" teklifleri <c>Expired</c> yapar ve sorumluya e-posta bildirir;
/// ayar açıksa bitmeden önce sorumlu için hatırlatma aktivitesi açar ve e-posta bildirir. Kabul anındaki süre denetimi bu işe bağlı değildir (iş geç
/// kalsa da kabul reddedilir). Tekrar çalıştırmak güvenlidir: durum geçişi sürüm denetimlidir, hatırlatma tarihçe işaretiyle tek kez açılır.
/// E-postalar yalnızca sorumlu aktif tenant'ın etkin üyesiyse gönderilir. Sistem kimliğiyle (<c>crm.quote-expiry</c>) çalışır.
/// </summary>
public interface IQuoteExpiry
{
    Task<QuoteExpiryRun> RunAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed class QuoteExpiry(
    IRecordPipeline pipeline, QuoteWorkflowGate gate, QuoteReader reader, QuoteMarkers markers, IEmailQueue emails, INotificationRecipients recipients,
    IQuotePolicyProvider? policyProvider = null) : IQuoteExpiry
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
                await NotifyOwnerAsync(quote, owner => QuoteOwnerEmails.Expired(owner, quote, recipients.Link($"crm/teklifler/{quote.Id}")), cancellationToken);
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

                    var activity = await QuoteActivities.CreateAsync(pipeline, $"{QuoteActivities.ReminderPrefix}{quote.Title} ({quote.QuoteNumber})", due, record.Value!, ct);
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
                    await NotifyOwnerAsync(quote, owner => QuoteOwnerEmails.ExpiryReminder(owner, quote, recipients.Link($"crm/teklifler/{quote.Id}")), cancellationToken);
                }
                else if (!created.IsSuccess)
                {
                    warning = $"Teklif {quote.QuoteNumber} hatırlatması açılamadı: {created.Error!.Message} {string.Join("; ", created.Error.FieldErrors?.Select(f => $"{f.Field}: {f.Message}") ?? [])}";
                }
            }
        }

        var approvalReminders = policy.ApprovalReminderHours > 0 ? await RemindApproversAsync(now.AddHours(-policy.ApprovalReminderHours), now, cancellationToken) : 0;
        return new QuoteExpiryRun(expired, reminders, warning, ApprovalRemindersSent: approvalReminders);
    }

    /// <summary>
    /// Onay bekleyen tekliflerde şu anki adımın adlı onaylayanlarına (ve vekillerine) bir kez e-posta hatırlatması. Adsız adımda alıcı
    /// belirlenemez (onay izni olan herkes), gönderilmez ve işaretlenmez.
    /// </summary>
    private async Task<int> RemindApproversAsync(DateTimeOffset submittedBefore, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sent = 0;
        var rules = await reader.ActiveRulesAsync(cancellationToken);
        var delegations = await reader.ActiveDelegationsAsync(now, cancellationToken);
        foreach (var quote in await reader.PendingApprovalsAsync(submittedBefore, BatchSize, cancellationToken))
        {
            if (await reader.HasEventSinceSubmissionAsync(quote.Id, QuoteMarkerKinds.ApprovalReminderSent, cancellationToken))
            {
                continue;
            }

            var matches = QuoteApprovalEvaluator.Match(rules, quote.Currency, quote.ListTotal, quote.DiscountTotal, quote.GrossTotal);
            var approvals = (await reader.StepApprovalsSinceSubmissionAsync(quote.Id, cancellationToken)).Where(e => e.ActorId is not null)
                .Select(e => new QuoteApprovalGiven(e.ActorId!.Value, new HashSet<Guid>(delegations.Where(d => d.ToUserId == e.ActorId).Select(d => d.FromUserId)) { e.ActorId!.Value }));
            var current = QuoteApprovalEvaluator.Current(QuoteApprovalEvaluator.Steps(matches, approvals));
            if (current is null || current.Approvers.Count == 0)
            {
                continue;
            }

            var recipientIds = current.Approvers.Where(a => !current.ApprovedBy.Contains(a))
                .SelectMany(a => delegations.Where(d => d.FromUserId == a).Select(d => d.ToUserId).Prepend(a)).Distinct().ToList();
            var link = recipients.Link($"crm/teklifler/{quote.Id}");
            var delivered = 0;
            foreach (var userId in recipientIds)
            {
                if (await recipients.FindAsync(userId, cancellationToken) is { } to)
                {
                    try
                    {
                        emails.Enqueue(QuoteOwnerEmails.ApprovalReminder(to, quote, link));
                        delivered++;
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }

            if (delivered > 0)
            {
                await markers.MarkAsync(quote.Id, QuoteMarkerKinds.ApprovalReminderSent, $"{delivered} onaylayana hatırlatma e-postası kuyruğa alındı (adım {current.Step}).", MarkPermissions, cancellationToken);
                sent++;
            }
        }

        return sent;
    }

    /// <summary>Sorumluya e-posta; sorumlu yoksa ya da etkin üye değilse sessizce atlanır. Bildirim hatası işin sonucunu bozmaz.</summary>
    private async Task NotifyOwnerAsync(Quote quote, Func<NotificationRecipient, EmailMessage> build, CancellationToken cancellationToken)
    {
        if (quote.OwnerId is not { } ownerId || await recipients.FindAsync(ownerId, cancellationToken) is not { } owner)
        {
            return;
        }

        try
        {
            emails.Enqueue(build(owner));
        }
        catch (ArgumentException)
        {
            // Geçersiz adres vb.: bildirim atlanır, teklif durumu değişmez.
        }
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
            quote.PaymentTerms, quote.DeliveryTerms, quote.CommercialTerms, lines)
        {
            PartyId = await reader.PartyOfCompanyAsync(quote.CompanyId, cancellationToken),
            PersonId = quote.ContactId is { } contactId ? await reader.PersonOfContactAsync(contactId, cancellationToken) : null,
        };
    }
}
