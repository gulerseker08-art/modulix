namespace Modulix.Shared.Hr;

/// <summary>
/// İnsan Kaynakları modülünün izin, kayıt türü, liste ve lookup anahtarları (arayüz için); sunucudaki <c>HrModule</c> ile aynıdır (birim testle
/// denetlenir). Çalışan kartları genel kayıt uçlarından (<c>/api/records/hr.employee</c>) okunur ve yazılır; rehber ve organizasyon şeması
/// aynı kayıtları okur.
/// </summary>
public static class HrAccess
{
    public const string ModuleKey = "hr";

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
    public const string BlogRead = "hr.blog.read";
    public const string BlogManage = "hr.blog.manage";
    public const string GalleryRead = "hr.gallery.read";
    public const string GalleryManage = "hr.gallery.manage";

    public const string EmployeeEntityKey = "hr.employee";
    public const string EmployeePositionEntityKey = "hr.employee-position";
    public const string LeaveRequestEntityKey = "hr.leave-request";
    public const string LeaveBalanceEntityKey = "hr.leave-balance";
    public const string HolidayEntityKey = "hr.holiday";
    public const string LeaveTypeList = "hr.leave-type";
    public const string LeaveRequestEmployeeLookup = "hr.leave-request.employee";
    public const string LeaveBalanceEmployeeLookup = "hr.leave-balance.employee";
    public const string ExpenseClaimEntityKey = "hr.expense-claim";
    public const string ExpenseLineEntityKey = "hr.expense-line";
    public const string ExpenseTypeList = "hr.expense-type";
    public const string ExpenseClaimEmployeeLookup = "hr.expense-claim.employee";
    public const string AssetEntityKey = "hr.asset";
    public const string AssetAssignmentEntityKey = "hr.asset-assignment";
    public const string AssetCategoryList = "hr.asset-category";
    public const string AssignmentEmployeeLookup = "hr.asset-assignment.employee";
    public const string AssignmentAssetLookup = "hr.asset-assignment.asset";
    public const string ChecklistTemplateEntityKey = "hr.checklist-template";
    public const string ChecklistTemplateItemEntityKey = "hr.checklist-template-item";
    public const string EmployeeChecklistEntityKey = "hr.employee-checklist";
    public const string ChecklistTaskEntityKey = "hr.checklist-task";
    public const string ChecklistEmployeeLookup = "hr.employee-checklist.employee";
    public const string ChecklistTemplateLookup = "hr.employee-checklist.template";
    public const string AnnouncementEntityKey = "hr.announcement";
    public const string SurveyEntityKey = "hr.survey";
    public const string SurveyQuestionEntityKey = "hr.survey-question";
    public const string SurveyResponseEntityKey = "hr.survey-response";
    public const string BlogPostEntityKey = "hr.blog-post";
    public const string PhotoAlbumEntityKey = "hr.photo-album";
    public const string AnnouncementReadEntityKey = "hr.announcement-read";

    /// <summary>Yalnızca bu kategorideki izin türü yıllık haktan düşer.</summary>
    public const string AnnualCategory = "annual";

    public const string DepartmentList = "hr.department";

    public const string EmployeePersonLookup = "hr.employee.person";
    public const string EmployeeManagerLookup = "hr.employee.manager";
    public const string EmployeeUserLookup = "hr.employee.user";

    public const string Terminated = "terminated";

    /// <summary>Durumlar (sabit; sunucudaki <c>Employee.Statuses</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> Statuses { get; } =
    [
        ("active", "Çalışıyor"),
        ("onLeave", "İzinde"),
        ("terminated", "Ayrıldı"),
    ];

    /// <summary>Çalışma şekilleri (sabit; sunucudaki <c>Employee.EmploymentTypes</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> EmploymentTypes { get; } =
    [
        ("fullTime", "Tam zamanlı"),
        ("partTime", "Yarı zamanlı"),
        ("fixedTerm", "Belirli süreli"),
        ("intern", "Stajyer"),
    ];

    /// <summary>İzin talebi durumları (sabit; sunucudaki <c>LeaveRequest.Statuses</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> LeaveStatuses { get; } =
    [
        ("pending", "Onay bekliyor"),
        ("approved", "Onaylandı"),
        ("rejected", "Reddedildi"),
        ("cancelled", "İptal edildi"),
    ];

    /// <summary>Masraf beyanı durumları (sabit; sunucudaki <c>ExpenseClaim.Statuses</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> ExpenseStatuses { get; } =
    [
        ("draft", "Taslak"),
        ("submitted", "Onay bekliyor"),
        ("approved", "Onaylandı"),
        ("paid", "Ödendi"),
        ("rejected", "Reddedildi"),
        ("cancelled", "İptal edildi"),
    ];

    /// <summary>Demirbaş durumları (sabit; sunucudaki <c>Asset.Statuses</c>); "zimmetli" yalnızca zimmetle gelir.</summary>
    public static IReadOnlyList<(string Key, string Label)> AssetStatuses { get; } =
    [
        ("inStock", "Depoda"),
        ("assigned", "Zimmetli"),
        ("maintenance", "Bakımda"),
        ("retired", "Hurda"),
        ("lost", "Kayıp"),
    ];

    /// <summary>Zimmet durumları (sabit; sunucudaki <c>AssetAssignment.Statuses</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> AssignmentStatuses { get; } =
    [
        ("active", "Zimmette"),
        ("returned", "İade alındı"),
        ("lost", "Kayıp"),
    ];

    /// <summary>Süreç türleri (sabit; sunucudaki <c>Checklists.Kinds</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> ChecklistKinds { get; } =
    [
        ("onboarding", "İşe giriş"),
        ("offboarding", "İşten çıkış"),
    ];

    /// <summary>Görev sorumluları (sabit; sunucudaki <c>Checklists.Responsibles</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> ChecklistResponsibles { get; } =
    [
        ("employee", "Çalışan"),
        ("manager", "Yönetici"),
        ("hr", "İK"),
    ];

    /// <summary>Görev türleri (sabit; sunucudaki <c>Checklists.TaskTypes</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> ChecklistTaskTypes { get; } =
    [
        ("manual", "Elle tamamlanır"),
        ("assetReturn", "Zimmet iadesi (açık zimmet kalmamalı)"),
    ];

    /// <summary>Süreç durumları (sabit; sunucudaki <c>EmployeeChecklist.Statuses</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> ChecklistStatuses { get; } =
    [
        ("open", "Sürüyor"),
        ("completed", "Tamamlandı"),
        ("cancelled", "İptal edildi"),
    ];

    /// <summary>Görev durumları (sabit; sunucudaki <c>ChecklistTask.Statuses</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> TaskStatuses { get; } =
    [
        ("open", "Açık"),
        ("done", "Tamamlandı"),
        ("skipped", "Atlandı"),
    ];

    /// <summary>Anket durumları (sabit; sunucudaki <c>Survey.Statuses</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> SurveyStatuses { get; } =
    [
        ("draft", "Taslak"),
        ("open", "Yanıta açık"),
        ("closed", "Kapalı"),
    ];

    /// <summary>Soru türleri (sabit; sunucudaki <c>SurveyQuestion.Types</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> QuestionTypes { get; } =
    [
        ("rating", "1–5 puan"),
        ("choice", "Tek seçim"),
        ("multiChoice", "Çoklu seçim"),
        ("text", "Serbest metin"),
    ];

    /// <summary>Özlük alanları (hassas; <see cref="EmployeesPersonal"/> izni olmadan sunucu göndermez).</summary>
    public static IReadOnlyList<string> PersonalFields { get; } =
        ["nationalId", "birthDate", "homeAddress", "emergencyContactName", "emergencyContactPhone", "iban"];
}

/// <summary>İzin talebi eylemi (onayla, reddet, iptal): yapılabilir mi, değilse neden; gerekçe ister mi; yıkıcı mı.</summary>
public sealed record HrActionDto(string Action, string Label, bool Allowed, string? Message, bool RequiresReason, bool Destructive);

/// <summary>Eylem isteği; sürüm eşzamanlı değişikliği yakalar (409).</summary>
public sealed record RunHrActionRequest(string? Action, long Version, string? Reason);

/// <summary>Gün sayısı önizlemesi isteği (yazmaz).</summary>
public sealed record LeavePreviewRequest(Guid EmployeeId, string? LeaveTypeKey, DateOnly StartDate, DateOnly EndDate, bool HalfDay);

/// <summary>İş günü sayısı; yıllık izinde görülebiliyorsa o yılın kalan hakkı ve onay bekleyen günler.</summary>
public sealed record LeavePreviewDto(decimal Days, decimal? RemainingDays, decimal? PendingDays);

/// <summary>İşe giriş/çıkış süreci başlatma isteği: çalışan, şablon ve görev son tarihlerinin hesaplanacağı başlangıç günü.</summary>
public sealed record StartChecklistRequest(Guid EmployeeId, Guid TemplateId, DateOnly StartDate);

/// <summary>Anket yanıtı: soru başına bir cevap (puan, seçenek ya da metin; sorunun türüne göre biri dolu).</summary>
public sealed record SubmitSurveyRequest(IReadOnlyList<SurveyAnswerItem>? Answers);

/// <summary>Bir sorunun cevabı; çoklu seçim sorusunda seçilenler <see cref="Choices"/>'tadır.</summary>
public sealed record SurveyAnswerItem(Guid QuestionId, int? Rating, string? Choice, string? Text, IReadOnlyList<string>? Choices = null);

/// <summary>Anket sonucu; <see cref="HiddenReason"/> doluysa (kimliği gizli ankette yanıt az) sorular boştur.</summary>
public sealed record SurveyResultsDto(Guid SurveyId, int ResponseCount, string? HiddenReason, IReadOnlyList<SurveyQuestionResultDto> Questions);

/// <summary>Soru sonucu: cevap sayısı, puan ortalaması, puan/seçenek dağılımı, metin cevapları (yanıtlayan bilgisi olmadan).</summary>
public sealed record SurveyQuestionResultDto(
    Guid QuestionId, string Text, string QuestionType, int AnswerCount, decimal? Average, IReadOnlyList<SurveyOptionCountDto> Counts, IReadOnlyList<string> Texts);

public sealed record SurveyOptionCountDto(string Option, int Count);

/// <summary>Core departmanı (ADR-0026) İK seçicileri için: üst departman, ağaç yolu adı ("Merkez › Satış"), derinlik, etkin mi.</summary>
public sealed record HrBusinessUnitDto(Guid Id, Guid? ParentId, string Name, string PathName, int Depth, bool IsActive);

/// <summary>
/// Eski İK departman değerinin Core departmanına eşlemesi (ADR-0042 bölüm 22): bulunan departman (yoksa boş: Core'da aynı adla departman yok ya da birden
/// çok departman uyuyor) ve henüz Core departmanı olmayan çalışan, duyuru, anket sayıları.
/// </summary>
public sealed record DepartmentMappingRowDto(
    string DepartmentKey, string Label, Guid? BusinessUnitId, string? BusinessUnitName, int EmployeeCount, int AnnouncementCount, int SurveyCount);

/// <summary>Eşleme uygulamasının sonucu: güncellenen kayıt sayısı.</summary>
public sealed record DepartmentMappingResultDto(int Updated);
