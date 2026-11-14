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
        Assert.Null(Catalog.FindImport(HrModule.Entities.Employee)); // kişi kaydı gerektirir; Core kararı (bölüm 19)

        // Envanter no ve tatil günü kesin eşleşmedir; izin hakkı çalışana sicil numarasıyla bağlanır.
        Assert.Equal([new ImportMatchRule("assetTag", null, Exact: true)], asset.MatchRules);
        Assert.Equal([new ImportMatchRule("date", null, Exact: true)], holiday.MatchRules);
        var employee = Assert.Single(balance.Fields, f => f.Key == "employeeId");
        Assert.Equal((ImportFieldType.Reference, true, HrModule.Entities.Employee), (employee.Type, employee.Required, employee.Reference!.EntityKey));
        Assert.Equal(["employeeNumber"], employee.Reference.Filters);
        Assert.Equal(["assetTag", "name"], asset.Fields.Where(f => f.Required).Select(f => f.Key));
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
