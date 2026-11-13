namespace Modulix.ArchitectureTests;

/// <summary>
/// Talimat Bölüm 3, kural 10 ve 11: modüller yalnızca Core sözleşmelerine bağımlıdır,
/// birbirlerine ve Core'un iç koduna erişemez; arayüz yalnızca API sözleşmelerini görür.
/// Yeni bir proje eklendiğinde önce buradaki kural tablosuna eklenmelidir.
/// </summary>
public class ProjectReferenceRulesTests
{
    private const string CoreContracts = "Modulix.Core.Contracts";
    private const string ModulePrefix = "Modulix.Modules.";

    /// <summary>Her src projesinin referans verebileceği projeler.</summary>
    private static readonly Dictionary<string, string[]> AllowedReferences = new()
    {
        [CoreContracts] = [],
        ["Modulix.Shared"] = [],
        ["Modulix.Core"] = [CoreContracts],
        ["Modulix.Infrastructure"] = ["Modulix.Core", CoreContracts],
        ["Modulix.Modules.Crm"] = [CoreContracts],
        ["Modulix.Modules.Logistics"] = [CoreContracts],
        ["Modulix.Modules.Contracts"] = [CoreContracts],
        ["Modulix.Modules.Orders"] = [CoreContracts],
        ["Modulix.Modules.Support"] = [CoreContracts],
        ["Modulix.Modules.Marketing"] = [CoreContracts],
        ["Modulix.Modules.Warehouse"] = [CoreContracts],
        ["Modulix.Modules.Fleet"] = [CoreContracts],
        ["Modulix.Modules.Hr"] = [CoreContracts],
        ["Modulix.AI"] = [CoreContracts],
        ["Modulix.Web"] = ["Modulix.Shared"],

        // Host: modülleri kaydeder, uç noktalarını eşler ve arayüzü (Modulix.Web) aynı adresten sunar.
        ["Modulix.Api"] =
        [
            CoreContracts,
            "Modulix.Core",
            "Modulix.Infrastructure",
            "Modulix.Modules.Crm",
            "Modulix.Modules.Logistics",
            "Modulix.Modules.Contracts",
            "Modulix.Modules.Orders",
            "Modulix.Modules.Marketing",
            "Modulix.Modules.Support",
            "Modulix.Modules.Warehouse",
            "Modulix.Modules.Fleet",
            "Modulix.Modules.Hr",
            "Modulix.AI",
            "Modulix.Shared",
            "Modulix.Web",
        ],
    };

    public static TheoryData<string> SourceProjectNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in AllowedReferences.Keys.Order())
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void Every_source_project_has_a_reference_rule()
    {
        var actual = RepositoryLayout.SourceProjects().Select(RepositoryLayout.ProjectName).ToHashSet();
        var expected = AllowedReferences.Keys.ToHashSet();

        var withoutRule = actual.Except(expected).Order().ToList();
        var missingProject = expected.Except(actual).Order().ToList();

        Assert.True(
            withoutRule.Count == 0,
            $"Kural tablosunda olmayan proje(ler): {string.Join(", ", withoutRule)}. "
            + "Önce ProjectReferenceRulesTests.AllowedReferences'a ekleyin.");
        Assert.True(
            missingProject.Count == 0,
            $"Kural tablosunda olup src altında bulunamayan proje(ler): {string.Join(", ", missingProject)}.");
    }

    [Theory]
    [MemberData(nameof(SourceProjectNames))]
    public void Source_project_references_only_allowed_projects(string projectName)
    {
        var csproj = RepositoryLayout.SourceProject(projectName);
        var forbidden = RepositoryLayout.ProjectReferences(csproj)
            .Except(AllowedReferences[projectName])
            .Order()
            .ToList();

        Assert.True(
            forbidden.Count == 0,
            $"{projectName} izin verilmeyen projelere referans veriyor: {string.Join(", ", forbidden)}.");
    }

    [Fact]
    public void Rule_table_keeps_modules_isolated()
    {
        // Kural tablosu gevşetilerek bir modülün başka bir modüle veya Core iç koduna
        // bağlanmasına izin verilmesin.
        var modules = AllowedReferences.Where(rule => rule.Key.StartsWith(ModulePrefix, StringComparison.Ordinal));

        foreach (var (module, allowed) in modules)
        {
            Assert.True(
                allowed.SequenceEqual(new[] { CoreContracts }),
                $"{module} yalnızca {CoreContracts} projesine referans verebilir.");
        }

        Assert.True(
            AllowedReferences["Modulix.AI"].SequenceEqual(new[] { CoreContracts }),
            $"Modulix.AI yalnızca {CoreContracts} projesine referans verebilir.");
    }

    [Fact]
    public void Rule_table_keeps_web_on_api_contracts_only()
    {
        // Arayüz verilere yalnızca HTTP API üzerinden ulaşır (ADR-003).
        Assert.True(
            AllowedReferences["Modulix.Web"].SequenceEqual(new[] { "Modulix.Shared" }),
            "Modulix.Web yalnızca Modulix.Shared projesine referans verebilir.");
    }

    [Fact]
    public void Source_projects_do_not_reference_test_projects()
    {
        var testProjects = RepositoryLayout.TestProjects().Select(RepositoryLayout.ProjectName).ToHashSet();

        var violations = RepositoryLayout.SourceProjects()
            .SelectMany(csproj => RepositoryLayout.ProjectReferences(csproj)
                .Where(testProjects.Contains)
                .Select(reference => $"{RepositoryLayout.ProjectName(csproj)} -> {reference}"))
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"Kaynak projeler test projelerine referans veremez: {string.Join(", ", violations)}.");
    }
}
