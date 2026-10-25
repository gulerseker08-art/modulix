using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.SharedRecords;

namespace Modulix.Core.Contracts.Modules;

/// <summary>
/// Bir modülün platforma kendini tanıttığı sözleşme. Her modül projesinde tek bir uygulaması
/// bulunur ve API host'u açılışta kaydeder (<c>AddModulixModules</c>).
/// <list type="bullet">
///   <item><see cref="Key"/> değişmez; tenant entitlement'ları ve izin anahtarları buna bağlıdır.</item>
///   <item>Her izin anahtarı modül anahtarı ve nokta ile başlar (ör. "crm.companies.read").</item>
///   <item>Bir tenant için kapalı olan modülün izinleri, rolde tanımlı olsa bile geçersizdir.</item>
/// </list>
/// </summary>
public interface IModule
{
    /// <summary>Kısa, küçük harfli anahtar (ör. "crm"). Değişmez.</summary>
    string Key { get; }

    /// <summary>Arayüzde gösterilen ad (ör. "CRM").</summary>
    string DisplayName { get; }

    /// <summary>Modülün tanımladığı izinler. Rollere yalnızca bu listedeki izinler atanabilir.</summary>
    IReadOnlyList<PermissionDefinition> Permissions { get; }

    /// <summary>
    /// Kişiye atanan (sorumlusu olan) kayıt türleri. Kullanıcı devre dışı bırakılırken bu kayıtlar başka bir üyeye
    /// devredilebilir (ADR-0004). Devri çekirdek, kayıt boru hattıyla yapar: sorumlu alanı
    /// <see cref="OwnedRecordType.OwnerField"/> adında olmalı, kayıt deposu aynı adla filtre sunmalıdır.
    /// </summary>
    IReadOnlyList<OwnedRecordType> OwnedRecords => [];

    /// <summary>
    /// Modülün önerdiği hazır roller (ADR-0005): rol ekranında "Şablondan başla" olarak sunulur. Şablon yalnızca kendi
    /// modülünün izinlerini içerir; modül tenant için kapalıysa şablon görünmez.
    /// </summary>
    IReadOnlyList<RoleTemplate> RoleTemplates => [];

    /// <summary>
    /// Denetim kaydı ekranında (Y5) gösterilen kayıt türleri: türün ve alanların görünen adları. Listede olmayan tür
    /// teknik adıyla gösterilir. Anahtar modül anahtarıyla başlar.
    /// </summary>
    IReadOnlyList<AuditedEntity> AuditedEntities => [];

    /// <summary>
    /// Modülün firma ayarları (Y6): tipi, varsayılanı ve doğrulaması kodda tanımlıdır; tanımsız ayar yazılamaz.
    /// Anahtar modül anahtarıyla başlar. Modül kapalıyken ayarları gösterilmez ve yazılamaz, kayıtlı değerler silinmez.
    /// </summary>
    IReadOnlyList<SettingDefinition> Settings => [];

    /// <summary>
    /// Hassas alanlar (Y8b): kayıt türünü görebilen ama alanın iznine sahip olmayan kişiye alan gönderilmez, o alanı
    /// yazamaz ve ona göre süzemez; denetim kaydında değeri gizlenir. İzin modülün kendi iznidir.
    /// </summary>
    IReadOnlyList<SensitiveFields> SensitiveFields => [];

    /// <summary>
    /// Firmanın özel alan ekleyebildiği kayıt türleri (Y9a). Türün kayıt deposu özel alanları saklamalıdır
    /// (<see cref="Records.IRecordStore.SupportsCustomFields"/>). Anahtar modül anahtarıyla başlar.
    /// </summary>
    IReadOnlyList<CustomizableEntity> CustomizableEntities => [];

    /// <summary>
    /// Firmanın düzenleyebildiği seçim listeleri (Y9b): bir kayıt alanının değerleri (ör. fırsat aşaması). Hazır değerler
    /// kodda tanımlıdır; firma adlarını, sırasını ve etkinliğini değiştirir, yeni değer ekler. Kod değere değil kategoriye
    /// bakar (ör. "kazanıldı"). Anahtar modül anahtarıyla başlar.
    /// </summary>
    IReadOnlyList<ChoiceListDefinition> ChoiceLists => [];

    /// <summary>
    /// Modülün kullandığı ortak kayıt türleri ve onları okuyup yazmaya yeten izinleri (ADR-0006, ör. CRM: Taraf okuma
    /// "crm.companies.read"). Bildirmeyen modülün izinleri ortak kayda erişim vermez.
    /// </summary>
    IReadOnlyList<SharedRecordAccess> SharedRecordAccess => [];

    /// <summary>Modülün ortak kayda başvuran alanları (ADR-0006); çekirdek ortak kaydın kullanımını bunlarla sayar.</summary>
    IReadOnlyList<SharedRecordReference> SharedRecordReferences => [];

    /// <summary>Modülün kendi servislerini kaydeder. Modül başka modülün servisine erişmez.</summary>
    void ConfigureServices(IServiceCollection services)
    {
    }
}

/// <summary>
/// Bir izin. <see cref="Key"/> biçimi: "modül.kaynak.işlem" (ör. "crm.companies.read").
/// <see cref="ResourceName"/>: rol ekranındaki izin matrisinin satır adı (ör. "Firmalar"); aynı kaynağın okuma ve yazma
/// izinleri aynı satırda gösterilir. Verilmezse anahtardaki kaynak adı kullanılır.
/// </summary>
public sealed record PermissionDefinition(string Key, string DisplayName, string? ResourceName = null);

/// <summary>
/// Kişiye atanan kayıt türü (ADR-0004). <see cref="Label"/> sayıyla birlikte gösterilir (ör. "12 firma").
/// <see cref="Filters"/> devredilecek kayıtları daraltır (ör. "open" = "true": yalnızca açık fırsatlar; kapanmış
/// kayıtlar geçmiş olarak eski sorumlusunda kalır).
/// </summary>
public sealed record OwnedRecordType(string EntityKey, string Label, IReadOnlyDictionary<string, string>? Filters = null)
{
    /// <summary>Sorumlu alanının ve filtresinin adı; değeri kullanıcı kimliğidir.</summary>
    public const string OwnerField = "ownerId";
}

/// <summary>Bir kayıt türünün, <see cref="Permission"/> olmadan görülemeyen alanları (ör. fırsat tutarı, maaş).</summary>
public sealed record SensitiveFields(string EntityKey, string Permission, IReadOnlyList<string> Fields);

/// <summary>Özel alan eklenebilen kayıt türü ve tanım ekranındaki adı (ör. "Fırsat").</summary>
public sealed record CustomizableEntity(string EntityKey, string DisplayName);

/// <summary>
/// Düzenlenebilir seçim listesi (Y9b). <see cref="Field"/>, <see cref="EntityKey"/> türünün kayıt alanıdır; değeri listedeki
/// bir anahtardır. <see cref="Required"/> listede değer boş bırakılamaz, verilmezse ilk etkin değer atanır.
/// <see cref="Categories"/> boş değilse her değer bir kategoriye bağlıdır ve her kategoride en az bir etkin değer kalır.
/// </summary>
public sealed record ChoiceListDefinition(
    string Key,
    string DisplayName,
    string EntityKey,
    string Field,
    bool Required,
    IReadOnlyList<ChoiceCategory> Categories,
    IReadOnlyList<ChoiceValueDefinition> Values)
{
    /// <summary>Yeni kayıtta değer verilmezse atanan hazır değer; verilmezse (veya pasifse) ilk etkin değer.</summary>
    public string? Default { get; init; }
}

/// <summary>Kategori: kodun anladığı anlam (ör. "won" → "Kazanıldı").</summary>
public sealed record ChoiceCategory(string Key, string DisplayName);

/// <summary>Hazır değer. Anahtarı değişmez (kayıtlarda ve API'de bu yazılır).</summary>
public sealed record ChoiceValueDefinition(string Key, string Label, string? Category = null);

/// <summary>
/// Hazır rol önerisi (ADR-0005). <see cref="Key"/> modül anahtarıyla başlar (ör. "crm.sales-rep") ve değişmez;
/// <see cref="Permissions"/> modülün kendi izinleridir. Şablondan oluşturulan rol sıradan bir roldür; şablona bağlı kalmaz.
/// </summary>
public sealed record RoleTemplate(string Key, string Name, string Description, IReadOnlyList<string> Permissions)
{
    /// <summary>Önerilen kayıt kapsamı (Y8); rol oluşturulurken kullanılır.</summary>
    public Authorization.RecordScope Scope { get; init; } = Authorization.RecordScope.All;
}

/// <summary>
/// Denetim kaydında bir kayıt türü (Y5). <see cref="Fields"/>: alanın teknik adı → görünen adı (ör. "status" → "Durum").
/// <see cref="ReadPermission"/>: bu izni olmayan denetim okuru satırı görür ama alan değerleri gizlidir (yönetici her
/// şeyi görür); boşsa değerler her denetim okuruna açıktır. <see cref="TitleField"/>: kaydın adını taşıyan alan
/// (ad birden fazla alandan oluşuyorsa <see cref="TitleFields"/>, ör. ad ve soyad).
/// <see cref="Options"/>: seçenekli alanlarda değer → görünen ad. <see cref="UserFields"/>: değeri kullanıcı kimliği
/// olan alanlar (ad gösterilir). <see cref="References"/>: değeri başka bir kaydın kimliği olan alanlar → o kaydın türü.
/// </summary>
public sealed record AuditedEntity(
    string EntityKey,
    string DisplayName,
    string? ReadPermission,
    IReadOnlyDictionary<string, string> Fields,
    string? TitleField = null)
{
    public IReadOnlyList<string> TitleFields { get; init; } = TitleField is null ? [] : [TitleField];

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Options { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    public IReadOnlyCollection<string> UserFields { get; init; } = [];

    public IReadOnlyDictionary<string, string> References { get; init; } = new Dictionary<string, string>();
}

public enum SettingType
{
    Text = 1,

    /// <summary>Çok satırlı metin (ör. adres).</summary>
    MultilineText = 2,

    Email = 3,
    Phone = 4,

    /// <summary>Değer <see cref="SettingDefinition.Options"/>'tan biri olmalıdır.</summary>
    Choice = 5,
}

/// <summary>Seçenekli ayarda bir seçenek: kaydedilen değer ve görünen adı.</summary>
public sealed record SettingOption(string Value, string Label);

/// <summary>
/// Bir firma ayarı (Y6). <see cref="Key"/> biçimi: "modül.grup.ad" (ör. "core.regional.currency"); değişmez, kayıtlı
/// değerler buna bağlıdır. <see cref="Group"/>: ayarlar ekranındaki bölümün adı (ör. "Bölgesel ayarlar").
/// Kaydedilmemiş ayarın değeri <see cref="DefaultValue"/>'dur; boş değer "ayarlanmamış" demektir.
/// <see cref="Pattern"/>: değer boş değilse uyması gereken düzenli ifade, hata metni <see cref="PatternMessage"/>.
/// </summary>
public sealed record SettingDefinition(string Key, string DisplayName, string Group, SettingType Type, string DefaultValue = "")
{
    public const int DefaultMaxLength = 200;

    public string? Description { get; init; }

    public int MaxLength { get; init; } = DefaultMaxLength;

    public bool Required { get; init; }

    public IReadOnlyList<SettingOption> Options { get; init; } = [];

    public string? Pattern { get; init; }

    public string? PatternMessage { get; init; }
}
