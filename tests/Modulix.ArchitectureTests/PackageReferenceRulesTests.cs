namespace Modulix.ArchitectureTests;

/// <summary>
/// ADR-0001: veritabanına özgü kod yalnızca <c>Modulix.Infrastructure</c> içinde durur. Modüller EF Core'un
/// veritabanından bağımsız sözleşmelerini Core.Contracts üzerinden görür (entity yapılandırması,
/// <c>IModuleData</c>); SQL Server paketine ve bağlantısına erişemez. Arayüz hiçbir veri erişim paketi kullanmaz.
/// </summary>
public class PackageReferenceRulesTests
{
    private static readonly string[] DatabaseSpecificPackages =
    [
        "Microsoft.EntityFrameworkCore.SqlServer",
        "Microsoft.Data.SqlClient",
    ];

    private static readonly string[] ClientProjects = ["Modulix.Web", "Modulix.Shared"];

    public static TheoryData<string> SourceProjectNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in RepositoryLayout.SourceProjects().Select(RepositoryLayout.ProjectName).Order())
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SourceProjectNames))]
    public void Only_infrastructure_references_database_specific_packages(string projectName)
    {
        if (projectName == "Modulix.Infrastructure")
        {
            return;
        }

        var forbidden = RepositoryLayout.PackageReferences(RepositoryLayout.SourceProject(projectName))
            .Where(p => DatabaseSpecificPackages.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            forbidden.Count == 0,
            $"{projectName} veritabanına özgü paket kullanıyor: {string.Join(", ", forbidden)}. Bu kod Modulix.Infrastructure'da durur.");
    }

    [Fact]
    public void Client_projects_do_not_reference_data_access_packages()
    {
        var violations = ClientProjects
            .SelectMany(name => RepositoryLayout.PackageReferences(RepositoryLayout.SourceProject(name))
                .Where(p => p.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
                            || DatabaseSpecificPackages.Contains(p, StringComparer.OrdinalIgnoreCase))
                .Select(p => $"{name} -> {p}"))
            .ToList();

        Assert.True(violations.Count == 0, $"Arayüz projeleri veri erişim paketi kullanamaz: {string.Join(", ", violations)}.");
    }
}
