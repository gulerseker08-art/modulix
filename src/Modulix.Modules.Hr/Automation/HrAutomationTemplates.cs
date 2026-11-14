using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Automation;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Automation;

/// <summary>
/// İK'nın başlangıç kuralı şablonları (ADR-0027 Dilim 3b; ADR-0042 bölüm 18). Firma "kullan" deyince şablon taslak kural olur (çalışmaz); yayın ve
/// açma firmanın kararıdır. Bildirim şablonları alıcıyı kaydın kullanıcı alanına bağlar (sorumlu, onaylayan, zimmetli); alıcı kaydı göremiyorsa Core
/// bildirimi göndermez. Süreç şablonları çalışana işe giriş ya da işten çıkış süreci başlatır; bildirim ya da e-posta göndermez, kayıt değiştirmez.
/// </summary>
public static class HrAutomationTemplates
{
    public const string TaskAssigned = "hr.task-assigned-notify";

    public const string TaskDueTomorrow = "hr.task-due-reminder";

    public const string LeaveAwaitingApproval = "hr.leave-approval-notify";

    public const string ExpenseAwaitingApproval = "hr.expense-approval-notify";

    public const string AssetReturnOverdue = "hr.asset-return-overdue-notify";

    public const string OnboardingOnHire = "hr.onboarding-on-hire";

    public const string OffboardingOnTermination = "hr.offboarding-on-termination";

    private const string Task = HrModule.Entities.ChecklistTask;
    private const string Leave = HrModule.Entities.LeaveRequest;
    private const string Expense = HrModule.Entities.ExpenseClaim;
    private const string Assignment = HrModule.Entities.AssetAssignment;
    private const string Employee = HrModule.Entities.Employee;

    private static string Var(string entity, string field) => "{{" + entity + "." + field + "}}";

    public static IReadOnlyList<AutomationTemplateDefinition> All { get; } =
    [
        new(
            TaskAssigned,
            "Süreç görevi atanınca sorumluya bildir",
            "İşe giriş ya da çıkış sürecinde çalışana veya yöneticisine görev açılınca sorumlusunun bildirim kutusuna yazar (İK görevlerinde sorumlu boştur, bildirim gitmez).",
            Task,
            new AutomationDefinition(
                new RecordCreatedTrigger(),
                new ConditionLeaf($"{Task}.assigneeUserId", AutomationOperator.IsNotEmpty, []),
                [Notify($"{Task}.assigneeUserId", $"Yeni görev: {Var(Task, "title")} (son gün {Var(Task, "dueDate")})")])),
        new(
            TaskDueTomorrow,
            "Görevin son gününe 1 gün kala hatırlat",
            "Açık süreç görevinin son gününden bir gün önce sorumlusuna hatırlatma yazar.",
            Task,
            new AutomationDefinition(
                new DateRelativeTrigger($"{Task}.dueDate", -1),
                new ConditionGroup(ConditionLogic.And,
                [
                    new ConditionLeaf($"{Task}.status", AutomationOperator.Eq, [ChecklistTask.Statuses.Open]),
                    new ConditionLeaf($"{Task}.assigneeUserId", AutomationOperator.IsNotEmpty, []),
                ]),
                [Notify($"{Task}.assigneeUserId", $"Yarın son gün: {Var(Task, "title")}")])),
        new(
            LeaveAwaitingApproval,
            "İzin talebi açılınca onaylayana bildir",
            "Onay bekleyen yeni izin talebini, talebin onaylayanına (çalışanın yöneticisi) bildirir.",
            Leave,
            new AutomationDefinition(
                new RecordCreatedTrigger(),
                new ConditionGroup(ConditionLogic.And,
                [
                    new ConditionLeaf($"{Leave}.status", AutomationOperator.Eq, [LeaveRequest.Statuses.Pending]),
                    new ConditionLeaf($"{Leave}.approverUserId", AutomationOperator.IsNotEmpty, []),
                ]),
                [Notify($"{Leave}.approverUserId", $"Onayınızı bekleyen izin talebi: {Var(Leave, "employeeName")} ({Var(Leave, "startDate")} başlangıçlı)")])),
        new(
            ExpenseAwaitingApproval,
            "Masraf beyanı onaya gönderilince onaylayana bildir",
            "Masraf beyanı onaya gönderildiğinde beyanın onaylayanına bildirim yazar.",
            Expense,
            new AutomationDefinition(
                new StatusTransitionTrigger($"{Expense}.status", null, ExpenseClaim.Statuses.Submitted),
                new ConditionLeaf($"{Expense}.approverUserId", AutomationOperator.IsNotEmpty, []),
                [Notify($"{Expense}.approverUserId", $"Onayınızı bekleyen masraf beyanı: {Var(Expense, "title")}")])),
        new(
            AssetReturnOverdue,
            "Zimmet iade tarihi geçince zimmetliye bildir",
            "Beklenen iade gününün ertesi günü hâlâ zimmette olan demirbaş için zimmetli çalışana bildirim yazar.",
            Assignment,
            new AutomationDefinition(
                new DateRelativeTrigger($"{Assignment}.expectedReturnOn", 1),
                new ConditionGroup(ConditionLogic.And,
                [
                    new ConditionLeaf($"{Assignment}.status", AutomationOperator.Eq, [AssetAssignment.Statuses.Active]),
                    new ConditionLeaf($"{Assignment}.employeeUserId", AutomationOperator.IsNotEmpty, []),
                ]),
                [Notify($"{Assignment}.employeeUserId", $"İade tarihi geçti: {Var(Assignment, "assetTag")} ({Var(Assignment, "expectedReturnOn")})")])),
        new(
            OnboardingOnHire,
            "Çalışan eklenince işe giriş süreci başlat",
            "Yeni çalışan kartı açılınca işe giriş tarihini başlangıç sayarak türün ilk etkin işe giriş şablonuyla süreç başlatır.",
            Employee,
            new AutomationDefinition(
                new RecordCreatedTrigger(),
                new ConditionLeaf($"{Employee}.status", AutomationOperator.Ne, [Domain.Employee.Statuses.Terminated]),
                [StartChecklist(Checklists.Kinds.Onboarding, $"{Employee}.hireDate")])),
        new(
            OffboardingOnTermination,
            "Çalışan ayrılınca işten çıkış süreci başlat",
            "Çalışanın durumu \"Ayrıldı\" olunca ayrılış tarihini başlangıç sayarak türün ilk etkin işten çıkış şablonuyla süreç başlatır.",
            Employee,
            new AutomationDefinition(
                new StatusTransitionTrigger($"{Employee}.status", null, Domain.Employee.Statuses.Terminated),
                null,
                [StartChecklist(Checklists.Kinds.Offboarding, $"{Employee}.terminationDate")])),
    ];

    private static AutomationActionStep Notify(string recipientField, string title) =>
        new("bildir", HrAutomation.NotifyAction, new JsonObject
        {
            ["recipientId"] = new JsonObject { ["$field"] = recipientField },
            ["title"] = title,
        });

    private static AutomationActionStep StartChecklist(string kind, string startDateField) =>
        new("surec", HrAutomation.StartChecklist, new JsonObject
        {
            ["kind"] = kind,
            ["startDate"] = new JsonObject { ["$field"] = startDateField },
        });
}
