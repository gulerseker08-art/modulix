using System.Xml.Linq;

namespace Modulix.ArchitectureTests;

/// <summary>
/// Repo içindeki proje dosyalarını bulur ve okur. Testler derlenmiş kodu değil,
/// .csproj dosyalarındaki ProjectReference kayıtlarını denetler; böylece kullanılmayan
/// bir referans bile eklendiği anda yakalanır.
/// </summary>
internal static class RepositoryLayout
{
    private static readonly Lazy<DirectoryInfo> LazyRoot = new(FindRoot);

    public static DirectoryInfo Root => LazyRoot.Value;

    public static IReadOnlyList<FileInfo> SourceProjects() => ProjectsUnder("src");

    public static IReadOnlyList<FileInfo> TestProjects() => ProjectsUnder("tests");

    public static FileInfo SourceProject(string projectName) =>
        SourceProjects().Single(p => ProjectName(p) == projectName);

    public static string ProjectName(FileInfo csproj) =>
        Path.GetFileNameWithoutExtension(csproj.Name);

    public static IReadOnlyList<string> ProjectReferences(FileInfo csproj) =>
        XDocument.Load(csproj.FullName)
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .ToList();

    private static IReadOnlyList<FileInfo> ProjectsUnder(string folder)
    {
        var directory = new DirectoryInfo(Path.Combine(Root.FullName, folder));
        return directory.Exists
            ? directory.EnumerateFiles("*.csproj", SearchOption.AllDirectories).ToList()
            : [];
    }

    private static DirectoryInfo FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory
            ?? throw new InvalidOperationException(
                "Repo kökü bulunamadı (Directory.Build.props yok). Testleri repo içinden çalıştırın.");
    }
}
