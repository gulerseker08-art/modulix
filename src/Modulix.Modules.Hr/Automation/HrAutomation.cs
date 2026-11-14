using System.Linq.Expressions;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Modules.Hr.Domain;
using Modulix.Modules.Hr.Records;

namespace Modulix.Modules.Hr.Automation;

/// <summary>
/// İK'nın kural altyapısı yetenek beyanı (ADR-0027; ADR-0042 bölüm 18): kayıt türleri ve olaylar, koşulda kullanılabilen alanlar, tarama kaynağı,
/// "süreç başlat" aksiyonu ve başlangıç şablonları. Tarih sütunlarının tek kaynağı bu sınıftır; tarama kaynağı beyan edilen her tarih alanını
/// destekler (birim testle denetlenir).
/// <para>
/// Kullanıcı alanları (onaylayan, sorumlu, zimmetli, çalışanın hesabı) Core'un bildirim alıcısı lookup'ıyla beyan edilir; böylece "Bildirim gönder"
/// aksiyonunun "Kime" parametresi bu alanlara bağlanabilir. Bildirim, alıcı kaydı kendi yetkisiyle görebiliyorsa gider (Core kuralı).
/// </para>
/// </summary>
public static class HrAutomation
{
    public const string StartChecklist = "hr.checklist.start";

    /// <summary>Core'un bildirim alıcısı lookup'ı (<c>NotifyAutomation.RecipientLookup</c>); modül Core'a başvuramadığı için anahtar burada yazılır.</summary>
    public const string NotifyRecipientLookup = "core.notification.recipient";

    /// <summary>Core'un "Bildirim gönder" aksiyonu (<c>NotifyAutomation.NotifyAction</c>).</summary>
    public const string NotifyAction = "core.notify";

    internal static IReadOnlyDictionary<string, Expression<Func<Employee, DateOnly?>>> EmployeeDateColumns { get; } =
        new Dictionary<string, Expression<Func<Employee, DateOnly?>>>(StringComparer.Ordinal)
        {
            ["hireDate"] = e => e.HireDate,
            ["terminationDate"] = e => e.TerminationDate,
        };

    internal static IReadOnlyDictionary<string, Expression<Func<LeaveRequest, DateOnly?>>> LeaveDateColumns { get; } =
        new Dictionary<string, Expression<Func<LeaveRequest, DateOnly?>>>(StringComparer.Ordinal)
        {
            ["startDate"] = r => r.StartDate,
        };

    internal static IReadOnlyDictionary<string, Expression<Func<AssetAssignment, DateOnly?>>> AssignmentDateColumns { get; } =
        new Dictionary<string, Expression<Func<AssetAssignment, DateOnly?>>>(StringComparer.Ordinal)
        {
            ["expectedReturnOn"] = a => a.ExpectedReturnOn,
        };

    internal static IReadOnlyDictionary<string, Expression<Func<ChecklistTask, DateOnly?>>> TaskDateColumns { get; } =
        new Dictionary<string, Expression<Func<ChecklistTask, DateOnly?>>>(StringComparer.Ordinal)
        {
            ["dueDate"] = t => t.DueDate,
        };

    /// <summary>Taranabilen tarih alanları, kayıt türüne göre (beyan–kaynak uyumu testi için).</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyCollection<string>> DateFields { get; } = new Dictionary<string, IReadOnlyCollection<string>>
    {
        [HrModule.Entities.Employee] = [.. EmployeeDateColumns.Keys],
        [HrModule.Entities.LeaveRequest] = [.. LeaveDateColumns.Keys],
        [HrModule.Entities.AssetAssignment] = [.. AssignmentDateColumns.Keys],
        [HrModule.Entities.ChecklistTask] = [.. TaskDateColumns.Keys],
    };

    public static AutomationCapabilities Capabilities { get; } = new()

    {
        Entities =
        [
            new(HrModule.Entities.Employee, "Çalışan", HrModule.Permissions.EmployeesRead) { RecordSource = typeof(HrAutomationRecordSource), LabelField = "fullName" },
            new(HrModule.Entities.LeaveRequest, "İzin talebi", HrModule.Permissions.LeavesRead) { RecordSource = typeof(HrAutomationRecordSource), LabelField = "employeeName" },
            new(HrModule.Entities.ExpenseClaim, "Masraf beyanı", HrModule.Permissions.ExpensesRead) { LabelField = "title" },
            new(HrModule.Entities.AssetAssignment, "Zimmet", HrModule.Permissions.AssetsRead) { RecordSource = typeof(HrAutomationRecordSource), LabelField = "assetTag" },
            new(HrModule.Entities.ChecklistTask, "Süreç görevi", HrModule.Permissions.ChecklistsRead) { RecordSource = typeof(HrAutomationRecordSource), LabelField = "title" },
            new(HrModule.Entities.Announcement, "Duyuru", HrModule.Permissions.AnnouncementsManage) { LabelField = "title" },
            new(HrModule.Entities.Survey, "Anket", HrModule.Permissions.SurveysManage) { LabelField = "title" },
            new(HrModule.Entities.BlogPost, "Blog yazısı", HrModule.Permissions.BlogManage) { LabelField = "title" },
            new(HrModule.Entities.PhotoAlbum, "Fotoğraf albümü", HrModule.Permissions.GalleryManage) { LabelField = "title" },
        ],
        Events =
        [
            new("hr.employee.created", HrModule.Entities.Employee, DomainEventTypes.RecordCreated, "Çalışan eklendi"),
            new("hr.employee.updated", HrModule.Entities.Employee, DomainEventTypes.RecordUpdated, "Çalışan güncellendi"),
            new("hr.leave-request.created", HrModule.Entities.LeaveRequest, DomainEventTypes.RecordCreated, "İzin talebi açıldı"),
            new("hr.leave-request.updated", HrModule.Entities.LeaveRequest, DomainEventTypes.RecordUpdated, "İzin talebinin durumu değişti"),
            new("hr.expense-claim.created", HrModule.Entities.ExpenseClaim, DomainEventTypes.RecordCreated, "Masraf beyanı açıldı"),
            new("hr.expense-claim.updated", HrModule.Entities.ExpenseClaim, DomainEventTypes.RecordUpdated, "Masraf beyanı güncellendi"),
            new("hr.asset-assignment.created", HrModule.Entities.AssetAssignment, DomainEventTypes.RecordCreated, "Demirbaş zimmetlendi"),
            new("hr.asset-assignment.updated", HrModule.Entities.AssetAssignment, DomainEventTypes.RecordUpdated, "Zimmet onaylandı ya da kapandı"),
            new("hr.checklist-task.created", HrModule.Entities.ChecklistTask, DomainEventTypes.RecordCreated, "Süreç görevi açıldı"),
            new("hr.checklist-task.updated", HrModule.Entities.ChecklistTask, DomainEventTypes.RecordUpdated, "Süreç görevi tamamlandı ya da değişti"),
            new("hr.announcement.created", HrModule.Entities.Announcement, DomainEventTypes.RecordCreated, "Duyuru yazıldı"),
            new("hr.announcement.updated", HrModule.Entities.Announcement, DomainEventTypes.RecordUpdated, "Duyuru güncellendi ya da yayınlandı"),
            new("hr.survey.created", HrModule.Entities.Survey, DomainEventTypes.RecordCreated, "Anket hazırlandı"),
            new("hr.survey.updated", HrModule.Entities.Survey, DomainEventTypes.RecordUpdated, "Anket güncellendi, yanıta açıldı ya da kapandı"),
            new("hr.blog-post.created", HrModule.Entities.BlogPost, DomainEventTypes.RecordCreated, "Blog yazısı yazıldı"),
            new("hr.blog-post.updated", HrModule.Entities.BlogPost, DomainEventTypes.RecordUpdated, "Blog yazısı güncellendi ya da yayınlandı"),
            new("hr.photo-album.created", HrModule.Entities.PhotoAlbum, DomainEventTypes.RecordCreated, "Fotoğraf albümü açıldı"),
            new("hr.photo-album.updated", HrModule.Entities.PhotoAlbum, DomainEventTypes.RecordUpdated, "Fotoğraf albümü güncellendi ya da yayınlandı"),
        ],
        Fields =
        [
            new(HrModule.Entities.Employee, "status", "Durum", AutomationFieldType.Choice)
            {
                Options = [new(Employee.Statuses.Active, "Çalışıyor"), new(Employee.Statuses.OnLeave, "İzinde"), new(Employee.Statuses.Terminated, "Ayrıldı")],
            },
            new(HrModule.Entities.Employee, "employmentType", "Çalışma şekli", AutomationFieldType.Choice)
            {
                Options =
                [
                    new(Employee.EmploymentTypes.FullTime, "Tam zamanlı"), new(Employee.EmploymentTypes.PartTime, "Yarı zamanlı"),
                    new(Employee.EmploymentTypes.FixedTerm, "Belirli süreli"), new(Employee.EmploymentTypes.Intern, "Stajyer"),
                ],
            },
            new(HrModule.Entities.Employee, "fullName", "Ad soyad", AutomationFieldType.Text),
            new(HrModule.Entities.Employee, "jobTitle", "Unvan", AutomationFieldType.Text),
            new(HrModule.Entities.Employee, "hireDate", "İşe giriş tarihi", AutomationFieldType.Date),
            new(HrModule.Entities.Employee, "terminationDate", "Ayrılış tarihi", AutomationFieldType.Date),
            new(HrModule.Entities.Employee, "userId", "Kullanıcı hesabı", AutomationFieldType.Lookup)
            {
                LookupKey = NotifyRecipientLookup,
                Hint = "Çalışanın kendisine bildirim için (Bildirim gönder → Kime).",
            },
            new(HrModule.Entities.LeaveRequest, "employeeName", "Çalışan", AutomationFieldType.Text),
            new(HrModule.Entities.LeaveRequest, "status", "Durum", AutomationFieldType.Choice)
            {
                Options =
                [
                    new(LeaveRequest.Statuses.Pending, "Onay bekliyor"), new(LeaveRequest.Statuses.Approved, "Onaylandı"),
                    new(LeaveRequest.Statuses.Rejected, "Reddedildi"), new(LeaveRequest.Statuses.Cancelled, "İptal edildi"),
                ],
            },
            new(HrModule.Entities.LeaveRequest, "startDate", "Başlangıç", AutomationFieldType.Date),
            new(HrModule.Entities.LeaveRequest, "days", "Gün", AutomationFieldType.Number),
            new(HrModule.Entities.LeaveRequest, "employeeUserId", "Talep eden", AutomationFieldType.Lookup) { LookupKey = NotifyRecipientLookup },
            new(HrModule.Entities.LeaveRequest, "approverUserId", "Onaylayan", AutomationFieldType.Lookup) { LookupKey = NotifyRecipientLookup },
            new(HrModule.Entities.ExpenseClaim, "title", "Başlık", AutomationFieldType.Text),
            new(HrModule.Entities.ExpenseClaim, "status", "Durum", AutomationFieldType.Choice)
            {
                Options =
                [
                    new(ExpenseClaim.Statuses.Draft, "Taslak"), new(ExpenseClaim.Statuses.Submitted, "Onay bekliyor"),
                    new(ExpenseClaim.Statuses.Approved, "Onaylandı"), new(ExpenseClaim.Statuses.Paid, "Ödendi"),
                    new(ExpenseClaim.Statuses.Rejected, "Reddedildi"), new(ExpenseClaim.Statuses.Cancelled, "İptal edildi"),
                ],
            },
            new(HrModule.Entities.ExpenseClaim, "totalAmount", "Toplam", AutomationFieldType.Number),
            new(HrModule.Entities.ExpenseClaim, "employeeUserId", "Beyan eden", AutomationFieldType.Lookup) { LookupKey = NotifyRecipientLookup },
            new(HrModule.Entities.ExpenseClaim, "approverUserId", "Onaylayan", AutomationFieldType.Lookup) { LookupKey = NotifyRecipientLookup },
            new(HrModule.Entities.AssetAssignment, "assetTag", "Envanter numarası", AutomationFieldType.Text),
            new(HrModule.Entities.AssetAssignment, "status", "Durum", AutomationFieldType.Choice)
            {
                Options =
                [
                    new(AssetAssignment.Statuses.Active, "Zimmette"), new(AssetAssignment.Statuses.Returned, "İade alındı"),
                    new(AssetAssignment.Statuses.Lost, "Kayıp"),
                ],
            },
            new(HrModule.Entities.AssetAssignment, "expectedReturnOn", "Beklenen iade", AutomationFieldType.Date),
            new(HrModule.Entities.AssetAssignment, "employeeUserId", "Zimmetli", AutomationFieldType.Lookup) { LookupKey = NotifyRecipientLookup },
            new(HrModule.Entities.ChecklistTask, "title", "Başlık", AutomationFieldType.Text),
            new(HrModule.Entities.ChecklistTask, "status", "Durum", AutomationFieldType.Choice)
            {
                Options = [new(ChecklistTask.Statuses.Open, "Açık"), new(ChecklistTask.Statuses.Done, "Tamamlandı"), new(ChecklistTask.Statuses.Skipped, "Atlandı")],
            },
            new(HrModule.Entities.ChecklistTask, "dueDate", "Son tarih", AutomationFieldType.Date),
            new(HrModule.Entities.ChecklistTask, "assigneeUserId", "Sorumlu", AutomationFieldType.Lookup)
            {
                LookupKey = NotifyRecipientLookup,
                Hint = "Çalışana ya da yöneticiye atanan görevde dolu; İK görevinde boş.",
            },
            new(HrModule.Entities.ChecklistTask, "employeeUserId", "Çalışan", AutomationFieldType.Lookup) { LookupKey = NotifyRecipientLookup },
            new(HrModule.Entities.Announcement, "title", "Başlık", AutomationFieldType.Text),
            new(HrModule.Entities.Announcement, "isPublished", "Yayında", AutomationFieldType.Boolean),
            new(HrModule.Entities.Survey, "title", "Başlık", AutomationFieldType.Text),
            new(HrModule.Entities.Survey, "status", "Durum", AutomationFieldType.Choice)
            {
                Options = [new(Survey.Statuses.Draft, "Taslak"), new(Survey.Statuses.Open, "Yanıta açık"), new(Survey.Statuses.Closed, "Kapalı")],
            },
            new(HrModule.Entities.BlogPost, "title", "Başlık", AutomationFieldType.Text),
            new(HrModule.Entities.BlogPost, "isPublished", "Yayında", AutomationFieldType.Boolean),
            new(HrModule.Entities.PhotoAlbum, "title", "Başlık", AutomationFieldType.Text),
            new(HrModule.Entities.PhotoAlbum, "isPublished", "Yayında", AutomationFieldType.Boolean),
        ],
        Actions =
        [
            AutomationActionRegistration.For<StartChecklistAction>(new AutomationActionDefinition(
                StartChecklist,
                "İşe giriş/çıkış süreci başlat",
                "Çalışana şablondan işe giriş ya da işten çıkış süreci başlatır (görevler sorumlularına açılır). Şablon verilmezse türün ilk etkin "
                + "şablonu kullanılır. Çalışanın aynı türde açık süreci varsa yeni süreç açılmaz.",
                [
                    new AutomationParameter("kind", "Süreç türü", AutomationFieldType.Choice)
                    {
                        Options = [new(Checklists.Kinds.Onboarding, "İşe giriş"), new(Checklists.Kinds.Offboarding, "İşten çıkış")],
                        AllowFieldBinding = false,
                    },
                    new AutomationParameter("templateId", "Şablon", AutomationFieldType.Lookup, Required: false)
                    {
                        LookupKey = HrLookupDefinitions.ChecklistTemplate,
                        Description = "Boşsa türün ada göre ilk etkin şablonu.",
                        AllowFieldBinding = false,
                    },
                    new AutomationParameter("startDate", "Başlangıç günü", AutomationFieldType.Date, Required: false)
                    {
                        Description = "Görev son tarihleri buna göre hesaplanır. Çalışanın işe giriş ya da ayrılış tarihine bağlanabilir; boşsa bugün.",
                    },
                ],
                [HrModule.Permissions.ChecklistsManage],
                [HrModule.ModuleKey],
                AutomationSideEffect.Write,
                SupportsDryRun: true)),
        ],
        Templates = HrAutomationTemplates.All,
    };
}
