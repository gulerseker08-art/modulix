using Modulix.Core.Contracts.Modules;
using Modulix.Modules.Logistics;
using Modulix.Modules.Logistics.Domain;
using Modulix.Modules.Logistics.Lifecycle;

namespace Modulix.Modules.Logistics.Tests;

/// <summary>ADR-0038 dilim 8: sefer masrafı beyanları (veritabanı gerekmez).</summary>
public class LogisticsExpenseModuleTests
{
    private static IModule Module { get; } = new LogisticsModule();

    [Fact]
    public void Expense_decisions_need_the_approve_permission_while_entry_and_resubmit_need_only_write()
    {
        var approve = LogisticsTransitions.Find(LogisticsModule.Entities.TripExpense, LogisticsActions.Approve)!;
        var reject = LogisticsTransitions.Find(LogisticsModule.Entities.TripExpense, LogisticsActions.Reject)!;
        var resubmit = LogisticsTransitions.Find(LogisticsModule.Entities.TripExpense, LogisticsActions.Resubmit)!;

        Assert.Equal(LogisticsModule.Permissions.ExpensesApprove, approve.Permission);
        Assert.Equal(LogisticsModule.Permissions.ExpensesApprove, reject.Permission);
        Assert.True(reject.RequiresReason);
        Assert.Equal(LogisticsModule.Permissions.ExpensesWrite, resubmit.Permission);
        Assert.Equal([TripExpense.Statuses.Rejected], resubmit.From);
        Assert.Equal(TripExpense.Statuses.Pending, resubmit.To);

        // Onaylanan masraf kapanır: hiçbir komut onaylı masrafı başka duruma geçirmez.
        Assert.DoesNotContain(LogisticsTransitions.All, t => t.EntityKey == LogisticsModule.Entities.TripExpense && t.From.Contains(TripExpense.Statuses.Approved));
    }

    [Fact]
    public void Only_the_manager_template_approves_expenses_and_operations_can_enter_them()
    {
        var operations = Module.RoleTemplates.Single(t => t.Key == "logistics.operations").Permissions;
        var manager = Module.RoleTemplates.Single(t => t.Key == "logistics.manager").Permissions;

        Assert.Contains(LogisticsModule.Permissions.ExpensesWrite, operations);
        Assert.DoesNotContain(LogisticsModule.Permissions.ExpensesApprove, operations);
        Assert.Contains(LogisticsModule.Permissions.ExpensesApprove, manager);
        Assert.All(Module.Permissions.Where(p => p.Key.StartsWith("logistics.expenses.", StringComparison.Ordinal)), p => Assert.StartsWith(LogisticsModule.ModuleKey + ".", p.Key));
    }

    [Fact]
    public void The_expense_type_list_has_a_category_for_every_value_and_the_fuel_category_exists()
    {
        var list = Assert.Single(Module.ChoiceLists, l => l.Key == LogisticsModule.Lists.ExpenseType);
        Assert.Equal(LogisticsModule.Entities.TripExpense, list.EntityKey);
        Assert.Contains(list.Categories, c => c.Key == LogisticsModule.ExpenseCategories.Fuel);
        Assert.Contains(list.Categories, c => c.Key == LogisticsModule.ExpenseCategories.Fine);
        Assert.All(list.Values, v => Assert.Contains(list.Categories, c => c.Key == v.Category));
        Assert.Contains(list.Values, v => v.Category == LogisticsModule.ExpenseCategories.Fuel);
    }

    [Fact]
    public void The_expense_is_an_approval_target_gated_by_the_approve_command_and_is_audited()
    {
        var target = Assert.Single(Module.ApprovalTargets);
        Assert.Equal(LogisticsModule.Entities.TripExpense, target.EntityKey);
        Assert.Equal("amount", target.AmountField);
        Assert.Equal("currency", target.CurrencyField);
        Assert.Contains(target.Commands, c => c.Key == LogisticsModule.ApprovalCommands.ApproveExpense);
        Assert.Contains(Module.AuditedEntities, a => a.EntityKey == LogisticsModule.Entities.TripExpense && a.ReadPermission == LogisticsModule.Permissions.ExpensesRead);
        Assert.Contains(Module.CustomizableEntities, e => e.EntityKey == LogisticsModule.Entities.TripExpense);
        Assert.All(TripExpense.Statuses.All, s => Assert.True(LogisticsModule.ExpenseStatusLabels.ContainsKey(s)));
    }
}
