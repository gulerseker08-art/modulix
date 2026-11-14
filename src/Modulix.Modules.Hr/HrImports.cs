using Modulix.Core.Contracts.Modules;

namespace Modulix.Modules.Hr;

/// <summary>
/// İK'nın içe aktarma tanımları (ADR-0033; ADR-0042 bölüm 19 ve 21). Sütunlar elle girilebilen alanlardır; zimmet gibi iş akışı alanları dışarıdadır
/// (yeni demirbaş depoda başlar). Sıra: önce kişiler (Core, <c>core.people.import</c>), sonra çalışan kartları (kişi e-postayla, yönetici sicil numarasıyla
/// bağlanır; yöneticiler dosyada astlarından önce gelmelidir), sonra izin hakları (sicil numarasıyla).
/// </summary>
internal static class HrImports
{
    private static readonly ImportReference EmployeeByNumber = new(HrModule.Entities.Employee, ["employeeNumber"]);

    public static IReadOnlyList<ImportDefinition> All { get; } =
    [
        new(HrModule.Entities.Employee, "Çalışan kartı", HrModule.Permissions.EmployeesWrite,
        [
            new("personId", "Kişi e-postası", ImportFieldType.Reference, Required: true) { Reference = new("core.person", ["email"]) },
            new("employeeNumber", "Sicil no"),
            new("hireDate", "İşe giriş tarihi", ImportFieldType.Date, Required: true),
            new("jobTitle", "Unvan"),
            new("departmentKey", "Departman", ImportFieldType.Choice) { ChoiceListKey = HrModule.Lists.Department },
            new("managerEmployeeId", "Yönetici sicil no", ImportFieldType.Reference) { Reference = EmployeeByNumber },
            new("employmentType", "Çalışma şekli", ImportFieldType.Choice)
            {
                Options =
                [
                    new(Domain.Employee.EmploymentTypes.FullTime, "Tam zamanlı"), new(Domain.Employee.EmploymentTypes.PartTime, "Yarı zamanlı"),
                    new(Domain.Employee.EmploymentTypes.FixedTerm, "Belirli süreli"), new(Domain.Employee.EmploymentTypes.Intern, "Stajyer"),
                ],
            },
            new("workLocation", "Çalışma yeri"),
            new("userId", "Kullanıcı hesabı", ImportFieldType.User),
            new("status", "Durum", ImportFieldType.Choice)
            {
                Options =
                [
                    new(Domain.Employee.Statuses.Active, "Çalışıyor"), new(Domain.Employee.Statuses.OnLeave, "İzinde"),
                    new(Domain.Employee.Statuses.Terminated, "Ayrıldı"),
                ],
            },
            new("terminationDate", "Ayrılış tarihi", ImportFieldType.Date),

            // Özlük alanları hassastır: içe aktarıcının özlük izni yoksa dolu hücre satır hatası olur (kayıt boru hattı reddeder).
            new("nationalId", "T.C. kimlik no"),
            new("birthDate", "Doğum tarihi", ImportFieldType.Date),
            new("iban", "IBAN"),
            new("notes", "Notlar"),
        ])
        {
            MatchRules = [new("employeeNumber", null, Exact: true), new("personId", null, Exact: true)],
        },
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
