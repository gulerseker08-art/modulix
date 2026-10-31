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
    Guid? UpdatedBy);

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
    long? Version = null);

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
    IReadOnlyList<ContractSnapshotPartyDto> Parties, IReadOnlyList<ContractSnapshotLineDto> Lines);

public sealed record ContractVersionDto(Guid Id, int VersionNo, string Kind, string Status, string Sha256, string? Reason, Guid? CreatedBy, DateTimeOffset FrozenAt, ContractSnapshotDto? Snapshot);

public sealed record FieldChangeDto(string Field, string? Before, string? After);

public sealed record LineChangeDto(Guid StableKey, string Change, string Name, IReadOnlyList<FieldChangeDto> Fields);

public sealed record PartyChangeDto(Guid Id, string Change, string Role, IReadOnlyList<FieldChangeDto> Fields);

public sealed record ContractCompareDto(IReadOnlyList<FieldChangeDto> Header, IReadOnlyList<LineChangeDto> Lines, IReadOnlyList<PartyChangeDto> Parties);
