namespace Modulix.Shared.Crm;

// Müşteri lokasyonları, adresler, iletişim noktaları ve firma–firma ilişkileri (ADR-0012). Alan adları kayıt boru
// hattındaki teknik adlarla aynıdır. Anahtar listeleri sunucudaki CrmModule ile aynıdır (entegrasyon testi denetler).

/// <summary>Müşteri lokasyonu türleri.</summary>
public static class CrmLocationTypes
{
    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>
    {
        ["HeadOffice"] = "Merkez",
        ["Branch"] = "Şube",
        ["Plant"] = "Fabrika / tesis",
        ["Warehouse"] = "Depo",
        ["Store"] = "Mağaza",
        ["Delivery"] = "Teslimat noktası",
        ["Billing"] = "Fatura noktası",
        ["Other"] = "Diğer",
    };
}

public static class CrmAddressTypes
{
    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>
    {
        ["Registered"] = "Tescilli adres",
        ["Billing"] = "Fatura adresi",
        ["Shipping"] = "Sevkiyat adresi",
        ["Office"] = "Ofis",
        ["Other"] = "Diğer",
    };
}

public static class CrmContactPointTypes
{
    public const string Phone = "Phone";
    public const string Email = "Email";

    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>
    {
        ["Work"] = "İş",
        ["Mobile"] = "Cep",
        ["Switchboard"] = "Santral",
        ["Billing"] = "Fatura",
        ["Support"] = "Destek",
        ["Personal"] = "Kişisel",
        ["Other"] = "Diğer",
    };
}

public static class CrmCompanyRelationTypes
{
    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>
    {
        ["Subsidiary"] = "İştirak (bağlı şirket)",
        ["Affiliate"] = "Grup şirketi",
        ["Other"] = "Diğer",
    };
}

/// <summary>
/// Müşterinin aynı tüzel kişilik altındaki lokasyonu (şube, depo, teslimat noktası...). Ayrı bir firma kaydı açmaz;
/// ayrı tüzel kişilik ayrı firmadır ve <see cref="CompanyRelationDto"/> ile bağlanır. Firma yalnızca oluştururken verilir.
/// </summary>
public sealed record LocationDto(
    Guid Id,
    long Version,
    Guid CompanyId,
    string? CompanyName,
    string Name,
    string? Code,
    string Type,
    bool IsActive,
    Guid? ContactId,
    string? Notes);

public sealed record SaveLocationRequest(
    Guid CompanyId,
    string Name,
    string? Code = null,
    string Type = "Other",
    bool IsActive = true,
    Guid? ContactId = null,
    string? Notes = null,
    long? Version = null);

/// <summary>
/// Yapılandırılmış adres; orijinal alanlar korunur, <see cref="NormalizedText"/> arama içindir (salt okunur). Firma düzeyindeki
/// ana adres (LocationId boş) firmadaki eski <c>address</c>/<c>city</c> alanlarının kaynağıdır.
/// </summary>
public sealed record AddressDto(
    Guid Id,
    long Version,
    Guid CompanyId,
    Guid? LocationId,
    string Type,
    bool IsPrimary,
    bool IsActive,
    string Country,
    string? City,
    string? District,
    string? PostalCode,
    string Line1,
    string? Line2,
    string? NormalizedText);

public sealed record SaveAddressRequest(
    Guid CompanyId,
    string Line1,
    Guid? LocationId = null,
    string Type = "Other",
    bool IsPrimary = false,
    bool IsActive = true,
    string Country = "TR",
    string? City = null,
    string? District = null,
    string? PostalCode = null,
    string? Line2 = null,
    long? Version = null);

/// <summary>
/// Firma veya lokasyon sahipli telefon/e-posta. <see cref="Value"/> girildiği gibi korunur, <see cref="NormalizedValue"/>
/// arama içindir. Paylaşılan santral/ortak posta kutusu (<see cref="IsShared"/>) kimlik kanıtı sayılmaz. İletişim noktası
/// pazarlama izni değildir.
/// </summary>
public sealed record ContactPointDto(
    Guid Id,
    long Version,
    Guid CompanyId,
    Guid? LocationId,
    string Channel,
    string Type,
    string Value,
    string? NormalizedValue,
    bool IsPrimary,
    bool IsActive,
    bool IsShared,
    string? Note);

public sealed record SaveContactPointRequest(
    Guid CompanyId,
    string Value,
    string Channel = "Phone",
    Guid? LocationId = null,
    string Type = "Work",
    bool IsPrimary = false,
    bool IsActive = true,
    bool IsShared = false,
    string? Note = null,
    long? Version = null);

/// <summary>Kişi sahipli telefon/e-posta; <see cref="CompanyContactId"/> doluysa yalnızca o firma–kişi ilişkisi için geçerlidir.</summary>
public sealed record PersonContactPointDto(
    Guid Id,
    long Version,
    Guid ContactId,
    Guid? CompanyContactId,
    string Channel,
    string Type,
    string Value,
    string? NormalizedValue,
    bool IsPrimary,
    bool IsActive,
    bool IsShared,
    string? Note);

public sealed record SavePersonContactPointRequest(
    Guid ContactId,
    string Value,
    string Channel = "Phone",
    Guid? CompanyContactId = null,
    string Type = "Work",
    bool IsPrimary = false,
    bool IsActive = true,
    bool IsShared = false,
    string? Note = null,
    long? Version = null);

/// <summary>
/// Firma–firma ilişkisi: ana firma → bağlı firma. İki ucu da çağıranın kapsamında olmalıdır. Kendine bağ ve döngü 422 alır;
/// bitiş tarihi verilen ilişki pasifleşir.
/// </summary>
public sealed record CompanyRelationDto(
    Guid Id,
    long Version,
    Guid ParentCompanyId,
    Guid ChildCompanyId,
    string? ParentCompanyName,
    string? ChildCompanyName,
    string Type,
    DateOnly? StartDate,
    DateOnly? EndDate,
    bool IsActive,
    string? Notes);

public sealed record SaveCompanyRelationRequest(
    Guid ParentCompanyId,
    Guid ChildCompanyId,
    string Type = "Subsidiary",
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    string? Notes = null,
    long? Version = null);

public static class CrmContactPointLimits
{
    public const int ValueMaxLength = 254;
    public const int NoteMaxLength = 200;
}

public static class CrmDirectoryLimits
{
    public const int LocationNameMaxLength = 150;
    public const int LocationCodeMaxLength = 50;
    public const int AddressLineMaxLength = 250;
    public const int AddressPartMaxLength = 100;
    public const int PostalCodeMaxLength = 20;
    public const int RelationNotesMaxLength = 500;
}
