namespace Modulix.Core.Contracts.Credentials;

// Süreli belge (ADR-0038 K3): araç muayenesi, sigorta, ehliyet, SRC gibi geçerlilik süresi olan belgeler Core'da tek tabloda tutulur. Belgenin
// sahibi bir modül kaydıdır (ör. Filo aracı, Lojistik sürücüsü); hangi kayıt türünün belge taşıyabileceğini sahip modül bildirir
// (IModule.CredentialOwners). Belgeye erişim sahip kaydın erişimidir: sahip kaydı okuyabilen belgesini görür, güncelleyebilen belge ekler.

/// <summary>Süreli belge kayıt türü ve seçim listesi anahtarları.</summary>
public static class CredentialEntities
{
    /// <summary>Kayıt boru hattındaki tür anahtarı (<c>/api/records/core.credential</c>).</summary>
    public const string Credential = "core.credential";

    /// <summary>Belge türü listesi (Y9b): tenant tür ekler; kod türün adına değil kategorisine bakar.</summary>
    public const string TypeList = "core.credential-type";
}

/// <summary>Belge türü kategorileri: sahip kayıt türünün bildirdiği kategori, o kayda eklenebilecek belge türlerini belirler.</summary>
public static class CredentialCategories
{
    public const string Vehicle = "vehicle";
    public const string Driver = "driver";
    public const string Party = "party";
    public const string Facility = "facility";
    public const string Employee = "employee";

    public static IReadOnlyList<string> All { get; } = [Vehicle, Driver, Party, Facility, Employee];
}

/// <summary>Belge durumu: iptal edilen belge geçerlilik tarihinden bağımsız olarak geçersizdir. Süresi dolmuşluk tarihten hesaplanır, saklanmaz.</summary>
public static class CredentialStatuses
{
    public const string Active = "active";
    public const string Revoked = "revoked";

    public static IReadOnlyList<string> All { get; } = [Active, Revoked];
}

/// <summary>
/// Süreli belge taşıyabilen kayıt türü. <see cref="EntityKey"/> bildiren modülün kayıt türüdür ve kayıt deposu olmalıdır;
/// <see cref="Category"/> <see cref="CredentialCategories"/> değerlerinden biridir. <see cref="DisplayName"/>: sahip türün adı (ör. "Araç").
/// <see cref="ReadPermission"/>: sahip türü okuma izni (modülün izni); kural düzenleyicide "Süreli belge" kayıt türü bu izinlerden biri olana görünür.
/// </summary>
public sealed record CredentialOwner(string EntityKey, string Category, string DisplayName, string ReadPermission);

/// <summary>
/// Süreli belgelerin modüller arası okuma sözleşmesi (ADR-0038 K3: "sahip modül belgenin zorunluluğunu kendi kuralıyla söyler; Core saklar ve
/// sorgular"). Ör. Lojistik sefer uygunluğunda aracın ve sürücünün belgelerine bakar. Tenant filtresi ve RLS uygulanır; kayıt izni sormaz (çağıran
/// modül kendi kaydının iznini denetler). İptal edilen belgeler de döner; durumu <see cref="CredentialSummary.Status"/>'tadır.
/// </summary>
public interface ICredentialDirectory
{
    /// <summary>Verilen sahip kayıtların belgeleri (sahip türü + kimlikler). En çok 500 kimlik.</summary>
    Task<IReadOnlyList<CredentialSummary>> ForOwnersAsync(string ownerEntityKey, IReadOnlyCollection<Guid> ownerIds, CancellationToken cancellationToken);
}

/// <summary>Belgenin modüller arası özeti; <see cref="TypeLabel"/> belge türü listesindeki görünen addır (liste dışıysa anahtar).</summary>
public sealed record CredentialSummary(
    Guid Id, string OwnerEntityKey, Guid OwnerId, string TypeKey, string TypeLabel, string? DocumentNo, DateOnly? ValidFrom, DateOnly? ValidTo, string Status);
