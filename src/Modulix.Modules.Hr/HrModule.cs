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
        public const string LeavesRead = "hr.leaves.read";
        public const string LeavesRequest = "hr.leaves.request";
        public const string LeavesApprove = "hr.leaves.approve";
        public const string LeavesManage = "hr.leaves.manage";
        public const string ExpensesRead = "hr.expenses.read";
        public const string ExpensesRequest = "hr.expenses.request";
        public const string ExpensesApprove = "hr.expenses.approve";
        public const string ExpensesManage = "hr.expenses.manage";
        public const string ExpensesPay = "hr.expenses.pay";
    }

    public static class Entities
    {
        public const string Employee = "hr.employee";
        public const string LeaveRequest = "hr.leave-request";
        public const string LeaveBalance = "hr.leave-balance";
        public const string Holiday = "hr.holiday";
        public const string ExpenseClaim = "hr.expense-claim";
        public const string ExpenseLine = "hr.expense-line";
    }

    public static class Lists
    {
        public const string Department = "hr.department";
        public const string LeaveType = "hr.leave-type";
        public const string ExpenseType = "hr.expense-type";
    }

    /// <summary>Masraf türü kategorileri (dilim 3): raporlar kategoriye bakar; vergi ve harcırah sınırları koda yazılmaz (ADR-0042 bölüm 8).</summary>
    public static class ExpenseCategories
    {
        public const string Transport = "transport";
        public const string Accommodation = "accommodation";
        public const string Meal = "meal";
        public const string Other = "other";
    }

    /// <summary>
    /// İzin türü kategorileri (dilim 2): kod türün adına değil kategorisine bakar. Yalnızca <see cref="Annual"/> yıllık izin hakkından düşer; diğerleri
    /// takvimde yer tutar ve onaya gider. Türlerin yasal süreleri koda yazılmaz (ADR-0042 bölüm 8).
    /// </summary>
    public static class LeaveCategories
    {
        public const string Annual = "annual";
        public const string Excuse = "excuse";
        public const string Sick = "sick";
        public const string Unpaid = "unpaid";
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
        new(Permissions.LeavesRead, "İzin taleplerini, izin haklarını ve resmî tatilleri görüntüler (kayıt kapsamına göre)", "İzinler"),
        new(Permissions.LeavesRequest, "Kendi adına izin talebi açar ve bekleyen talebini iptal eder", "İzinler"),
        new(Permissions.LeavesApprove, "Onaylayanı olduğu izin taleplerini onaylar ve reddeder", "İzin onayı"),
        new(Permissions.LeavesManage, "Herkes adına talep açar, her talebi onaylar ve iptal eder; izin haklarını ve resmî tatilleri yönetir", "İzin yönetimi"),
        new(Permissions.ExpensesRead, "Masraf beyanlarını görüntüler (kayıt kapsamına göre)", "Masraflar"),
        new(Permissions.ExpensesRequest, "Kendi adına masraf beyanı açar, satır ve fiş ekler, onaya gönderir", "Masraflar"),
        new(Permissions.ExpensesApprove, "Onaylayanı olduğu masraf beyanlarını onaylar, iade eder ve reddeder", "Masraf onayı"),
        new(Permissions.ExpensesManage, "Herkes adına beyan açar ve düzenler, her beyana karar verir ve iptal eder", "Masraf yönetimi"),
        new(Permissions.ExpensesPay, "Onaylanmış masraf beyanlarını ödendi olarak işaretler", "Masraf ödemesi"),
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
        new(Entities.LeaveRequest, "İzin talebi", Permissions.LeavesRead, new Dictionary<string, string>
        {
            ["employeeId"] = "Çalışan", ["leaveTypeKey"] = "İzin türü", ["startDate"] = "Başlangıç", ["endDate"] = "Bitiş", ["halfDay"] = "Yarım gün",
            ["days"] = "Gün", ["description"] = "Açıklama", ["status"] = "Durum", ["approverUserId"] = "Onaylayan",
            ["decidedByUserId"] = "Karar veren", ["decisionReason"] = "Gerekçe",
        }, "employeeName")
        {
            References = new Dictionary<string, string> { ["employeeId"] = Entities.Employee },
            UserFields = ["approverUserId", "decidedByUserId", "employeeUserId"],
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string>
                {
                    [Domain.LeaveRequest.Statuses.Pending] = "Onay bekliyor", [Domain.LeaveRequest.Statuses.Approved] = "Onaylandı",
                    [Domain.LeaveRequest.Statuses.Rejected] = "Reddedildi", [Domain.LeaveRequest.Statuses.Cancelled] = "İptal edildi",
                },
            },
        },
        new(Entities.LeaveBalance, "İzin hakkı", Permissions.LeavesRead, new Dictionary<string, string>
        {
            ["employeeId"] = "Çalışan", ["year"] = "Yıl", ["entitledDays"] = "Hak (gün)", ["carriedOverDays"] = "Devreden (gün)",
            ["usedDays"] = "Kullanılan (gün)", ["notes"] = "Notlar",
        }, "employeeName")
        {
            References = new Dictionary<string, string> { ["employeeId"] = Entities.Employee },
        },
        new(Entities.ExpenseClaim, "Masraf beyanı", Permissions.ExpensesRead, new Dictionary<string, string>
        {
            ["employeeId"] = "Çalışan", ["title"] = "Başlık", ["currency"] = "Para birimi", ["description"] = "Açıklama", ["totalAmount"] = "Toplam",
            ["lineCount"] = "Satır sayısı", ["status"] = "Durum", ["approverUserId"] = "Onaylayan", ["decidedByUserId"] = "Karar veren",
            ["decisionReason"] = "Gerekçe", ["paidByUserId"] = "Ödemeyi işaretleyen", ["paymentReference"] = "Ödeme referansı",
        }, "title")
        {
            References = new Dictionary<string, string> { ["employeeId"] = Entities.Employee },
            UserFields = ["approverUserId", "decidedByUserId", "paidByUserId", "employeeUserId"],
            Options = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["status"] = new Dictionary<string, string>
                {
                    [Domain.ExpenseClaim.Statuses.Draft] = "Taslak", [Domain.ExpenseClaim.Statuses.Submitted] = "Onay bekliyor",
                    [Domain.ExpenseClaim.Statuses.Approved] = "Onaylandı", [Domain.ExpenseClaim.Statuses.Paid] = "Ödendi",
                    [Domain.ExpenseClaim.Statuses.Rejected] = "Reddedildi", [Domain.ExpenseClaim.Statuses.Cancelled] = "İptal edildi",
                },
            },
        },
        new(Entities.ExpenseLine, "Masraf satırı", Permissions.ExpensesRead, new Dictionary<string, string>
        {
            ["claimId"] = "Masraf beyanı", ["expenseDate"] = "Tarih", ["expenseTypeKey"] = "Masraf türü", ["description"] = "Açıklama",
            ["amount"] = "Tutar (KDV dahil)", ["vatAmount"] = "KDV",
        }, "description")
        {
            References = new Dictionary<string, string> { ["claimId"] = Entities.ExpenseClaim },
        },
        new(Entities.Holiday, "Resmî tatil", Permissions.LeavesRead, new Dictionary<string, string>
        {
            ["date"] = "Tarih", ["name"] = "Ad", ["halfDay"] = "Yarım gün",
        }, "name"),
    ];

    IReadOnlyList<SensitiveFields> IModule.SensitiveFields { get; } =
    [
        new(Entities.Employee, Permissions.EmployeesPersonal, PersonalFields),
    ];

    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Employee, "Çalışan"),
        new(Entities.LeaveRequest, "İzin talebi"),
        new(Entities.LeaveBalance, "İzin hakkı"),
        new(Entities.ExpenseClaim, "Masraf beyanı"),
        new(Entities.ExpenseLine, "Masraf satırı"),
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

        // İzin türü (Y9b): hazır değerler örnektir, firma ekler ve adlandırır. Kategori bakiye davranışını belirler; yasal süreler koda yazılmaz.
        new(
            Lists.LeaveType,
            "İzin türü",
            Entities.LeaveRequest,
            "leaveTypeKey",
            Required: true,
            [
                new(LeaveCategories.Annual, "Yıllık izin (haktan düşer)"),
                new(LeaveCategories.Excuse, "Ücretli izin (haktan düşmez)"),
                new(LeaveCategories.Sick, "Hastalık / rapor"),
                new(LeaveCategories.Unpaid, "Ücretsiz izin"),
            ],
            [
                new("Annual", "Yıllık izin", LeaveCategories.Annual),
                new("Excuse", "Mazeret izni", LeaveCategories.Excuse),
                new("Marriage", "Evlilik izni", LeaveCategories.Excuse),
                new("Bereavement", "Ölüm izni", LeaveCategories.Excuse),
                new("Paternity", "Babalık izni", LeaveCategories.Excuse),
                new("Maternity", "Doğum izni", LeaveCategories.Excuse),
                new("Administrative", "İdari izin", LeaveCategories.Excuse),
                new("Sick", "Hastalık izni (rapor)", LeaveCategories.Sick),
                new("Unpaid", "Ücretsiz izin", LeaveCategories.Unpaid),
            ])
        {
            Default = "Annual",
        },

        // Masraf türü (Y9b): hazır değerler örnektir; firma ekler. Kategori raporlama içindir; vergi/harcırah kuralı koda yazılmaz.
        new(
            Lists.ExpenseType,
            "Masraf türü",
            Entities.ExpenseLine,
            "expenseTypeKey",
            Required: true,
            [
                new(ExpenseCategories.Transport, "Ulaşım"),
                new(ExpenseCategories.Accommodation, "Konaklama"),
                new(ExpenseCategories.Meal, "Yemek ve ağırlama"),
                new(ExpenseCategories.Other, "Diğer"),
            ],
            [
                new("Taxi", "Taksi", ExpenseCategories.Transport),
                new("PublicTransport", "Toplu taşıma", ExpenseCategories.Transport),
                new("Flight", "Uçak", ExpenseCategories.Transport),
                new("Fuel", "Yakıt", ExpenseCategories.Transport),
                new("Parking", "Otopark ve köprü/otoyol", ExpenseCategories.Transport),
                new("Hotel", "Otel", ExpenseCategories.Accommodation),
                new("Meal", "Yemek", ExpenseCategories.Meal),
                new("Hospitality", "Müşteri ağırlama", ExpenseCategories.Meal),
                new("Office", "Kırtasiye ve ofis", ExpenseCategories.Other),
                new("Other", "Diğer", ExpenseCategories.Other),
            ])
        {
            Default = "Taxi",
        },
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
            new(Entities.LeaveRequest, "İzin talebi", Permissions.LeavesRead) { LabelField = "employeeName" },
            new(Entities.ExpenseClaim, "Masraf beyanı", Permissions.ExpensesRead) { LabelField = "title" },
        ],
        Events =
        [
            new("hr.employee.created", Entities.Employee, DomainEventTypes.RecordCreated, "Çalışan eklendi"),
            new("hr.employee.updated", Entities.Employee, DomainEventTypes.RecordUpdated, "Çalışan güncellendi"),
            new("hr.leave-request.created", Entities.LeaveRequest, DomainEventTypes.RecordCreated, "İzin talebi açıldı"),
            new("hr.leave-request.updated", Entities.LeaveRequest, DomainEventTypes.RecordUpdated, "İzin talebinin durumu değişti"),
            new("hr.expense-claim.created", Entities.ExpenseClaim, DomainEventTypes.RecordCreated, "Masraf beyanı açıldı"),
            new("hr.expense-claim.updated", Entities.ExpenseClaim, DomainEventTypes.RecordUpdated, "Masraf beyanı güncellendi"),
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
            new(Entities.LeaveRequest, "employeeName", "Çalışan", AutomationFieldType.Text),
            new(Entities.LeaveRequest, "status", "Durum", AutomationFieldType.Choice)
            {
                Options =
                [
                    new(Domain.LeaveRequest.Statuses.Pending, "Onay bekliyor"), new(Domain.LeaveRequest.Statuses.Approved, "Onaylandı"),
                    new(Domain.LeaveRequest.Statuses.Rejected, "Reddedildi"), new(Domain.LeaveRequest.Statuses.Cancelled, "İptal edildi"),
                ],
            },
            new(Entities.LeaveRequest, "startDate", "Başlangıç", AutomationFieldType.Date),
            new(Entities.LeaveRequest, "days", "Gün", AutomationFieldType.Number),
            new(Entities.ExpenseClaim, "title", "Başlık", AutomationFieldType.Text),
            new(Entities.ExpenseClaim, "status", "Durum", AutomationFieldType.Choice)
            {
                Options =
                [
                    new(Domain.ExpenseClaim.Statuses.Draft, "Taslak"), new(Domain.ExpenseClaim.Statuses.Submitted, "Onay bekliyor"),
                    new(Domain.ExpenseClaim.Statuses.Approved, "Onaylandı"), new(Domain.ExpenseClaim.Statuses.Paid, "Ödendi"),
                    new(Domain.ExpenseClaim.Statuses.Rejected, "Reddedildi"), new(Domain.ExpenseClaim.Statuses.Cancelled, "İptal edildi"),
                ],
            },
            new(Entities.ExpenseClaim, "totalAmount", "Toplam", AutomationFieldType.Number),
        ],
    };

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "hr.specialist",
            "İK uzmanı",
            "Çalışan kartlarını ve özlük bilgilerini, bütün izin taleplerini, izin haklarını ve resmî tatilleri yönetir.",
            [
                Permissions.EmployeesRead, Permissions.EmployeesWrite, Permissions.EmployeesPersonal,
                Permissions.LeavesRead, Permissions.LeavesRequest, Permissions.LeavesApprove, Permissions.LeavesManage,
                Permissions.ExpensesRead, Permissions.ExpensesRequest, Permissions.ExpensesApprove, Permissions.ExpensesManage,
            ]),
        new(
            "hr.finance",
            "Finans (masraf ödemesi)",
            "Bütün masraf beyanlarını görür ve onaylanmış beyanları ödendi olarak işaretler; beyana karar vermez.",
            [Permissions.EmployeesRead, Permissions.ExpensesRead, Permissions.ExpensesPay]),
        new(
            "hr.staff",
            "Çalışan (rehber)",
            "Rehberi ve organizasyon şemasını görüntüler, kendi izin hakkını görür ve kendi adına izin talebi açar; özlük bilgilerini göremez.",
            [Permissions.EmployeesRead, Permissions.LeavesRead, Permissions.LeavesRequest, Permissions.ExpensesRead, Permissions.ExpensesRequest])
        {
            Scope = Core.Contracts.Authorization.RecordScope.Own,
        },
        new(
            "hr.manager",
            "Yönetici (izin onayı)",
            "Çalışan izinlerine ek olarak onaylayanı olduğu (astlarının) izin taleplerini görür, onaylar ve reddeder.",
            [
                Permissions.EmployeesRead, Permissions.LeavesRead, Permissions.LeavesRequest, Permissions.LeavesApprove,
                Permissions.ExpensesRead, Permissions.ExpensesRequest, Permissions.ExpensesApprove,
            ])
        {
            Scope = Core.Contracts.Authorization.RecordScope.Own,
        },
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<HrStoreContext>();
        services.AddScoped<HrWorkflowGate>();
        services.AddScoped<IRecordStore, EmployeeRecordStore>();
        services.AddScoped<IRecordStore, LeaveRequestRecordStore>();
        services.AddScoped<IRecordStore, LeaveBalanceRecordStore>();
        services.AddScoped<IRecordStore, HolidayRecordStore>();
        services.AddScoped<ILeaveWorkflow, LeaveWorkflow>();
        services.AddScoped<IRecordStore, ExpenseClaimRecordStore>();
        services.AddScoped<IRecordStore, ExpenseLineRecordStore>();
        services.AddScoped<IExpenseWorkflow, ExpenseWorkflow>();
    }
}
