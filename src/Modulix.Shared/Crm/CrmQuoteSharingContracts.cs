namespace Modulix.Shared.Crm;

// Teklif belgesi, paylaşım bağlantısı ve müşteri sayfası sözleşmeleri (ADR-0019, dilim 3).

public sealed record QuoteDocumentDto(Guid Id, int RevisionNo, string FileName, int SizeBytes, string Sha256, int TemplateVersion, DateTimeOffset CreatedAt);

/// <summary>
/// Bağlantı durumu <c>State</c>: Active, Expired ya da Revoked. E-posta durumu <c>DeliveryStatus</c>: NotSent, Pending (kuyrukta), Processing (deneniyor),
/// Succeeded (sağlayıcı kabul etti; alıcıya ulaştığı anlamına gelmez) ya da Failed. Ham belirteç burada yoktur; yalnızca oluşturma yanıtında döner.
/// </summary>
public sealed record QuoteShareLinkDto(
    Guid Id, Guid DocumentId, string TokenHint, string? RecipientName, string? RecipientEmail, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt,
    string? RevokeReason, DateTimeOffset? FirstViewedAt, DateTimeOffset CreatedAt, string State,
    string DeliveryStatus = "NotSent", string? DeliveryError = null, DateTimeOffset? ProviderAcceptedAt = null);

/// <summary>
/// Oluşturma yanıtı. <see cref="Path"/> müşteri sayfasının yoludur ve belirteci URL parçasında (#) taşır; parça tarayıcıdan sunucuya
/// gönderilmediği için erişim günlüklerine ve yönlendiren başlığına girmez. Belirteç bir daha gösterilmez.
/// </summary>
public sealed record QuoteShareLinkCreatedDto(QuoteShareLinkDto Link, string Token, string Path);

public sealed record CreateQuoteShareLinkRequest(Guid? DocumentId = null, int? ValidDays = null, string? RecipientName = null, string? RecipientEmail = null, bool SendEmail = false);

public sealed record RevokeQuoteShareLinkRequest(string Reason);

public sealed record QuoteDecisionDto(
    Guid Id, Guid ShareLinkId, Guid DocumentId, string DocumentSha256, string Decision, string SignerName, string? SignerEmail, string? SignerTitle,
    string? Reason, string? TermsStatement, string? ClientAddressMasked, DateTimeOffset At);

// ---- Müşteri sayfası (oturumsuz)

public sealed record PublicQuoteTokenRequest(string Token);

public sealed record PublicQuoteDecisionRequest(
    string Token, string Decision, string SignerName, string? SignerEmail = null, string? SignerTitle = null, string? Reason = null,
    bool ConfirmTerms = false, string DocumentSha256 = "");

public sealed record PublicQuoteSeller(string Name, string? Details, string? FooterNote);

public sealed record PublicQuoteLine(
    string? GroupName, string? Code, string Name, string? Description, decimal Quantity, string? Unit, decimal UnitPrice,
    string DiscountKind, decimal DiscountValue, decimal TaxRatePercent, string Role, bool IsSelected, bool Included, string? RecurringPeriod,
    decimal ListAmount, decimal LineDiscount, decimal Net, decimal Tax, decimal Gross);

public sealed record PublicQuoteTax(decimal RatePercent, decimal Net, decimal Tax);

/// <summary>Müşterinin gördüğü sabit belge verisi. Alan adları sunucudaki belge verisiyle (QuoteDocumentSnapshot) aynıdır; maliyet ve iç not yoktur.</summary>
public sealed record PublicQuoteContent(
    int Schema, string QuoteNumber, int RevisionNo, string Title, string? Description, DateOnly IssueDate, DateTimeOffset? ValidUntil,
    DateTimeOffset PresentedAt, string Currency, bool PriceIncludesTax, string CustomerName, string? CustomerTaxNumber, string? CustomerCity,
    string? ContactName, string? ContactEmail, PublicQuoteSeller Seller, IReadOnlyList<PublicQuoteLine> Lines, IReadOnlyList<PublicQuoteTax> TaxGroups,
    decimal ListTotal, decimal DiscountTotal, decimal NetTotal, decimal TaxTotal, decimal GrossTotal, decimal OptionalNet,
    string? PaymentTerms, string? DeliveryTerms, string? CommercialTerms);

public sealed record PublicQuoteDecisionInfo(string Decision, string SignerName, DateTimeOffset At);

/// <summary>
/// Müşteri sayfasının durumu. <c>State</c>: open (karar verilebilir), accepted, rejected, expired, superseded, cancelled, link-expired,
/// link-revoked. <see cref="Content"/> yalnızca açık ve sonuçlanmış tekliflerde dolu gelir.
/// </summary>
public sealed record PublicQuoteView(
    string State, bool CanDecide, string? Message, string QuoteNumber, int RevisionNo, string DocumentSha256, DateTimeOffset LinkExpiresAt,
    PublicQuoteContent? Content, string? AcceptanceStatement, PublicQuoteDecisionInfo? Decision);
