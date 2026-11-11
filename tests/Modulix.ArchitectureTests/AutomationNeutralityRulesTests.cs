using System.Text.RegularExpressions;

namespace Modulix.ArchitectureTests;

/// <summary>
/// ADR-0027: kural ve iş akışı altyapısı Core'dadır ve sektöre özel bir şey bilmez. Modül yetenekleri yalnızca
/// <c>IModule.Automation</c> beyanıyla gelir; Core hiçbir modülün kayıt türü, alanı veya izni adını kodlamaz.
/// (Proje referansı kuralı Core'un modül projelerine başvurmasını zaten engeller; bu test metin düzeyindeki sızıntıyı yakalar.)
/// </summary>
public partial class AutomationNeutralityRulesTests
{
    private static readonly string[] ModuleKeys = ["crm", "logistics", "contracts", "orders", "support", "marketing", "warehouse", "fleet"];

    private static IEnumerable<FileInfo> AutomationSources(string project)
    {
        var folder = Path.Combine(RepositoryLayout.SourceProject(project).Directory!.FullName, "Automation");
        return Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*.cs", SearchOption.AllDirectories) : [];
    }

    [Theory]
    [InlineData("Modulix.Core")]
    [InlineData("Modulix.Core.Contracts")]
    public void Core_automation_code_does_not_name_any_module_key(string project)
    {
        var files = AutomationSources(project).ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            // Açıklama satırları (örnek anahtarlar içerebilir) kod sayılmaz.
            var text = string.Join("\n", File.ReadAllLines(file.FullName).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (Match match in StringLiteralPattern().Matches(text))
            {
                var literal = match.Groups["text"].Value;
                var module = ModuleKeys.FirstOrDefault(m => literal.StartsWith(m + ".", StringComparison.Ordinal) || literal == m);
                Assert.True(module is null, $"{file.Name}: Core kural kodu '{literal}' ile '{module}' modülünü adlandırıyor; yetenekler IModule.Automation ile beyan edilmeli.");
            }
        }
    }

    [Fact]
    public void Core_automation_code_has_no_conditions_on_sector_names()
    {
        var sectorCheck = SectorConditionPattern();
        foreach (var file in AutomationSources("Modulix.Core"))
        {
            Assert.False(sectorCheck.IsMatch(File.ReadAllText(file.FullName)), $"{file.Name}: sektöre göre koşul yazılamaz (CLAUDE.md).");
        }
    }

    [Theory]
    [InlineData("Modulix.Core")]
    [InlineData("Modulix.Core.Contracts")]
    public void Rule_code_has_no_ai_or_llm_dependency(string project)
    {
        var references = RepositoryLayout.ProjectReferences(RepositoryLayout.SourceProject(project));
        Assert.DoesNotContain("Modulix.AI", references);

        foreach (var file in AutomationSources(project))
        {
            Assert.DoesNotMatch(@"Anthropic|OpenAI|Embedding|VectorStore|LLM", File.ReadAllText(file.FullName).Replace("LLM çağrısı", string.Empty));
        }
    }

    [GeneratedRegex(@"""(?<text>[a-z][a-z0-9]*(?:\.[A-Za-z0-9_-]+)*)""")]
    private static partial Regex StringLiteralPattern();

    [GeneratedRegex(@"sektor\s*==|sector\s*==|Sektor\s*==", RegexOptions.IgnoreCase)]
    private static partial Regex SectorConditionPattern();
}
