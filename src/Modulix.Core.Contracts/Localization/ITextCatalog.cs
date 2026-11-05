namespace Modulix.Core.Contracts.Localization;

/// <summary>
/// Ürünün sabit arayüz ve ileti metinleri (ADR-0034). Anahtar <c>modül.alan.öğe</c> biçimindedir; metin dile göre gelir.
/// Eksik çeviri sessizce Türkçeye (kaynak dil) düşer; Türkçede de yoksa anahtarın kendisi döner. Kullanıcının girdiği
/// veri ve kayıtlı teknik değerler çevrilmez.
/// </summary>
public interface ITextCatalog
{
    /// <summary>Metni <paramref name="language"/> dilinde döner; <paramref name="args"/> varsa <c>{0}</c> yer tutucularına işlenir.</summary>
    string Get(string language, string key, params object?[] args);

    /// <summary>Dilin bütün metinleri; eksik anahtarlar Türkçeden tamamlanmıştır. Bilinmeyen dil Türkçe sayılır.</summary>
    IReadOnlyDictionary<string, string> Bundle(string language);
}
