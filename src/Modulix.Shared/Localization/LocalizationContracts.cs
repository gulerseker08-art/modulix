namespace Modulix.Shared.Localization;

/// <summary>Bir dilin bütün arayüz metinleri (ADR-0034); eksik anahtarlar Türkçeden tamamlanmıştır.</summary>
public sealed record TextBundleResponse(string Language, IReadOnlyDictionary<string, string> Texts);
