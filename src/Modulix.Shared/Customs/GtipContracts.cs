namespace Modulix.Shared.Customs;

/// <summary>GTİP kataloğunun bir sürümü (ADR-0032 K9): <see cref="EffectiveFrom"/> tarihinden itibaren yürürlüktedir.</summary>
public sealed record GtipVersionDto(Guid Id, string Name, DateOnly EffectiveFrom, string? SourceFileName, int EntryCount, DateTimeOffset CreatedAt);

public sealed record GtipEntryDto(string Code, string Description, string? Unit);

public sealed record GtipEntryPageDto(List<GtipEntryDto> Items, int TotalCount, int Page, int PageSize);

/// <summary>
/// Bir kodun katalog denetimi. <c>Status</c>: <c>NoCatalog</c> (yürürlükte katalog yok, uyarı üretilmez), <c>Found</c>, <c>PrefixOnly</c>
/// (kod katalogdaki daha uzun kodların başı), <c>NotFound</c> (uyarı). Hiçbir durum kaydı engellemez.
/// </summary>
public sealed record GtipCheckDto(string Status, string? Description, string? VersionName);

/// <summary>Yüklenen dosyanın önizlemesi (hiçbir şey yazılmaz). <c>CanImport</c> hata yoksa ve satır varsa doğrudur.</summary>
public sealed record GtipImportPreviewDto(
    string? FatalError,
    bool CanImport,
    int RowsRead,
    int ValidCount,
    int HeadingRows,
    int PaddedCodes,
    int ErrorCount,
    List<GtipImportErrorDto> Errors,
    List<GtipEntryDto> Sample);

public sealed record GtipImportErrorDto(int Row, string Message);

/// <summary>İstemci ve sunucunun ortak dosya sınırı (sunucudaki <c>GtipImportRules.MaxFileBytes</c> ile aynıdır; testle denetlenir).</summary>
public static class GtipImportLimits
{
    public const int MaxFileBytes = 20 * 1024 * 1024;
}
