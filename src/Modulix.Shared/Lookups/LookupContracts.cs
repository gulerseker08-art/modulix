namespace Modulix.Shared.Lookups;

/// <summary>
/// Lookup alanı ve firma için geçerli davranışı (ADR-0016). <see cref="Searchable"/> firmanın seçimi ya da varsayılandır;
/// <see cref="Customized"/> varsayılandan farklı bir seçim yapıldığını, <see cref="Version"/> eşzamanlı düzenleme denetimini gösterir.
/// </summary>
public sealed record LookupDto(
    string Key,
    string ModuleKey,
    string SourceEntityKey,
    string SourceEntityName,
    string Field,
    string Title,
    bool IsMember,
    string? TargetEntityKey,
    string? TargetEntityName,
    string SearchDescription,
    bool DefaultSearchable,
    bool Searchable,
    bool Customized,
    long Version,
    int MinSearchLength,
    int PageSize,
    string? DependsOnField,
    string? DependsOnHint,
    string? ViewPath);

/// <summary>Sonuç satırı; erişilemeyen kayıtta <see cref="Label"/> boştur ve <see cref="Unavailable"/> doludur.</summary>
public sealed record LookupItemDto(Guid Id, string? Label, string? Secondary, bool Unavailable);

public sealed record LookupPageDto(IReadOnlyList<LookupItemDto> Items, int Page, int PageSize, int TotalCount, bool Searchable, bool CanView);

public sealed record ResolveLookupRequest(IReadOnlyList<Guid> Ids);

/// <summary><see cref="Searchable"/> boşsa firmanın seçimi kaldırılır (varsayılana dönülür). <see cref="Version"/>: ekranın gördüğü sürüm.</summary>
public sealed record SaveLookupSettingRequest(bool? Searchable, long Version);

/// <summary>Kimlikten çözümleme sonucu; <see cref="CanView"/> hedef kaydın ayrıntısını açma bağlantısının gösterilip gösterilmeyeceğidir.</summary>
public sealed record LookupResolveDto(IReadOnlyList<LookupItemDto> Items, bool CanView);
