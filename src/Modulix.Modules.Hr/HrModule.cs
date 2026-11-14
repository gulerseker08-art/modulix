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
        public const string AssetsRead = "hr.assets.read";
        public const string AssetsManage = "hr.assets.manage";
        public const string ChecklistsRead = "hr.checklists.read";
        public const string ChecklistsManage = "hr.checklists.manage";
        public const string AnnouncementsRead = "hr.announcements.read";
        public const string AnnouncementsManage = "hr.announcements.manage";
        public const string SurveysRespond = "hr.surveys.respond";
        public const string SurveysManage = "hr.surveys.manage";

        /// <summary>Tek tek cevap kayıtları ve denetim kaydındaki cevap değerleri; hiçbir rol şablonunda yoktur (sonuçlar özetten görülür).</summary>
        public const string SurveysAnswers = "hr.surveys.answers";
        public const string BlogRead = "hr.blog.read";
        public const string BlogManage = "hr.blog.manage";
        public const string GalleryRead = "hr.gallery.read";
        public const string GalleryManage = "hr.gallery.manage";
    }

    public static class Entities
    {
        public const string Employee = "hr.employee";
        public const string LeaveRequest = "hr.leave-request";
        public const string LeaveBalance = "hr.leave-balance";
        public const string Holiday = "hr.holiday";
        public const string ExpenseClaim = "hr.expense-claim";
        public const string ExpenseLine = "hr.expense-line";
        public const string Asset = "hr.asset";
        public const string AssetAssignment = "hr.asset-assignment";
        public const string ChecklistTemplate = "hr.checklist-template";
        public const string ChecklistTemplateItem = "hr.checklist-template-item";
        public const string EmployeeChecklist = "hr.employee-checklist";
        public const string ChecklistTask = "hr.checklist-task";
        public const string Announcement = "hr.announcement";
        public const string AnnouncementRead = "hr.announcement-read";
        public const string Survey = "hr.survey";
        public const string SurveyQuestion = "hr.survey-question";
        public const string SurveyResponse = "hr.survey-response";
        public const string SurveyAnswer = "hr.survey-answer";
        public const string BlogPost = "hr.blog-post";
        public const string PhotoAlbum = "hr.photo-album";
    }

    public static class Lists
    {
        public const string Department = "hr.department";
        public const string LeaveType = "hr.leave-type";
        public const string ExpenseType = "hr.expense-type";
        public const string AssetCategory = "hr.asset-category";
    }

    /// <summary>Demirbaş kategorileri (dilim 4): raporlar ve işe giriş/çıkış listeleri kategoriye bakar.</summary>
    public static class AssetCategories
    {
        public const string It = "it";
        public const string Access = "access";
        public const string Furniture = "furniture";
        public const string Equipment = "equipment";
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
        new(Permissions.AssetsRead, "Demirbaşları ve zimmetleri görüntüler (kayıt kapsamına göre: çalışan kendi zimmetlerini)", "Zimmet"),
        new(Permissions.AssetsManage, "Demirbaş ekler ve düzenler, zimmetler, iade alır", "Zimmet"),
        new(Permissions.ChecklistsRead, "İşe giriş/çıkış süreçlerini ve görevlerini görüntüler, kendisine atanan görevi tamamlar (kayıt kapsamına göre)", "İşe giriş ve çıkış"),
        new(Permissions.ChecklistsManage, "Süreç şablonlarını yönetir, süreç başlatır, her görevi tamamlar, atlar ve süreci iptal eder", "İşe giriş ve çıkış"),
        new(Permissions.AnnouncementsRead, "Kendisine hedeflenen yayındaki duyuruları okur", "Duyurular"),
        new(Permissions.AnnouncementsManage, "Duyuru yazar, yayınlar, sabitler ve kimin okuduğunu görür", "Duyurular"),
        new(Permissions.SurveysRespond, "Kendisine yönelik açık anketleri yanıtlar", "Anketler"),
        new(Permissions.SurveysManage, "Anket hazırlar, yanıta açar, kapatır ve özet sonuçları görür", "Anketler"),
        new(Permissions.SurveysAnswers, "Tek tek cevap kayıtlarını görür (kimliği gizli anketler hariç); varsayılan rollerde yoktur", "Anketler"),
        new(Permissions.BlogRead, "Yayındaki blog yazılarını ve eklerini okur", "Blog"),
        new(Permissions.BlogManage, "Blog yazısı yazar, yayınlar, ek ekler ve kaldırır", "Blog"),
        new(Permissions.GalleryRead, "Yayındaki fotoğraf albümlerini görür", "Galeri"),
        new(Permissions.GalleryManage, "Albüm açar, fotoğraf ekler ve kaldırır, albümü yayınlar", "Galeri"),
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
        new(Entities.Asset, "Demirbaş", Permissions.AssetsRead, new Dictionary<string, string>
        {
            ["assetTag"] = "Envanter numarası", ["name"] = "Ad", ["categoryKey"] = "Kategori", ["serialNumber"] = "Seri numarası",
            ["brand"] = "Marka", ["model"] = "Model", ["purchaseDate"] = "Alış tarihi", ["purchaseCost"] = "Alış tutarı", ["currency"] = "Para birimi",
            ["status"] = "Durum", ["notes"] = "Notlar", ["currentEmployeeId"] = "Zimmetli çalışan",
        }, "assetTag")
        {
            References = new Dictionary<string, string> { ["currentEmployeeId"] = Entities.Employee },
        },
        new(Entities.AssetAssignment, "Zimmet", Permissions.AssetsRead, new Dictionary<string, string>
        {
            ["assetId"] = "Demirbaş", ["employeeId"] = "Çalışan", ["assignedOn"] = "Teslim tarihi", ["expectedReturnOn"] = "Beklenen iade",
            ["conditionOut"] = "Teslimdeki durum", ["status"] = "Durum", ["acknowledgedAt"] = "Teslim onayı", ["returnedOn"] = "İade tarihi",
            ["conditionIn"] = "İadedeki durum", ["closedByUserId"] = "Kapatan",
        }, "assetTag")
        {
            References = new Dictionary<string, string> { ["assetId"] = Entities.Asset, ["employeeId"] = Entities.Employee },
            UserFields = ["assignedByUserId", "closedByUserId", "employeeUserId"],
        },
        new(Entities.ChecklistTemplate, "Süreç şablonu", Permissions.ChecklistsRead, new Dictionary<string, string>
        {
            ["name"] = "Ad", ["kind"] = "Tür", ["description"] = "Açıklama", ["isActive"] = "Etkin",
        }, "name"),
        new(Entities.ChecklistTemplateItem, "Şablon adımı", Permissions.ChecklistsRead, new Dictionary<string, string>
        {
            ["templateId"] = "Şablon", ["sequence"] = "Sıra", ["title"] = "Başlık", ["description"] = "Açıklama", ["responsibleRole"] = "Sorumlu",
            ["dueOffsetDays"] = "Gün farkı", ["taskType"] = "Görev türü",
        }, "title")
        {
            References = new Dictionary<string, string> { ["templateId"] = Entities.ChecklistTemplate },
        },
        new(Entities.EmployeeChecklist, "İşe giriş/çıkış süreci", Permissions.ChecklistsRead, new Dictionary<string, string>
        {
            ["employeeId"] = "Çalışan", ["templateId"] = "Şablon", ["startDate"] = "Başlangıç", ["status"] = "Durum", ["cancelReason"] = "İptal gerekçesi",
        }, "name")
        {
            References = new Dictionary<string, string> { ["employeeId"] = Entities.Employee, ["templateId"] = Entities.ChecklistTemplate },
            UserFields = ["employeeUserId", "startedByUserId"],
        },
        new(Entities.ChecklistTask, "Süreç görevi", Permissions.ChecklistsRead, new Dictionary<string, string>
        {
            ["checklistId"] = "Süreç", ["title"] = "Başlık", ["dueDate"] = "Son tarih", ["status"] = "Durum", ["note"] = "Not",
            ["assigneeUserId"] = "Sorumlu", ["completedByUserId"] = "Tamamlayan",
        }, "title")
        {
            References = new Dictionary<string, string> { ["checklistId"] = Entities.EmployeeChecklist },
            UserFields = ["assigneeUserId", "completedByUserId", "employeeUserId"],
        },
        new(Entities.Announcement, "Duyuru", Permissions.AnnouncementsRead, new Dictionary<string, string>
        {
            ["title"] = "Başlık", ["body"] = "Metin", ["audienceDepartmentKey"] = "Hedef departman", ["isPinned"] = "Sabitlenmiş",
            ["isPublished"] = "Yayında", ["publishOn"] = "Başlangıç", ["expiresOn"] = "Bitiş",
        }, "title")
        {
            UserFields = ["authorUserId"],
        },
        new(Entities.Survey, "Anket", Permissions.SurveysManage, new Dictionary<string, string>
        {
            ["title"] = "Başlık", ["description"] = "Açıklama", ["isAnonymous"] = "Kimliği gizli", ["audienceDepartmentKey"] = "Hedef departman",
            ["closesOn"] = "Kapanış", ["status"] = "Durum",
        }, "title")
        {
            UserFields = ["authorUserId"],
        },
        new(Entities.SurveyQuestion, "Anket sorusu", Permissions.SurveysManage, new Dictionary<string, string>
        {
            ["surveyId"] = "Anket", ["text"] = "Soru", ["questionType"] = "Tür", ["options"] = "Seçenekler", ["isRequired"] = "Zorunlu",
        }, "text")
        {
            References = new Dictionary<string, string> { ["surveyId"] = Entities.Survey },
        },
        // Cevap değerleri denetim kaydında yalnızca bu izinle görünür (kimliği gizli anket; ADR-0042 dilim 7). Hassas alan yapılmaz: hassas alanı
        // izni olmayan yazamaz, cevabı ise her yanıtlayan yazar.
        new(Entities.SurveyAnswer, "Anket cevabı", Permissions.SurveysAnswers, new Dictionary<string, string>
        {
            ["questionId"] = "Soru", ["rating"] = "Puan", ["choice"] = "Seçim", ["text"] = "Metin",
        }, "questionId"),
        new(Entities.BlogPost, "Blog yazısı", Permissions.BlogRead, new Dictionary<string, string>
        {
            ["title"] = "Başlık", ["summary"] = "Özet", ["body"] = "Metin", ["isPublished"] = "Yayında",
        }, "title")
        {
            UserFields = ["authorUserId"],
        },
        new(Entities.PhotoAlbum, "Fotoğraf albümü", Permissions.GalleryRead, new Dictionary<string, string>
        {
            ["title"] = "Başlık", ["description"] = "Açıklama", ["eventDate"] = "Etkinlik günü", ["isPublished"] = "Yayında",
        }, "title")
        {
            UserFields = ["authorUserId"],
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
        new(Entities.Asset, "Demirbaş"),
        new(Entities.AssetAssignment, "Zimmet"),
        new(Entities.ChecklistTemplate, "Süreç şablonu"),
        new(Entities.ChecklistTemplateItem, "Şablon adımı"),
        new(Entities.Announcement, "Duyuru"),
        new(Entities.Survey, "Anket"),
        new(Entities.SurveyQuestion, "Anket sorusu"),
        new(Entities.BlogPost, "Blog yazısı"),
        new(Entities.PhotoAlbum, "Fotoğraf albümü"),
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

        // Demirbaş kategorisi (Y9b): hazır değerler örnektir; firma ekler. Araçlar Filo modülündedir.
        new(
            Lists.AssetCategory,
            "Demirbaş kategorisi",
            Entities.Asset,
            "categoryKey",
            Required: true,
            [
                new(AssetCategories.It, "Bilişim"),
                new(AssetCategories.Access, "Giriş ve kimlik"),
                new(AssetCategories.Furniture, "Mobilya"),
                new(AssetCategories.Equipment, "Ekipman ve alet"),
            ],
            [
                new("Laptop", "Dizüstü bilgisayar", AssetCategories.It),
                new("Desktop", "Masaüstü bilgisayar", AssetCategories.It),
                new("Monitor", "Monitör", AssetCategories.It),
                new("Phone", "Cep telefonu", AssetCategories.It),
                new("SimCard", "Hat (SIM kart)", AssetCategories.It),
                new("AccessCard", "Giriş kartı", AssetCategories.Access),
                new("Key", "Anahtar", AssetCategories.Access),
                new("Furniture", "Masa ve sandalye", AssetCategories.Furniture),
                new("Tool", "El aleti", AssetCategories.Equipment),
                new("Ppe", "İş güvenliği ekipmanı", AssetCategories.Equipment),
            ])
        {
            Default = "Laptop",
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
    AutomationCapabilities IModule.Automation => Automation.HrAutomation.Capabilities;

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
                Permissions.AssetsRead, Permissions.AssetsManage, Permissions.ChecklistsRead, Permissions.ChecklistsManage,
                Permissions.AnnouncementsRead, Permissions.AnnouncementsManage, Permissions.SurveysRespond, Permissions.SurveysManage,
                Permissions.BlogRead, Permissions.BlogManage, Permissions.GalleryRead, Permissions.GalleryManage,
            ]),
        new(
            "hr.asset-keeper",
            "Demirbaş sorumlusu",
            "Demirbaşları ve bütün zimmetleri yönetir: demirbaş ekler, zimmetler, iade alır; izin, masraf ve özlük verisine erişmez.",
            [Permissions.EmployeesRead, Permissions.AssetsRead, Permissions.AssetsManage, Permissions.ChecklistsRead]),
        new(
            "hr.finance",
            "Finans (masraf ödemesi)",
            "Bütün masraf beyanlarını görür ve onaylanmış beyanları ödendi olarak işaretler; beyana karar vermez.",
            [Permissions.EmployeesRead, Permissions.ExpensesRead, Permissions.ExpensesPay]),
        new(
            "hr.staff",
            "Çalışan (rehber)",
            "Rehberi ve organizasyon şemasını görüntüler, kendi izin hakkını görür ve kendi adına izin talebi açar; özlük bilgilerini göremez.",
            [Permissions.EmployeesRead, Permissions.LeavesRead, Permissions.LeavesRequest, Permissions.ExpensesRead, Permissions.ExpensesRequest, Permissions.AssetsRead,
                Permissions.ChecklistsRead, Permissions.AnnouncementsRead, Permissions.SurveysRespond, Permissions.BlogRead, Permissions.GalleryRead])
        {
            Scope = Core.Contracts.Authorization.RecordScope.Own,
        },
        new(
            "hr.manager",
            "Yönetici (izin onayı)",
            "Çalışan izinlerine ek olarak onaylayanı olduğu (astlarının) izin taleplerini görür, onaylar ve reddeder.",
            [
                Permissions.EmployeesRead, Permissions.LeavesRead, Permissions.LeavesRequest, Permissions.LeavesApprove,
                Permissions.ExpensesRead, Permissions.ExpensesRequest, Permissions.ExpensesApprove, Permissions.AssetsRead,
                Permissions.ChecklistsRead, Permissions.AnnouncementsRead, Permissions.SurveysRespond, Permissions.BlogRead, Permissions.GalleryRead,
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
        services.AddScoped<IRecordStore, AssetRecordStore>();
        services.AddScoped<IRecordStore, AssetAssignmentRecordStore>();
        services.AddScoped<IAssetWorkflow, AssetWorkflow>();
        services.AddScoped<IRecordStore, ChecklistTemplateRecordStore>();
        services.AddScoped<IRecordStore, ChecklistTemplateItemRecordStore>();
        services.AddScoped<IRecordStore, EmployeeChecklistRecordStore>();
        services.AddScoped<IRecordStore, ChecklistTaskRecordStore>();
        services.AddScoped<IChecklistWorkflow, ChecklistWorkflow>();
        services.AddScoped<IRecordStore, AnnouncementRecordStore>();
        services.AddScoped<IRecordStore, AnnouncementReadRecordStore>();
        services.AddScoped<IAnnouncementService, AnnouncementService>();
        services.AddScoped<IRecordStore, SurveyRecordStore>();
        services.AddScoped<IRecordStore, SurveyQuestionRecordStore>();
        services.AddScoped<IRecordStore, SurveyResponseRecordStore>();
        services.AddScoped<IRecordStore, SurveyAnswerRecordStore>();
        services.AddScoped<ISurveyService, SurveyService>();
        services.AddScoped<IRecordStore, BlogPostRecordStore>();
        services.AddScoped<IRecordStore, PhotoAlbumRecordStore>();
        services.AddScoped<HrAutomationRecordSource>();
        services.AddScoped<Automation.StartChecklistAction>();
    }
}
