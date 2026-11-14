using System.Globalization;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// Departmanı adla bulma (ADR-0042 bölüm 22: içe aktarma ve eski İK değerlerinin eşlenmesi). Ad, departmanın kendi adı ya da ağaç yolu ("Merkez › Satış")
/// olabilir; yolun başı (ör. kök departman) yazılmayabilir ("Satış › İstanbul"). Büyük/küçük harf Türkçe kurallarla yok sayılır. Yalnızca etkin
/// departmanlar aranır; birden çok departmana uyan ad belirsizdir (çağıran reddeder).
/// </summary>
internal static class BusinessUnitNames
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static IReadOnlyList<BusinessUnitInfo> Find(IReadOnlyList<BusinessUnitInfo> units, string name)
    {
        var wanted = Normalize(name);
        return [.. units.Where(u => u.IsActive && (Same(u.Name, wanted) || Same(Normalize(u.PathNames), wanted) || EndsWith(Normalize(u.PathNames), wanted)))];
    }

    // Ağaç yolu ayracı olarak "›", ">" ve "/" kabul edilir; ayraç çevresindeki boşluklar tekleştirilir.
    private static string Normalize(string value) =>
        string.Join(" › ", value.Split(['›', '>', '/'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    private static bool Same(string a, string b) => string.Compare(a.Trim(), b, Turkish, CompareOptions.IgnoreCase) == 0;

    private static bool EndsWith(string path, string wanted) => wanted.Contains('›') && path.EndsWith(" › " + wanted, true, Turkish);

    /// <summary>Kayıtlardaki departmanların adları (ağaç yolu); listede göstermek için. Departman yoksa sorgu yapılmaz.</summary>
    public static async Task<Dictionary<Guid, string>> PathsAsync(HrStoreContext context, IEnumerable<Guid?> units, CancellationToken cancellationToken) =>
        units.Any(u => u is not null)
            ? (await context.BusinessUnits.ListAsync(cancellationToken)).ToDictionary(u => u.Id, u => u.PathNames)
            : [];
}
