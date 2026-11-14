using System.Text.RegularExpressions;

namespace Modulix.ArchitectureTests;

/// <summary>
/// ADR-0028: firmanın erişime açık olup olmadığı <c>TenantStatusRules.Operational</c> ile sorulur (Active ve Trial). Kaynakta
/// <c>Status == TenantStatus.Active</c> karşılaştırması deneme süresindeki firmayı sessizce dışarıda bırakır (arka plan işçileri
/// kurallarını, raporlarını, gönderimlerini çalıştırmaz); bu test o karşılaştırmanın yeniden yazılmasını yakalar.
/// </summary>
public partial class TenantStatusRulesTests
{
    [Fact]
    public void Tenant_status_is_never_compared_to_Active_directly()
    {
        var sources = RepositoryLayout.SourceProjects()
            .SelectMany(p => p.Directory!.EnumerateFiles("*.cs", SearchOption.AllDirectories))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(sources);

        var offenders = new List<string>();
        foreach (var file in sources)
        {
            string[] lines = File.ReadAllLines(file.FullName);
            for (var i = 0; i < lines.Length; i++)
            {
                // Yorum satırları (kuralı anlatan belgeler) sayılmaz.
                if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && DirectComparison().IsMatch(lines[i]))
                {
                    offenders.Add($"{file.Name}:{i + 1}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Firma durumu doğrudan Active ile karşılaştırılıyor (TenantStatusRules.Operational kullanın): " + string.Join(", ", offenders));
    }

    [GeneratedRegex(@"[!=]=\s*TenantStatus\.Active\b|\bTenantStatus\.Active\s*[!=]=")]
    private static partial Regex DirectComparison();
}
