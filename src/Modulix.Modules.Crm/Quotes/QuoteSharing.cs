using Modulix.Core.Contracts.Records;

namespace Modulix.Modules.Crm.Quotes;

// Teklif belgesi, paylaşım bağlantısı ve müşteri portalı sözleşmeleri (ADR-0019, dilim 3).

/// <summary>Satıcı (tenant) bilgisi; API ayarlardan doldurur (modül ayar altyapısına erişemez).</summary>
public sealed record QuoteSellerInfo(string Name, string? Details, string? FooterNote);

public sealed record QuoteSnapshotLine(
    string? GroupName, string? Code, string Name, string? Description, decimal Quantity, string? Unit, decimal UnitPrice,
    string DiscountKind, decimal DiscountValue, decimal TaxRatePercent, string Role, bool IsSelected, bool Included, string? RecurringPeriod,
    decimal ListAmount, decimal LineDiscount, decimal Net, decimal Tax, decimal Gross);

public sealed record QuoteSnapshotTax(decimal RatePercent, decimal Net, decimal Tax);

/// <summary>
/// Belgenin üretildiği andaki veri. Hem PDF hem müşteri sayfası bundan üretilir; maliyet ve iç not içermez. Alan adları
/// API'deki herkese açık sözleşmeyle (PublicQuoteContent) birebir aynıdır: ikisi JSON üzerinden eşlenir.
/// </summary>
public sealed record QuoteDocumentSnapshot(
    int Schema, string QuoteNumber, int RevisionNo, string Title, string? Description, DateOnly IssueDate, DateTimeOffset? ValidUntil,
    DateTimeOffset PresentedAt, string Currency, bool PriceIncludesTax, string CustomerName, string? CustomerTaxNumber, string? CustomerCity,
    string? ContactName, string? ContactEmail, QuoteSellerInfo Seller, IReadOnlyList<QuoteSnapshotLine> Lines, IReadOnlyList<QuoteSnapshotTax> TaxGroups,
    decimal ListTotal, decimal DiscountTotal, decimal NetTotal, decimal TaxTotal, decimal GrossTotal, decimal OptionalNet,
    string? PaymentTerms, string? DeliveryTerms, string? CommercialTerms);

public sealed record QuoteDocumentInfo(Guid Id, int RevisionNo, string FileName, int SizeBytes, string Sha256, int TemplateVersion, DateTimeOffset CreatedAt);

public sealed record QuoteDocumentFile(QuoteDocumentInfo Info, string ContentType, byte[] Content);

/// <summary>Bağlantı durumu: <c>Active</c>, <c>Expired</c> ya da <c>Revoked</c>.</summary>
public sealed record QuoteShareLinkInfo(
    Guid Id, Guid DocumentId, string TokenHint, string? RecipientName, string? RecipientEmail, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt,
    string? RevokeReason, DateTimeOffset? FirstViewedAt, DateTimeOffset CreatedAt, string State);

/// <summary>Bağlantı oluşturma sonucu: ham belirteç yalnızca burada, bir kez döner; veritabanında özeti saklanır.</summary>
public sealed record QuoteShareLinkCreated(QuoteShareLinkInfo Link, string Token);

public sealed record QuoteShareRequest(Guid? DocumentId, int? ValidDays, string? RecipientName, string? RecipientEmail);

public sealed record QuoteDecisionInfo(
    Guid Id, Guid ShareLinkId, Guid DocumentId, string DocumentSha256, string Decision, string SignerName, string? SignerEmail, string? SignerTitle,
    string? Reason, string? TermsStatement, string? ClientAddressMasked, DateTimeOffset At);

/// <summary>Teklif belgesi ve paylaşım işlemleri (iç kullanıcı). Görünürlük ve izin kayıt boru hattında/serviste uygulanır.</summary>
public interface IQuoteSharing
{
    /// <summary>Belge verisini oluşturur (yazmaz). Yalnızca sunulmuş ya da sonuçlanmış tekliften; <c>crm.quotes.share</c> ister.</summary>
    Task<RecordResult<QuoteDocumentSnapshot>> BuildSnapshotAsync(Guid quoteId, QuoteSellerInfo seller, CancellationToken cancellationToken);

    /// <summary>Üretilen PDF'i sabitler. Teklif revizyonu başına tek belge vardır; varsa mevcut belge döner (değiştirilmez).</summary>
    Task<RecordResult<QuoteDocumentInfo>> StoreDocumentAsync(
        Guid quoteId, QuoteDocumentSnapshot snapshot, byte[] pdf, int templateVersion, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<QuoteDocumentInfo>>> ListDocumentsAsync(Guid quoteId, CancellationToken cancellationToken);

    Task<RecordResult<QuoteDocumentFile>> GetDocumentAsync(Guid quoteId, Guid documentId, CancellationToken cancellationToken);

    Task<RecordResult<QuoteShareLinkCreated>> CreateLinkAsync(Guid quoteId, QuoteShareRequest request, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<QuoteShareLinkInfo>>> ListLinksAsync(Guid quoteId, CancellationToken cancellationToken);

    Task<RecordResult<QuoteShareLinkInfo>> RevokeLinkAsync(Guid quoteId, Guid linkId, string reason, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<QuoteDecisionInfo>>> DecisionsAsync(Guid quoteId, CancellationToken cancellationToken);

    /// <summary>
    /// Gönderim durumunu teklif başlığına işler (<see cref="QuoteDeliveryStatus"/>). Durum e-posta kuyruğundan okunur; <c>Succeeded</c> yalnızca
    /// sağlayıcının (SMTP sunucusunun) kabul ettiği anlamına gelir, alıcıya ulaştığı anlamına gelmez. Değişmediyse yazmaz.
    /// </summary>
    Task<RecordResult<string>> SetDeliveryStatusAsync(Guid quoteId, string status, string? note, CancellationToken cancellationToken);
}

// ---- Müşteri portalı (oturumsuz; kimlik doğrulama = bağlantı belirtecine sahip olmak)

public enum QuotePortalError
{
    None,

    /// <summary>Belirteç tanınmıyor.</summary>
    NotFound,

    /// <summary>İstek geçersiz (alan hataları döner).</summary>
    Invalid,

    /// <summary>Teklifin durumu bu kararı artık kabul etmiyor ya da başka bir karar yarışı kazandı.</summary>
    Conflict,
}

public sealed record QuotePortalResult<T>(T? Value, QuotePortalError Error = QuotePortalError.None, string? Message = null, IReadOnlyDictionary<string, string>? Fields = null)
{
    public bool IsSuccess => Error == QuotePortalError.None;

    public static QuotePortalResult<T> Ok(T value) => new(value);

    public static QuotePortalResult<T> Fail(QuotePortalError error, string message, IReadOnlyDictionary<string, string>? fields = null) => new(default, error, message, fields);
}

/// <summary>Müşterinin kararı (kabul ya da ret) ve kimliği; istemci bilgisi kanıt içindir.</summary>
public sealed record QuotePortalDecision(
    string Token, bool Accept, string SignerName, string? SignerEmail, string? SignerTitle, string? Reason, bool ConfirmTerms, string DocumentSha256);

public sealed record QuoteClientInfo(string? Address, string? UserAgent);

/// <summary>Bir karar bilgisi (müşteri sayfasında gösterilir).</summary>
public sealed record QuotePortalDecisionInfo(string Decision, string SignerName, DateTimeOffset At);

/// <summary>
/// Müşteri sayfasının durumu. <c>State</c>: <c>open</c> (karar verilebilir), <c>accepted</c>, <c>rejected</c>, <c>expired</c> (geçerlilik doldu),
/// <c>superseded</c>, <c>cancelled</c>, <c>link-expired</c>, <c>link-revoked</c>. Belge içeriği yalnızca açık ve sonuçlanmış tekliflerde döner.
/// </summary>
public sealed record QuotePortalView(
    string State, bool CanDecide, string? Message, string QuoteNumber, int RevisionNo, string DocumentSha256, DateTimeOffset LinkExpiresAt,
    QuoteDocumentSnapshot? Snapshot, string? AcceptanceStatement, QuotePortalDecisionInfo? Decision);

public interface IQuoteCustomerPortal
{
    Task<QuotePortalResult<QuotePortalView>> ViewAsync(string token, CancellationToken cancellationToken);

    /// <summary>Müşterinin gördüğü sabit PDF. Yalnızca belge içeriğinin gösterilebildiği durumlarda.</summary>
    Task<QuotePortalResult<QuoteDocumentFile>> DocumentAsync(string token, CancellationToken cancellationToken);

    Task<QuotePortalResult<QuotePortalView>> DecideAsync(QuotePortalDecision decision, QuoteClientInfo client, CancellationToken cancellationToken);
}
