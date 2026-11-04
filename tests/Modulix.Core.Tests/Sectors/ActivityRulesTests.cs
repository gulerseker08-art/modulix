using Modulix.Core.Sectors;

namespace Modulix.Core.Tests.Sectors;

public class ActivityRulesTests
{
    private static readonly Guid Logistics = Guid.NewGuid();
    private static readonly Guid Transport = Guid.NewGuid();
    private static readonly Guid Road = Guid.NewGuid();
    private static readonly Guid Storage = Guid.NewGuid();
    private static readonly Guid Bonded = Guid.NewGuid();
    private static readonly Guid Free = Guid.NewGuid();

    private static readonly List<ActivityNodeInfo> Nodes =
    [
        new(Logistics, "lojistik", null, ActivityNodeKind.Sector, "Lojistik", true),
        new(Transport, "tasimacilik", Logistics, ActivityNodeKind.Group, "Taşımacılık", true),
        new(Road, "karayolu", Transport, ActivityNodeKind.Activity, "Karayolu", true),
        new(Storage, "depolama", Logistics, ActivityNodeKind.Group, "Depolama", true),
        new(Bonded, "gumruklu-antrepo", Storage, ActivityNodeKind.Activity, "Gümrüklü antrepo", true),
        new(Free, "serbest-depolama", Storage, ActivityNodeKind.Activity, "Serbest depolama", true, ModuleReadiness.Available),
    ];

    [Theory]
    [InlineData("karayolu", true)]
    [InlineData("gumruklu-antrepo", true)]
    [InlineData("Karayolu", false)]
    [InlineData("kara yolu", false)]
    [InlineData("", false)]
    public void Code_format_is_checked(string code, bool valid) => Assert.Equal(valid, ActivityTreeRules.IsValidCode(code));

    [Fact]
    public void Ancestors_run_from_root_to_parent()
    {
        Assert.Equal([Logistics, Transport], ActivityTreeRules.Ancestors(Nodes, Road));
        Assert.Empty(ActivityTreeRules.Ancestors(Nodes, Logistics));
    }

    [Fact]
    public void Descendants_include_all_levels_but_not_the_node()
    {
        var all = ActivityTreeRules.Descendants(Nodes, Logistics);

        Assert.Equal(5, all.Count);
        Assert.DoesNotContain(Logistics, all);
    }

    [Fact]
    public void Only_sector_can_be_a_root()
    {
        Assert.Empty(ActivityTreeRules.ValidateParent(Nodes, null, ActivityNodeKind.Sector, null));
        Assert.NotEmpty(ActivityTreeRules.ValidateParent(Nodes, null, ActivityNodeKind.Sector, Logistics));
        Assert.NotEmpty(ActivityTreeRules.ValidateParent(Nodes, null, ActivityNodeKind.Group, null));
    }

    [Fact]
    public void Levels_are_not_limited_to_three()
    {
        // Alt faaliyetin altına yeni düğüm eklenebilir.
        Assert.Empty(ActivityTreeRules.ValidateParent(Nodes, null, ActivityNodeKind.Activity, Road));
    }

    [Fact]
    public void Node_cannot_be_its_own_parent_or_move_under_its_descendant()
    {
        Assert.NotEmpty(ActivityTreeRules.ValidateParent(Nodes, Transport, ActivityNodeKind.Group, Transport));
        Assert.NotEmpty(ActivityTreeRules.ValidateParent(Nodes, Transport, ActivityNodeKind.Group, Road));
        Assert.Empty(ActivityTreeRules.ValidateParent(Nodes, Road, ActivityNodeKind.Activity, Storage));
    }

    [Fact]
    public void Inactive_or_unknown_parent_is_rejected()
    {
        List<ActivityNodeInfo> nodes = [.. Nodes.Select(n => n.Id == Storage ? n with { IsActive = false } : n)];

        Assert.NotEmpty(ActivityTreeRules.ValidateParent(nodes, null, ActivityNodeKind.Activity, Storage));
        Assert.NotEmpty(ActivityTreeRules.ValidateParent(nodes, null, ActivityNodeKind.Activity, Guid.NewGuid()));
    }

    [Fact]
    public void Node_with_active_children_cannot_be_deactivated()
    {
        Assert.NotEmpty(ActivityTreeRules.ValidateDeactivate(Nodes, Storage));
        Assert.Empty(ActivityTreeRules.ValidateDeactivate(Nodes, Road));
    }

    [Fact]
    public void Used_nodes_block_a_move()
    {
        var impact = ActivityTreeRules.Impact(Nodes, Storage, new HashSet<Guid> { Bonded });

        Assert.Equal(2, impact.DescendantCount);
        Assert.Equal(1, impact.UsedNodeCount);
        Assert.False(impact.Allowed);
        Assert.True(ActivityTreeRules.Impact(Nodes, Storage, new HashSet<Guid>()).Allowed);
    }

    [Fact]
    public void Selecting_a_group_does_not_activate_its_activities_and_context_is_derived()
    {
        var preview = Preview(selected: [Bonded, Road]);

        Assert.Equal(new HashSet<Guid> { Logistics, Transport, Storage }, preview.ContextNodeIds.ToHashSet());
        Assert.DoesNotContain(Free, preview.ContextNodeIds);
    }

    [Fact]
    public void Selecting_only_a_parent_keeps_children_unselected()
    {
        var preview = Preview(selected: [Storage]);

        Assert.Equal([Logistics], preview.ContextNodeIds);
        Assert.DoesNotContain(Bonded, preview.ContextNodeIds);
        Assert.DoesNotContain(Free, preview.ContextNodeIds);
    }

    [Fact]
    public void Missing_capability_and_its_dependencies_are_reported_before_publishing()
    {
        var preview = Preview(
            selected: [Bonded],
            nodeCapabilities: new Dictionary<Guid, IReadOnlyList<string>> { [Bonded] = ["storage-need"] },
            dependencies: new Dictionary<string, IReadOnlyList<string>> { ["storage-need"] = ["service-profile"] },
            available: new HashSet<string> { "service-profile" });

        CapabilityGap gap = Assert.Single(preview.Gaps);
        Assert.Equal("storage-need", gap.Capability);
        Assert.Equal([Bonded], gap.RequiredByNodes);
        Assert.False(preview.IsComplete);
    }

    [Fact]
    public void Inactive_and_unknown_selections_are_errors_and_catalog_only_is_flagged()
    {
        var preview = Preview(selected: [Road, Free, Guid.NewGuid()]);

        Assert.Equal(2, preview.Errors.Count);
        Assert.Equal([Road], preview.CatalogOnlyNodeIds);
    }

    [Fact]
    public void Capability_dependency_cycle_is_detected()
    {
        var errors = CapabilityGraphRules.ValidateDependencies(new Dictionary<string, IReadOnlyList<string>>
        {
            ["a"] = ["b"],
            ["b"] = ["c"],
            ["c"] = ["a"],
        });

        Assert.Single(errors);
        Assert.Empty(CapabilityGraphRules.ValidateDependencies(new Dictionary<string, IReadOnlyList<string>> { ["a"] = ["b"], ["b"] = [] }));
        Assert.Equal(["a", "b", "c"], CapabilityGraphRules.Closure("a", new Dictionary<string, IReadOnlyList<string>> { ["a"] = ["b"], ["b"] = ["c", "a"] }));
    }

    private static SelectionPreview Preview(
        Guid[] selected,
        Dictionary<Guid, IReadOnlyList<string>>? nodeCapabilities = null,
        Dictionary<string, IReadOnlyList<string>>? dependencies = null,
        HashSet<string>? available = null)
    {
        List<ActivityNodeInfo> nodes = [.. Nodes.Select(n => n.Id == Free ? n with { IsActive = false } : n)];
        return ActivitySelectionRules.Preview(
            nodes,
            selected.ToHashSet(),
            nodeCapabilities ?? [],
            dependencies ?? [],
            available ?? []);
    }
}
