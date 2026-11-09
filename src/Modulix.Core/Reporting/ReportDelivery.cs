using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Reporting;

public enum ReportExportSource
{
    Manual = 1,
    Schedule = 2,
}

/// <summary>Dışa aktarma dosya biçimi. Zamanlanmış çalışma yalnızca CSV üretir.</summary>
public enum ReportFileFormat
{
    Csv = 1,
    Xlsx = 2,
}

/// <summary>
/// Hazırlanmış dışa aktarma (ADR-0035, dilim 4): rapor sonucunun CSV dosyası. Dosya yalnızca sahibi tarafından, indirme anında yetki yeniden
/// denetlenerek indirilir; <see cref="AccessStamp"/> oluşturulduğu andaki erişim parmak izidir (izinler, veri kapsamı, kayıt sahipleri): kişinin
/// yetkisi sonradan değiştiyse eski dosya indirilemez, yeniden hazırlanması gerekir. Dosya <see cref="ExpiresAt"/>'te geçersiz olur ve silinir.
/// Tenant verisidir; Row-Level Security ile korunur.
/// </summary>
public sealed class ReportExport : ITenantEntity, ICompanyShared
{
    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>Dosyanın saklanma süresi.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>Bir kullanıcının aynı anda saklayabileceği en fazla dosya (süresi geçmemiş).</summary>
    public const int MaxActivePerUser = 20;

    private ReportExport()
    {
        // EF Core için
    }

    public ReportExport(
        Guid ownerUserId, Guid? reportId, string reportName, string datasetKey, string fileName, byte[] content, int rowCount, bool truncated,
        string accessStamp, ReportExportSource source, DateTimeOffset now, ReportFileFormat format = ReportFileFormat.Csv)
    {
        Id = Guid.CreateVersion7();
        OwnerUserId = ownerUserId;
        ReportId = reportId;
        ReportName = reportName;
        DatasetKey = datasetKey;
        FileName = fileName;
        ContentType = format == ReportFileFormat.Xlsx ? ReportXlsx.ContentType : "text/csv; charset=utf-8";
        Content = content;
        SizeBytes = content.Length;
        RowCount = rowCount;
        Truncated = truncated;
        AccessStamp = accessStamp;
        Source = source;
        CreatedAt = now;
        ExpiresAt = now + Retention;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid OwnerUserId { get; private set; }

    /// <summary>Kayıtlı rapordan üretildiyse raporun kimliği; rapor silinse de dosya süresi dolana kadar kalır (zayıf bağ).</summary>
    public Guid? ReportId { get; private set; }

    public string ReportName { get; private set; } = string.Empty;

    public string DatasetKey { get; private set; } = string.Empty;

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public byte[] Content { get; private set; } = [];

    public long SizeBytes { get; private set; }

    public int RowCount { get; private set; }

    /// <summary>Okuma sınırına takıldı: dosya eksik olabilir (kullanıcıya da söylenir).</summary>
    public bool Truncated { get; private set; }

    public string AccessStamp { get; private set; } = string.Empty;

    public ReportExportSource Source { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public int DownloadCount { get; private set; }

    public DateTimeOffset? LastDownloadedAt { get; private set; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public void MarkDownloaded(DateTimeOffset now)
    {
        DownloadCount++;
        LastDownloadedAt = now;
    }
}

/// <summary>
/// Zamanlanmış rapor (ADR-0035, dilim 4): kayıtlı raporu belirli aralıkla <b>sahibinin yetkisiyle</b> çalıştırıp CSV hazırlar ve sahibine indirme
/// bağlantısı e-postalar (veri e-postaya konmaz). Çalışma anında sahibin üyeliği, modül, rapor görünürlüğü ve izinler yeniden denetlenir;
/// biri bozulduysa zamanlama <see cref="StoppedReason"/> ile durdurulur ve hiçbir şey teslim edilmez. Tenant verisidir.
/// </summary>
public sealed class ReportSchedule : ITenantEntity, ICompanyShared
{
    public const int MessageMaxLength = 300;

    private ReportSchedule()
    {
        // EF Core için
    }

    public ReportSchedule(
        Guid reportId, Guid ownerUserId, ScheduleFrequency frequency, int hour, DayOfWeek? dayOfWeek, int? dayOfMonth, DateTimeOffset nextRunAt, DateTimeOffset now,
        ReportFileFormat format = ReportFileFormat.Csv)
    {
        Id = Guid.CreateVersion7();
        Format = format;
        ReportId = reportId;
        OwnerUserId = ownerUserId;
        Frequency = frequency;
        Hour = hour;
        DayOfWeek = dayOfWeek;
        DayOfMonth = dayOfMonth;
        NextRunAt = nextRunAt;
        Enabled = true;
        CreatedAt = now;
        UpdatedAt = now;
        Version = 1;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ReportId { get; private set; }

    public Guid OwnerUserId { get; private set; }

    public ScheduleFrequency Frequency { get; private set; }

    /// <summary>Firmanın saat diliminde yerel saat (0-23).</summary>
    public int Hour { get; private set; }

    /// <summary>Zamanlanmış dosyanın biçimi (varsayılan CSV).</summary>
    public ReportFileFormat Format { get; private set; } = ReportFileFormat.Csv;

    public DayOfWeek? DayOfWeek { get; private set; }

    public int? DayOfMonth { get; private set; }

    public bool Enabled { get; private set; }

    public DateTimeOffset NextRunAt { get; private set; }

    public DateTimeOffset? LastRunAt { get; private set; }

    /// <summary>Son çalışmanın durumu: Delivered, Stopped, Failed.</summary>
    public string? LastStatus { get; private set; }

    public string? LastMessage { get; private set; }

    /// <summary>Zamanlama kendiliğinden durdurulduysa nedeni (yetki kaybı, üyelik kapalı, rapor yok...).</summary>
    public string? StoppedReason { get; private set; }

    /// <summary>Aynı planlı an için art arda başarısız deneme sayısı (teslimde sıfırlanır); bkz. <see cref="MaxAttempts"/>.</summary>
    public int Attempts { get; private set; }

    /// <summary>Bir planlı an için en çok deneme (ilk deneme + 2 yeniden deneme). Sonra zamanlama bir sonraki döneme geçer.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Yeniden deneme gecikmesi: 1. denemeden sonra 15 dk, 2.den sonra 60 dk.</summary>
    public static TimeSpan RetryDelay(int attempts) => attempts <= 1 ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(60);

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public void Reschedule(
        ScheduleFrequency frequency, int hour, DayOfWeek? dayOfWeek, int? dayOfMonth, DateTimeOffset nextRunAt, DateTimeOffset now, ReportFileFormat format = ReportFileFormat.Csv)
    {
        Frequency = frequency;
        Format = format;
        Hour = hour;
        DayOfWeek = dayOfWeek;
        DayOfMonth = dayOfMonth;
        NextRunAt = nextRunAt;
        Enabled = true;
        StoppedReason = null;
        Touch(now);
    }

    public void SetEnabled(bool enabled, DateTimeOffset nextRunAt, DateTimeOffset now)
    {
        Enabled = enabled;
        if (enabled)
        {
            NextRunAt = nextRunAt;
            StoppedReason = null;
        }

        Touch(now);
    }

    /// <summary>Deneme başarısız: sınır dolmadıysa aynı dönem için kısa gecikmeyle yeniden denenir, dolduysa bir sonraki döneme geçilir.</summary>
    public void RecordFailure(string? message, DateTimeOffset nextPeriodRunAt, DateTimeOffset now)
    {
        Attempts++;
        var retry = Attempts < MaxAttempts;
        LastRunAt = now;
        LastStatus = "Failed";
        LastMessage = Trim(message);
        NextRunAt = retry ? now + RetryDelay(Attempts) : nextPeriodRunAt;
        if (!retry)
        {
            Attempts = 0;
        }

        Touch(now);
    }

    public void RecordRun(string status, string? message, DateTimeOffset nextRunAt, DateTimeOffset now)
    {
        Attempts = 0;
        LastRunAt = now;
        LastStatus = status;
        LastMessage = Trim(message);
        NextRunAt = nextRunAt;
        Touch(now);
    }

    /// <summary>Teslim edilemez: zamanlama durur (bir sonraki çalışma yok) ve nedeni saklanır.</summary>
    public void Stop(string reason, DateTimeOffset now)
    {
        Enabled = false;
        LastRunAt = now;
        LastStatus = "Stopped";
        LastMessage = Trim(reason);
        StoppedReason = Trim(reason);
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }

    private static string? Trim(string? message) =>
        message is { Length: > MessageMaxLength } ? message[..MessageMaxLength] : message;
}

/// <summary>
/// Bir zamanlama çalışması. (zamanlama, slot) benzersizdir: aynı slot'u iki uygulama örneği ya da tekrar teslim aynı anda başlatsa bile yalnızca biri
/// kaydı yazabilir; diğeri atlar, bu yüzden aynı dönem için iki dosya ya da iki e-posta oluşmaz.
/// </summary>
public sealed class ReportScheduleRun : ITenantEntity, ICompanyShared
{
    private ReportScheduleRun()
    {
        // EF Core için
    }

    public ReportScheduleRun(Guid scheduleId, DateTimeOffset slot, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        ScheduleId = scheduleId;
        Slot = slot;
        Status = "Started";
        StartedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ScheduleId { get; private set; }

    /// <summary>Çalışmanın ait olduğu planlı an (zamanlamanın o andaki <c>NextRunAt</c> değeri).</summary>
    public DateTimeOffset Slot { get; private set; }

    /// <summary>Started, Delivered, Stopped, Failed.</summary>
    public string Status { get; private set; } = string.Empty;

    public Guid? ExportId { get; private set; }

    public string? Message { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public void Finish(string status, Guid? exportId, string? message, DateTimeOffset now)
    {
        Status = status;
        ExportId = exportId;
        Message = message is { Length: > ReportSchedule.MessageMaxLength } ? message[..ReportSchedule.MessageMaxLength] : message;
        FinishedAt = now;
    }
}

/// <summary>
/// Zamanlanmış raporun dağıtım listesindeki bir alıcı. Alıcı sahibin dosyasını değil, <b>kendi yetkisiyle üretilmiş</b> kendi dosyasını alır: raporu göremiyorsa,
/// rapor merkezi ya da dışa aktarma izni yoksa ya da firmanın etkin üyesi değilse o çalışmada atlanır (veri başkasının kapsamıyla gitmez).
/// </summary>
public sealed class ReportScheduleRecipient : ITenantEntity, ICompanyShared
{
    private ReportScheduleRecipient()
    {
        // EF Core için
    }

    public ReportScheduleRecipient(Guid scheduleId, Guid userId, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        ScheduleId = scheduleId;
        UserId = userId;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ScheduleId { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Dışa aktarma dosyasının üst bilgisi (içerik yok).</summary>
public sealed record ReportExportInfo(
    Guid Id,
    Guid? ReportId,
    string ReportName,
    string DatasetKey,
    string FileName,
    long SizeBytes,
    int RowCount,
    bool Truncated,
    ReportExportSource Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int DownloadCount,
    bool Expired);

public enum ReportDownloadStatus
{
    Ok = 1,
    NotFound = 2,
    Expired = 3,

    /// <summary>İndirme anında izinler yeniden denetlendi ve yetersiz (rapor merkezi, dışa aktarma ya da veri izni; modül kapalı).</summary>
    Forbidden = 4,

    /// <summary>Dosya hazırlandığından beri kişinin yetkisi/veri kapsamı değişti; dosya artık verilmez, yeniden hazırlanmalı.</summary>
    AccessChanged = 5,
}

public sealed record ReportDownload(ReportDownloadStatus Status, string? Message, string? FileName = null, string? ContentType = null, byte[]? Content = null);

/// <summary>
/// Dışa aktarma (ADR-0035, dilim 4). Dosya, ekrandaki sonuçla aynı hesaptan (<see cref="IReportRunner"/>) çağıranın yetkisiyle üretilir; hassas
/// alan, veri kapsamı ve tenant sınırı çalıştırıcıda uygulanır. Yetki (<c>core.reports.export</c>) uç noktada denetlenir; indirme anında ayrıca
/// <see cref="DownloadAsync"/> içinde yeniden denetlenir.
/// </summary>
public interface IReportExports
{
    /// <summary>Kayıtlı raporu (çağıranın görebildiği) dışa aktarır. Rapor görünmüyorsa null.</summary>
    Task<ReportExportInfo?> CreateAsync(Guid reportId, CancellationToken cancellationToken, ReportFileFormat format = ReportFileFormat.Csv);

    /// <summary>Kayıtsız tanımı (oluşturucudaki önizleme) dışa aktarır.</summary>
    Task<ReportExportInfo> CreateAsync(ReportSpec spec, string? name, CancellationToken cancellationToken, ReportFileFormat format = ReportFileFormat.Csv);

    /// <summary>
    /// Zamanlanmış çalışma için: kayıtlı raporu çağıranın (zamanlama sahibinin) bağlamında dışa aktarır, kaynak <see cref="ReportExportSource.Schedule"/>.
    /// Rapor görünmüyorsa null.
    /// </summary>
    Task<ReportExportInfo?> CreateScheduledAsync(Guid reportId, CancellationToken cancellationToken, ReportFileFormat format = ReportFileFormat.Csv);

    /// <summary>
    /// Modülün kendi hesapladığı hazır sonucu (ör. satış genel görünümü kart ayrıntısı) dosyaya çevirir; aynı dosya kuralları geçerlidir (sahibine özel,
    /// 24 saat, sınır, denetim). <paramref name="datasetKey"/> erişim kuralını taşır: çağıran o veri kümesini okuyamıyorsa dosya hazırlanmaz ve indirme
    /// anında da yeniden denetlenir. Sonucun içeriği çağıranın yetkisiyle üretilmiş olmalıdır (burada yeniden hesap yoktur).
    /// </summary>
    Task<ReportExportInfo> CreateFromResultAsync(string name, string datasetKey, ReportResult result, ReportFileFormat format, CancellationToken cancellationToken);

    /// <summary>Çağıranın kendi dosyaları, en yeni üstte (süresi geçmişler henüz silinmediyse işaretli).</summary>
    Task<IReadOnlyList<ReportExportInfo>> ListAsync(CancellationToken cancellationToken);

    Task<ReportDownload> DownloadAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Çağıranın kendi dosyasını siler; yoksa false.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Süresi geçmiş dosyaları siler (aktif tenant); silinen adet.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken);
}

public sealed record ReportScheduleInfo(
    Guid Id,
    Guid ReportId,
    string? ReportName,
    ScheduleFrequency Frequency,
    int Hour,
    DayOfWeek? DayOfWeek,
    int? DayOfMonth,
    bool Enabled,
    DateTimeOffset NextRunAt,
    DateTimeOffset? LastRunAt,
    string? LastStatus,
    string? LastMessage,
    string? StoppedReason,
    long Version,
    ReportFileFormat Format = ReportFileFormat.Csv,
    IReadOnlyList<ReportScheduleRecipientInfo>? Recipients = null);

/// <summary>Dağıtım listesindeki alıcı; <see cref="DisplayName"/> etkin üye değilse null (eski üye).</summary>
public sealed record ReportScheduleRecipientInfo(Guid UserId, string? DisplayName);

/// <param name="RecipientIds">
/// Sahibi dışındaki alıcılar (en çok <see cref="ReportScheduleRules.MaxRecipients"/>, hepsi firmanın etkin üyesi). <c>null</c> güncellemede listeye dokunmaz,
/// boş liste listeyi temizler; sahibin kendisi listeye yazılmaz (zaten alır).
/// </param>
public sealed record ReportScheduleInput(
    ScheduleFrequency Frequency, int Hour, DayOfWeek? DayOfWeek, int? DayOfMonth, ReportFileFormat Format = ReportFileFormat.Csv, IReadOnlyList<Guid>? RecipientIds = null);

/// <summary>
/// Kullanıcının zamanlanmış raporları (ADR-0035, dilim 4); yalnızca kendi zamanlamalarını görür ve yönetir. İzin (<c>core.reports.schedule</c>) uç
/// noktada denetlenir; zamanlama çalışırken yetkiler yeniden denetlenir (bkz. <see cref="ReportSchedule"/>).
/// </summary>
public interface IReportSchedules
{
    Task<IReadOnlyList<ReportScheduleInfo>> ListAsync(Guid? reportId, CancellationToken cancellationToken);

    /// <summary>Rapor görünmüyorsa null. Geçersiz girdi <see cref="ReportException"/>.</summary>
    Task<ReportScheduleInfo?> CreateAsync(Guid reportId, ReportScheduleInput input, CancellationToken cancellationToken);

    /// <summary>Zamanlama yoksa (ya da çağıranın değilse) null. Yeniden açar ve bir sonraki çalışmayı hesaplar.</summary>
    Task<ReportScheduleInfo?> UpdateAsync(Guid id, ReportScheduleInput input, long? expectedVersion, CancellationToken cancellationToken);

    Task<ReportScheduleInfo?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
