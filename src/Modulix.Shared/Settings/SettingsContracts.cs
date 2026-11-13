namespace Modulix.Shared.Settings;

// Kuruluş ve modül ayarları (Y6) sözleşmeleri: /api/settings. Okuma her üyeye açıktır; yazma core.settings.manage ister.

public enum SettingKind
{
    Text = 1,
    MultilineText = 2,
    Email = 3,
    Phone = 4,
    Choice = 5,
}

public sealed record SettingOptionDto(string Value, string Label);

/// <summary>
/// Bir ayar ve aktif firmadaki değeri. <see cref="Version"/>: kaydedilmemiş ayar için 0; kaydederken geri gönderilir,
/// arada başkası kaydetmişse 409.
/// </summary>
public sealed record SettingDto(
    string Key,
    string ModuleKey,
    string Group,
    string Label,
    string? Description,
    SettingKind Kind,
    int MaxLength,
    bool Required,
    IReadOnlyList<SettingOptionDto> Options,
    string DefaultValue,
    string Value,
    long Version);

/// <summary><see cref="Logo"/>: firmanın logosu varsa bilgisi; görüntü <c>/api/settings/logo?v={Version}</c> adresindedir.</summary>
public sealed record SettingsResponse(IReadOnlyList<SettingDto> Settings, TenantLogoDto? Logo = null);

public sealed record TenantLogoDto(string ContentType, int Size, long Version)
{
    public const int MaxBytes = 256 * 1024;

    public string Url => $"api/settings/logo?v={Version}";
}

public sealed record SettingWriteDto(string Value, long Version);

/// <summary>Ayar anahtarı → yeni değer ve okunduğu sürüm. Hepsi birlikte kaydedilir ya da hiçbiri.</summary>
public sealed record SaveSettingsRequest(IReadOnlyDictionary<string, SettingWriteDto> Values);

/// <summary>Çekirdeğin ayar anahtarları; arayüz bunlarla okur.</summary>
public static class SettingKeys
{
    public const string DisplayName = "core.profile.display-name";
    public const string LegalName = "core.profile.legal-name";
    public const string TaxOffice = "core.profile.tax-office";
    public const string TaxNumber = "core.profile.tax-number";
    public const string Address = "core.profile.address";
    public const string Phone = "core.profile.phone";
    public const string Email = "core.profile.email";
    public const string TimeZone = "core.regional.time-zone";
    public const string Currency = "core.regional.currency";
    public const string Format = "core.regional.format";
    public const string Language = "core.regional.language";
    public const string RequireTwoFactor = "core.security.require-two-factor";

    /// <summary>Otomasyon acil durdurma anahtarı (ADR-0027): "false" ise firmanın hiçbir kuralı çalışmaz.</summary>
    public const string AutomationEnabled = "core.automation.enabled";

    /// <summary>Yapay zekâ erişimi: "Hayır" ise firmada yapay zekâ özellikleri kapalıdır (varsayılan kapalı; açık rıza).</summary>
    public const string AiEnabled = "core.ai.enabled";
}
