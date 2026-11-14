using System.Text.RegularExpressions;

namespace Modulix.ArchitectureTests;

/// <summary>
/// ADR-0046 Dilim 0: <c>Modulix.Api</c> her projeye başvurabildiği için modül sınırı proje başvurularıyla denetlenemez.
/// Bu testler <c>Modulix.Api/&lt;Ad&gt;/</c> altındaki modül klasörlerinin kaynak kodunu okur:
/// klasör başka modülün ana projesine başvuramaz (yalnızca sözleşme projesine, ADR-0044), ve modüle taşınmasını
/// engelleyen host içi başvurular (<c>Core</c> iç kodu, <c>Infrastructure</c>, başka Api klasörü) aşağıdaki listeyle sabittir.
/// Liste yalnızca küçülür: taşıma dilimleri ilerledikçe satırlar silinir, yeni satır eklenmez.
/// </summary>
/// <remarks>
/// Denetim metin üzerindedir: <c>using</c> satırları ve tam adla yazılmış başvurular (<c>Modulix.X.Y.Tip</c>) yakalanır.
/// Üst ad alanı üzerinden kısaltılmış başvuru (<c>Modulix.Api.Crm</c> içinden <c>Users.Tip</c>) yakalanmaz.
/// </remarks>
public partial class ApiModuleFolderRulesTests
{
    private const string ModulesPrefix = "Modulix.Modules.";

    /// <summary>Adı modül projesiyle aynı olmayan modül klasörleri (klasör → sahibi olacak modül projesi).</summary>
    private static readonly Dictionary<string, string> FoldersOwnedByAnotherName = new()
    {
        // Gümrük (GTİP, rejim profili) Warehouse modülüne taşınacak (ADR-0046 karar 3, Dilim 7).
        ["Customs"] = "Modulix.Modules.Warehouse",
    };

    /// <summary>Modüle taşınınca <c>Modulix.Core.Hosting</c>'e gidecek Api klasörleri; modül klasörü bunlara başvurabilir.</summary>
    private static readonly string[] HostingFolders = ["Access", "Records"];

    /// <summary>
    /// Modül klasörlerinin bugünkü host içi başvuruları (14 Kasım 2026 envanteri). Klasör modüle taşınmadan önce
    /// bunlar temizlenir (ADR-0046 bölüm 4). Bu listeye yeni satır eklenmez; temizlenen satır silinir.
    /// </summary>
    private static readonly Dictionary<string, string[]> PendingCleanup = new()
    {
        ["Contracts"] =
        [
            "Modulix.Api.Crm",
            "Modulix.Core.Events",
            "Modulix.Core.Lookups",
            "Modulix.Core.Modules",
            "Modulix.Core.Platform",
            "Modulix.Core.Records",
            "Modulix.Infrastructure",
        ],
        ["Crm"] =
        [
            "Modulix.Api.Users",
            "Modulix.Core.Events",
            "Modulix.Core.Modules",
            "Modulix.Core.Platform",
            "Modulix.Core.Records",
            "Modulix.Core.Reporting",
            "Modulix.Core.Settings",
            "Modulix.Infrastructure",
        ],
        ["Customs"] =
        [
            "Modulix.Api.Imports",
            "Modulix.Api.Platform",
            "Modulix.Api.Users",
            "Modulix.Core.Customs",
        ],
        ["Marketing"] =
        [
            "Modulix.Core.Events",
            "Modulix.Core.Modules",
            "Modulix.Core.Platform",
            "Modulix.Infrastructure",
        ],
        ["Orders"] =
        [
            "Modulix.Core.Events",
            "Modulix.Core.Modules",
            "Modulix.Core.Platform",
            "Modulix.Infrastructure",
        ],
        ["Support"] =
        [
            "Modulix.Api.Crm",
            "Modulix.Core.Events",
            "Modulix.Core.Modules",
            "Modulix.Core.Platform",
            "Modulix.Core.Records",
            "Modulix.Infrastructure",
        ],
    };

    [Fact]
    public void Module_folder_does_not_reference_another_modules_main_project()
    {
        var contractsProjects = RepositoryLayout.SourceProjects()
            .Select(RepositoryLayout.ProjectName)
            .Where(RepositoryLayout.IsModuleContracts)
            .ToList();

        var violations = new List<string>();
        foreach (var (folder, owner) in ModuleFolders())
        {
            foreach (var (file, reference) in References(folder))
            {
                if (!reference.StartsWith(ModulesPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                bool own = IsSameOrChild(reference, owner);
                bool contracts = contractsProjects.Any(project => IsSameOrChild(reference, project));
                if (!own && !contracts)
                {
                    violations.Add($"{file}: {reference}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Api modül klasörü başka modülün ana projesine başvuruyor:{Environment.NewLine}"
            + string.Join(Environment.NewLine, violations.Distinct().Order())
            + $"{Environment.NewLine}Modüller arası kullanım sahibi olan modülün sözleşme projesinden "
            + "(Modulix.Modules.<Ad>.Contracts) geçer (ADR-0044, ADR-0046).");
    }

    [Fact]
    public void Module_folder_host_references_do_not_grow()
    {
        var violations = new List<string>();
        foreach (var (folder, _) in ModuleFolders())
        {
            string[] pending = PendingCleanup.GetValueOrDefault(folder, []);
            foreach (var (file, reference) in References(folder))
            {
                string? host = HostReference(folder, reference);
                if (host is not null && !pending.Contains(host))
                {
                    violations.Add($"{file}: {host}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Api modül klasörü listede olmayan host içi koda başvuruyor:{Environment.NewLine}"
            + string.Join(Environment.NewLine, violations.Distinct().Order())
            + $"{Environment.NewLine}Modül uç noktası yalnızca kendi modülüne, Modulix.Core.Contracts'a, Modulix.Shared'a, "
            + "modül sözleşme projelerine ve Api/Access ile Api/Records'a başvurur (ADR-0046 bölüm 4). "
            + "Gereken şey Core.Contracts'ta arayüz olarak açılır; PendingCleanup listesine yeni satır eklenmez.");
    }

    [Fact]
    public void Pending_cleanup_entries_are_still_needed()
    {
        var folders = ModuleFolders().Select(f => f.Folder).ToList();
        var stale = new List<string>();
        foreach (var (folder, pending) in PendingCleanup)
        {
            if (!folders.Contains(folder))
            {
                stale.Add($"{folder}: klasör artık yok ya da modül klasörü değil");
                continue;
            }

            var used = References(folder)
                .Select(r => HostReference(folder, r.Reference))
                .OfType<string>()
                .ToHashSet();
            stale.AddRange(pending.Where(entry => !used.Contains(entry)).Select(entry => $"{folder}: {entry}"));
        }

        Assert.True(
            stale.Count == 0,
            $"PendingCleanup listesinde artık kullanılmayan satır var; listeden silin:{Environment.NewLine}"
            + string.Join(Environment.NewLine, stale.Order()));
    }

    /// <summary>Modüle taşınmayı engelleyen host içi başvuruyu ad alanı köküne indirger; engel değilse <c>null</c>.</summary>
    private static string? HostReference(string folder, string reference)
    {
        string[] parts = reference.Split('.');
        return parts switch
        {
            ["Modulix", "Infrastructure", ..] => "Modulix.Infrastructure",
            ["Modulix", "Core", "Contracts", ..] => null,
            ["Modulix", "Core", var area, ..] => $"Modulix.Core.{area}",
            ["Modulix", "Api", var area, ..] when area != folder && !HostingFolders.Contains(area) => $"Modulix.Api.{area}",
            _ => null,
        };
    }

    /// <summary>
    /// Api altındaki modül klasörleri: adı bir modül projesiyle (<c>Modulix.Modules.&lt;Ad&gt;</c>) aynı olanlar ve
    /// <see cref="FoldersOwnedByAnotherName"/> içindekiler. Yeni modülün klasörü kendiliğinden kapsama girer.
    /// </summary>
    private static List<(string Folder, string Owner)> ModuleFolders()
    {
        var modules = RepositoryLayout.SourceProjects()
            .Select(RepositoryLayout.ProjectName)
            .Where(name => name.Split('.') is ["Modulix", "Modules", _])
            .ToList();

        var result = new List<(string Folder, string Owner)>();
        foreach (var directory in ApiDirectory().EnumerateDirectories())
        {
            string? owner = FoldersOwnedByAnotherName.GetValueOrDefault(directory.Name)
                ?? modules.FirstOrDefault(module => module == ModulesPrefix + directory.Name);
            if (owner is not null)
            {
                result.Add((directory.Name, owner));
            }
        }

        return result;
    }

    private static List<(string File, string Reference)> References(string folder)
    {
        var api = ApiDirectory();
        var directory = new DirectoryInfo(Path.Combine(api.FullName, folder));
        var result = new List<(string File, string Reference)>();
        foreach (var file in directory.EnumerateFiles("*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(api.FullName, file.FullName).Replace('\\', '/');
            foreach (Match match in ModulixReference().Matches(File.ReadAllText(file.FullName)))
            {
                result.Add((relative, match.Value));
            }
        }

        return result;
    }

    private static DirectoryInfo ApiDirectory() => RepositoryLayout.SourceProject("Modulix.Api").Directory!;

    private static bool IsSameOrChild(string reference, string root) =>
        reference == root || reference.StartsWith(root + ".", StringComparison.Ordinal);

    [GeneratedRegex(@"\bModulix(\.[A-Za-z_]\w*)+")]
    private static partial Regex ModulixReference();
}
