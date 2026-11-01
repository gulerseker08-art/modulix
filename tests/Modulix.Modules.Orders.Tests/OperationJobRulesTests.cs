using Modulix.Modules.Orders.Lifecycle;

namespace Modulix.Modules.Orders.Tests;

/// <summary>ADR-0023 dilim 4: operasyon işi üretim planı, şablonlar, geçiş tablosu ve bağımlılık kuralları (saf kurallar).</summary>
public class OperationJobRulesTests
{
    private static readonly Guid Device = Guid.NewGuid();
    private static readonly Guid Cable = Guid.NewGuid();
    private static readonly Guid Setup = Guid.NewGuid();

    private static IReadOnlyList<JobLineInput> Lines() =>
    [
        new(Device, "Sunucu", LineKinds.Product), new(Cable, "Kablo seti", LineKinds.Product), new(Setup, "Kurulum", LineKinds.Service),
    ];

    [Fact]
    public void Per_kind_strategy_groups_products_into_delivery_and_services_into_service_start()
    {
        var plan = JobGenerationPlanner.Plan("SP-1", Lines(), JobStrategy.PerKind, serviceAfterDelivery: false);

        Assert.Equal(["kind:Product", "kind:Service"], plan.Select(p => p.GroupKey));
        Assert.Equal([JobKind.Delivery, JobKind.Service], plan.Select(p => p.Kind));
        Assert.Equal([Device, Cable], plan[0].LineIds);
        Assert.Equal([Setup], plan[1].LineIds);
        Assert.All(plan, p => Assert.Null(p.DependsOnGroupKey));
        Assert.Contains("SP-1", plan[0].Title);
    }

    [Fact]
    public void Service_after_delivery_makes_the_service_job_depend_on_the_delivery_job_only_when_both_exist()
    {
        var both = JobGenerationPlanner.Plan("SP-1", Lines(), JobStrategy.PerKind, serviceAfterDelivery: true);
        var onlyServices = JobGenerationPlanner.Plan("SP-1", [new(Setup, "Kurulum", LineKinds.Service)], JobStrategy.PerKind, serviceAfterDelivery: true);

        Assert.Equal("kind:Product", both.Single(p => p.GroupKey == "kind:Service").DependsOnGroupKey);
        Assert.Null(Assert.Single(onlyServices).DependsOnGroupKey);
    }

    [Fact]
    public void Single_strategy_makes_one_job_and_labels_a_mixed_order_as_mixed()
    {
        var mixed = Assert.Single(JobGenerationPlanner.Plan("SP-1", Lines(), JobStrategy.Single, false));
        var products = Assert.Single(JobGenerationPlanner.Plan("SP-1", Lines().Take(2).ToList(), JobStrategy.Single, false));

        Assert.Equal(("all", JobKind.Mixed, 3), (mixed.GroupKey, mixed.Kind, mixed.LineIds.Count));
        Assert.Equal(JobKind.Delivery, products.Kind);
    }

    [Fact]
    public void Per_line_strategy_makes_a_job_per_line_with_a_stable_distinct_key()
    {
        var first = JobGenerationPlanner.Plan("SP-1", Lines(), JobStrategy.PerLine, false);
        var second = JobGenerationPlanner.Plan("SP-1", Lines(), JobStrategy.PerLine, false);

        Assert.Equal(3, first.Count);
        Assert.Equal(first.Select(p => p.GroupKey), second.Select(p => p.GroupKey));
        Assert.Equal(3, first.Select(p => p.GroupKey).Distinct().Count());
        Assert.Equal([JobKind.Delivery, JobKind.Delivery, JobKind.Service], first.Select(p => p.Kind));
    }

    [Fact]
    public void No_lines_means_no_jobs_and_every_kind_has_template_tasks()
    {
        Assert.Empty(JobGenerationPlanner.Plan("SP-1", [], JobStrategy.PerKind, false));
        Assert.All(JobKind.All, k => Assert.NotEmpty(OperationTemplates.For(k)));
        Assert.Equal(OperationTemplates.Delivery.Count + OperationTemplates.Service.Count, OperationTemplates.For(JobKind.Mixed).Count);
    }

    private static JobState State(string status, bool assigned = true, int deps = 0, int tasks = 0, string order = OrderStatus.Approved) => new(status, assigned, deps, tasks, order);

    [Fact]
    public void Starting_needs_an_assignee_completed_dependencies_and_an_approved_order()
    {
        Assert.True(JobTransitions.Can(JobAction.Start, State(JobStatus.Planned)).Allowed);
        Assert.Equal("unassigned", JobTransitions.Can(JobAction.Start, State(JobStatus.AwaitingAssignment, assigned: false)).Code);
        Assert.Equal("dependency", JobTransitions.Can(JobAction.Start, State(JobStatus.Planned, deps: 1)).Code);
        Assert.Equal("order-not-approved", JobTransitions.Can(JobAction.Start, State(JobStatus.Planned, order: OrderStatus.Cancelled)).Code);
        Assert.Equal("not-startable", JobTransitions.Can(JobAction.Start, State(JobStatus.InProgress)).Code);
        Assert.Equal("not-startable", JobTransitions.Can(JobAction.Start, State(JobStatus.Completed)).Code);
    }

    [Fact]
    public void Completing_needs_an_in_progress_job_with_all_required_tasks_done_and_cancelling_needs_an_open_job()
    {
        Assert.True(JobTransitions.Can(JobAction.Complete, State(JobStatus.InProgress)).Allowed);
        Assert.Equal("tasks", JobTransitions.Can(JobAction.Complete, State(JobStatus.InProgress, tasks: 2)).Code);
        Assert.Equal("not-in-progress", JobTransitions.Can(JobAction.Complete, State(JobStatus.Planned)).Code);
        Assert.All([JobStatus.AwaitingAssignment, JobStatus.Planned, JobStatus.InProgress], s => Assert.True(JobTransitions.Can(JobAction.Cancel, State(s)).Allowed));
        Assert.All([JobStatus.Completed, JobStatus.Cancelled], s => Assert.False(JobTransitions.Can(JobAction.Cancel, State(s)).Allowed));
        Assert.False(JobTransitions.Can(JobAction.Assign, State(JobStatus.Completed)).Allowed);
        Assert.True(JobTransitions.RequiresReason(JobAction.Cancel));
        Assert.False(JobTransitions.RequiresReason(JobAction.Start));
    }

    [Fact]
    public void Assignment_moves_a_waiting_job_to_planned_and_a_job_without_assignee_waits()
    {
        Assert.Equal(JobStatus.AwaitingAssignment, JobTransitions.InitialStatus(false));
        Assert.Equal(JobStatus.Planned, JobTransitions.InitialStatus(true));
        Assert.Equal(JobStatus.Planned, JobTransitions.StatusAfterAssign(JobStatus.AwaitingAssignment, true));
        Assert.Equal(JobStatus.InProgress, JobTransitions.StatusAfterAssign(JobStatus.InProgress, true));
    }

    [Fact]
    public void Dependencies_cannot_point_to_themselves_or_close_a_cycle()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var edges = new[] { (b, a), (c, b) }; // b a'yı, c b'yi bekler

        Assert.True(JobDependencyRules.WouldCycle(edges, a, a));
        Assert.True(JobDependencyRules.WouldCycle(edges, a, c)); // a → c → b → a
        Assert.True(JobDependencyRules.WouldCycle(edges, a, b));
        Assert.False(JobDependencyRules.WouldCycle(edges, c, a)); // c zaten dolaylı olarak a'yı bekler; döngü değil
        Assert.False(JobDependencyRules.WouldCycle(edges, Guid.NewGuid(), a));
    }
}
