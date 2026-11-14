using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Modules;
using Modulix.Modules.Hr.Records;

namespace Modulix.Modules.Hr.Tests;

/// <summary>İK içe aktarma tanımları (ADR-0042 bölüm 19). Katalog açılışta tanımı doğrular; hatalı tanım uygulamayı açtırmaz.</summary>
public class HrImportTests
{
    private static readonly ModuleCatalog Catalog = ModuleCatalog.Create(new HrModule());

    [Fact]
    public void The_real_catalog_registers_asset_leave_balance_and_holiday_imports_with_the_module_permissions()
    {
        var asset = Catalog.FindImport(HrModule.Entities.Asset)!.Value.Definition;
        var balance = Catalog.FindImport(HrModule.Entities.LeaveBalance)!.Value.Definition;
        var holiday = Catalog.FindImport(HrModule.Entities.Holiday)!.Value.Definition;

        Assert.Equal(HrModule.Permissions.AssetsManage, asset.CreatePermission);
        Assert.Equal(HrModule.Permissions.LeavesManage, balance.CreatePermission);
        Assert.Equal(HrModule.Permissions.LeavesManage, holiday.CreatePermission);

        // Envanter no ve tatil günü kesin eşleşmedir; izin hakkı çalışana sicil numarasıyla bağlanır.
        Assert.Equal([new ImportMatchRule("assetTag", null, Exact: true)], asset.MatchRules);
        Assert.Equal([new ImportMatchRule("date", null, Exact: true)], holiday.MatchRules);
        var employee = Assert.Single(balance.Fields, f => f.Key == "employeeId");
        Assert.Equal((ImportFieldType.Reference, true, HrModule.Entities.Employee), (employee.Type, employee.Required, employee.Reference!.EntityKey));
        Assert.Equal(["employeeNumber"], employee.Reference.Filters);
        Assert.Equal(["assetTag", "name"], asset.Fields.Where(f => f.Required).Select(f => f.Key));
    }

    [Fact]
    public void Employees_link_to_people_by_email_and_managers_by_employee_number_after_core_people_are_imported()
    {
        var employee = Catalog.FindImport(HrModule.Entities.Employee)!.Value.Definition;
        var people = Catalog.FindImport("core.person")!.Value;

        Assert.Equal(HrModule.Permissions.EmployeesWrite, employee.CreatePermission);
        var person = Assert.Single(employee.Fields, f => f.Key == "personId");
        Assert.Equal((true, "core.person"), (person.Required, person.Reference!.EntityKey));
        Assert.Equal(["email"], person.Reference.Filters);
        var manager = Assert.Single(employee.Fields, f => f.Key == "managerEmployeeId");
        Assert.Equal(HrModule.Entities.Employee, manager.Reference!.EntityKey);
        Assert.Equal(["employeeNumber"], manager.Reference.Filters);
        Assert.Equal(["employeeNumber", "personId"], employee.MatchRules.Where(r => r.Exact).Select(r => r.Field));

        // Kişi içe aktarma Core'un ayrı iznidir (ADR-0042 bölüm 21); e-posta olası mükerrerdir, kişiler birleştirilmez.
        Assert.Equal(("core", "core.people.import"), (people.ModuleKey, people.Definition.CreatePermission));
        Assert.Equal(["firstName", "lastName"], people.Definition.Fields.Where(f => f.Required).Select(f => f.Key));
        Assert.Equal([new ImportMatchRule("email", "email")], people.Definition.MatchRules);
    }

    [Theory]
    [InlineData(2026, true)]
    [InlineData(2026.0, true)]
    [InlineData(2026.5, false)]
    [InlineData(1999, false)]
    public void Integer_fields_accept_whole_numbers_given_as_decimals_by_the_importer(double value, bool valid)
    {
        var field = Field.Integer<object>("year", _ => null, (_, _) => { }, 2000, 2100);

        Assert.Equal(valid, field.Parse(JsonValue.Create((decimal)value)).Error is null);
        Assert.Null(field.Parse(JsonValue.Create(2026)).Error);
    }
}
