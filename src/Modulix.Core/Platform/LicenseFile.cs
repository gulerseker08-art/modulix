using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Modulix.Core.Platform;

/// <summary>
/// On-prem lisans dosyasının içeriği (ADR-0028 Dilim 7b). Müşteri tarafında platform paneli yoktur; modül ve limit hakları bu imzalı
/// dosyayla gelir ve <see cref="ILicenseApplier"/> ile aynı plan/abonelik modeline yazılır. Dosya tek firmaya (<see cref="TenantSlug"/>)
/// ve bir bitişe (<see cref="ValidUntil"/>) bağlıdır; bitiş sonrası ek süre ve salt okunur kuralları Dilim 3'teki gibi işler.
/// </summary>
public sealed record LicensePayload(
    string LicenseId,
    string TenantSlug,
    string Customer,
    IReadOnlyList<string> Modules,
    IReadOnlyDictionary<string, int>? Limits,
    DateTimeOffset IssuedAt,
    DateTimeOffset ValidUntil);

/// <summary>Lisans dosyası okunamadı, imzası geçersiz ya da içeriği uygulanamaz.</summary>
public sealed class LicenseException(string message) : Exception(message);

/// <summary>
/// İmzalı lisans dosyası biçimi: <c>{ "version": 1, "payload": "&lt;base64 UTF-8 JSON&gt;", "signature": "&lt;base64&gt;" }</c>.
/// İmza, JSON'un kanonikleştirilmesine bağlı kalmamak için <b>base64'e giren baytlar</b> üzerindedir (ECDSA P-256, SHA-256,
/// IEEE P1363). Doğrulama için yalnızca genel anahtar (SubjectPublicKeyInfo, base64) gerekir; özel anahtar müşteri ortamına girmez.
/// </summary>
public static class LicenseFile
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Firmanın lisans planının anahtarı: <c>lisans-{kısa ad}</c>. Plan anahtarı 40 karakteri aşamaz; uzun kısa ad kırpılır ve
    /// ayırt edilebilmesi için kısa adın özeti eklenir.
    /// </summary>
    public static string PlanKeyFor(string tenantSlug)
    {
        var slug = (tenantSlug ?? string.Empty).Trim().ToLowerInvariant();
        var key = $"lisans-{slug}";
        if (key.Length <= Plan.KeyMaxLength)
        {
            return key;
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(slug)))[..8];
        return $"lisans-{slug[..(Plan.KeyMaxLength - "lisans-".Length - 1 - digest.Length)].TrimEnd('-')}-{digest}";
    }

    /// <summary>Yeni imza anahtar çifti: özel anahtar PEM, genel anahtar base64 (SubjectPublicKeyInfo).</summary>
    public static (string PrivateKeyPem, string PublicKey) GenerateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>İçeriği özel anahtarla imzalar ve dosya metnini üretir. Yalnızca lisansı düzenleyen taraf çağırır.</summary>
    public static string Sign(LicensePayload payload, string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidatePayload(payload);
        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(privateKeyPem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new LicenseException("Özel anahtar okunamadı (PEM beklenir).");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Compact);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.Serialize(new Envelope(CurrentVersion, Convert.ToBase64String(bytes), Convert.ToBase64String(signature)), Indented);
    }

    /// <summary>
    /// İmzayı genel anahtarla doğrular ve içeriği döndürür. Biçim, sürüm, imza ve içerik kuralı bozuksa <see cref="LicenseException"/>.
    /// Süre kontrolü uygulayıcıdadır (burada yalnızca özgünlük ve biçim).
    /// </summary>
    public static LicensePayload Verify(string fileText, string publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            throw new LicenseException("Lisans genel anahtarı ayarlı değil (License:PublicKey).");
        }

        Envelope? envelope;
        byte[] bytes;
        byte[] signature;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(fileText ?? string.Empty, Compact);
            bytes = Convert.FromBase64String(envelope?.Payload ?? string.Empty);
            signature = Convert.FromBase64String(envelope?.Signature ?? string.Empty);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new LicenseException("Lisans dosyası okunamadı.");
        }

        if (envelope is null || bytes.Length == 0 || signature.Length == 0)
        {
            throw new LicenseException("Lisans dosyası eksik.");
        }

        if (envelope.Version != CurrentVersion)
        {
            throw new LicenseException($"Desteklenmeyen lisans sürümü: {envelope.Version}.");
        }

        using var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new LicenseException("Lisans genel anahtarı geçersiz.");
        }

        bool valid;
        try
        {
            valid = key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            valid = false;
        }

        if (!valid)
        {
            throw new LicenseException("Lisans imzası geçersiz.");
        }

        LicensePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LicensePayload>(bytes, Compact);
        }
        catch (JsonException)
        {
            throw new LicenseException("Lisans içeriği okunamadı.");
        }

        ValidatePayload(payload ?? throw new LicenseException("Lisans içeriği boş."));
        return payload;
    }

    private static void ValidatePayload(LicensePayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.LicenseId) || string.IsNullOrWhiteSpace(payload.TenantSlug) || string.IsNullOrWhiteSpace(payload.Customer))
        {
            throw new LicenseException("Lisans numarası, firma kısa adı ve müşteri adı zorunludur.");
        }

        if (payload.Modules is null || payload.Modules.Count == 0)
        {
            throw new LicenseException("Lisans en az bir modül içermeli.");
        }

        if (payload.ValidUntil <= payload.IssuedAt)
        {
            throw new LicenseException("Lisans bitişi düzenlenme tarihinden sonra olmalı.");
        }
    }

    private sealed record Envelope(int Version, string Payload, string Signature);
}

/// <summary>Uygulanan lisansın özeti.</summary>
public sealed record LicenseApplyResult(string LicenseId, string TenantSlug, string PlanKey, DateTimeOffset ValidUntil, IReadOnlyList<string> Modules);

/// <summary>
/// İmzalı lisansı firmaya uygular (ADR-0028 Dilim 7b): imza doğrulanır, firmanın lisans planı (<c>lisans-{kısa ad}</c>) lisanstaki
/// modül ve limitlerle oluşturulur ya da güncellenir ve firmaya <see cref="IPlanAdministration.AssignPlanAsync"/> ile atanır. Yeni
/// lisans eskisinin yerine geçer; <b>hiçbir kayıt silinmez</b> (eski abonelik kapanır, planda olmayan modüller kapatılır, veri kalır).
/// Hatalar <see cref="LicenseException"/>.
/// </summary>
public interface ILicenseApplier
{
    Task<LicenseApplyResult> ApplyAsync(string licenseText, string publicKey, CancellationToken cancellationToken);
}
