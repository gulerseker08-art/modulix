using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Modules.Hr.Domain;

/// <summary>
/// İK tablolarının ortak tabanı. Tenant verisidir (<see cref="ITenantEntity"/>): sorgu filtresi ve Row-Level Security uygulanır.
/// Dilim 1'de çalışan kartı şirketler arasında ortaktır (<see cref="ICompanyShared"/>, ADR-0042 bölüm 4); işveren şirket bağı
/// şirket kapsamlı kayıtların uçtan uca testiyle birlikte sonraki dilimde gelir.
/// </summary>
public abstract class HrEntity : ITenantEntity, ICompanyShared
{
    protected HrEntity()
    {
        Id = Guid.CreateVersion7();
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        Version = 1;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Her değişiklikte artar; eşzamanlı güncellemeleri yakalamak için kullanılır (409).</summary>
    public long Version { get; private set; }

    /// <summary>Firmanın tanımladığı özel alanların değerleri (Y9a), JSON nesnesi; boşsa null.</summary>
    public string? CustomFields { get; internal set; }

    internal void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}

/// <summary>
/// Çalışan kartı (özlük, ADR-0042 K2): ortak kişi kaydının (<c>core.Person</c>) İK uzantısıdır; ad, iş telefonu ve iş e-postası kişi
/// kaydındadır, burada tekrar tutulmaz. Bir kişinin tek çalışan kartı olur. Kullanıcı hesabı isteğe bağlıdır (hesabı olmayan çalışan ve
/// çalışan olmayan kullanıcı desteklenir, yol haritası M4.1); bağlanırsa bir kullanıcı tek karta bağlanır ve izin, masraf gibi self servis
/// işlemlerde "ben" bu kart olur. Yönetici başka bir çalışan kartıdır; organizasyon şeması bu bağdan çizilir (döngü yasaktır).
/// Özlük bilgileri (TC kimlik no, doğum tarihi, ev adresi, acil durum kişisi, IBAN) hassas alandır (K4). Kart silinmez (K9): ayrılan
/// çalışanın durumu "Ayrıldı" yapılır, kayıt yasal saklama için kalır.
/// </summary>
public sealed class Employee : HrEntity
{
    public const int EmployeeNumberMaxLength = 30;
    public const int JobTitleMaxLength = 150;
    public const int WorkLocationMaxLength = 150;
    public const int NationalIdLength = 11;
    public const int AddressMaxLength = 500;
    public const int EmergencyNameMaxLength = 150;
    public const int PhoneMaxLength = 50;
    public const int IbanLength = 26;
    public const int NotesMaxLength = 2000;

    /// <summary>Çalışma şekli (sabit; kod buna göre davranır, ör. stajyerde izin hakkı kuralı).</summary>
    public static class EmploymentTypes
    {
        public const string FullTime = "fullTime";
        public const string PartTime = "partTime";
        public const string FixedTerm = "fixedTerm";
        public const string Intern = "intern";

        public static IReadOnlyList<string> All { get; } = [FullTime, PartTime, FixedTerm, Intern];
    }

    public static class Statuses
    {
        public const string Active = "active";
        public const string OnLeave = "onLeave";
        public const string Terminated = "terminated";

        public static IReadOnlyList<string> All { get; } = [Active, OnLeave, Terminated];
    }

    /// <summary>Çalışanın kişi kaydı; yalnızca oluştururken seçilir. Tenant içinde bir kişinin tek çalışan kartı olur.</summary>
    public Guid PersonId { get; internal set; }

    /// <summary>Sicil numarası (büyük harf); verilirse tenant içinde benzersiz.</summary>
    public string? EmployeeNumber { get; internal set; }

    /// <summary>Bağlı kullanıcı hesabı (üyelik kullanıcı kimliği); verilirse tenant içinde tek karta bağlanır.</summary>
    public Guid? UserId { get; internal set; }

    public string? JobTitle { get; internal set; }

    /// <summary>Departman (Y9b seçim listesi <c>hr.department</c>); boş olabilir.</summary>
    public string? DepartmentKey { get; internal set; }

    /// <summary>Bağlı olduğu yönetici (başka bir çalışan kartı); en üst yöneticide boştur.</summary>
    public Guid? ManagerEmployeeId { get; internal set; }

    public string EmploymentType { get; internal set; } = EmploymentTypes.FullTime;

    public string? WorkLocation { get; internal set; }

    public DateOnly HireDate { get; internal set; }

    /// <summary>İşten ayrılış tarihi; ileri tarihli olabilir (planlanan çıkış). "Ayrıldı" durumunda zorunludur.</summary>
    public DateOnly? TerminationDate { get; internal set; }

    public string Status { get; internal set; } = Statuses.Active;

    // Özlük bilgileri: hassas alanlar (hr.employees.personal izni olmadan okunamaz, yazılamaz ve süzülemez).

    /// <summary>T.C. kimlik numarası (11 hane, denetim haneli); verilirse tenant içinde benzersiz.</summary>
    public string? NationalId { get; internal set; }

    public DateOnly? BirthDate { get; internal set; }

    public string? HomeAddress { get; internal set; }

    public string? EmergencyContactName { get; internal set; }

    public string? EmergencyContactPhone { get; internal set; }

    /// <summary>Maaş ödemesi için IBAN (boşluksuz, büyük harf; TR ve 26 karakter).</summary>
    public string? Iban { get; internal set; }

    public string? Notes { get; internal set; }
}
