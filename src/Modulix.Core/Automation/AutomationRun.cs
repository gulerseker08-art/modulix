using System.Security.Cryptography;
using System.Text;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Automation;

public enum AutomationRunOrigin
{
    /// <summary>Kayıt olayı (ADR-0007) kuralı tetikledi.</summary>
    Event = 1,

    /// <summary>Yetkili bir kullanıcı elle çalıştırdı.</summary>
    Manual = 2,

    /// <summary>Günlük tarama (zamanlanmış, tarihe bağlı ya da hareketsizlik tetikleyicisi) kaydı seçti (Dilim 2b).</summary>
    Scan = 3,
}

public enum AutomationRunStatus
{
    /// <summary>Tetikleyici eşleşti; koşul henüz değerlendirilmedi.</summary>
    Pending = 1,

    /// <summary>Koşul sağlandı; adımlar sırayla çalışıyor ya da yeniden deneme bekliyor.</summary>
    Running = 2,

    Succeeded = 3,

    /// <summary>Bir adım kalıcı olarak başarısız oldu (ya da deneme hakkı bitti); kalan adımlar iptal edildi.</summary>
    Failed = 4,

    /// <summary>Kural kapandı, firma ya da platform anahtarı kapandı ya da yetkili durdurdu; çalışmayan adımlar iptal.</summary>
    Cancelled = 5,

    /// <summary>Koşul sağlanmadı; aksiyon çalışmadı.</summary>
    NotMatched = 6,
}

public enum AutomationStepStatus
{
    Pending = 1,
    Succeeded = 2,

    /// <summary>Etki zaten vardı ya da koşul gereği yapılmadı (hata değil).</summary>
    Skipped = 3,

    Failed = 4,
    Cancelled = 5,
}

/// <summary>
/// Kural çalışması (ADR-0027, Dilim 2): bir kural sürümünün bir kayıt için tek bir tetiklenmesi. <see cref="DedupeKey"/>
/// (tenant + olay + kural sürümü + hedef kayıt) tenant içinde tekildir: aynı olayın ikinci kez teslimi ikinci çalışma üretmez.
/// Çalışma başladığı <b>sürüme</b> bağlı kalır; kural sonradan yeniden yayınlansa da yürüyen çalışma eski sürümle biter.
/// Kayıt değeri saklanmaz (kişisel veri yok): parametreler çalışma anında kayıttan yeniden çözülür.
/// </summary>
public sealed class AutomationRun : ITenantEntity, ICompanyShared
{
    public const int DedupeKeyLength = 64;
    public const int NoteMaxLength = 500;
    public const int CorrelationMaxLength = 100;

    private AutomationRun()
    {
        // EF Core için
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RuleId { get; private set; }

    public Guid RuleVersionId { get; private set; }

    public string EntityKey { get; private set; } = string.Empty;

    public Guid RecordId { get; private set; }

    public AutomationRunOrigin Origin { get; private set; }

    /// <summary>Tetikleyen olayın kimliği (olay yolu); elle çalıştırmada boş.</summary>
    public Guid? EventId { get; private set; }

    public AutomationRunStatus Status { get; private set; }

    public string DedupeKey { get; private set; } = string.Empty;

    /// <summary>Tetikleyen olayın izi; zincirleme (kural kaynaklı) olaylar aynı izi taşır.</summary>
    public string? CorrelationId { get; private set; }

    public int Depth { get; private set; }

    /// <summary>Elle çalıştıran kullanıcı; olay yolunda boş.</summary>
    public Guid? InitiatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Bu andan önce işlenmez (ilk işlem için oluşturma anı; yeniden denemede geri çekilme).</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    public Guid? LeaseToken { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Kullanıcıya gösterilen kısa sonuç; kayıt değeri ve kişisel veri içermez.</summary>
    public string? Note { get; private set; }

    public bool IsTerminal => Status is AutomationRunStatus.Succeeded or AutomationRunStatus.Failed
        or AutomationRunStatus.Cancelled or AutomationRunStatus.NotMatched;

    public static AutomationRun Create(
        Guid tenantId, Guid ruleId, Guid ruleVersionId, string entityKey, Guid recordId, AutomationRunOrigin origin, Guid? eventId,
        string dedupeKey, string? correlationId, int depth, Guid? initiatedBy, DateTimeOffset now)
    {
        return new AutomationRun
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            RuleId = ruleId,
            RuleVersionId = ruleVersionId,
            EntityKey = entityKey,
            RecordId = recordId,
            Origin = origin,
            EventId = eventId,
            Status = AutomationRunStatus.Pending,
            DedupeKey = dedupeKey,
            CorrelationId = correlationId is { Length: > CorrelationMaxLength } ? correlationId[..CorrelationMaxLength] : correlationId,
            Depth = depth,
            InitiatedBy = initiatedBy,
            CreatedAt = now,
            NextAttemptAt = now,
        };
    }
}

/// <summary>
/// Çalışmanın bir aksiyon adımı. Adımlar sırayla çalışır; bir adım bitmeden sonraki başlamaz. <see cref="IdempotencyKey"/> adımın
/// bütün denemelerinde aynıdır ve aksiyona geçirilir: yeniden deneme ikinci etki üretmez.
/// </summary>
public sealed class AutomationRunStep : ITenantEntity, ICompanyShared
{
    public const int StepIdMaxLength = 50;
    public const int ActionMaxLength = 100;
    public const int SummaryMaxLength = 500;

    private AutomationRunStep()
    {
        // EF Core için
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RunId { get; private set; }

    /// <summary>Çalışma içindeki sıra (1'den).</summary>
    public int StepNo { get; private set; }

    /// <summary>Kural tanımındaki adım kimliği.</summary>
    public string StepId { get; private set; } = string.Empty;

    public string Action { get; private set; } = string.Empty;

    public AutomationStepStatus Status { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    public int Attempts { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Aksiyonun özeti ya da son hata; kişisel veri içermez.</summary>
    public string? Summary { get; private set; }

    /// <summary>Aksiyonun etkilediği kayıt (varsa).</summary>
    public string? EffectEntityKey { get; private set; }

    public Guid? EffectRecordId { get; private set; }

    public static AutomationRunStep Create(Guid tenantId, Guid runId, int stepNo, string stepId, string action, string idempotencyKey) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            RunId = runId,
            StepNo = stepNo,
            StepId = stepId,
            Action = action,
            Status = AutomationStepStatus.Pending,
            IdempotencyKey = idempotencyKey,
        };
}

/// <summary>Tekilleştirme ve idempotency anahtarları (ADR-0027): tenant + olay + kural sürümü + hedef kayıt (+ adım).</summary>
public static class AutomationRunKeys
{
    /// <summary>Olay yolunda olay kimliği, elle çalıştırmada <paramref name="trigger"/> olarak "manual:" + tenant günü verilir.</summary>
    public static string Dedupe(Guid tenantId, string trigger, Guid ruleVersionId, string entityKey, Guid recordId) =>
        Hash($"{tenantId:N}|{trigger}|{ruleVersionId:N}|{entityKey}|{recordId:N}");

    public static string Step(string dedupeKey, string stepId) => Hash($"{dedupeKey}|{stepId}");

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Yeniden deneme çizelgesi (ADR-0007 ile aynı basamaklar); belirsiz sonuçta bile ikinci etki idempotency anahtarıyla engellenir.</summary>
public static class AutomationRetryPolicy
{
    public const int MaxAttempts = 5;

    /// <summary>1.-4. başarısız denemeden sonra bekleme: 1 dk, 5 dk, 15 dk, 1 sa.</summary>
    public static TimeSpan Delay(int failedAttempts) => failedAttempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromHours(1),
    };
}

/// <summary>Tenant başına koruma sınırları (ADR-0027): bir kiracı diğerlerinin işini aç bırakamaz.</summary>
public static class AutomationLimits
{
    /// <summary>Etkin (yayınlanıp açık) kural sayısı.</summary>
    public const int MaxActiveRulesPerTenant = 200;

    /// <summary>Bir olayın tek bir kural için üretebileceği çalışma sayısı sınırı yoktur (tekilleştirme sınırlar); bir olayın aday kural sayısı bu kadarla kesilir.</summary>
    public const int MaxRulesPerEvent = 50;
}
