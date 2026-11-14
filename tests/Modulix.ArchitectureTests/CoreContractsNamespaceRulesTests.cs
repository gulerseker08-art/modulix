using System.Text.RegularExpressions;

namespace Modulix.ArchitectureTests;

/// <summary>
/// ADR-0044: <c>Modulix.Core.Contracts</c> yalnızca bütün modüllerin ortak kullandığı çekirdek sözleşmeleri tutar.
/// Bir modüle özel sözleşme buraya değil, sahibi olan modülün sözleşme projesine
/// (<c>Modulix.Modules.&lt;Ad&gt;.Contracts</c>) konur. Projedeki ad alanları aşağıdaki listeyle sabittir;
/// listede olmayan yeni ad alanı testi kırar.
/// </summary>
public partial class CoreContractsNamespaceRulesTests
{
    private const string CoreContracts = "Modulix.Core.Contracts";

    /// <summary>Her modülün kullanabildiği çekirdek sözleşme ad alanları.</summary>
    private static readonly string[] SharedNamespaces =
    [
        "Modulix.Core.Contracts.Approvals",
        "Modulix.Core.Contracts.Authorization",
        "Modulix.Core.Contracts.Automation",
        "Modulix.Core.Contracts.Communication",
        "Modulix.Core.Contracts.Credentials",
        "Modulix.Core.Contracts.Customization",
        "Modulix.Core.Contracts.Events",
        "Modulix.Core.Contracts.Files",
        "Modulix.Core.Contracts.Localization",
        "Modulix.Core.Contracts.Modules",
        "Modulix.Core.Contracts.Notifications",
        "Modulix.Core.Contracts.Persistence",
        "Modulix.Core.Contracts.Pricing", // ortak hesap çekirdeği: para, birim, indirim, teklif toplamı (ADR-0044 karar 2)
        "Modulix.Core.Contracts.Records",
        "Modulix.Core.Contracts.Reporting",
        "Modulix.Core.Contracts.Sectors",
        "Modulix.Core.Contracts.SharedRecords",
        "Modulix.Core.Contracts.Tenancy",
    ];

    /// <summary>
    /// Modüle özel olduğu hâlde henüz taşınmamış ad alanları. Taşınınca buradan silinir; bu listeye yeni ad alanı eklenmez.
    /// </summary>
    private static readonly string[] PendingMoves =
    [
        // Gümrük (GTİP, rejim profili): uygulaması platform tablolarında olduğu için sözleşmeyle birlikte, uç noktaların ve
        // platform tablolarının modüle taşınmasını ele alacak ADR'de taşınır (ADR-0044 karar 3).
        "Modulix.Core.Contracts.Customs",
    ];

    [Fact]
    public void Core_contracts_declares_only_listed_namespaces()
    {
        var allowed = SharedNamespaces.Concat(PendingMoves).ToList();
        var unexpected = DeclaredNamespaces()
            .Where(declared => !allowed.Any(entry => IsSameOrChild(declared.Namespace, entry)))
            .Select(declared => $"{declared.File}: {declared.Namespace}")
            .Order()
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"{CoreContracts} içinde listede olmayan ad alanı var:{Environment.NewLine}{string.Join(Environment.NewLine, unexpected)}"
            + $"{Environment.NewLine}Modüle özel sözleşme sahibi olan modülün sözleşme projesine konur (ADR-0044). "
            + "Bütün modüllerin kullanacağı yeni bir çekirdek sözleşmeyse SharedNamespaces listesine ekleyin.");
    }

    [Fact]
    public void Listed_namespaces_are_still_in_use()
    {
        // Taşınan ya da silinen ad alanı listede kalmasın; özellikle PendingMoves taşıma bitince boşalmalı.
        var declared = DeclaredNamespaces().Select(d => d.Namespace).ToList();
        var unused = SharedNamespaces.Concat(PendingMoves)
            .Where(entry => !declared.Any(ns => IsSameOrChild(ns, entry)))
            .Order()
            .ToList();

        Assert.True(
            unused.Count == 0,
            $"Listede olup {CoreContracts} içinde artık bulunmayan ad alanı: {string.Join(", ", unused)}. Listeden silin.");
    }

    [Fact]
    public void Lists_do_not_overlap()
    {
        var both = SharedNamespaces.Intersect(PendingMoves).ToList();

        Assert.True(both.Count == 0, $"Ad alanı iki listede birden: {string.Join(", ", both)}.");
    }

    private static bool IsSameOrChild(string declared, string entry) =>
        declared == entry || declared.StartsWith(entry + ".", StringComparison.Ordinal);

    private static List<(string File, string Namespace)> DeclaredNamespaces()
    {
        var directory = RepositoryLayout.SourceProject(CoreContracts).Directory!;
        var result = new List<(string File, string Namespace)>();
        foreach (var file in directory.EnumerateFiles("*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
        {
            var relative = Path.GetRelativePath(directory.FullName, file.FullName);
            var matches = NamespaceDeclaration().Matches(File.ReadAllText(file.FullName));
            if (matches.Count == 0)
            {
                // Ad alanı olmayan dosya genel (global) ad alanına tip koyar; o da listede değildir.
                result.Add((relative, "(ad alanı yok)"));
            }

            foreach (Match match in matches)
            {
                result.Add((relative, match.Groups[1].Value));
            }
        }

        return result;
    }

    private static bool IsBuildOutput(FileInfo file) =>
        file.FullName.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");

    [GeneratedRegex(@"^\s*namespace\s+([A-Za-z_][\w.]*)", RegexOptions.Multiline)]
    private static partial Regex NamespaceDeclaration();
}
