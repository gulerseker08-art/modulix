using System.Buffers.Text;
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Quotes;

/// <summary>Paylaşım belirteci üretimi ve özeti. Ham belirteç yalnızca oluşturulurken görünür; saklanan SHA-256 özetidir.</summary>
internal static class QuoteTokens
{
    public const int Bytes = 32;

    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(Bytes));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string HashBytes(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    /// <summary>Üretilebilecek bir belirteç biçimi mi? Biçimi bozuk istek veritabanına hiç gitmez.</summary>
    public static bool LooksValid(string? token) =>
        token is { Length: >= 40 and <= 64 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

internal static class QuoteSharingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Teklif belgesi ve paylaşım işlemleri. Belge, bağlantı ve karar teklif toplamının parçasıdır: yazma teklif güncellemesiyle aynı işlemde
/// (<see cref="QuoteWorkflowGate.Effect"/>) yapılır; böylece sürüm denetimi, audit ve tarihçe ortaktır. Görünürlük (izin ve veri kapsamı)
/// önce kayıt boru hattında doğrulanır; bulunamayan/kapsam dışı teklif 404 alır.
/// </summary>
internal sealed class QuoteSharing(
    IRecordPipeline pipeline, QuoteWorkflowGate gate, ICurrentAccess access, QuoteReader reader, TimeProvider time) : IQuoteSharing
{
    private const string Key = CrmModule.Entities.Quote;
    private const int MaxPdfBytes = 8 * 1024 * 1024;
    private const int MaxLinkDays = 180;

    /// <summary>Belge üretimi paylaşım izniyle ya da sunum izniyle yapılır (sunum sırasında belge sunan kişi adına üretilir).</summary>
    private static readonly string[] DocumentPermissions = [CrmModule.Permissions.QuotesShare, CrmModule.Permissions.QuotesPresent];

    private static readonly string[] SharePermissions = [CrmModule.Permissions.QuotesShare];

    public async Task<RecordResult<QuoteDocumentSnapshot>> BuildSnapshotAsync(Guid quoteId, QuoteSellerInfo seller, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync<QuoteDocumentSnapshot>(quoteId, DocumentPermissions, cancellationToken);
        if (loaded.Error is { } error)
        {
            return error;
        }

        var quote = loaded.Quote!;
        if (quote.Status == QuoteStatus.Draft)
        {
            return Conflict<QuoteDocumentSnapshot>("Taslak teklif için müşteri belgesi üretilemez; önce müşteriye sunun.");
        }

        var company = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Company, quote.CompanyId), cancellationToken);
        var customerName = quote.CustomerNameSnapshot ?? (company.IsSuccess ? Text(company.Value!, "name") : null) ?? "—";
        string? contactName = null;
        string? contactEmail = null;
        if (quote.ContactId is { } contactId)
        {
            var contact = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Contact, contactId), cancellationToken);
            if (contact.IsSuccess)
            {
                contactName = string.Join(' ', new[] { Text(contact.Value!, "firstName"), Text(contact.Value!, "lastName") }.Where(p => p is not null));
                contactEmail = Text(contact.Value!, "email");
            }
        }

        var lines = (await reader.LinesAsync(quoteId, cancellationToken))
            .Select(l => new QuoteSnapshotLine(
                l.GroupName, l.ProductCode, l.Name, l.Description, l.Quantity, l.UnitName, l.UnitPrice, l.DiscountKind, l.DiscountValue,
                l.TaxRatePercent, l.Role, l.IsSelected, l.Included, l.RecurringPeriod, l.ListAmount, l.LineDiscount, l.Net, l.Tax, l.Gross))
            .ToList();
        var taxGroups = lines.Where(l => l.Included).GroupBy(l => l.TaxRatePercent).OrderBy(g => g.Key)
            .Select(g => new QuoteSnapshotTax(g.Key, g.Sum(l => l.Net), g.Sum(l => l.Tax))).ToList();

        return RecordResult<QuoteDocumentSnapshot>.Ok(new QuoteDocumentSnapshot(
            1, quote.QuoteNumber, quote.RevisionNo, quote.Title, quote.Description, quote.IssueDate, quote.ValidUntil, quote.PresentedAt ?? time.GetUtcNow(),
            quote.Currency, quote.PriceIncludesTax, customerName, company.IsSuccess ? Text(company.Value!, "taxNumber") : null,
            company.IsSuccess ? Text(company.Value!, "city") : null, contactName is { Length: > 0 } ? contactName : null, contactEmail, seller, lines, taxGroups,
            quote.ListTotal, quote.DiscountTotal, quote.NetTotal, quote.TaxTotal, quote.GrossTotal, quote.OptionalNet,
            quote.PaymentTerms, quote.DeliveryTerms, quote.CommercialTerms));
    }

    public async Task<RecordResult<QuoteDocumentInfo>> StoreDocumentAsync(
        Guid quoteId, QuoteDocumentSnapshot snapshot, byte[] pdf, int templateVersion, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync<QuoteDocumentInfo>(quoteId, DocumentPermissions, cancellationToken);
        if (loaded.Error is { } error)
        {
            return error;
        }

        var quote = loaded.Quote!;
        if (quote.Status == QuoteStatus.Draft)
        {
            return Conflict<QuoteDocumentInfo>("Taslak teklif için müşteri belgesi üretilemez; önce müşteriye sunun.");
        }

        // Revizyon başına tek belge: varsa mevcut belge döner, hiçbir şey değiştirilmez.
        if ((await reader.DocumentsAsync(quoteId, cancellationToken)).FirstOrDefault() is { } existing)
        {
            return RecordResult<QuoteDocumentInfo>.Ok(existing.ToInfo());
        }

        if (pdf.Length == 0 || pdf.Length > MaxPdfBytes || !pdf.AsSpan().StartsWith("%PDF-"u8))
        {
            return RecordResult<QuoteDocumentInfo>.Fail(RecordErrorCode.ValidationFailed, "Üretilen belge geçerli bir PDF değil.");
        }

        var document = new QuoteDocument
        {
            QuoteId = quoteId,
            RevisionNo = quote.RevisionNo,
            FileName = $"{quote.QuoteNumber}-V{quote.RevisionNo}.pdf",
            ContentType = "application/pdf",
            SizeBytes = pdf.Length,
            Sha256 = QuoteTokens.HashBytes(pdf),
            TemplateVersion = templateVersion,
            Snapshot = JsonSerializer.Serialize(snapshot, QuoteSharingJson.Options),
            Content = pdf,
        };

        RecordResult<RecordData> result;
        try
        {
            result = await Mutate(loaded.Record!, DocumentPermissions, "DocumentGenerated", $"Belge özeti {document.Sha256[..12]}",
                (context, _, _) =>
                {
                    document.CreatedBy = context.CurrentUserId;
                    context.Data.Set<QuoteDocument>().Add(document);
                    return Task.CompletedTask;
                }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Aynı revizyon için eşzamanlı ikinci üretim benzersiz indekste elenir.
            return Conflict<QuoteDocumentInfo>("Belge başka bir istekte üretildi; sayfayı yenileyin.");
        }

        return result.IsSuccess
            ? RecordResult<QuoteDocumentInfo>.Ok(new QuoteDocumentInfo(document.Id, document.RevisionNo, document.FileName, document.SizeBytes, document.Sha256, document.TemplateVersion, document.CreatedAt))
            : Failed<QuoteDocumentInfo>(result);
    }

    public async Task<RecordResult<IReadOnlyList<QuoteDocumentInfo>>> ListDocumentsAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync<IReadOnlyList<QuoteDocumentInfo>>(quoteId, null, cancellationToken);
        return loaded.Error ?? RecordResult<IReadOnlyList<QuoteDocumentInfo>>.Ok([.. (await reader.DocumentsAsync(quoteId, cancellationToken)).Select(d => d.ToInfo())]);
    }

    public async Task<RecordResult<QuoteDocumentFile>> GetDocumentAsync(Guid quoteId, Guid documentId, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync<QuoteDocumentFile>(quoteId, null, cancellationToken);
        if (loaded.Error is { } error)
        {
            return error;
        }

        if (await reader.DocumentMetaAsync(documentId, cancellationToken) is not { } meta || meta.QuoteId != quoteId
            || await reader.DocumentContentAsync(documentId, cancellationToken) is not { } content)
        {
            return RecordResult<QuoteDocumentFile>.Fail(RecordErrorCode.NotFound, "Belge bulunamadı.");
        }

        return RecordResult<QuoteDocumentFile>.Ok(new QuoteDocumentFile(meta.ToInfo(), meta.ContentType, content));
    }

    public async Task<RecordResult<QuoteShareLinkCreated>> CreateLinkAsync(Guid quoteId, QuoteShareRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync<QuoteShareLinkCreated>(quoteId, SharePermissions, cancellationToken);
        if (loaded.Error is { } error)
        {
            return error;
        }

        var quote = loaded.Quote!;
        var now = time.GetUtcNow();
        var rule = QuoteRules.Can(QuoteAction.Share, quote.Status, quote.ApprovalStatus, quote.ValidUntil, now, true);
        if (!rule.Allowed)
        {
            return Conflict<QuoteShareLinkCreated>(rule.Message ?? "Bu teklif paylaşılamaz.");
        }

        var errors = new List<RecordFieldError>();
        var name = Clean(request.RecipientName);
        var email = Clean(request.RecipientEmail);
        if (name is { Length: > QuoteShareLink.NameMaxLength })
        {
            errors.Add(new RecordFieldError("recipientName", "Alıcı adı çok uzun."));
        }

        if (email is not null && !IsEmail(email))
        {
            errors.Add(new RecordFieldError("recipientEmail", "Geçerli bir e-posta adresi girin."));
        }

        if (request.ValidDays is { } requested and (< 1 or > MaxLinkDays))
        {
            errors.Add(new RecordFieldError("validDays", $"Bağlantı süresi 1-{MaxLinkDays} gün olmalıdır."));
        }

        var documents = await reader.DocumentsAsync(quoteId, cancellationToken);
        var document = request.DocumentId is { } wanted ? documents.FirstOrDefault(d => d.Id == wanted) : documents.FirstOrDefault();
        if (document is null)
        {
            errors.Add(new RecordFieldError("documentId", "Bağlantı için önce müşteri belgesi (PDF) üretilmelidir."));
        }

        if (errors.Count > 0)
        {
            return RecordResult<QuoteShareLinkCreated>.Fail(RecordErrorCode.ValidationFailed, errors[0].Message, errors);
        }

        // Varsayılan süre: teklif geçerlilik bitişinden bir hafta sonrası (kapanış sayfasını görebilsin), yoksa 30 gün.
        var days = request.ValidDays
            ?? (quote.ValidUntil is { } until ? Math.Clamp((int)Math.Ceiling((until - now).TotalDays) + 7, 1, MaxLinkDays) : 30);
        var token = QuoteTokens.New();
        var link = new QuoteShareLink
        {
            QuoteId = quoteId,
            DocumentId = document!.Id,
            TokenHash = QuoteTokens.Hash(token),
            TokenHint = token[^4..],
            RecipientName = name,
            RecipientEmail = email,
            ExpiresAt = now.AddDays(days),
        };

        var result = await Mutate(loaded.Record!, SharePermissions, "ShareLinkCreated", name is null ? null : $"Alıcı: {name}",
            (context, _, _) =>
            {
                link.CreatedBy = context.CurrentUserId;
                context.Data.Set<QuoteShareLink>().Add(link);
                return Task.CompletedTask;
            }, cancellationToken);
        return result.IsSuccess
            ? RecordResult<QuoteShareLinkCreated>.Ok(new QuoteShareLinkCreated(ToInfo(link, now), token))
            : Failed<QuoteShareLinkCreated>(result);
    }

    public async Task<RecordResult<IReadOnlyList<QuoteShareLinkInfo>>> ListLinksAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync<IReadOnlyList<QuoteShareLinkInfo>>(quoteId, SharePermissions, cancellationToken);
        if (loaded.Error is { } error)
        {
            return error;
        }

        var now = time.GetUtcNow();
        return RecordResult<IReadOnlyList<QuoteShareLinkInfo>>.Ok([.. (await reader.LinksAsync(quoteId, cancellationToken)).Select(l => ToInfo(l, now))]);
    }

    public async Task<RecordResult<QuoteShareLinkInfo>> RevokeLinkAsync(Guid quoteId, Guid linkId, string reason, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync<QuoteShareLinkInfo>(quoteId, SharePermissions, cancellationToken);
        if (loaded.Error is { } error)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return RecordResult<QuoteShareLinkInfo>.Fail(RecordErrorCode.ValidationFailed, "İptal gerekçesi zorunludur.", [new RecordFieldError("reason", "İptal gerekçesi zorunludur.")]);
        }

        var link = (await reader.LinksAsync(quoteId, cancellationToken)).FirstOrDefault(l => l.Id == linkId);
        if (link is null)
        {
            return RecordResult<QuoteShareLinkInfo>.Fail(RecordErrorCode.NotFound, "Bağlantı bulunamadı.");
        }

        if (link.RevokedAt is not null)
        {
            return Conflict<QuoteShareLinkInfo>("Bağlantı zaten iptal edilmiş.");
        }

        var now = time.GetUtcNow();
        var text = reason.Trim().Length > Quote.ReasonMaxLength ? reason.Trim()[..Quote.ReasonMaxLength] : reason.Trim();
        QuoteShareLink? changed = null;
        var result = await Mutate(loaded.Record!, SharePermissions, "ShareLinkRevoked", text,
            async (context, _, ct) =>
            {
                changed = await context.Data.Set<QuoteShareLink>().SingleAsync(l => l.Id == linkId, ct);
                changed.RevokedAt = now;
                changed.RevokedBy = context.CurrentUserId;
                changed.RevokeReason = text;
                changed.Touch();
            }, cancellationToken);
        return result.IsSuccess && changed is not null ? RecordResult<QuoteShareLinkInfo>.Ok(ToInfo(changed, now)) : Failed<QuoteShareLinkInfo>(result);
    }

    public async Task<RecordResult<IReadOnlyList<QuoteDecisionInfo>>> DecisionsAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync<IReadOnlyList<QuoteDecisionInfo>>(quoteId, null, cancellationToken);
        if (loaded.Error is { } error)
        {
            return error;
        }

        return RecordResult<IReadOnlyList<QuoteDecisionInfo>>.Ok([.. (await reader.DecisionsAsync(quoteId, cancellationToken)).Select(d => new QuoteDecisionInfo(
            d.Id, d.ShareLinkId, d.DocumentId, d.DocumentSha256, d.Decision, d.SignerName, d.SignerEmail, d.SignerTitle, d.Reason, d.TermsStatement,
            d.ClientAddressMasked, d.CreatedAt))]);
    }

    public async Task<RecordResult<string>> SetDeliveryStatusAsync(Guid quoteId, string status, string? note, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync<string>(quoteId, SharePermissions, cancellationToken);
        if (loaded.Error is { } error)
        {
            return error;
        }

        if (!QuoteDeliveryStatus.All.Contains(status))
        {
            return RecordResult<string>.Fail(RecordErrorCode.ValidationFailed, "Geçersiz gönderim durumu.", [new RecordFieldError("deliveryStatus", "Geçersiz gönderim durumu.")]);
        }

        if (loaded.Quote!.DeliveryStatus == status)
        {
            return RecordResult<string>.Ok(status);
        }

        var result = await Mutate(loaded.Record!, SharePermissions, "DeliveryUpdated", note is null ? null : (note.Length > Quote.ReasonMaxLength ? note[..Quote.ReasonMaxLength] : note),
            (_, _, _) => Task.CompletedTask, cancellationToken, new JsonObject { ["deliveryStatus"] = status });
        return result.IsSuccess ? RecordResult<string>.Ok(status) : Failed<string>(result);
    }

    // ---- yardımcılar

    private readonly record struct Loaded<T>(RecordData? Record, Quote? Quote, RecordResult<T>? Error);

    /// <summary>Teklifi boru hattından okur (izin + veri kapsamı), sonra eylem iznini denetler: kapsam dışı 404, izinsiz 403.</summary>
    private async Task<Loaded<T>> LoadAsync<T>(Guid quoteId, IReadOnlyList<string>? anyOf, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, quoteId), cancellationToken);
        if (!record.IsSuccess)
        {
            return new Loaded<T>(null, null, RecordResult<T>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors));
        }

        if (anyOf is not null && !(await access.GetPermissionsAsync(cancellationToken)).Overlaps(anyOf))
        {
            return new Loaded<T>(null, null, RecordResult<T>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{anyOf[0]}' izni gerekir."));
        }

        var quote = await reader.QuoteAsync(quoteId, cancellationToken);
        return quote is null
            ? new Loaded<T>(null, null, RecordResult<T>.Fail(RecordErrorCode.NotFound, "Teklif bulunamadı."))
            : new Loaded<T>(record.Value, quote, null);
    }

    /// <summary>Teklif değerlerine dokunmayan güncelleme: ek yazma, tarihçe satırı ve audit teklifin işleminde birlikte kaydedilir.</summary>
    private async Task<RecordResult<RecordData>> Mutate(
        RecordData record, IReadOnlyList<string> permissions, string eventKind, string? reason, Func<CrmStoreContext, Quote, CancellationToken, Task> effect,
        CancellationToken cancellationToken, JsonObject? values = null)
    {
        using (gate.Open(permissions, eventKind, reason, effect))
        {
            return await pipeline.UpdateAsync(
                new RecordCommand(Key, record.Id, values ?? new JsonObject(), record.Version) { Reason = $"quote.{eventKind}" }, cancellationToken);
        }
    }

    private static QuoteShareLinkInfo ToInfo(QuoteShareLink link, DateTimeOffset now) => new(
        link.Id, link.DocumentId, link.TokenHint, link.RecipientName, link.RecipientEmail, link.ExpiresAt, link.RevokedAt, link.RevokeReason,
        link.FirstViewedAt, link.CreatedAt, link.RevokedAt is not null ? "Revoked" : link.ExpiresAt <= now ? "Expired" : "Active");

    private static RecordResult<T> Conflict<T>(string message) =>
        RecordResult<T>.Fail(RecordErrorCode.Conflict, message, [new RecordFieldError("status", message)]);

    private static RecordResult<T> Failed<T>(RecordResult<RecordData> result) =>
        RecordResult<T>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool IsEmail(string text) =>
        text.Length <= QuoteShareLink.EmailMaxLength && MailAddress.TryCreate(text, out var address) && address.Address == text && text.Contains('.');

    private static string? Text(RecordData record, string name) =>
        record.Values[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}

/// <summary>
/// Müşteri portalı (oturumsuz). Kimlik doğrulama bağlantı belirtecine sahip olmaktır; istek sistem kimliğiyle ("crm.quote-customer")
/// çalışır ve yalnızca belirtecin bağlı olduğu belgeyi/teklifi görür. Müşteriye giden her şey belgenin sabitlenmiş verisinden gelir
/// (maliyet, iç not ve canlı katalog verisi sızmaz). Kararlar teklif geçişiyle aynı işlemde ve sürüm denetimiyle yazılır: eşzamanlı
/// kabul/ret/iptal yarışında yalnızca biri kazanır.
/// </summary>
internal sealed class QuoteCustomerPortal(
    IRecordPipeline pipeline, QuoteWorkflowGate gate, QuoteReader reader, TimeProvider time, Modulix.Core.Contracts.Events.IBusinessEventPublisher events) : IQuoteCustomerPortal
{
    private const string Key = CrmModule.Entities.Quote;
    private const string Unknown = "Bağlantı geçersiz.";

    public async Task<QuotePortalResult<QuotePortalView>> ViewAsync(string token, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(token, cancellationToken) is not { } context)
        {
            return QuotePortalResult<QuotePortalView>.Fail(QuotePortalError.NotFound, Unknown);
        }

        var view = await BuildViewAsync(context, cancellationToken);
        if (view.Snapshot is not null && context.Link.FirstViewedAt is null && context.Link.RevokedAt is null && context.Link.ExpiresAt > time.GetUtcNow())
        {
            await MarkViewedAsync(context, cancellationToken);
        }

        return QuotePortalResult<QuotePortalView>.Ok(view);
    }

    public async Task<QuotePortalResult<QuoteDocumentFile>> DocumentAsync(string token, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(token, cancellationToken) is not { } context)
        {
            return QuotePortalResult<QuoteDocumentFile>.Fail(QuotePortalError.NotFound, Unknown);
        }

        var view = await BuildViewAsync(context, cancellationToken);
        if (view.Snapshot is null || await reader.DocumentContentAsync(context.Document.Id, cancellationToken) is not { } content)
        {
            return QuotePortalResult<QuoteDocumentFile>.Fail(QuotePortalError.Conflict, view.Message ?? "Belge şu an görüntülenemez.");
        }

        return QuotePortalResult<QuoteDocumentFile>.Ok(new QuoteDocumentFile(context.Document.ToInfo(), context.Document.ContentType, content));
    }

    public async Task<QuotePortalResult<QuotePortalView>> DecideAsync(QuotePortalDecision request, QuoteClientInfo client, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(request.Token, cancellationToken) is not { } context)
        {
            return QuotePortalResult<QuotePortalView>.Fail(QuotePortalError.NotFound, Unknown);
        }

        var view = await BuildViewAsync(context, cancellationToken);
        if (!view.CanDecide)
        {
            return QuotePortalResult<QuotePortalView>.Fail(QuotePortalError.Conflict, view.Message ?? "Bu teklif için karar verilemez.");
        }

        var fields = new Dictionary<string, string>();
        var name = request.SignerName?.Trim();
        var email = string.IsNullOrWhiteSpace(request.SignerEmail) ? null : request.SignerEmail.Trim();
        var title = string.IsNullOrWhiteSpace(request.SignerTitle) ? null : request.SignerTitle.Trim();
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (string.IsNullOrEmpty(name) || name.Length < 2)
        {
            fields["signerName"] = "Ad soyad zorunludur.";
        }
        else if (name.Length > QuoteDecision.NameMaxLength)
        {
            fields["signerName"] = "Ad soyad çok uzun.";
        }

        if (email is not null && (email.Length > QuoteShareLink.EmailMaxLength || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || !email.Contains('.')))
        {
            fields["signerEmail"] = "Geçerli bir e-posta adresi girin.";
        }

        if (title is { Length: > QuoteDecision.NameMaxLength })
        {
            fields["signerTitle"] = "Ünvan çok uzun.";
        }

        if (reason is { Length: > Quote.ReasonMaxLength })
        {
            fields["reason"] = $"Gerekçe en fazla {Quote.ReasonMaxLength} karakter olabilir.";
        }

        if (request.Accept && !request.ConfirmTerms)
        {
            fields["confirmTerms"] = "Kabul için onay metnini işaretlemelisiniz.";
        }

        if (!request.Accept && reason is null)
        {
            fields["reason"] = "Ret için kısa bir gerekçe yazın.";
        }

        // Müşteri gördüğü belgeye karar verir: sayfa eskidiyse (yeni belge/revizyon) karar reddedilir.
        if (!string.Equals(request.DocumentSha256, context.Document.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            fields["documentSha256"] = "Belge değişmiş; sayfayı yenileyip tekrar inceleyin.";
        }

        if (fields.Count > 0)
        {
            return QuotePortalResult<QuotePortalView>.Fail(QuotePortalError.Invalid, "Lütfen işaretli alanları düzeltin.", fields);
        }

        var now = time.GetUtcNow();
        var action = request.Accept ? QuoteAction.Accept : QuoteAction.Reject;
        var rule = QuoteRules.Can(action, context.Quote.Status, context.Quote.ApprovalStatus, context.Quote.ValidUntil, now, true);
        if (!rule.Allowed)
        {
            return QuotePortalResult<QuotePortalView>.Fail(QuotePortalError.Conflict, rule.Message ?? "Bu teklif için karar verilemez.");
        }

        var decision = new QuoteDecision
        {
            QuoteId = context.Quote.Id,
            ShareLinkId = context.Link.Id,
            DocumentId = context.Document.Id,
            DocumentSha256 = context.Document.Sha256,
            Decision = request.Accept ? "Accepted" : "Rejected",
            SignerName = name!,
            SignerEmail = email,
            SignerTitle = title,
            Reason = reason,
            TermsStatement = request.Accept ? view.AcceptanceStatement : null,
            ClientAddressMasked = MaskAddress(client.Address),
            UserAgent = client.UserAgent is { Length: > 0 } agent ? (agent.Length > QuoteDecision.AgentMaxLength ? agent[..QuoteDecision.AgentMaxLength] : agent) : null,
        };

        var values = new JsonObject
        {
            ["status"] = request.Accept ? QuoteStatus.Accepted : QuoteStatus.Rejected,
            ["decidedAt"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["statusReason"] = Truncate(request.Accept ? $"Müşteri kabul etti: {name}" : $"Müşteri reddetti: {reason}", Quote.ReasonMaxLength),
        };
        var kind = request.Accept ? "CustomerAccepted" : "CustomerRejected";
        RecordResult<RecordData> result;
        using (gate.Open([CrmModule.Permissions.QuotesRecordDecision], kind, Truncate($"{name}{(reason is null ? string.Empty : $": {reason}")}", Quote.ReasonMaxLength),
            (context2, _, _) =>
            {
                context2.Data.Set<QuoteDecision>().Add(decision);
                return Task.CompletedTask;
            }))
        {
            var command = new RecordCommand(Key, context.Quote.Id, values, context.Quote.Version) { Reason = $"quote.{kind}" };
            result = request.Accept
                // Müşterinin kabulü de iş olayını (ADR-0039) durum ve karar kaydıyla aynı işlemde yazar.
                ? await pipeline.RunAtomicAsync(async ct =>
                {
                    var updated = await pipeline.UpdateAsync(command, ct);
                    if (updated.IsSuccess)
                    {
                        await QuoteAcceptedEvent.PublishAsync(events, updated.Value!, QuoteAcceptedEvent.Portal, ct);
                    }

                    return updated;
                }, cancellationToken)
                : await pipeline.UpdateAsync(command, cancellationToken);
        }

        if (!result.IsSuccess)
        {
            // Sürüm çakışması: başka bir karar/iptal/revizyon yarışı kazanmıştır; müşteriye güncel durum söylenir.
            var latest = await ResolveAsync(request.Token, cancellationToken);
            var message = latest is null ? "Teklif durumu değişti." : (await BuildViewAsync(latest, cancellationToken)).Message ?? "Teklif durumu değişti; sayfayı yenileyin.";
            return QuotePortalResult<QuotePortalView>.Fail(QuotePortalError.Conflict, result.Error!.Code == RecordErrorCode.Conflict ? message : result.Error.Message);
        }

        var refreshed = await ResolveAsync(request.Token, cancellationToken);
        return QuotePortalResult<QuotePortalView>.Ok(await BuildViewAsync(refreshed!, cancellationToken));
    }

    // ---- yardımcılar

    private sealed record Context(QuoteShareLink Link, QuoteDocumentMeta Document, Quote Quote);

    private async Task<Context?> ResolveAsync(string token, CancellationToken cancellationToken)
    {
        if (!QuoteTokens.LooksValid(token))
        {
            return null;
        }

        if (await reader.LinkByHashAsync(QuoteTokens.Hash(token), cancellationToken) is not { } link
            || await reader.DocumentMetaAsync(link.DocumentId, cancellationToken) is not { } document
            || await reader.QuoteAsync(link.QuoteId, cancellationToken) is not { } quote)
        {
            return null;
        }

        return new Context(link, document, quote);
    }

    private async Task<QuotePortalView> BuildViewAsync(Context context, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var quote = context.Quote;
        var document = context.Document;
        QuoteDocumentSnapshot? Snapshot() => JsonSerializer.Deserialize<QuoteDocumentSnapshot>(document.Snapshot, QuoteSharingJson.Options);

        QuotePortalView Closed(string state, string message, QuoteDocumentSnapshot? snapshot = null, QuotePortalDecisionInfo? decision = null) =>
            new(state, false, message, quote.QuoteNumber, document.RevisionNo, document.Sha256, context.Link.ExpiresAt, snapshot, null, decision);

        if (context.Link.RevokedAt is not null)
        {
            return Closed("link-revoked", "Bu bağlantı iptal edilmiş. Lütfen teklifi gönderen kişiyle iletişime geçin.");
        }

        if (context.Link.ExpiresAt <= now)
        {
            return Closed("link-expired", "Bu bağlantının süresi dolmuş. Lütfen teklifi gönderen kişiden yeni bir bağlantı isteyin.");
        }

        // Bağlantı başka bir revizyonun belgesine aitse (ya da teklif artık o sürümde değilse) içerik gösterilmez.
        switch (quote.Status)
        {
            case QuoteStatus.Superseded:
                return Closed("superseded", "Bu teklifin yeni bir revizyonu hazırlandı. Güncel teklif için lütfen teklifi gönderen kişiyle iletişime geçin.");
            case QuoteStatus.Cancelled:
                return Closed("cancelled", "Bu teklif iptal edilmiş.");
            case QuoteStatus.Draft:
                return Closed("cancelled", "Bu teklif şu an müşteriye açık değil.");
        }

        var snapshot = Snapshot();
        if (quote.Status is QuoteStatus.Accepted or QuoteStatus.Rejected)
        {
            var last = await reader.LastDecisionAsync(quote.Id, cancellationToken);
            var info = last is null ? null : new QuotePortalDecisionInfo(last.Decision, last.SignerName, last.CreatedAt);
            return Closed(quote.Status == QuoteStatus.Accepted ? "accepted" : "rejected",
                quote.Status == QuoteStatus.Accepted ? "Bu teklif kabul edilmiştir." : "Bu teklif reddedilmiştir.", snapshot, info);
        }

        if (quote.Status == QuoteStatus.Expired || (quote.ValidUntil is { } until && until <= now))
        {
            return Closed("expired", "Bu teklifin geçerlilik süresi dolmuş. Yeni bir teklif için lütfen teklifi gönderen kişiyle iletişime geçin.", snapshot);
        }

        var statement = $"{snapshot?.CustomerName ?? "Firma"} adına, {quote.QuoteNumber} numaralı teklifi (V{quote.RevisionNo}, belge özeti {document.Sha256[..12]}) inceledim; "
            + "belgedeki fiyat, koşul ve geçerlilik şartlarını kabul ediyorum.";
        return new QuotePortalView("open", true, null, quote.QuoteNumber, document.RevisionNo, document.Sha256, context.Link.ExpiresAt, snapshot, statement, null);
    }

    /// <summary>İlk görüntülemeyi bağlantıya işler. Görüntüleme hatası müşteri sayfasını asla bozmaz.</summary>
    private async Task MarkViewedAsync(Context context, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        try
        {
            using (gate.Open([CrmModule.Permissions.QuotesRead], "CustomerViewed", null, async (store, _, ct) =>
            {
                var link = await store.Data.Set<QuoteShareLink>().SingleAsync(l => l.Id == context.Link.Id, ct);
                if (link.FirstViewedAt is null)
                {
                    link.FirstViewedAt = now;
                    link.Touch();
                }
            }))
            {
                await pipeline.UpdateAsync(
                    new RecordCommand(Key, context.Quote.Id, new JsonObject(), context.Quote.Version) { Reason = "quote.CustomerViewed" }, cancellationToken);
            }
        }
        catch (DbUpdateException)
        {
            // Eşzamanlı ilk görüntüleme ya da yarış; kayıp yalnızca bir istatistiktir.
        }
    }

    private static string? MaskAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var text = address.Trim();
        if (text.Contains(':'))
        {
            var groups = text.Split(':', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(':', groups.Take(3)) + "::x";
        }

        var parts = text.Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.{parts[2]}.x" : null;
    }

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] : text;
}

/// <summary>"crm.quote-customer" kimliğinin izin çapası (herkese açık müşteri sayfası sistem adına çalışır); olay işlemez.</summary>
internal sealed class QuoteCustomerAnchor : Modulix.Core.Contracts.Events.IEventSubscriber
{
    public Task HandleAsync(Modulix.Core.Contracts.Events.EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}
