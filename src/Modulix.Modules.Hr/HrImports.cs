using Modulix.Core.Contracts.Modules;

namespace Modulix.Modules.Hr;

/// <summary>
/// İK'nın içe aktarma tanımları (ADR-0033; ADR-0042 bölüm 19). Sütunlar elle girilebilen alanlardır; durum ve zimmet gibi iş akışı alanları dışarıdadır
/// (yeni demirbaş depoda başlar). İzin hakkı çalışana sicil numarasıyla bağlanır: önce çalışan kartları (formdan), sonra haklar yüklenir. Çalışan kartının
/// toplu içe aktarılması kişi kaydını da açmayı gerektirdiğinden Core kararına bırakıldı (bölüm 19).
/// </summary>
internal static class HrImports
{
    private static readonly ImportReference EmployeeByNumber = new(HrModule.Entities.Employee, ["employeeNumber"]);

    public static IReadOnlyList<ImportDefinition> All { get; } =
    [
        new(HrModule.Entities.Asset, "Demirbaş", HrModule.Permissions.AssetsManage,
        [
            new("assetTag", "Envanter no", Required: true),
            new("name", "Ad", Required: true),
            new("categoryKey", "Kategori", ImportFieldType.Choice) { ChoiceListKey = HrModule.Lists.AssetCategory },
            new("serialNumber", "Seri no"),
            new("brand", "Marka"),
            new("model", "Model"),
            new("purchaseDate", "Alış tarihi", ImportFieldType.Date),
            new("purchaseCost", "Alış bedeli", ImportFieldType.Decimal),
            new("currency", "Para birimi"),
            new("notes", "Notlar"),
        ])
        {
            MatchRules = [new("assetTag", null, Exact: true)],
        },
        new(HrModule.Entities.LeaveBalance, "İzin hakkı", HrModule.Permissions.LeavesManage,
        [
            new("employeeId", "Sicil no", ImportFieldType.Reference, Required: true) { Reference = EmployeeByNumber },
            new("year", "Yıl", ImportFieldType.Decimal, Required: true),
            new("entitledDays", "Hak (gün)", ImportFieldType.Decimal, Required: true),
            new("carriedOverDays", "Devreden (gün)", ImportFieldType.Decimal),
            new("notes", "Notlar"),
        ]),
        new(HrModule.Entities.Holiday, "Resmî tatil", HrModule.Permissions.LeavesManage,
        [
            new("date", "Tarih", ImportFieldType.Date, Required: true),
            new("name", "Ad", Required: true),
            new("halfDay", "Yarım gün", ImportFieldType.YesNo),
        ])
        {
            MatchRules = [new("date", null, Exact: true)],
        },
    ];
}
