namespace Modulix.Shared.Contracts;

// Sözleşme modülü API sözleşmeleri (ADR-0021). Durum, sürüm ve toplam alanları sunucu tarafından yazılır; istekte gönderilmez.
// Tutar alanları hassastır (contracts.financials.read): izni olmayan kullanıcıya null döner, gönderdiği değer yok sayılır.

public sealed record ContractDto(
    Guid Id,
    long Version,
    string Number,
    string Title,
    string Type,
    string? Description,
    Guid PartyId,
    Guid? PersonId,
    Guid? OwnerId,
    Guid? PreviousContractId,
    DateOnly? StartDate,
    DateOnly? EndDate,
    bool IsIndefinite,
    string Currency,
    bool PriceIncludesTax,
    decimal? ContractValue,
    string? PaymentTerms,
    string RenewalMode,
    int? RenewalPeriodMonths,
    int? NoticePeriodDays,
    DateOnly? NoticeDeadline,
    string Confidentiality,
    bool HasNonStandardTerms,
    string RiskLevel,
    string PreparationStatus,
    string ApprovalStatus,
    string SignatureStatus,
    string EffectiveStatus,
    string? StatusReason,
    int CurrentVersionNo,
    int? EffectiveVersionNo,
    decimal? NetTotal,
    decimal? TaxTotal,
    decimal? GrossTotal,
    string? RecurringSummary,
    DateTimeOffset? CalculatedAt,
    Guid? CreatedBy,
    Guid? UpdatedBy,
    Guid? SourceQuoteId,
    string? SourceQuoteNumber,
    int? SourceQuoteRevisionNo,
    string? SourceQuoteSha256,
    Guid? SourceOpportunityId,
    System.Text.Json.Nodes.JsonObject? Custom = null);

public sealed record SaveContractRequest(
    string Title,
    Guid PartyId,
    string? Type = null,
    string? Description = null,
    Guid? PersonId = null,
    Guid? OwnerId = null,
    Guid? PreviousContractId = null,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    bool IsIndefinite = false,
    string Currency = "TRY",
    bool PriceIncludesTax = false,
    decimal? ContractValue = null,
    string? PaymentTerms = null,
    string RenewalMode = "None",
    int? RenewalPeriodMonths = null,
    int? NoticePeriodDays = null,
    string Confidentiality = "Internal",
    bool HasNonStandardTerms = false,
    string RiskLevel = "Low",
    long? Version = null,
    System.Text.Json.Nodes.JsonObject? Custom = null);

public sealed record ContractPartyDto(
    Guid Id,
    long Version,
    Guid ContractId,
    string Role,
    Guid? PartyId,
    Guid? PersonId,
    int Position,
    string? Notes);

public sealed record SaveContractPartyRequest(
    Guid ContractId,
    string Role,
    Guid? PartyId = null,
    Guid? PersonId = null,
    int? Position = null,
    string? Notes = null,
    long? Version = null);

public sealed record ContractLineDto(
    Guid Id,
    long Version,
    Guid ContractId,
    Guid StableKey,
    int Position,
    string Kind,
    string? ProductCode,
    string Name,
    string? Description,
    decimal Quantity,
    string? UnitName,
    decimal? UnitPrice,
    decimal? DiscountPercent,
    decimal TaxRatePercent,
    string? BillingPeriod,
    Guid? SourceQuoteLineKey,
    decimal? ListAmount,
    decimal? LineDiscount,
    decimal? Net,
    decimal? Tax,
    decimal? Gross);

public sealed record SaveContractLineRequest(
    Guid ContractId,
    string Name,
    string Kind = "Service",
    string? ProductCode = null,
    string? Description = null,
    decimal Quantity = 1m,
    string? UnitName = null,
    decimal? UnitPrice = null,
    decimal? DiscountPercent = null,
    decimal TaxRatePercent = 0m,
    string? BillingPeriod = null,
    int? Position = null,
    long? Version = null);

public sealed record ContractHistoryDto(Guid Id, string Kind, DateTimeOffset At, Guid? ActorId, string? FromStatus, string? ToStatus, string? Reason);

/// <summary>Taraf rolleri; anahtarlar sunucudaki <c>PartyRoles</c> ile aynıdır.</summary>
public static class ContractPartyRoles
{
    public static IReadOnlyList<(string Key, string Label)> All { get; } =
    [
        ("Customer", "Müşteri"), ("Provider", "Hizmet veren"), ("Guarantor", "Kefil / garantör"), ("Subcontractor", "Alt yüklenici"),
        ("Signatory", "İmzacı"), ("BillingContact", "Fatura yetkilisi"), ("TechnicalContact", "Teknik yetkili"), ("Other", "Diğer"),
    ];
}

/// <summary>Sözleşme modülünün izin anahtarları (arayüz için); sunucudaki <c>ContractsModule.Permissions</c> ile aynıdır.</summary>
public static class ContractsAccess
{
    public const string ModuleKey = "contracts";
    public const string ContractsRead = "contracts.contracts.read";
    public const string ContractsWrite = "contracts.contracts.write";
    public const string FinancialsRead = "contracts.financials.read";
    public const string ContractsApprove = "contracts.contracts.approve";
    public const string TemplatesEdit = "contracts.templates.edit";
    public const string TemplatesPublish = "contracts.templates.publish";
    public const string DocumentsGenerate = "contracts.documents.generate";
    public const string DocumentsDownload = "contracts.documents.download";
    public const string ApprovalsManage = "contracts.approvals.manage";
    public const string SignaturesManage = "contracts.signatures.manage";
    public const string SignaturesVerify = "contracts.signatures.verify";

    public const string ContractEntityKey = "contracts.contract";

    public const string ContractTypeList = "contracts.contract-type";
}

// ---- Dilim 2: durum geçişleri ve sürümler

/// <summary>Eylem isteği: kaydın sürümü zorunlu (eşzamanlı çakışmada 409); gerekçe bazı eylemlerde zorunludur.</summary>
public sealed record ContractTransitionRequest(long Version, string? Reason = null);

public sealed record ContractActionDto(string Action, bool Allowed, string? Code, string? Message, bool RequiresReason);

public sealed record ContractSnapshotLineDto(
    Guid StableKey, int Position, string Kind, string? ProductCode, string Name, string? Description, decimal Quantity, string? UnitName,
    decimal UnitPrice, decimal DiscountPercent, decimal TaxRatePercent, string? BillingPeriod, decimal Net, decimal Tax, decimal Gross);

public sealed record ContractSnapshotPartyDto(Guid Id, string Role, Guid? PartyId, Guid? PersonId, int Position, string? Notes);

/// <summary>Dondurulmuş içerik; finansal alanlar izni olmayan okuyucuya sıfır/boş gelir.</summary>
public sealed record ContractSnapshotDto(
    string Number, int VersionNo, string Title, string Type, string? Description, Guid PartyId, Guid? PersonId, DateOnly? StartDate, DateOnly? EndDate,
    bool IsIndefinite, string Currency, bool PriceIncludesTax, decimal? ContractValue, string? PaymentTerms, string RenewalMode, int? RenewalPeriodMonths,
    int? NoticePeriodDays, DateOnly? NoticeDeadline, string Confidentiality, decimal NetTotal, decimal TaxTotal, decimal GrossTotal, string? RecurringSummary,
    IReadOnlyList<ContractSnapshotPartyDto> Parties, IReadOnlyList<ContractSnapshotLineDto> Lines,
    bool HasNonStandardTerms = false, string RiskLevel = "Low");

public sealed record ContractVersionDto(Guid Id, int VersionNo, string Kind, string Status, string Sha256, string? Reason, Guid? CreatedBy, DateTimeOffset FrozenAt, ContractSnapshotDto? Snapshot);

public sealed record FieldChangeDto(string Field, string? Before, string? After);

public sealed record LineChangeDto(Guid StableKey, string Change, string Name, IReadOnlyList<FieldChangeDto> Fields);

public sealed record PartyChangeDto(Guid Id, string Change, string Role, IReadOnlyList<FieldChangeDto> Fields);

public sealed record ContractCompareDto(IReadOnlyList<FieldChangeDto> Header, IReadOnlyList<LineChangeDto> Lines, IReadOnlyList<PartyChangeDto> Parties);

// ---- Dilim 3: kabul edilmiş tekliften sözleşme

/// <param name="TermMonths">Süre (ay); verilirse başlangıç tarihi zorunlu, bitiş takvim ayıyla sunucuda hesaplanır.</param>
/// <param name="LineKeys">Sözleşmeye alınacak teklif kalemleri (kararlı anahtar); boşsa tümü (kısmi kapsam için alt küme).</param>
public sealed record CreateContractFromQuoteRequest(
    string? Type = null,
    DateOnly? StartDate = null,
    int? TermMonths = null,
    string? RenewalMode = null,
    int? RenewalPeriodMonths = null,
    int? NoticePeriodDays = null,
    IReadOnlyList<Guid>? LineKeys = null,
    Guid? OwnerId = null);

public sealed record QuoteSourceLineDto(
    Guid StableKey, int Position, string Name, decimal Quantity, string? Unit, decimal Net, decimal Gross, string? RecurringPeriod,
    bool Taken, Guid? ContractId, string? ContractNumber);

public sealed record QuoteSourceDto(
    Guid QuoteId, string QuoteNumber, int RevisionNo, string Title, string Currency, decimal NetTotal, decimal GrossTotal, DateTimeOffset AcceptedAt,
    string? AcceptedBy, string? DocumentSha256, bool CustomerLinked, IReadOnlyList<QuoteSourceLineDto> Lines);

// ---- Dilim 4: şablonlar ve belgeler

public sealed record ContractTemplateDto(
    Guid Id,
    long Version,
    string Name,
    string Language,
    string? ContractType,
    string? UsageScope,
    string? Description,
    string Body,
    string BodySha256,
    bool IsExample,
    string Status,
    int? PublishedVersionNo,
    Guid? CreatedBy,
    Guid? UpdatedBy);

public sealed record SaveContractTemplateRequest(
    string Name,
    string Body,
    string Language = "tr",
    string? ContractType = null,
    string? UsageScope = null,
    string? Description = null,
    bool IsExample = false,
    long? Version = null);

/// <summary>Şablon eylemi: kaydın sürümü zorunlu (409); gerekçe arşivlemede zorunlu, yayımlamada not olarak saklanır.</summary>
public sealed record TemplateActionRequest(long Version, string? Reason = null);

public sealed record ContractTemplateVersionDto(Guid Id, int VersionNo, string Name, string Sha256, bool IsExample, string? Notes, Guid? PublishedBy, DateTimeOffset PublishedAt, string? Body);

public sealed record TemplateVariableDto(string Key, string Label, string Group, bool Financial, bool IsBlock);

/// <summary>Örnek şablon metni; hukuki inceleme yapılmamıştır, kurum kendi onaylı metnini kullanmalıdır.</summary>
public sealed record TemplateSampleDto(string Name, string Body, string Notice);

public sealed record ContractDocumentDto(
    Guid Id, int ContractVersionNo, bool IsFinal, Guid TemplateId, int TemplateVersionNo, string TemplateName, bool IsExample, string FileName, int SizeBytes,
    string Sha256, Guid? CreatedBy, DateTimeOffset CreatedAt);

public sealed record GenerateContractDocumentRequest(Guid TemplateId);

/// <param name="UseDraft">Yayımlanmamış çalışma kopyasıyla önizle (şablon düzenleme izni ister).</param>
public sealed record PreviewContractDocumentRequest(Guid TemplateId, bool UseDraft = false);

public sealed record SignatureSlotDto(string Title, string Name);

/// <summary>Render edilmiş blok: <c>Kind</c> = Heading | Paragraph | Table | PartyList | Signature | PageBreak.</summary>
public sealed record RenderedBlockDto(
    string Kind, int Level, string? Text, IReadOnlyList<string>? Header, IReadOnlyList<IReadOnlyList<string>>? Rows, IReadOnlyList<string>? Items,
    IReadOnlyList<SignatureSlotDto>? Slots);

public sealed record ContractPreviewDto(
    string TemplateName, int TemplateVersionNo, bool IsExample, bool IsDraftTemplate, bool IsFinal, IReadOnlyList<RenderedBlockDto> Blocks, IReadOnlyList<string> Missing);
