using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;

namespace Modulix.Modules.Hr;

/// <summary>
/// İK modülünün lookup alanları (ADR-0016): çalışanın kişi kaydı, yöneticisi (başka bir çalışan kartı), bağlı kullanıcı hesabı ve
/// izin talebi ile izin hakkının çalışanı.
/// Yönetici etiketi çalışan kaydının hesaplanan "fullName" alanıdır (kişi kaydından gelir).
/// </summary>
public static class HrLookupDefinitions
{
    public const string EmployeePerson = "hr.employee.person";
    public const string EmployeeManager = "hr.employee.manager";
    public const string EmployeeUser = "hr.employee.user";
    public const string LeaveRequestEmployee = "hr.leave-request.employee";
    public const string LeaveBalanceEmployee = "hr.leave-balance.employee";
    public const string ExpenseClaimEmployee = "hr.expense-claim.employee";

    private static readonly string[] PersonLabel = ["firstName", "lastName"];
    private static readonly string[] PersonSecondary = ["email", "phone"];
    private static readonly string[] EmployeeLabel = ["fullName"];
    private static readonly string[] EmployeeSecondary = ["jobTitle", "employeeNumber"];

    public static IReadOnlyList<LookupDefinition> All { get; } =
    [
        new(EmployeePerson, HrModule.Entities.Employee, "personId", "Kişi", LookupTargetKind.Record, SharedEntities.Person)
        {
            LabelFields = PersonLabel,
            SecondaryFields = PersonSecondary,
            SearchDescription = "Ad, soyad ve e-posta",
        },
        new(EmployeeManager, HrModule.Entities.Employee, "managerEmployeeId", "Yönetici", LookupTargetKind.Record, HrModule.Entities.Employee)
        {
            LabelFields = EmployeeLabel,
            SecondaryFields = EmployeeSecondary,
            SearchDescription = "Ad, soyad, e-posta, telefon, sicil numarası ve unvan",
        },
        new(EmployeeUser, HrModule.Entities.Employee, "userId", "Kullanıcı hesabı", LookupTargetKind.Member),
        Employee(LeaveRequestEmployee, HrModule.Entities.LeaveRequest),
        Employee(LeaveBalanceEmployee, HrModule.Entities.LeaveBalance),
        Employee(ExpenseClaimEmployee, HrModule.Entities.ExpenseClaim),
    ];

    private static LookupDefinition Employee(string key, string sourceEntity) =>
        new(key, sourceEntity, "employeeId", "Çalışan", LookupTargetKind.Record, HrModule.Entities.Employee)
        {
            LabelFields = EmployeeLabel,
            SecondaryFields = EmployeeSecondary,
            SearchDescription = "Ad, soyad, e-posta, telefon, sicil numarası ve unvan",
        };
}
