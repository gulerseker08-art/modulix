namespace Modulix.ArchitectureTests;

/// <summary>
/// ADR-0044: modül sözleşme projesi (<c>Modulix.Modules.&lt;Ad&gt;.Contracts</c>) yalnızca başka modüllere açılan arayüzleri ve
/// veri tiplerini taşır. Entity, veri erişimi ya da modül kaydı içermez; bunlar modülün ana projesinde kalır.
/// </summary>
public class ModuleContractsRulesTests
{
    /// <summary>Sözleşme projesinde bulunmaması gereken adlar ve gerekçesi.</summary>
    private static readonly (string Token, string Reason)[] Forbidden =
    [
        ("ITenantEntity", "entity modülün ana projesinde durur"),
        ("IModuleData", "veri erişimi yalnızca modülün kayıt depolarındadır"),
        ("DbContext", "sözleşme projesi veritabanına erişmez"),
        ("DbSet", "sözleşme projesi veritabanına erişmez"),
        ("SaveChanges", "sözleşme projesi kaydetmez"),
        ("IModule", "modül kaydı modülün ana projesindedir"),
    ];

    public static TheoryData<string> ContractsProjectNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in RepositoryLayout.SourceProjects()
                     .Select(RepositoryLayout.ProjectName)
                     .Where(RepositoryLayout.IsModuleContracts)
                     .Order())
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ContractsProjectNames))]
    public void Contracts_project_holds_only_interfaces_and_data_types(string projectName)
    {
        var directory = RepositoryLayout.SourceProject(projectName).Directory!;
        var problems = new List<string>();
        foreach (var file in directory.EnumerateFiles("*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
        {
            var text = File.ReadAllText(file.FullName);
            var relative = Path.GetRelativePath(directory.FullName, file.FullName);
            foreach (var (token, reason) in Forbidden)
            {
                if (ContainsIdentifier(text, token))
                {
                    problems.Add($"{relative}: {token} kullanılamaz ({reason}).");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [MemberData(nameof(ContractsProjectNames))]
    public void Contracts_project_belongs_to_an_existing_module(string projectName)
    {
        var module = projectName[..^".Contracts".Length];
        var exists = RepositoryLayout.SourceProjects().Any(p => RepositoryLayout.ProjectName(p) == module);

        Assert.True(exists, $"{projectName}: sahibi olan {module} projesi bulunamadı.");
    }

    /// <summary>Adı tam sözcük olarak arar; <c>IModule</c> araması <c>IModuleData</c>'yı ayrıca saymaz.</summary>
    private static bool ContainsIdentifier(string text, string token)
    {
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            var before = index == 0 || !IsIdentifierChar(text[index - 1]);
            var end = index + token.Length;
            var after = end >= text.Length || !IsIdentifierChar(text[end]);
            if (before && after)
            {
                return true;
            }

            index = end;
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsBuildOutput(FileInfo file) =>
        file.FullName.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");
}
