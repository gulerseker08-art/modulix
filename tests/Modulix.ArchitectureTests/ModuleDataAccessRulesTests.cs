namespace Modulix.ArchitectureTests;

/// <summary>
/// Ç0.3 boru hattı atlama kapısı: modüller kendi tablolarına yalnızca kayıt deposu katmanından (<c>Records</c> klasörü,
/// <c>IRecordStore</c> uygulamaları) erişir ve hiçbir yerde kaydetmez. Böylece her yazım kayıt boru hattının izin, kapsam,
/// hassas alan, denetim kaydı ve olay adımlarından geçer.
/// </summary>
public class ModuleDataAccessRulesTests
{
    private const string RecordsFolder = "Records";

    public static TheoryData<string> ModuleProjectNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in RepositoryLayout.SourceProjects()
                     .Select(RepositoryLayout.ProjectName)
                     .Where(n => n.StartsWith("Modulix.Modules.", StringComparison.Ordinal) || n == "Modulix.AI")
                     .Order())
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ModuleProjectNames))]
    public void Module_data_is_reached_only_from_record_stores_and_never_saved(string projectName)
    {
        var directory = RepositoryLayout.SourceProject(projectName).Directory!;
        var problems = new List<string>();
        foreach (var file in directory.EnumerateFiles("*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
        {
            var text = File.ReadAllText(file.FullName);
            var relative = Path.GetRelativePath(directory.FullName, file.FullName);
            if (text.Contains("IModuleData", StringComparison.Ordinal) && !relative.StartsWith(RecordsFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                problems.Add($"{relative}: IModuleData yalnızca {RecordsFolder} klasöründeki kayıt depolarında kullanılır.");
            }

            if (text.Contains("SaveChanges", StringComparison.Ordinal))
            {
                problems.Add($"{relative}: modül kaydetmez; kayıt boru hattı kayıt, denetim ve olayı birlikte yazar.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static bool IsBuildOutput(FileInfo file) =>
        file.FullName.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");
}
