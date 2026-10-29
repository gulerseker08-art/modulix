namespace Modulix.Core.Contracts.Modules;

/// <summary>Lookup'ın hedefi: bir kayıt türü (kayıt boru hattı üzerinden) ya da kuruluşun üyeleri (kullanıcı seçimi).</summary>
public enum LookupTargetKind
{
    Record = 1,
    Member = 2,
}

/// <summary>
/// Kullanıcının seçebildiği bir N→1 referans alanı (ADR-0016). Teknik tanım kodda sabittir; yönetici yalnızca
/// <see cref="DefaultSearchable"/> değerini firma için değiştirebilir. Hedef kayıtlara erişim (izin, veri kapsamı,
/// hassas alanlar) kayıt boru hattının kurallarıyla belirlenir; "aranabilir" bir erişim yetkisi değildir.
/// </summary>
/// <param name="Key">Sabit kimlik: "modül.kaynak.alan" (ör. "crm.contact.company"). Değişmez; firma ayarı buna bağlıdır.</param>
/// <param name="SourceEntityKey">Alanı taşıyan kayıt türü (ör. "crm.contact").</param>
/// <param name="Field">Kaynak kayıttaki alan adı (ör. "companyId").</param>
/// <param name="Title">Alanın görünen başlığı (ör. "Firma").</param>
/// <param name="Kind">Hedef türü.</param>
/// <param name="TargetEntityKey">Hedef kayıt türü; üye lookup'larında boş.</param>
public sealed record LookupDefinition(
    string Key,
    string SourceEntityKey,
    string Field,
    string Title,
    LookupTargetKind Kind,
    string? TargetEntityKey = null)
{
    /// <summary>Sonuç etiketini oluşturan hedef alanlar (boşlukla birleşir, ör. ad + soyad).</summary>
    public IReadOnlyList<string> LabelFields { get; init; } = [];

    /// <summary>Aynı etiketli kayıtları ayırt eden ikincil alanlar (" · " ile birleşir, ör. şehir). Yalnızca kullanıcının görebildiği alanlar gelir.</summary>
    public IReadOnlyList<string> SecondaryFields { get; init; } = [];

    /// <summary>Metin aramasının hangi hedef alanlarda çalıştığının kısa açıklaması (yönetici ekranı). Alanlar kayıt deposunda sabittir.</summary>
    public string SearchDescription { get; init; } = string.Empty;

    /// <summary>Yönetici dokunmadıysa metin araması açık mı? Kapalıysa seçim yalnızca sayfalı listeden yapılır.</summary>
    public bool DefaultSearchable { get; init; } = true;

    /// <summary>Metin aramasında en az karakter.</summary>
    public int MinSearchLength { get; init; } = 2;

    /// <summary>Bir sayfadaki varsayılan sonuç sayısı; en çok <see cref="RecordLookupLimits.MaxPageSize"/>.</summary>
    public int PageSize { get; init; } = 20;

    /// <summary>
    /// Bağımlı lookup: üst alanın adı (kaynak kayıtta, ör. "companyId") ve hedefte süzmek için kullanılan filtre adı. Üst değer
    /// verilmişse sonuçlar sunucuda süzülür ve ilişki kayıt sırasında da doğrulanır; verilmemişse süzülmez (kullanıcının
    /// görebildiği tüm kayıtlar, sayfalı).
    /// </summary>
    public string? DependsOnField { get; init; }

    public string? DependsOnFilter { get; init; }

    /// <summary>Üst değer yokken alt alanın davranışı (arayüz metni).</summary>
    public string? DependsOnHint { get; init; }

    /// <summary>Seçili kaydın ayrıntı sayfası ("crm/firmalar/{id}"); bağlantı yalnızca hedefi okuyabilen kullanıcıya gösterilir.</summary>
    public string? ViewPath { get; init; }

    public bool IsMember => Kind == LookupTargetKind.Member;
}

/// <summary>Lookup sınırları; sınırsız sonuç döndürülmez.</summary>
public static class RecordLookupLimits
{
    public const int MaxPageSize = 50;

    public const int MaxSearchLength = 100;

    public const int MaxResolveIds = 50;
}
