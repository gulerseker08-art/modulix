using Modulix.Core.Sectors;

namespace Modulix.Core.Tests.Sectors;

/// <summary>ADR-0029 Dilim D: başlangıç paketi tanım denetimi. Core sektör bilmez; paket veridir.</summary>
public class StarterPackageRulesTests
{
    private static StarterPackageNode Node(string code, string? parent, ActivityNodeKind kind, params string[] capabilities) =>
        new(code, parent, kind, code.ToUpperInvariant(), null, 10, capabilities);

    private static StarterPackage Package(IReadOnlyList<StarterPackageNode>? nodes = null, IReadOnlyList<StarterPackageCapability>? capabilities = null) =>
        new("demo", "Demo", null, false, null,
            capabilities ?? [new("cap.a", "A", []), new("cap.b", "B", ["cap.a"])],
            nodes ?? [Node("kok", null, ActivityNodeKind.Sector), Node("grup", "kok", ActivityNodeKind.Group), Node("is", "grup", ActivityNodeKind.Activity, "cap.b")]);

    [Fact]
    public void A_well_formed_package_is_valid_and_parents_come_before_children()
    {
        var package = Package(nodes:
        [
            Node("is", "grup", ActivityNodeKind.Activity),
            Node("grup", "kok", ActivityNodeKind.Group),
            Node("kok", null, ActivityNodeKind.Sector),
        ]);

        Assert.Empty(StarterPackageRules.Validate(package));
        Assert.Equal(["kok", "grup", "is"], StarterPackageRules.InParentOrder(package).Select(n => n.Code));
    }

    [Fact]
    public void Structural_mistakes_are_reported_before_anything_is_installed()
    {
        Assert.Contains(StarterPackageRules.Validate(Package(nodes: [Node("kok", null, ActivityNodeKind.Sector), Node("kok", null, ActivityNodeKind.Sector)])), e => e.Contains("tekrar"));
        Assert.Contains(StarterPackageRules.Validate(Package(nodes: [Node("is", "yok", ActivityNodeKind.Activity)])), e => e.Contains("Üst düğüm pakette yok"));
        Assert.Contains(StarterPackageRules.Validate(Package(nodes: [Node("kok", "baska", ActivityNodeKind.Sector), Node("baska", null, ActivityNodeKind.Sector)])), e => e.Contains("Yalnızca sektör kök"));
        Assert.Contains(StarterPackageRules.Validate(Package(nodes: [Node("is", null, ActivityNodeKind.Activity)])), e => e.Contains("Yalnızca sektör kök"));
        Assert.Contains(StarterPackageRules.Validate(Package(nodes: [Node("Bozuk Kod", null, ActivityNodeKind.Sector)])), e => e.Contains("geçersiz"));
        Assert.Contains(StarterPackageRules.Validate(Package(nodes: [Node("kok", null, ActivityNodeKind.Sector), Node("is", "kok", ActivityNodeKind.Activity, "yok.yetenek")])), e => e.Contains("olmayan yeteneği"));
    }

    [Fact]
    public void Parent_cycles_and_capability_cycles_are_rejected()
    {
        var parentCycle = Package(nodes: [Node("a", "b", ActivityNodeKind.Group), Node("b", "a", ActivityNodeKind.Group)]);
        Assert.Contains(StarterPackageRules.Validate(parentCycle), e => e.Contains("döngü"));
        Assert.Throws<InvalidOperationException>(() => StarterPackageRules.InParentOrder(parentCycle));

        var capabilityCycle = Package(capabilities: [new("cap.a", "A", ["cap.b"]), new("cap.b", "B", ["cap.a"])]);
        Assert.Contains(StarterPackageRules.Validate(capabilityCycle), e => e.Contains("döngü"));
        Assert.Contains(StarterPackageRules.Validate(Package(capabilities: [new("cap.a", "A", ["cap.yok"])])), e => e.Contains("pakette olmayan yeteneğe"));
    }
}
