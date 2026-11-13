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

    public const string EmployeeEntityKey = "hr.employee";

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

    /// <summary>Özlük alanları (hassas; <see cref="EmployeesPersonal"/> izni olmadan sunucu göndermez).</summary>
    public static IReadOnlyList<string> PersonalFields { get; } =
        ["nationalId", "birthDate", "homeAddress", "emergencyContactName", "emergencyContactPhone", "iban"];
}
