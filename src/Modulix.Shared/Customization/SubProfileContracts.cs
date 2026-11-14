namespace Modulix.Shared.Customization;

/// <summary>Alt profil durumları (ADR-0041): <c>Draft</c> hazırlanıyor, <c>Active</c> etkin, <c>Inactive</c> pasif.</summary>
public static class SubProfileStatuses
{
    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string Inactive = "Inactive";

    public static string Label(string status) => status switch
    {
        Draft => "Taslak",
        Active => "Etkin",
        Inactive => "Pasif",
        _ => status,
    };
}

/// <summary>Seçenek türleri: <c>Field</c> kayda alan ekler, <c>ListValue</c> listeye değer ekler.</summary>
public static class SubProfileOptionKinds
{
    public const string Field = "Field";
    public const string ListValue = "ListValue";

    public static string Label(string kind) => kind switch
    {
        Field => "Alan",
        ListValue => "Liste değeri",
        _ => kind,
    };
}

/// <summary>
/// Alt profilin seçeneği. <see cref="TargetKey"/> kayıt türü ya da liste anahtarı, <see cref="TargetName"/> arayüz adıdır.
/// <see cref="AppliedAt"/> doluysa firmaya uygulanmıştır ve artık değiştirilemez.
/// </summary>
public sealed record SubProfileOptionDto(
    Guid Id,
    string Kind,
    string TargetKey,
    string TargetName,
    string Label,
    CustomFieldType? FieldType,
    bool Required,
    bool ShowInList,
    IReadOnlyList<string> Choices,
    string? Category,
    DateTimeOffset? AppliedAt);

/// <summary>Alt profil. Listede <see cref="Options"/> boştur (yalnızca sayılar); tek alt profil okunurken dolar.</summary>
public sealed record SubProfileDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    string? ActivityCode,
    string? ActivityName,
    string Status,
    long Revision,
    int OptionCount,
    int PendingCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    IReadOnlyList<SubProfileOptionDto> Options);

/// <summary>
/// Hazır alt profil (öneri): tek tıkla firmanın taslak alt profili olarak eklenir. <see cref="MissingModules"/> kapalı olduğu için
/// eklenemeyen modül adlarıdır; <see cref="Added"/> firmada bu hazır alt profil zaten varsa true.
/// </summary>
public sealed record SubProfileStarterDto(
    string Key,
    string Name,
    string Description,
    string? ActivityName,
    IReadOnlyList<string> Options,
    IReadOnlyList<string> MissingModules,
    bool Added);

/// <summary>Ekleme ya da düzenleme; <see cref="Revision"/> yalnızca düzenlemede gönderilir (açık ekranın eski olup olmadığını denetler).</summary>
public sealed record SaveSubProfileRequest(string Name, string? Description, string? ActivityCode, long Revision = 0);

public sealed record SetSubProfileStatusRequest(string Status, long Revision);

public sealed record SaveSubProfileOptionRequest(
    string Kind,
    string TargetKey,
    string Label,
    CustomFieldType? FieldType = null,
    bool Required = false,
    bool ShowInList = false,
    IReadOnlyList<string>? Choices = null,
    string? Category = null);
