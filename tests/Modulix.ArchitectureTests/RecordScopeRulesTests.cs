using System.Text.RegularExpressions;

namespace Modulix.ArchitectureTests;

/// <summary>
/// ADR-0030: veri kapsamı kayıt türü anahtarıyla sorulur. Türsüz <c>GetOwnersAsync(cancellationToken)</c> çağrısı, bir rol türe
/// özel daraltıldığında sessizce geniş sonuç verebileceği için (fail-open) kaynakta bulunmamalıdır. İmza değişikliği derleyiciyi
/// zaten zorlar; bu test türsüz bir uyumluluk sürümünün sonradan eklenmesini yakalar.
/// </summary>
public partial class RecordScopeRulesTests
{
    [Fact]
    public void GetOwnersAsync_is_never_called_without_an_entity_key()
    {
        var sources = RepositoryLayout.SourceProjects()
            .SelectMany(p => p.Directory!.EnumerateFiles("*.cs", SearchOption.AllDirectories))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(sources);

        var offenders = new List<string>();
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file.FullName);
            foreach (Match match in TypelessCall().Matches(text))
            {
                var line = text[..match.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{file.Name}:{line}");
            }
        }

        Assert.True(offenders.Count == 0, "Türsüz GetOwnersAsync çağrısı var (kayıt türü anahtarı verin): " + string.Join(", ", offenders));
    }

    [Fact]
    public void Scope_services_declare_entity_key_in_their_signature()
    {
        var core = File.ReadAllText(Path.Combine(
            RepositoryLayout.SourceProject("Modulix.Core").Directory!.FullName, "Records", "RecordPipelinePorts.cs"));
        var contracts = File.ReadAllText(Path.Combine(
            RepositoryLayout.SourceProject("Modulix.Core.Contracts").Directory!.FullName, "Records", "RecordAccess.cs"));

        Assert.Contains("GetOwnersAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken)", core, StringComparison.Ordinal);
        Assert.Contains("GetOwnersAsync(string entityKey, CancellationToken cancellationToken)", contracts, StringComparison.Ordinal);
    }

    // Yalnızca iptal belirteci alan çağrı; belirteç adları projede ct / cancellationToken. (tenant, belirteç) biçimini derleyici yakalar:
    // ikinci argüman string olmak zorundadır.
    [GeneratedRegex(@"GetOwnersAsync\(\s*(cancellationToken|ct|CancellationToken\.None)\s*\)")]
    private static partial Regex TypelessCall();
}
