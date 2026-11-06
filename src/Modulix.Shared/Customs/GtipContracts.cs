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
