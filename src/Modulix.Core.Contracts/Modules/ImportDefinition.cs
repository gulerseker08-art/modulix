namespace Modulix.Core.Contracts.Modules;

/// <summary>İçe aktarma sütununun değer türü; hücre metni bu türe göre yorumlanır (ADR-0033).</summary>
public enum ImportFieldType
{
    Text = 1,

    /// <summary>Türkçe biçimli sayı: 1.234,56 ya da 1234,56. Anlamı belirsiz biçim satır hatasıdır.</summary>
    Decimal = 2,

    /// <summary>gg.aa.yyyy ya da yyyy-aa-gg.</summary>
    Date = 3,

    /// <summary>Değer seçeneğin anahtarı ya da görünen adıdır; dosyada Türkçe ad yazılabilir.</summary>
    Choice = 4,

    /// <summary>Evet/Hayır.</summary>
    YesNo = 5,

    /// <summary>Birden çok seçenek; değerler "|" ile ayrılır.</summary>
    MultiChoice = 6,

    /// <summary>Başka bir kayda bağlantı: <see cref="ImportField.Reference"/> ile çözülür, kullanıcının göremediği kayıt bulunamaz.</summary>
    Reference = 7,

    /// <summary>Firmanın etkin üyesi; hücrede kullanıcının e-postası yazılır.</summary>
    User = 8,
}

/// <summary>
/// İçe aktarma sütunu. <see cref="Key"/> kayıt alanının teknik adıdır; dosyadaki başlık <see cref="Label"/> ya da anahtarla eşleşir.
/// Seçenekler sabitse <see cref="Options"/>, firmanın düzenlediği listeyse <see cref="ChoiceListKey"/> verilir.
/// </summary>
public sealed record ImportField(string Key, string Label, ImportFieldType Type = ImportFieldType.Text, bool Required = false)
{
    public IReadOnlyList<SettingOption> Options { get; init; } = [];

    public string? ChoiceListKey { get; init; }

    public ImportReference? Reference { get; init; }

    /// <summary>
    /// Firmanın kaydettiği ek başlık adları (ADR-0033 Dilim 4c, kayıtlı sütun eşlemesi): dosyada bu adlardan biri de bu sütuna bağlanır.
    /// Modül tanımı vermez; içe aktarma tasarımından gelir.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>Hücre değerinin hedef kaydı nasıl bulduğu: süzgeçler sırayla denenir, tek kayıt eşleşmelidir. Süzgeçler kayıt deposunda tanımlıdır.</summary>
public sealed record ImportReference(string EntityKey, IReadOnlyList<string> Filters);

/// <summary>
/// Mükerrer kuralı (ADR-0013). Olası eşleşme (<paramref name="Exact"/> false) <paramref name="Filter"/> süzgeciyle aranır ve yalnızca uyarıdır.
/// Kesin eşleşmede kayıtlı kaydı depo reddeder; motor dosya içi tekrarı satır hatası yapar, böylece kuru çalışma uygulamayla aynı sonucu verir.
/// </summary>
public sealed record ImportMatchRule(string Field, string? Filter, bool Exact = false);

/// <summary>
/// İçe aktarılabilir kayıt türü (ADR-0033 bölüm 3). Motor, ekran ve günlük türü bilmez; bir türü içe aktarılabilir yapmak modüle bu tanımı
/// eklemektir. Firmanın özel alanları sütunlara motor tarafından eklenir. Bu dilimde içe aktarma yalnızca yeni kayıt oluşturur; güncelleme
/// ve dış sistem kimliği sonraki dilimlerdedir.
/// </summary>
/// <param name="CreatePermission">Kayıt türünde oluşturma izni; modülün kendi iznidir.</param>
public sealed record ImportDefinition(string EntityKey, string DisplayName, string CreatePermission, IReadOnlyList<ImportField> Fields)
{
    /// <summary>Şablonun sürümü: sütunlar değişince artırılır, koşuda saklanır.</summary>
    public int TemplateVersion { get; init; } = 1;

    /// <summary>Mükerrer kuralları; kayıtlar hiçbir zaman otomatik birleştirilmez (ADR-0013).</summary>
    public IReadOnlyList<ImportMatchRule> MatchRules { get; init; } = [];
}
