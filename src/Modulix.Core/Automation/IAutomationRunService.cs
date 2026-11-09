namespace Modulix.Core.Automation;

public sealed record AutomationRunQuery(
    Guid? RuleId = null, AutomationRunStatus? Status = null, Guid? RecordId = null, int Page = 1, int PageSize = 50)
{
    public const int MaxPageSize = 200;
}

public sealed record AutomationRunSummary(
    Guid Id,
    Guid RuleId,
    string RuleName,
    int VersionNo,
    string EntityKey,
    Guid RecordId,
    AutomationRunOrigin Origin,
    AutomationRunStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Note);

public sealed record AutomationRunStepInfo(
    int StepNo,
    string StepId,
    string Action,
    AutomationStepStatus Status,
    int Attempts,
    string? Summary,
    string? EffectEntityKey,
    Guid? EffectRecordId,
    DateTimeOffset? CompletedAt)
{
    /// <summary>Adım bir bekleme adımında uyuyorsa yeniden çağrılacağı an (kalıcı akış, Dilim 4c-4); bekleyen adımda dolu.</summary>
    public DateTimeOffset? WaitingUntil { get; init; }
}

public sealed record AutomationRunDetail(
    AutomationRunSummary Run, DateTimeOffset NextAttemptAt, IReadOnlyList<AutomationRunStepInfo> Steps);

public sealed record AutomationRunPage(IReadOnlyList<AutomationRunSummary> Items, int Page, int PageSize, int TotalCount);

/// <summary>Elle çalıştırma, yeniden deneme ya da iptal geçersiz (kural/çalışma durumu uygun değil); ileti kullanıcıya gösterilir.</summary>
public sealed class AutomationRunException(string message) : Exception(message);

/// <summary>Elle çalıştırma tekilleştirmeye takıldı: aynı kural sürümü bu kayıt için bugün zaten çalıştırıldı.</summary>
public sealed class AutomationRunDuplicateException(Guid existingRunId)
    : Exception("Bu kural bu kayıt için bugün zaten elle çalıştırıldı.")
{
    public Guid ExistingRunId { get; } = existingRunId;
}

/// <summary>
/// Çalışma geçmişi ve elle müdahale (ADR-0027, Dilim 2). Okuma <c>core.automation-logs.read</c>, elle çalıştırma/yeniden deneme/iptal
/// <c>core.automation.run</c> ister (API denetler). Elle çalıştırma, kuralın tetikleyicisi <see cref="Contracts.Automation.ManualTrigger"/>
/// ise ve çağıran kayıt boru hattından kaydı <b>kendi yetkisiyle</b> okuyabiliyorsa mümkündür; çalışma yine kuralın kendi kimliğiyle işler.
/// Her müdahale <c>core.automation-rule</c> kaydı olarak denetime yazılır.
/// </summary>
public interface IAutomationRunService
{
    Task<AutomationRunPage> ListAsync(AutomationRunQuery query, CancellationToken cancellationToken);

    /// <summary>Çalışma yoksa (ya da başka tenant'ın) null.</summary>
    Task<AutomationRunDetail?> GetAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Kural yok / açık değil / tetikleyicisi elle değil / kayıt okunamıyor: <see cref="AutomationRunException"/>. Bugün çalıştırıldıysa <see cref="AutomationRunDuplicateException"/>. Kural yoksa null.</summary>
    Task<AutomationRunDetail?> RunManuallyAsync(Guid ruleId, Guid recordId, CancellationToken cancellationToken);

    /// <summary>Başarısız çalışmayı baştan (başarısız adımdan) tekrar kuyruğa alır; aynı idempotency anahtarları sürer. Çalışma yoksa null.</summary>
    Task<AutomationRunDetail?> RetryAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Bitmemiş çalışmayı iptal eder (çalışmamış adımlar iptal olur). Çalışma yoksa null.</summary>
    Task<AutomationRunDetail?> CancelAsync(Guid runId, CancellationToken cancellationToken);
}
