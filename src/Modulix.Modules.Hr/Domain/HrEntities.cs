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

/// <summary>
/// Yıllık izin hakkı (ADR-0042 dilim 2): bir çalışanın bir takvim yılındaki hakkı. Hak ve devreden gün İK tarafından girilir; kıdeme göre
/// otomatik hak ediş hesabı mevzuat doğrulaması bekler (ADR-0042 bölüm 8), koda yazılmaz. Kullanılan gün yalnızca onaylanan ve iptal edilen
/// yıllık izin taleplerinden sunucuda güncellenir; sürüm alanı eşzamanlı iki onayın bakiyeyi aşmasını engeller (M4.3). Kayıt sahibi çalışanın
/// kullanıcısıdır (veri kapsamı "kendi": çalışan yalnızca kendi hakkını görür).
/// </summary>
public sealed class LeaveBalance : HrEntity
{
    public const decimal MaxDays = 366m;
    public const int NotesMaxLength = 1000;

    public Guid EmployeeId { get; internal set; }

    /// <summary>Takvim yılı (izin talebi yılın içinde kalır).</summary>
    public int Year { get; internal set; }

    public decimal EntitledDays { get; internal set; }

    public decimal CarriedOverDays { get; internal set; }

    /// <summary>Onaylanmış yıllık izinlerin toplamı; yalnızca izin talebi iş akışı yazar.</summary>
    public decimal UsedDays { get; internal set; }

    /// <summary>Çalışanın bağlı kullanıcısı (veri kapsamı için; çalışan kartından kopyalanır).</summary>
    public Guid? OwnerId { get; internal set; }

    public string? Notes { get; internal set; }

    public decimal RemainingDays => EntitledDays + CarriedOverDays - UsedDays;

    internal void AddUsed(decimal days)
    {
        UsedDays += days;
        Touch();
    }
}

/// <summary>
/// İzin talebi (ADR-0042 dilim 2). Çalışan kendisi için (ya da İK herkes için) açar; gün sayısı sunucuda hafta sonu ve resmî tatiller düşülerek
/// hesaplanır, yarım gün yalnızca tek günlük talepte seçilir. Talep açılırken çalışanın yöneticisinin kullanıcısı onaylayan olarak kaydedilir:
/// yönetici sonradan değişse de bu talebin onay mercii değişmez (M4.2). Durum yalnızca komutla değişir (onayla, reddet, iptal); onaylanan yıllık
/// izin aynı işlemde bakiyeden düşer, onaylanmış iznin iptali bakiyeye geri ekler. Talep değiştirilmez ve silinmez: iptal edilip yenisi açılır.
/// </summary>
public sealed class LeaveRequest : HrEntity
{
    public const int TextMaxLength = 1000;
    public const int ReasonMaxLength = 500;

    public static class Statuses
    {
        public const string Pending = "pending";
        public const string Approved = "approved";
        public const string Rejected = "rejected";
        public const string Cancelled = "cancelled";

        public static IReadOnlyList<string> All { get; } = [Pending, Approved, Rejected, Cancelled];

        /// <summary>Takvimde yer tutan (çakışma denetimine giren) durumlar.</summary>
        public static IReadOnlyList<string> Active { get; } = [Pending, Approved];
    }

    public Guid EmployeeId { get; internal set; }

    /// <summary>İzin türü (Y9b seçim listesi <c>hr.leave-type</c>); yalnızca "yıllık izin" kategorisi bakiyeden düşer.</summary>
    public string LeaveTypeKey { get; internal set; } = string.Empty;

    /// <summary>Talep anındaki tür kategorisi (tür listesi sonradan değişse de bakiye hesabı bozulmaz).</summary>
    public string LeaveCategory { get; internal set; } = string.Empty;

    public DateOnly StartDate { get; internal set; }

    public DateOnly EndDate { get; internal set; }

    public bool HalfDay { get; internal set; }

    /// <summary>İş günü sayısı (yarım gün 0,5); sunucuda hesaplanır.</summary>
    public decimal Days { get; internal set; }

    public string? Description { get; internal set; }

    public string Status { get; internal set; } = Statuses.Pending;

    /// <summary>Talebin sahibi: çalışanın bağlı kullanıcısı (veri kapsamı; çalışanın hesabı yoksa boş).</summary>
    public Guid? OwnerId { get; internal set; }

    /// <summary>Talep anındaki onaylayan: çalışanın yöneticisinin kullanıcısı (yoksa boş; yalnızca İK onaylar).</summary>
    public Guid? ApproverUserId { get; internal set; }

    public Guid? DecidedByUserId { get; internal set; }

    public DateTimeOffset? DecidedAt { get; internal set; }

    /// <summary>Ret ya da iptal gerekçesi.</summary>
    public string? DecisionReason { get; internal set; }
}

/// <summary>
/// Resmî tatil ve idari izin günü (ADR-0042 dilim 2): izin gün sayısından düşülür. Firma kendi listesini girer; tarihler koda yazılmaz
/// (dinî bayramlar her yıl değişir). Yarım gün tatil (arife) yarım gün düşer.
/// </summary>
public sealed class Holiday : HrEntity
{
    public const int NameMaxLength = 150;

    public DateOnly Date { get; internal set; }

    public string Name { get; internal set; } = string.Empty;

    public bool HalfDay { get; internal set; }
}

/// <summary>
/// Masraf beyanı (ADR-0042 dilim 3): çalışanın bir dönemdeki harcamalarını topladığı belge; tutarlar satırlardadır, toplam sunucuda tutulur.
/// Durum yalnızca komutla değişir: taslak → onaya gönderildi → onaylandı → ödendi; onaya gönderilen beyan düzeltme için taslağa iade edilebilir,
/// reddedilebilir; taslak ve onay bekleyen beyan iptal edilebilir. Onaylayan, gönderim anındaki yöneticinin kullanıcısıdır ve değişmez (M4.2).
/// Ödeme kaydı (kasa/banka) finans işidir (ADR-0037); burada yalnızca "ödendi" bilgisi ve ödeme referansı tutulur. Beyan silinmez.
/// </summary>
public sealed class ExpenseClaim : HrEntity
{
    public const int TitleMaxLength = 200;
    public const int TextMaxLength = 1000;
    public const int ReasonMaxLength = 500;

    public static class Statuses
    {
        public const string Draft = "draft";
        public const string Submitted = "submitted";
        public const string Approved = "approved";
        public const string Paid = "paid";
        public const string Rejected = "rejected";
        public const string Cancelled = "cancelled";

        public static IReadOnlyList<string> All { get; } = [Draft, Submitted, Approved, Paid, Rejected, Cancelled];
    }

    public Guid EmployeeId { get; internal set; }

    public string Title { get; internal set; } = string.Empty;

    /// <summary>ISO 4217 para birimi; satırlar beyanın para birimindedir.</summary>
    public string Currency { get; internal set; } = "TRY";

    public string? Description { get; internal set; }

    /// <summary>Satırların toplamı (KDV dahil); yalnızca satır deposu yazar.</summary>
    public decimal TotalAmount { get; internal set; }

    public int LineCount { get; internal set; }

    public string Status { get; internal set; } = Statuses.Draft;

    /// <summary>Çalışanın bağlı kullanıcısı (veri kapsamı).</summary>
    public Guid? OwnerId { get; internal set; }

    /// <summary>Gönderim anındaki onaylayan (yöneticinin kullanıcısı); yoksa yalnızca İK karar verir.</summary>
    public Guid? ApproverUserId { get; internal set; }

    public DateTimeOffset? SubmittedAt { get; internal set; }

    public Guid? DecidedByUserId { get; internal set; }

    public DateTimeOffset? DecidedAt { get; internal set; }

    /// <summary>İade, ret ya da iptal gerekçesi.</summary>
    public string? DecisionReason { get; internal set; }

    public Guid? PaidByUserId { get; internal set; }

    public DateTimeOffset? PaidAt { get; internal set; }

    /// <summary>Ödemenin dekont/havale referansı (serbest metin).</summary>
    public string? PaymentReference { get; internal set; }

    internal void SetTotals(decimal total, int count)
    {
        TotalAmount = total;
        LineCount = count;
        Touch();
    }
}

/// <summary>
/// Masraf satırı (ADR-0042 dilim 3): tarih, masraf türü (Y9b <c>hr.expense-type</c>), açıklama, KDV dahil tutar ve isteğe bağlı KDV tutarı. Fiş ya da
/// fatura satıra dosya olarak eklenir (ADR-0040). Satır yalnızca beyan taslakken eklenir, değişir ve silinir; beyanın toplamı aynı işlemde güncellenir.
/// Görünürlük beyanınkiyle aynıdır (sahip ve onaylayan).
/// </summary>
public sealed class ExpenseLine : HrEntity
{
    public const int DescriptionMaxLength = 500;
    public const decimal MaxAmount = 1_000_000m;

    public Guid ClaimId { get; internal set; }

    public DateOnly ExpenseDate { get; internal set; }

    public string ExpenseTypeKey { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    /// <summary>KDV dahil tutar (beyanın para biriminde).</summary>
    public decimal Amount { get; internal set; }

    public decimal? VatAmount { get; internal set; }

    /// <summary>Beyanın sahibi (veri kapsamı; beyandan kopyalanır, değişmez).</summary>
    public Guid? OwnerId { get; internal set; }
}

/// <summary>
/// Demirbaş (ADR-0042 dilim 4): çalışana zimmetlenebilen eşya (dizüstü bilgisayar, telefon, giriş kartı, alet...). Envanter numarası tenant içinde
/// tektir. Durum elle yalnızca depoda / bakımda / hurda / kayıp arasında değişir; "zimmetli" durumu ve şu anki zimmetli çalışan yalnızca zimmet
/// kaydından türetilir. Araçlar Filo modülündedir, burada tutulmaz. Zimmetlenmiş demirbaş silinmez (geçmiş tutanaklar korunur).
/// </summary>
public sealed class Asset : HrEntity
{
    public const int TagMaxLength = 50;
    public const int NameMaxLength = 200;
    public const int SerialMaxLength = 100;
    public const int BrandMaxLength = 100;
    public const int NotesMaxLength = 2000;

    public static class Statuses
    {
        public const string InStock = "inStock";
        public const string Assigned = "assigned";
        public const string Maintenance = "maintenance";
        public const string Retired = "retired";
        public const string Lost = "lost";

        public static IReadOnlyList<string> All { get; } = [InStock, Assigned, Maintenance, Retired, Lost];

        /// <summary>Elle seçilebilen durumlar ("zimmetli" yalnızca zimmetle gelir).</summary>
        public static IReadOnlyList<string> Manual { get; } = [InStock, Maintenance, Retired, Lost];
    }

    /// <summary>Envanter (demirbaş) numarası; büyük harf, tenant içinde tekil.</summary>
    public string AssetTag { get; internal set; } = string.Empty;

    public string Name { get; internal set; } = string.Empty;

    /// <summary>Demirbaş kategorisi (Y9b seçim listesi <c>hr.asset-category</c>).</summary>
    public string CategoryKey { get; internal set; } = string.Empty;

    public string? SerialNumber { get; internal set; }

    public string? Brand { get; internal set; }

    public string? Model { get; internal set; }

    public DateOnly? PurchaseDate { get; internal set; }

    public decimal? PurchaseCost { get; internal set; }

    public string? Currency { get; internal set; }

    public string Status { get; internal set; } = Statuses.InStock;

    /// <summary>Şu anki zimmet (yalnızca zimmet kaydı yazar).</summary>
    public Guid? CurrentAssignmentId { get; internal set; }

    public Guid? CurrentEmployeeId { get; internal set; }

    /// <summary>Şu anki zimmetli çalışanın kullanıcısı (veri kapsamı: çalışan üzerindeki demirbaşı görür).</summary>
    public Guid? CurrentUserId { get; internal set; }

    public string? Notes { get; internal set; }

    internal void AssignTo(Guid assignmentId, Guid employeeId, Guid? userId)
    {
        CurrentAssignmentId = assignmentId;
        CurrentEmployeeId = employeeId;
        CurrentUserId = userId;
        Status = Statuses.Assigned;
        Touch();
    }

    internal void Release(string status)
    {
        CurrentAssignmentId = null;
        CurrentEmployeeId = null;
        CurrentUserId = null;
        Status = status;
        Touch();
    }
}

/// <summary>
/// Zimmet (ADR-0042 dilim 4): bir demirbaşın bir çalışana teslimi. Açılınca demirbaş aynı işlemde "zimmetli" olur; çalışan teslim aldığını ekrandan
/// onaylar; iade ya da kayıp komutla kapanır ve demirbaş depoya (ya da kayba) döner. Bir demirbaşın aynı anda tek açık zimmeti olur. Zimmet
/// değiştirilmez ve silinmez; tutanağı PDF olarak alınır. Kayıt sahibi çalışanın kullanıcısıdır (çalışan kendi zimmetlerini görür).
/// </summary>
public sealed class AssetAssignment : HrEntity
{
    public const int TextMaxLength = 1000;

    public static class Statuses
    {
        public const string Active = "active";
        public const string Returned = "returned";
        public const string Lost = "lost";

        public static IReadOnlyList<string> All { get; } = [Active, Returned, Lost];
    }

    public Guid AssetId { get; internal set; }

    public Guid EmployeeId { get; internal set; }

    public DateOnly AssignedOn { get; internal set; }

    public DateOnly? ExpectedReturnOn { get; internal set; }

    /// <summary>Teslimdeki durum ve aksesuarlar (ör. "Şarj aleti ve çanta ile, çiziksiz").</summary>
    public string? ConditionOut { get; internal set; }

    public string Status { get; internal set; } = Statuses.Active;

    public Guid? OwnerId { get; internal set; }

    public Guid? AssignedByUserId { get; internal set; }

    /// <summary>Çalışanın teslim aldığını onayladığı an.</summary>
    public DateTimeOffset? AcknowledgedAt { get; internal set; }

    public DateOnly? ReturnedOn { get; internal set; }

    /// <summary>İadedeki durum ya da kayıp açıklaması.</summary>
    public string? ConditionIn { get; internal set; }

    public Guid? ClosedByUserId { get; internal set; }
}

/// <summary>Süreç türleri ve görev sorumluları (dilim 5): sabit, kod buna göre davranır.</summary>
public static class Checklists
{
    public static class Kinds
    {
        public const string Onboarding = "onboarding";
        public const string Offboarding = "offboarding";

        public static IReadOnlyList<string> All { get; } = [Onboarding, Offboarding];
    }

    /// <summary>Görevin sorumlusu: çalışanın kendisi, yöneticisi ya da İK (İK görevini "süreç yönetimi" izni olan herkes tamamlar).</summary>
    public static class Responsibles
    {
        public const string Employee = "employee";
        public const string Manager = "manager";
        public const string Hr = "hr";

        public static IReadOnlyList<string> All { get; } = [Employee, Manager, Hr];
    }

    /// <summary>Görev türü: elle tamamlanan ya da çalışanın açık zimmeti kalmayınca tamamlanabilen "zimmet iadesi".</summary>
    public static class TaskTypes
    {
        public const string Manual = "manual";
        public const string AssetReturn = "assetReturn";

        public static IReadOnlyList<string> All { get; } = [Manual, AssetReturn];
    }
}

/// <summary>
/// İşe giriş ya da çıkış süreci şablonu (ADR-0042 dilim 5): adımların listesi. Şablon değişikliği yalnızca sonra başlatılan süreçleri etkiler;
/// başlatılmış süreç kendi görevlerini taşır. Kullanılmış şablon silinmez, pasife alınır.
/// </summary>
public sealed class ChecklistTemplate : HrEntity
{
    public const int NameMaxLength = 150;
    public const int TextMaxLength = 1000;

    public string Name { get; internal set; } = string.Empty;

    public string Kind { get; internal set; } = Checklists.Kinds.Onboarding;

    public string? Description { get; internal set; }

    public bool IsActive { get; internal set; } = true;
}

/// <summary>Şablon adımı: başlık, sorumlu, başlangıç tarihine göre gün farkı (çıkışta eksi olabilir) ve görev türü.</summary>
public sealed class ChecklistTemplateItem : HrEntity
{
    public const int TitleMaxLength = 200;
    public const int TextMaxLength = 1000;
    public const int MaxOffsetDays = 365;

    public Guid TemplateId { get; internal set; }

    public int Sequence { get; internal set; }

    public string Title { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    public string ResponsibleRole { get; internal set; } = Checklists.Responsibles.Hr;

    /// <summary>Son tarih = sürecin başlangıç tarihi + bu kadar gün.</summary>
    public int DueOffsetDays { get; internal set; }

    public string TaskType { get; internal set; } = Checklists.TaskTypes.Manual;
}

/// <summary>
/// Çalışana başlatılmış işe giriş ya da çıkış süreci (ADR-0042 dilim 5). Başlatılırken şablonun adımları aynı işlemde görevlere dönüşür; sorumlu
/// kullanıcı (çalışan, yöneticisi) o anda çözülür. Bütün görevler tamamlanınca ya da atlanınca süreç kendiliğinden tamamlanır; çıkış sürecindeki
/// "zimmet iadesi" görevi çalışanın açık zimmeti varken tamamlanamaz, bu yüzden açık zimmetle çıkış süreci kapanmaz. Süreç silinmez, iptal edilir.
/// </summary>
public sealed class EmployeeChecklist : HrEntity
{
    public const int ReasonMaxLength = 500;

    public static class Statuses
    {
        public const string Open = "open";
        public const string Completed = "completed";
        public const string Cancelled = "cancelled";

        public static IReadOnlyList<string> All { get; } = [Open, Completed, Cancelled];
    }

    public Guid EmployeeId { get; internal set; }

    public Guid TemplateId { get; internal set; }

    /// <summary>Şablondan kopyalanır (şablon sonradan değişse de sürecin türü ve adı korunur).</summary>
    public string Kind { get; internal set; } = Checklists.Kinds.Onboarding;

    public string Name { get; internal set; } = string.Empty;

    /// <summary>Görev son tarihlerinin hesaplandığı gün (işe girişte giriş tarihi, çıkışta ayrılış tarihi önerilir).</summary>
    public DateOnly StartDate { get; internal set; }

    public string Status { get; internal set; } = Statuses.Open;

    public int TaskCount { get; internal set; }

    /// <summary>Tamamlanan ya da atlanan görev sayısı.</summary>
    public int ClosedCount { get; internal set; }

    public Guid? OwnerId { get; internal set; }

    public Guid? StartedByUserId { get; internal set; }

    public DateTimeOffset? CompletedAt { get; internal set; }

    public string? CancelReason { get; internal set; }

    internal void SetProgress(int total, int closed)
    {
        TaskCount = total;
        ClosedCount = closed;
        if (Status != Statuses.Cancelled)
        {
            var done = total > 0 && closed == total;
            Status = done ? Statuses.Completed : Statuses.Open;
            CompletedAt = done ? CompletedAt ?? DateTimeOffset.UtcNow : null;
        }

        Touch();
    }
}

/// <summary>Süreç görevi: şablon adımının bu çalışana açılmış kopyası. Yalnızca komutla tamamlanır, atlanır ya da yeniden açılır.</summary>
public sealed class ChecklistTask : HrEntity
{
    public const int NoteMaxLength = 500;

    public static class Statuses
    {
        public const string Open = "open";
        public const string Done = "done";
        public const string Skipped = "skipped";

        public static IReadOnlyList<string> All { get; } = [Open, Done, Skipped];
    }

    public Guid ChecklistId { get; internal set; }

    public Guid EmployeeId { get; internal set; }

    public int Sequence { get; internal set; }

    public string Title { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    public string ResponsibleRole { get; internal set; } = Checklists.Responsibles.Hr;

    /// <summary>Görevi yapacak kullanıcı (çalışan ya da yöneticisi); İK görevinde boş.</summary>
    public Guid? AssigneeUserId { get; internal set; }

    public DateOnly DueDate { get; internal set; }

    public string TaskType { get; internal set; } = Checklists.TaskTypes.Manual;

    public string Status { get; internal set; } = Statuses.Open;

    /// <summary>Çalışanın kullanıcısı (veri kapsamı).</summary>
    public Guid? OwnerId { get; internal set; }

    public Guid? CompletedByUserId { get; internal set; }

    public DateTimeOffset? CompletedAt { get; internal set; }

    public string? Note { get; internal set; }
}

/// <summary>
/// Kurum içi duyuru (ADR-0042 K7, dilim 6). Yayında olan, başlangıç günü gelmiş ve süresi dolmamış duyuruyu hedef kitle görür: hedef departman boşsa
/// herkes, doluysa yalnızca o departmandaki çalışanlar (kullanıcının çalışan kartından). Sabitlenmiş duyuru listenin başındadır. Metin düz metindir;
/// ekranda HTML olarak yorumlanmaz. Okundu bilgisi ayrı kayıtta tutulur (<see cref="AnnouncementRead"/>).
/// </summary>
public sealed class Announcement : HrEntity
{
    public const int TitleMaxLength = 200;
    public const int BodyMaxLength = 8000;

    public string Title { get; internal set; } = string.Empty;

    public string Body { get; internal set; } = string.Empty;

    /// <summary>Hedef departman (Y9b <c>hr.department</c>); boşsa bütün çalışanlar.</summary>
    public string? AudienceDepartmentKey { get; internal set; }

    public bool IsPinned { get; internal set; }

    public bool IsPublished { get; internal set; }

    /// <summary>Okurlara görünmeye başladığı gün.</summary>
    public DateOnly PublishOn { get; internal set; }

    /// <summary>Bu günden sonra okurlara görünmez; boşsa süresiz.</summary>
    public DateOnly? ExpiresOn { get; internal set; }

    public Guid? AuthorUserId { get; internal set; }
}

/// <summary>Duyurunun bir kullanıcı tarafından okunduğu an (duyuru başına kullanıcı tekil). Okur duyuruyu açınca yazılır; değişmez.</summary>
public sealed class AnnouncementRead : HrEntity
{
    public Guid AnnouncementId { get; internal set; }

    public Guid UserId { get; internal set; }

    public DateTimeOffset ReadAt { get; internal set; }
}
