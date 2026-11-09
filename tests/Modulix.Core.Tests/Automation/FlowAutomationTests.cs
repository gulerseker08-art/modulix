using System.Text.Json.Nodes;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Modules;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

/// <summary>Kalıcı akışın bekleme adımları (ADR-0027, Dilim 4c-4): beyan, süre kuralları, okunur süre.</summary>
public class FlowAutomationTests
{
    [Fact]
    public void Core_declares_the_wait_steps_as_side_effect_free_actions()
    {
        var catalog = Catalog();

        var wait = catalog.FindAutomationAction(FlowAutomation.WaitAction)!.Value;
        var approval = catalog.FindAutomationAction(FlowAutomation.AwaitApprovalAction)!.Value;

        Assert.Equal(CoreModule.ModuleKey, wait.ModuleKey);
        Assert.Equal(AutomationSideEffect.Read, wait.Action.Definition.SideEffect);
        Assert.Empty(wait.Action.Definition.RequiredPermissions);
        Assert.All(wait.Action.Definition.Parameters, p => Assert.False(p.AllowFieldBinding));

        // Onay durumunu okuyan adım, talep açma izniyle aynı izni ister (yayınlayan onay sürecini görebilmeli).
        Assert.Equal([CoreModule.Permissions.RequestApprovals], approval.Action.Definition.RequiredPermissions);
        Assert.Equal(AutomationSideEffect.Read, approval.Action.Definition.SideEffect);
    }

    [Theory]
    [InlineData(2, 0, 48)]
    [InlineData(0, 5, 5)]
    [InlineData("1", "3", 27)]
    [InlineData(365, 23, 365 * 24 + 23)]
    public void Wait_duration_adds_days_and_hours(object days, object hours, int expectedHours)
    {
        var parameters = new JsonObject { ["days"] = JsonValue.Create(days), ["hours"] = JsonValue.Create(hours) };

        var (delay, error) = FlowAutomation.WaitDuration(parameters);

        Assert.Null(error);
        Assert.Equal(TimeSpan.FromHours(expectedHours), delay);
    }

    [Theory]
    [InlineData(0, 0, "sıfırdan büyük")]
    [InlineData(366, 0, "'Gün'")]
    [InlineData(-1, 0, "'Gün'")]
    [InlineData(0, 24, "'Saat'")]
    [InlineData("iki", 0, "'Gün'")]
    public void Wait_duration_rejects_empty_or_out_of_range_values(object days, object hours, string message)
    {
        var parameters = new JsonObject { ["days"] = JsonValue.Create(days), ["hours"] = JsonValue.Create(hours) };

        var (delay, error) = FlowAutomation.WaitDuration(parameters);

        Assert.Null(delay);
        Assert.Contains(message, error);
    }

    [Fact]
    public void Approval_timeout_defaults_to_thirty_days_and_is_bounded()
    {
        Assert.Equal(FlowAutomation.DefaultApprovalTimeoutDays, FlowAutomation.ApprovalTimeout([]).Days);
        Assert.Equal(FlowAutomation.DefaultApprovalTimeoutDays, FlowAutomation.ApprovalTimeout(new JsonObject { ["timeoutDays"] = "" }).Days);
        Assert.Equal(7, FlowAutomation.ApprovalTimeout(new JsonObject { ["timeoutDays"] = 7 }).Days);
        Assert.NotNull(FlowAutomation.ApprovalTimeout(new JsonObject { ["timeoutDays"] = 0 }).Error);
        Assert.NotNull(FlowAutomation.ApprovalTimeout(new JsonObject { ["timeoutDays"] = FlowAutomation.MaxApprovalTimeoutDays + 1 }).Error);
    }

    [Fact]
    public void Durations_read_naturally()
    {
        Assert.Equal("3 gün 4 saat", FlowAutomation.Describe(TimeSpan.FromHours(76)));
        Assert.Equal("2 saat", FlowAutomation.Describe(TimeSpan.FromHours(2)));
        Assert.Equal("1 gün", FlowAutomation.Describe(TimeSpan.FromDays(1)));
    }
}
