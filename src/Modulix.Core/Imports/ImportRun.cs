using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Imports;

public enum ImportRunMode
{
    /// <summary>Aynı doğrulama yazmadan çalışır; sonuç ekranda görünür.</summary>
    DryRun = 1,

    /// <summary>Hatasız satırlar kayıt boru hattından geçirilerek yazılır.</summary>
    Apply = 2,
}

public enum ImportRunStatus
{
    Pending = 1,
    Running = 2,

    /// <summary>Bütün satırlar işlendi (oluşturuldu, güncellendi ya da bilerek atlandı); hatalı satır yok.</summary>
    Succeeded = 3,

    /// <summary>Hatasız satırlar alındı, hatalı satırlar alınmadı (hata raporu indirilebilir).</summary>
    PartiallySucceeded = 4,

    /// <summary>Hiçbir satır alınmadı: dosya geçersiz, bütün satırlar hatalı ya da "hepsi ya da hiç" kuralı bozuldu.</summary>
    Failed = 5,

    Cancelled = 6,

    /// <summary>Alınan kayıtlar toplu olarak geri alındı.</summary>
    RolledBack = 7,
}

public enum ImportFailurePolicy
{
    /// <summary>Varsayılan: hatasız satırlar alınır, hatalılar rapora yazılır (ADR-0033).</summary>
    SkipInvalidRows = 1,

    /// <summary>Tek hatalı satır bütün yüklemeyi durdurur.</summary>
    AllOrNothing = 2,
}

public enum ImportIssueSeverity
{
    Error = 1,
    Warning = 2,
}

/// <summary>Satır hatası kodları: kapalı liste; süzgeç ve "en sık hatalar" özeti için kullanılır (ADR-0033).</summary>
public static class ImportIssueCodes
{
    public const string RequiredMissing = "required-missing";
    public const string FormatInvalid = "format-invalid";
    public const string LookupUnresolved = "lookup-unresolved";
    public const string DuplicateExact = "duplicate-exact";
    public const string DuplicatePossible = "duplicate-possible";
    public const string CustomFieldInvalid = "custom-field-invalid";
    public const string PermissionDenied = "permission-denied";
    public const string BusinessRule = "business-rule";
    public const string FileInvalid = "file-invalid";
    public const string SystemError = "system-error";

    /// <summary>Aynı dosya (özeti aynı) aynı kayıt türüne daha önce uygulanmış: uyarıdır, yüklemeyi engellemez (ADR-0033 Dilim 4b).</summary>
    public const string FileRepeated = "file-repeated";

    public static IReadOnlyList<string> All { get; } =
    [
        RequiredMissing, FormatInvalid, LookupUnresolved, DuplicateExact, DuplicatePossible,
        CustomFieldInvalid, PermissionDenied, BusinessRule, FileInvalid, SystemError, FileRepeated,
    ];

    public static bool IsKnown(string code) => All.Contains(code, StringComparer.Ordinal);
}

/// <summary>
/// İçe aktarma işi (ADR-0033): bir dosyanın bir kayıt türüne yüklenmesi. Satır değeri saklanmaz; yalnızca kim, hangi dosya
/// (adı, boyutu, özeti), hangi mod ve kaç satırın ne olduğu tutulur. Dosya özeti aynı dosyanın tekrar yüklenmesini
/// ve "hepsi ya da hiç" için kuru çalışma eşlemesini mümkün kılar. Kayıtlar kayıt boru hattından yazılır; bu tablo yalnızca izdir.
/// </summary>
public sealed class ImportRun : ITenantEntity, ICompanyShared
{
    public const int FileNameMaxLength = 255;
    public const int FileHashLength = 64;
    public const int CorrelationMaxLength = 64;
    public const int NoteMaxLength = 500;

    /// <summary>Koşu başına saklanan satır hatası üst sınırı; fazlası yalnızca <see cref="FailedCount"/> ile sayılır.</summary>
    public const int MaxStoredIssues = 5000;

    private ImportRun()
    {
        // EF Core için
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>İçe aktarmayı başlatan kullanıcı.</summary>
    public Guid UserId { get; private set; }

    public string EntityKey { get; private set; } = string.Empty;

    public string FileName { get; private set; } = string.Empty;

    public long FileSize { get; private set; }

    /// <summary>Dosyanın SHA-256 özeti (küçük harf onaltılık).</summary>
    public string FileHash { get; private set; } = string.Empty;

    /// <summary>İçe aktarma şablonunun sürümü; şablon değişince eski dosyalar fark edilir.</summary>
    public int TemplateVersion { get; private set; }

    public ImportRunMode Mode { get; private set; }

    public ImportFailurePolicy Policy { get; private set; }

    public ImportRunStatus Status { get; private set; }

    public int TotalRows { get; private set; }

    public int CreatedCount { get; private set; }

    public int UpdatedCount { get; private set; }

    public int SkippedCount { get; private set; }

    public int FailedCount { get; private set; }

    /// <summary>Koşuyu içe aktarma ile oluşan denetim kayıtlarına bağlayan iz (<c>AuditLog.CorrelationId</c>).</summary>
    public string? CorrelationId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Kullanıcıya gösterilen kısa sonuç; satır değeri ve kişisel veri içermez.</summary>
    public string? Note { get; private set; }

    public bool IsTerminal => Status is ImportRunStatus.Succeeded or ImportRunStatus.PartiallySucceeded
        or ImportRunStatus.Failed or ImportRunStatus.Cancelled or ImportRunStatus.RolledBack;

    public static ImportRun Create(
        Guid tenantId, Guid userId, string entityKey, string fileName, long fileSize, string fileHash, int templateVersion,
        ImportRunMode mode, ImportFailurePolicy policy, string? correlationId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        if (fileHash is not { Length: FileHashLength } || !fileHash.All(c => char.IsAsciiHexDigitLower(c)))
        {
            throw new ArgumentException("Dosya özeti 64 karakterlik küçük harf onaltılık SHA-256 olmalı.", nameof(fileHash));
        }

        return new ImportRun
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            EntityKey = entityKey,
            FileName = Truncate(fileName, FileNameMaxLength),
            FileSize = fileSize,
            FileHash = fileHash,
            TemplateVersion = templateVersion,
            Mode = mode,
            Policy = policy,
            Status = ImportRunStatus.Pending,
            CorrelationId = correlationId is null ? null : Truncate(correlationId, CorrelationMaxLength),
            CreatedAt = now,
        };
    }

    public void Start(int totalRows, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalRows);
        if (Status != ImportRunStatus.Pending)
        {
            throw new InvalidOperationException("Yalnızca bekleyen içe aktarma başlatılabilir.");
        }

        Status = ImportRunStatus.Running;
        TotalRows = totalRows;
        StartedAt = now;
    }

    /// <summary>
    /// Koşuyu sonuç sayılarıyla bitirir. Durumu sayılar belirler: hata yoksa başarılı, hem alınan hem hatalı satır varsa kısmi,
    /// hiçbiri alınmadıysa başarısız. "Hepsi ya da hiç" kuralında hatalı satır varken hiçbir satır alınmış olamaz.
    /// </summary>
    public void Complete(int created, int updated, int skipped, int failed, string? note, DateTimeOffset now)
    {
        if (Status != ImportRunStatus.Running)
        {
            throw new InvalidOperationException("Yalnızca çalışan içe aktarma bitirilebilir.");
        }

        if (created < 0 || updated < 0 || skipped < 0 || failed < 0 || created + updated + skipped + failed > TotalRows)
        {
            throw new ArgumentException("Satır sayıları tutarsız: toplamı dosyadaki satır sayısını aşamaz.");
        }

        var accepted = created + updated;
        if (Policy == ImportFailurePolicy.AllOrNothing && failed > 0 && accepted > 0)
        {
            throw new InvalidOperationException("'Hepsi ya da hiç' kuralında hatalı satır varken kayıt alınamaz.");
        }

        CreatedCount = created;
        UpdatedCount = updated;
        SkippedCount = skipped;
        FailedCount = failed;
        Status = failed == 0
            ? ImportRunStatus.Succeeded
            : accepted > 0 ? ImportRunStatus.PartiallySucceeded : ImportRunStatus.Failed;
        Finish(note, now);
    }

    /// <summary>Dosya okunamadı ya da iş hiç başlayamadı (şablon uyuşmazlığı, bozuk dosya, sistem hatası).</summary>
    public void Fail(string note, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        if (IsTerminal)
        {
            throw new InvalidOperationException("Biten içe aktarma tekrar başarısız yapılamaz.");
        }

        Status = ImportRunStatus.Failed;
        Finish(note, now);
    }

    /// <summary>Çalışan koşunun o ana kadarki sayıları: ekran ilerlemeyi gösterir, çökmede alınan satır sayısı bilinir.</summary>
    public void ReportProgress(int created, int failed)
    {
        if (Status != ImportRunStatus.Running)
        {
            throw new InvalidOperationException("Yalnızca çalışan içe aktarmanın ilerlemesi yazılır.");
        }

        CreatedCount = created;
        FailedCount = failed;
    }

    /// <summary>
    /// Uygulama kapanırken yarıda kalan koşu: o ana kadar işlenen satırlar kalıcıdır ve sayıları korunur, kalan satırlar işlenmemiştir.
    /// Aynı dosya yeniden yüklenirse alınmış satırlar tekrar oluşur; bu yüzden not bunu söyler.
    /// </summary>
    public void Stop(int created, int failed, string note, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        if (Status != ImportRunStatus.Running)
        {
            throw new InvalidOperationException("Yalnızca çalışan içe aktarma durdurulabilir.");
        }

        CreatedCount = created;
        FailedCount = failed;
        Status = ImportRunStatus.Cancelled;
        Finish(note, now);
    }

    public void Cancel(DateTimeOffset now)
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException("Biten içe aktarma iptal edilemez.");
        }

        Status = ImportRunStatus.Cancelled;
        Finish(null, now);
    }

    private void Finish(string? note, DateTimeOffset now)
    {
        CompletedAt = now;
        Note = note is null ? null : Truncate(note, NoteMaxLength);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// Satır hatası (ADR-0033). <b>Ham hücre değeri saklanmaz</b>: yalnızca satır no, sütun anahtarı, hata kodu ve değer içermeyen kısa
/// ileti. Düzeltme için asıl satır, yüklenen dosyadan indirme anında üretilen hata raporunda verilir.
/// </summary>
public sealed class ImportRowIssue : ITenantEntity, ICompanyShared
{
    public const int ColumnKeyMaxLength = 100;
    public const int CodeMaxLength = 40;
    public const int MessageMaxLength = 500;

    private ImportRowIssue()
    {
        // EF Core için
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RunId { get; private set; }

    /// <summary>Dosyadaki satır numarası (başlık satırı dahil, 1'den başlar); dosya düzeyi hatada 0.</summary>
    public int RowNo { get; private set; }

    public string? ColumnKey { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public ImportIssueSeverity Severity { get; private set; }

    public string Message { get; private set; } = string.Empty;

    /// <summary>Saklama süresi temizliği (firma ayarı) için.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    public static ImportRowIssue Create(
        Guid tenantId, Guid runId, int rowNo, string? columnKey, string code, ImportIssueSeverity severity, string message, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowNo);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!ImportIssueCodes.IsKnown(code))
        {
            throw new ArgumentException($"Bilinmeyen hata kodu: '{code}'.", nameof(code));
        }

        return new ImportRowIssue
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            RunId = runId,
            RowNo = rowNo,
            ColumnKey = columnKey is null ? null : (columnKey.Length <= ColumnKeyMaxLength ? columnKey : columnKey[..ColumnKeyMaxLength]),
            Code = code,
            Severity = severity,
            Message = message.Length <= MessageMaxLength ? message : message[..MessageMaxLength],
            CreatedAt = now,
        };
    }
}
