using Modulix.Core.Contracts.Modules;
using Modulix.Modules.Logistics;
using Modulix.Modules.Logistics.Domain;
using Modulix.Modules.Logistics.Lifecycle;
using Modulix.Shared.Logistics;

namespace Modulix.Modules.Logistics.Tests;

/// <summary>ADR-0038 dilim 8: sefer masrafı beyanları, geçiş tablosu, onay hedefi ve rol dağılımı (veritabanı gerekmez).</summary>
public class LogisticsExpenseModuleTests
{
    private static IModule Module { get; } = new LogisticsModule();

    [Fact]
    public void Expense_kind_list_has_the_five_categories_and_a_non_fuel_default()
    {
        var list = Assert.Single(Module.ChoiceLists, l => l.Key == LogisticsModule.Lists.ExpenseKind);
        Assert.Equal(LogisticsModule.Entities.TripExpense, list.EntityKey);
        Assert.Equal(
            [LogisticsModule.ExpenseKindCategories.Fuel, LogisticsModule.ExpenseKindCategories.Toll, LogisticsModule.ExpenseKindCategories.Allowance,
                LogisticsModule.ExpenseKindCategories.Fine, LogisticsModule.ExpenseKindCategories.Other],
            list.Categories.Select(c => c.Key));
        Assert.All(list.Values, v => Assert.Contains(list.Categories, c => c.Key == v.Category));
        Assert.Contains(list.Values, v => v.Category == LogisticsModule.ExpenseKindCategories.Fuel);

        // Varsayılan yakıt değildir: yakıt ek alan (litre) ister, bilinçli seçilmelidir.
        var defaultValue = list.Values.Single(v => v.Key == list.Default);
        Assert.NotEqual(LogisticsModule.ExpenseKindCategories.Fuel, defaultValue.Category);
    }

    [Fact]
    public void Only_the_manager_role_template_can_approve_expenses_and_operations_can_enter_them()
    {
        Assert.Contains(Module.Permissions, p => p.Key == LogisticsModule.Permissions.ExpensesApprove);
        Assert.Contains(Module.RoleTemplates, r => r.Key == "logistics.manager" && r.Permissions.Contains(LogisticsModule.Permissions.ExpensesApprove));
        var operations = Assert.Single(Module.RoleTemplates, r => r.Key == "logistics.operations");
        Assert.Contains(LogisticsModule.Permissions.ExpensesRead, operations.Permissions);
        Assert.Contains(LogisticsModule.Permissions.ExpensesWrite, operations.Permissions);
        Assert.DoesNotContain(LogisticsModule.Permissions.ExpensesApprove, operations.Permissions);
    }

    [Fact]
    public void Expense_decisions_need_the_approve_permission_start_from_pending_and_rejection_needs_a_reason()
    {
        var approve = LogisticsTransitions.Find(LogisticsModule.Entities.TripExpense, LogisticsActions.Approve)!;
        var reject = LogisticsTransitions.Find(LogisticsModule.Entities.TripExpense, LogisticsActions.Reject)!;
        Assert.All(new[] { approve, reject }, t =>
        {
            Assert.Equal(LogisticsModule.Permissions.ExpensesApprove, t.Permission);
            Assert.Equal([TripExpense.Statuses.Pending], t.From);
        });
        Assert.Equal(TripExpense.Statuses.Approved, approve.To);
        Assert.False(approve.RequiresReason);
        Assert.Equal(TripExpense.Statuses.Rejected, reject.To);
        Assert.True(reject.RequiresReason);

        // Masraf iptal edilmez ve yeniden açılmaz: yanlış masraf reddedilir (K12).
        Assert.Null(LogisticsTransitions.Find(LogisticsModule.Entities.TripExpense, LogisticsActions.Cancel));
        Assert.Null(LogisticsTransitions.Find(LogisticsModule.Entities.TripExpense, LogisticsActions.Reopen));
    }

    [Fact]
    public void Expense_is_an_approval_target_whose_amount_comes_from_the_record_and_approve_command_can_be_gated()
    {
        var target = Assert.Single(Module.ApprovalTargets);
        Assert.Equal(LogisticsModule.Entities.TripExpense, target.EntityKey);
        Assert.Equal("amount", target.AmountField);
        Assert.Equal("currency", target.CurrencyField);
        Assert.Equal("description", target.LabelField);
        Assert.Contains(target.Commands, c => c.Key == LogisticsModule.ApprovalCommands.ApproveExpense);
    }

    [Fact]
    public void Expense_automation_supports_record_events_status_and_amount_fields_and_the_audit_labels_exist()
    {
        var automation = Module.Automation;
        Assert.Contains(automation.Entities, e => e.EntityKey == LogisticsModule.Entities.TripExpense);
        Assert.Contains(automation.Events, e => e.Key == "logistics.trip-expense.created");
        foreach (var key in new[] { "status", "amount", "currency", "kindKey" })
        {
            Assert.Contains(automation.Fields, f => f.EntityKey == LogisticsModule.Entities.TripExpense && f.Name == key);
        }

        var audited = Assert.Single(Module.AuditedEntities, e => e.EntityKey == LogisticsModule.Entities.TripExpense);
        Assert.Equal(LogisticsModule.Permissions.ExpensesRead, audited.ReadPermission);
        Assert.Equal("Tutar", audited.Fields["amount"]);
    }

    [Fact]
    public void Expense_list_resolves_trip_numbers_and_supplier_names_and_the_approval_link_opens_the_expense_list()
    {
        var trip = Assert.Single(Module.Lookups, l => l.Key == LogisticsLookupDefinitions.ExpenseTrip);
        Assert.Equal(LogisticsModule.Entities.TripExpense, trip.SourceEntityKey);
        Assert.Equal("tripId", trip.Field);
        Assert.Equal(LogisticsModule.Entities.Trip, trip.TargetEntityKey);
        Assert.Equal(["number"], trip.LabelFields);
        Assert.Equal(LogisticsAccess.ExpenseTripLookup, trip.Key);

        var supplier = Assert.Single(Module.Lookups, l => l.Key == LogisticsLookupDefinitions.ExpenseSupplier);
        Assert.Equal("supplierPartyId", supplier.Field);
        Assert.Equal(LogisticsAccess.ExpenseSupplierLookup, supplier.Key);

        // Onay merkezi bağlantısı masraf listesine gider (masraf başına sayfa yoktur).
        Assert.Equal("lojistik/masraflar", Assert.Single(Module.ApprovalTargets).LinkTemplate);
    }

    [Fact]
    public void Shared_access_constants_match_the_server_module()
    {
        Assert.Equal(LogisticsModule.Permissions.ExpensesRead, LogisticsAccess.ExpensesRead);
        Assert.Equal(LogisticsModule.Permissions.ExpensesWrite, LogisticsAccess.ExpensesWrite);
        Assert.Equal(LogisticsModule.Permissions.ExpensesApprove, LogisticsAccess.ExpensesApprove);
        Assert.Equal(LogisticsModule.Entities.TripExpense, LogisticsAccess.TripExpenseEntityKey);
        Assert.Equal(LogisticsModule.Lists.ExpenseKind, LogisticsAccess.ExpenseKindList);
        Assert.Equal(LogisticsLookupDefinitions.ExpenseTrip, LogisticsAccess.ExpenseTripLookup);
        Assert.Equal(LogisticsLookupDefinitions.ExpenseSupplier, LogisticsAccess.ExpenseSupplierLookup);
        Assert.Equal(TripExpense.Statuses.All, LogisticsAccess.ExpenseStatuses.Select(s => s.Key));
    }
}
