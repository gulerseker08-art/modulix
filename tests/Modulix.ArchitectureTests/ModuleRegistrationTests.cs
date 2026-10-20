using System.Text.RegularExpressions;

namespace Modulix.ArchitectureTests;

/// <summary>
/// Her modül projesi kendini tek bir <c>IModule</c> uygulamasıyla tanıtır ve API host'u onu kaydeder.
/// Kaydedilmeyen modülün izinleri katalogda olmaz ve tenant'lara açılamaz.
/// </summary>
public partial class ModuleRegistrationTests
{
    private const string ModulePrefix = "Modulix.Modules.";

    public static TheoryData<string> ModuleProjectNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in RepositoryLayout.SourceProjects()
                     .Select(RepositoryLayout.ProjectName)
                     .Where(n => n.StartsWith(ModulePrefix, StringComparison.Ordinal))
                     .Order())
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ModuleProjectNames))]
    public void Module_project_declares_one_module_and_the_api_registers_it(string projectName)
    {
        var projectDirectory = RepositoryLayout.SourceProject(projectName).Directory!;
        var modules = projectDirectory
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .SelectMany(f => ModuleClassPattern().Matches(File.ReadAllText(f.FullName)).Select(m => m.Groups["name"].Value))
            .ToList();

        var module = Assert.Single(modules);

        var program = File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, "src", "Modulix.Api", "Program.cs"));
        Assert.True(
            program.Contains($"new {module}()", StringComparison.Ordinal),
            $"{projectName}: {module} Modulix.Api/Program.cs içinde AddModulixModules(...) ile kaydedilmemiş.");
    }

    private static bool IsBuildOutput(FileInfo file) =>
        file.FullName.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");

    [GeneratedRegex(@"\bclass\s+(?<name>\w+)\s*:\s*(?:[\w.]+\s*,\s*)*IModule\b")]
    private static partial Regex ModuleClassPattern();
}
