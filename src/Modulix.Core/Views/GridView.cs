using System.Text.RegularExpressions;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Views;

/// <summary>
/// Kullanıcının bir liste ekranı (grid) için kaydettiği kişisel görünüm (ADR-0036): görünen sütunlar ve sırası, sıralama ve süzgeç.
/// Yalnızca sahibi (üyeliği) görür ve değiştirir; yetki taşımaz: liste uç noktası her açılışta kullanıcının yetkisini ve veri kapsamını
/// yeniden uygular, görünüm yalnızca sunumu seçer. Tenant verisidir; Row-Level Security ile korunur ve üyeliğe bileşik yabancı anahtarla bağlıdır.
/// </summary>
public sealed class GridView : ITenantEntity, ICompanyShared
{
    public const int ScreenKeyMaxLength = 100;
    public const int NameMaxLength = 60;
    public const int ColumnsMaxLength = 1000;
    public const int SortMaxLength = 60;
    public const int FiltersMaxLength = 500;

    private GridView()
    {
        // EF Core için
    }

    public GridView(Guid membershipId, string screenKey)
    {
        if (membershipId == Guid.Empty)
        {
            throw new ArgumentException("MembershipId boş olamaz.", nameof(membershipId));
        }

        Id = Guid.CreateVersion7();
        MembershipId = membershipId;
        ScreenKey = screenKey;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        Version = 1;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public Guid MembershipId { get; private set; }

    /// <summary>Hangi liste ekranı (ör. <c>crm.companies</c>); sayfanın kendi anahtarıdır.</summary>
    public string ScreenKey { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Görünen sütun anahtarları, sıralı ve virgülle ayrılmış (<see cref="GridViewRules.JoinColumns"/>).</summary>
    public string Columns { get; private set; } = string.Empty;

    /// <summary>Sıralama anahtarı (ör. <c>-name</c>); boşsa ekranın varsayılan sırası.</summary>
    public string? Sort { get; private set; }

    /// <summary>Sorgu dizgisi biçiminde süzgeç (ör. <c>status=Active</c>); boş olabilir.</summary>
    public string? Filters { get; private set; }

    /// <summary>Her değişiklikte artar; eşzamanlı düzenleme denetimi içindir.</summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Set(GridViewInput input)
    {
        Name = input.Name.Trim();
        Columns = GridViewRules.JoinColumns(input.Columns);
        Sort = string.IsNullOrWhiteSpace(input.Sort) ? null : input.Sort.Trim();
        Filters = string.IsNullOrWhiteSpace(input.Filters) ? null : input.Filters.Trim();
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}

public sealed record GridViewInfo(Guid Id, long Version, string ScreenKey, string Name, IReadOnlyList<string> Columns, string? Sort, string? Filters);

public sealed record GridViewInput(string ScreenKey, string Name, IReadOnlyList<string> Columns, string? Sort, string? Filters);

/// <summary>
/// Kişisel liste görünümleri (ADR-0036), aktif tenant ve oturumdaki üyelik için. Her üye yalnızca kendi görünümlerini görür ve değiştirir;
/// başkasının görünümü "bulunamadı"dır. Ek bir izin istemez (kişisel tercihtir, veri yetkisi vermez).
/// </summary>
public interface IGridViewService
{
    /// <summary>Üyenin bu ekran için görünümleri, ada göre.</summary>
    Task<IReadOnlyList<GridViewInfo>> ListAsync(string screenKey, CancellationToken cancellationToken);

    Task<RecordResult<GridViewInfo>> CreateAsync(GridViewInput input, CancellationToken cancellationToken);

    /// <summary>Eski sürüm çakışmadır; başkasının görünümü bulunamaz.</summary>
    Task<RecordResult<GridViewInfo>> UpdateAsync(Guid id, GridViewInput input, long version, CancellationToken cancellationToken);

    Task<RecordResult<bool>> DeleteAsync(Guid id, long version, CancellationToken cancellationToken);
}

/// <summary>Görünüm doğrulaması. Veritabanı bilmez; birim testleriyle denetlenir.</summary>
public static partial class GridViewRules
{
    /// <summary>Ekran başına, üye başına en fazla görünüm.</summary>
    public const int MaxViewsPerScreen = 20;

    public const int MaxColumns = 40;

    public static string JoinColumns(IEnumerable<string> columns) => string.Join(',', columns.Select(c => c.Trim()));

    public static IReadOnlyList<string> SplitColumns(string columns) =>
        columns.Length == 0 ? [] : [.. columns.Split(',', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>Kaydedilecek görünümün hataları; boşsa geçerlidir. Alan adları hata iletisinin hedefidir.</summary>
    public static IReadOnlyList<RecordFieldError> Validate(GridViewInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new List<RecordFieldError>();

        if (string.IsNullOrWhiteSpace(input.ScreenKey) || input.ScreenKey.Length > GridView.ScreenKeyMaxLength || !ScreenPattern().IsMatch(input.ScreenKey))
        {
            errors.Add(new("screenKey", "Ekran anahtarı küçük harf, rakam, nokta ve tire içermeli ve çok uzun olmamalıdır."));
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            errors.Add(new("name", "Görünüm adı zorunludur."));
        }
        else if (input.Name.Trim().Length > GridView.NameMaxLength)
        {
            errors.Add(new("name", $"Görünüm adı en fazla {GridView.NameMaxLength} karakter olabilir."));
        }

        var columns = input.Columns ?? [];
        if (columns.Count is 0 or > MaxColumns)
        {
            errors.Add(new("columns", $"1 ile {MaxColumns} arasında sütun seçilmelidir."));
        }
        else if (columns.Any(c => c is null || !ColumnPattern().IsMatch(c.Trim())) || columns.Select(c => c.Trim()).Distinct(StringComparer.Ordinal).Count() != columns.Count)
        {
            errors.Add(new("columns", "Sütun anahtarı geçersiz ya da tekrarlanıyor."));
        }
        else if (JoinColumns(columns).Length > GridView.ColumnsMaxLength)
        {
            errors.Add(new("columns", "Sütun listesi çok uzun."));
        }

        if (!string.IsNullOrWhiteSpace(input.Sort) && (input.Sort.Trim().Length > GridView.SortMaxLength || !SortPattern().IsMatch(input.Sort.Trim())))
        {
            errors.Add(new("sort", "Sıralama anahtarı geçersiz."));
        }

        if (!string.IsNullOrWhiteSpace(input.Filters) && !IsValidFilters(input.Filters.Trim()))
        {
            errors.Add(new("filters", "Süzgeç geçersiz (anahtar=değer çiftleri, & ile ayrılır; sayfa, arama ve sıralama içeremez)."));
        }

        return errors;
    }

    // Süzgeç yalnızca anahtar=değer çiftleridir; sayfalama, arama ve sıralama kendi alanlarıyla ya da isteğin kendisiyle gelir.
    private static bool IsValidFilters(string filters)
    {
        if (filters.Length > GridView.FiltersMaxLength)
        {
            return false;
        }

        foreach (var pair in filters.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || !FilterKeyPattern().IsMatch(parts[0]) || parts[1].Length > 100 || parts[1].Any(char.IsControl))
            {
                return false;
            }

            if (parts[0] is "page" or "pageSize" or "search" or "sort")
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*$")]
    private static partial Regex ScreenPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.]{0,39}$")]
    private static partial Regex ColumnPattern();

    [GeneratedRegex("^-?[A-Za-z][A-Za-z0-9_.]*$")]
    private static partial Regex SortPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.]{0,39}$")]
    private static partial Regex FilterKeyPattern();
}
