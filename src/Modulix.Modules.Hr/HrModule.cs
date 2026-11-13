using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Credentials;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Hr.Domain;
using Modulix.Modules.Hr.Records;

namespace Modulix.Modules.Hr;

/// <summary>
/// İnsan Kaynakları (ADR-0042). Çalışan kartı (özlük) ortak kişi kaydının İK uzantısıdır; rehber ve organizasyon şeması aynı karttan okunur.
/// Dilim 1: çalışan kartı, rehber, organizasyon şeması. İzin, masraf, zimmet, işe giriş/çıkış ve kurum içi iletişim (duyuru, anket, blog,
/// galeri) sonraki dilimlerdedir (ADR-0042 bölüm 5).
/// </summary>
public sealed class HrModule : IModule
{
    public const string ModuleKey = "hr";

    public static class Permissions
    {
        public const string EmployeesRead = "hr.employees.read";
        public const string EmployeesWrite = "hr.employees.write";
        public const string EmployeesPersonal = "hr.employees.personal";
    }

    public static class Entities
    {
        public const string Employee = "hr.employee";
    }

    public static class Lists
    {
        public const string Department = "hr.department";
    }

    /// <summary>Özlük bilgileri (K4): rehberi gören herkes görmez; <see cref="Permissions.EmployeesPersonal"/> ister.</summary>
    public static IReadOnlyList<string> PersonalFields { get; } =
        ["nationalId", "birthDate", "homeAddress", "emergencyContactName", "emergencyContactPhone", "iban"];

    public string Key => ModuleKey;

    public string DisplayName => "İnsan Kaynakları";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.EmployeesRead, "Çalışanları, rehberi ve organizasyon şemasını görüntüler", "Çalışanlar"),
        new(Permissions.EmployeesWrite, "Çalışan kartı ekler ve düzenler", "Çalışanlar"),
        new(Permissions.EmployeesPersonal, "Özlük bilgilerini (T.C. kimlik no, doğum tarihi, adres, acil durum kişisi, IBAN) görür ve düzenler", "Özlük bilgileri"),
    ];

    /// <summary>Denetim kaydında görünen kayıt türleri (Y5); hassas alanların değeri denetim kaydında gizlenir.</summary>
    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(Entities.Employee, "Çalışan", Permissions.EmployeesRead, new Dictionary<string, string>
        {
            ["personId"] = "Kişi", ["employeeNumber"] = "Sicil numarası", ["userId"] = "Kullanıcı hesabı", ["jobTitle"] = "Unvan",
            ["departmentKey"] = "Departman", ["managerEmployeeId"] = "Yönetici", ["employmentType"] = "Çalışma şekli",
            ["workLocation"] = "Çalışma yeri", ["hireDate"] = "İşe giriş tarihi", ["terminationDate"] = "Ayrılış tarihi", ["status"] = "Durum",
            ["nationalId"] = "T.C. kimlik numarası", ["birthDate"] = "Doğum tarihi", ["homeAddress"] = "Ev adresi",
            ["emergencyContactName"] = "Acil durum kişisi", ["emergencyContactPhone"] = "Acil durum telefonu", ["iban"] = "IBAN", ["notes"] = "Notlar",
        }, "fullName")
        {
            References = new Dictionary<string, string> { ["personId"] = SharedEntities.Person, ["managerEmployeeId"] = Entities.Employee },
        },
    ];

    IReadOnlyList<SensitiveFields> IModule.SensitiveFields { get; } =
    [
        new(Entities.Employee, Permissions.EmployeesPersonal, PersonalFields),
    ];

    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Employee, "Çalışan"),
    ];

    /// <summary>Departman (Y9b): hazır değerler örnektir; firma kendi departmanlarını ekler. Kod departmanın adına bakmaz.</summary>
    IReadOnlyList<ChoiceListDefinition> IModule.ChoiceLists { get; } =
    [
        new(
            Lists.Department,
            "Departman",
            Entities.Employee,
            "departmentKey",
            Required: false,
            [],
            [
                new("Management", "Yönetim"),
                new("HumanResources", "İnsan Kaynakları"),
                new("Finance", "Finans ve Muhasebe"),
                new("Sales", "Satış ve Pazarlama"),
                new("Operations", "Operasyon"),
                new("IT", "Bilgi Teknolojileri"),
            ]),
    ];

    IReadOnlyList<LookupDefinition> IModule.Lookups => HrLookupDefinitions.All;

    /// <summary>Özlük belgeleri (iş sözleşmesi, sağlık raporu, diploma, sertifika...) Core süreli belgesidir (ADR-0038 K3); çalışan kartında yönetilir.</summary>
    IReadOnlyList<CredentialOwner> IModule.CredentialOwners { get; } =
    [
        new(Entities.Employee, CredentialCategories.Employee, "Çalışan", Permissions.EmployeesRead),
    ];

    /// <summary>Ortak kayıtlar (ADR-0006): çalışanın adı ve iletişim bilgisi kişi kaydındadır; kart yazma izniyle yeni kişi açılabilir.</summary>
    IReadOnlyList<SharedRecordAccess> IModule.SharedRecordAccess { get; } =
    [
        new(SharedEntities.Person, Permissions.EmployeesRead, Permissions.EmployeesWrite),
    ];

    /// <summary>Kişinin nerede kullanıldığı (silme ve birleştirme koruması).</summary>
    IReadOnlyList<SharedRecordReference> IModule.SharedRecordReferences { get; } =
    [
        new(SharedEntities.Person, Entities.Employee, "personId", "çalışan kartı"),
    ];

    /// <summary>Çalışan formunun varsayılan düzeni; firma Form tasarımı ekranından özelleştirir (ADR-0021).</summary>
    IReadOnlyList<FormDefinition> IModule.Forms => HrForms.All;

    /// <summary>Kural altyapısı yetenekleri (ADR-0027): çalışan eklendi/güncellendi olayları; durum, çalışma şekli ve tarihler (ör. işe girişe N gün kala).</summary>
    AutomationCapabilities IModule.Automation { get; } = new()
    {
        Entities =
        [
            new(Entities.Employee, "Çalışan", Permissions.EmployeesRead) { LabelField = "fullName" },
        ],
        Events =
        [
            new("hr.employee.created", Entities.Employee, DomainEventTypes.RecordCreated, "Çalışan eklendi"),
            new("hr.employee.updated", Entities.Employee, DomainEventTypes.RecordUpdated, "Çalışan güncellendi"),
        ],
        Fields =
        [
            new(Entities.Employee, "status", "Durum", AutomationFieldType.Choice)
            {
                Options = [new(Employee.Statuses.Active, "Çalışıyor"), new(Employee.Statuses.OnLeave, "İzinde"), new(Employee.Statuses.Terminated, "Ayrıldı")],
            },
            new(Entities.Employee, "employmentType", "Çalışma şekli", AutomationFieldType.Choice)
            {
                Options =
                [
                    new(Employee.EmploymentTypes.FullTime, "Tam zamanlı"), new(Employee.EmploymentTypes.PartTime, "Yarı zamanlı"),
                    new(Employee.EmploymentTypes.FixedTerm, "Belirli süreli"), new(Employee.EmploymentTypes.Intern, "Stajyer"),
                ],
            },
            new(Entities.Employee, "fullName", "Ad soyad", AutomationFieldType.Text),
            new(Entities.Employee, "jobTitle", "Unvan", AutomationFieldType.Text),
            new(Entities.Employee, "hireDate", "İşe giriş tarihi", AutomationFieldType.Date),
            new(Entities.Employee, "terminationDate", "Ayrılış tarihi", AutomationFieldType.Date),
        ],
    };

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "hr.specialist",
            "İK uzmanı",
            "Çalışan kartlarını ve özlük bilgilerini görüntüler, ekler ve düzenler.",
            [Permissions.EmployeesRead, Permissions.EmployeesWrite, Permissions.EmployeesPersonal]),
        new(
            "hr.staff",
            "Çalışan (rehber)",
            "Rehberi ve organizasyon şemasını görüntüler; özlük bilgilerini göremez.",
            [Permissions.EmployeesRead]),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<HrStoreContext>();
        services.AddScoped<IRecordStore, EmployeeRecordStore>();
    }
}
