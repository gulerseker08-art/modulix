using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;

namespace Modulix.Infrastructure.Automation;

/// <summary>"Automation" yapılandırma bölümü.</summary>
public sealed class AutomationOptions
{
    public const string SectionName = "Automation";

    /// <summary>Platform acil durdurma anahtarı: kapalıysa hiçbir tenant'ın kuralı işlenmez (çalışmalar kuyrukta bekler, iptal edilmez).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Arka plan işçisi çalışsın mı? Entegrasyon testleri kapatır ve <see cref="AutomationRunner"/>'ı kendisi çağırır.</summary>
    public bool WorkerEnabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>Bir tenant için bir turda işlenen en çok çalışma (adillik: bir tenant diğerlerinin işini aç bırakamaz).</summary>
    public int RunsPerTenantPerRound { get; set; } = 25;

    /// <summary>Tek bir aksiyon adımının azami süresi.</summary>
    public int StepTimeoutSeconds { get; set; } = 90;

    /// <summary>Tenant başına etkin (yayınlanıp açık) kural sınırı; her olay etkin kuralları taradığından bir firma ortak kaynağı şişiremesin.</summary>
    public int MaxActiveRulesPerTenant { get; set; } = AutomationLimits.MaxActiveRulesPerTenant;

    /// <summary>Bir tenant için bir turda taramanın okuyabileceği en çok kayıt (tarama sayfa sayfa, birden çok turda ilerler).</summary>
    public int ScanRecordsPerTenantPerRound { get; set; } = 500;

    /// <summary>Kayıt kaynağından tek seferde istenen kayıt sayısı.</summary>
    public int ScanPageSize { get; set; } = 200;

    /// <summary>Bekleyen çalışma sayısı bu sınıra ulaşınca tarama yeni kayıt almaz; işleyici yetişince sürer.</summary>
    public int ScanBacklogLimit { get; set; } = 1000;

    /// <summary>Bitmiş (başarılı, iptal, koşulu sağlanmayan) çalışmaların ve adımlarının saklanacağı gün sayısı.</summary>
    public int RunRetentionDays { get; set; } = 90;

    /// <summary>Başarısız çalışmaların saklanacağı gün sayısı (inceleme için daha uzun).</summary>
    public int FailedRunRetentionDays { get; set; } = 180;

    /// <summary>Saklama temizliği turları arasındaki süre (dakika); varsayılan 6 saat. Acil durdurma anahtarından bağımsızdır.</summary>
    public int RetentionIntervalMinutes { get; set; } = 360;
}

/// <summary>Bir tenant için bir turun özeti.</summary>
/// <remarks><c>Scanned</c>: tarama tetikleyicilerinden bu turda kuyruğa alınan çalışma sayısı. <c>Waiting</c>: bir bekleme adımında uykuya geçen çalışma sayısı (kalıcı akış).</remarks>
public sealed record AutomationRound(int Evaluated, int NotMatched, int StepsRun, int Retrying, int Failed, int Cancelled, int Scanned = 0, int Waiting = 0);

/// <summary>
/// Kural çalışmalarını işler (ADR-0027, Dilim 2). Bir tur bir tenant içindir ve tenant başına oturum kilidiyle (<c>sp_getapplock</c>) tek örnekte
/// koşar; kilidi alamayan örnek turu atlar. İşlem sırası:
/// <list type="number">
///   <item><b>Acil durdurma:</b> platform anahtarı (<see cref="AutomationOptions.Enabled"/>) kapalıysa hiçbir şey işlenmez; firma ayarı
///   (<c>core.automation.enabled</c>) kapalıysa kuyruktaki çalışmalar iptal edilir.</item>
///   <item><b>Kira:</b> zamanı gelen çalışma koşullu UPDATE ile kiralanır (kira belirteciyle); kira bitmeden başka işleyici almaz. Sonuçlar
///   kira belirteci şartıyla yazılır, kirasını yitiren işleyici sonuç yazamaz.</item>
///   <item><b>Kimlik:</b> koşul ve aksiyonlar, çalışmayı tetikleyen kullanıcının değil <b>kuralın</b> kimliğiyle çalışır: izinleri yalnızca kural
///   tanımının gerektirdikleridir (<see cref="AutomationRuleIdentity"/>), kayıt kapsamı tenant geneli. Kural yaptığı değişikliği olay zincirine bağlar
///   (<see cref="EventHandlingScope"/>): aynı iz, derinlik + 1.</item>
///   <item><b>Değerlendirme:</b> kayıt boru hattından okunur (alan yetkisi uygulanır), koşul yan etkisiz değerlendirilir. Sağlanmazsa
///   <see cref="AutomationRunStatus.NotMatched"/>; sağlanırsa adımlar yazılır.</item>
///   <item><b>Adımlar:</b> sırayla, her biri kendi idempotency anahtarıyla. Geçici hata artan aralıkla yeniden denenir
///   (<see cref="AutomationRetryPolicy"/>); kalıcı hata ya da deneme hakkı bitince çalışma <see cref="AutomationRunStatus.Failed"/> olur, kalan adımlar iptal edilir.</item>
///   <item><b>Kalıcı akış (Dilim 4c-4):</b> <see cref="AutomationActionStatus.Waiting"/> dönen adım bekler; çalışma kirasını bırakır ve
///   <see cref="AutomationActionResult.ResumeAt"/> anında aynı adımdan sürer. <see cref="AutomationActionResult.StopFlow"/> akışı bilerek bitirir:
///   kalan adımlar atlanır, çalışma başarılı biter.</item>
///   <item><b>Dallanma (Dilim 4c-5):</b> koşullu adım (<see cref="AutomationActionStep.When"/>) ilk denemesinde kaydın güncel değerleriyle
///   sorulur; sağlanmazsa atlanır ve akış sürer. "Akışı bitir" adımı akışı bilerek bitirir.</item>
/// </list>
/// Loglara tenant, çalışma ve kural kimlikleri yazılır; kayıt değeri ve parametre yazılmaz.
/// </summary>
public sealed class AutomationRunner(
    IServiceScopeFactory scopes, ModuleCatalog catalog, AutomationScanner scanner, IOptions<AutomationOptions> options, TimeProvider time,
    ILogger<AutomationRunner> logger)
{
    public const string LockName = "modulix.automation";

    /// <summary>Çalışmanın kirası; işleyici bu sürede bitiremezse çalışma başka işleyiciye geçer.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(3);

    public static string LockResource(Guid tenantId) => $"{LockName}:{tenantId:N}";

    /// <summary>
    /// Kural yeteneği beyan eden bir modülü açık olan aktif tenant'lar (tek platform sorgusu). Kural yazılabilen kayıt türü yoksa boş.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> TenantIdsAsync(CancellationToken cancellationToken)
    {
        var moduleKeys = catalog.AutomationEntities.Select(e => e.ModuleKey).Distinct(StringComparer.Ordinal).ToList();
        if (moduleKeys.Count == 0)
        {
            return [];
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        return await (
            from tenant in db.Tenants.AsNoTracking()
            join entitlement in db.ModuleEntitlements.AsNoTracking() on tenant.Id equals entitlement.TenantId
            where tenant.Status == TenantStatus.Active && entitlement.IsEnabled && moduleKeys.Contains(entitlement.ModuleKey)
            select tenant.Id)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    /// <summary>Bir tenant için bir tur. <c>Skipped</c>: başka bir örnek aynı tenant için çalıştığından tur atlandı. <c>Round</c> boşsa tur çalışmadı.</summary>
    public async Task<(AutomationRound? Round, bool Skipped)> RunTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return (null, false);
        }

        await using var scope = scopes.CreateAsyncScope();
        var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var control = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        var services = control.ServiceProvider;
        var db = services.GetRequiredService<ModulixDbContext>();
        if (!await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, cancellationToken))
        {
            return (null, false);
        }

        await using var lockConnection = new SqlConnection(services.GetRequiredService<ModulixDatabaseOptions>().AppConnectionString);
        await lockConnection.OpenAsync(cancellationToken);
        if (!await TryLockAsync(lockConnection, tenantId, cancellationToken))
        {
            return (null, true);
        }

        try
        {
            var tally = new Tally();
            if (await services.GetRequiredService<ITenantSettings>().GetValueAsync(CoreModule.SettingKeys.AutomationEnabled, cancellationToken)
                == CoreModule.SettingValues.No)
            {
                tally.Cancelled += await CancelAllOpenAsync(db, "Otomasyon firma ayarıyla durduruldu.", cancellationToken);
                return (tally.ToRound(), false);
            }

            // Tarama tabanlı tetikleyiciler önce kuyruğa çalışma yazar; aynı turda sıradaki çalışmalar işlenirken bunlar da (sınır dahilinde) işlenir.
            tally.Scanned = await scanner.ScanAsync(services, db, tenantId, cancellationToken);

            var now = time.GetUtcNow();
            var due = await db.AutomationRuns.AsNoTracking()
                .Where(r => (r.Status == AutomationRunStatus.Pending || r.Status == AutomationRunStatus.Running)
                    && r.NextAttemptAt <= now && (r.LeaseUntil == null || r.LeaseUntil < now))
                .OrderBy(r => r.NextAttemptAt).ThenBy(r => r.Id)
                .Select(r => r.Id)
                .Take(Math.Max(1, options.Value.RunsPerTenantPerRound))
                .ToListAsync(cancellationToken);
            foreach (var runId in due)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ProcessRunAsync(tenantScopes, tenantId, db, runId, tally, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Bir çalışmanın kendi hata yönetimi de başarısız olursa (ör. veritabanı yok) tur sürer; kira dolunca yeniden denenir.
                    logger.LogError(exception, "Tenant {TenantId} otomasyon çalışması {RunId} işlenemedi.", tenantId, runId);
                }
            }

            return (tally.ToRound(), false);
        }
        finally
        {
            await ReleaseLockAsync(lockConnection, tenantId, cancellationToken);
        }
    }

    /// <summary>
    /// Bir tenant için saklama temizliği (<see cref="AutomationRetention"/>). Tur ile aynı oturum kilidini kullanır; kilidi alamayan örnek atlar.
    /// Platform acil durdurma anahtarından ve firma ayarından bağımsızdır: kapalı otomasyonun eski kayıtları da süresi dolunca silinir.
    /// </summary>
    /// <returns>Temizlik sonucu; kilit başka örnekteyse ya da tenant etkin değilse null.</returns>
    public async Task<AutomationRetentionResult?> CleanTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var control = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        var services = control.ServiceProvider;
        if (!await services.GetRequiredService<ModulixDbContext>().Tenants.AsNoTracking()
                .AnyAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, cancellationToken))
        {
            return null;
        }

        await using var lockConnection = new SqlConnection(services.GetRequiredService<ModulixDatabaseOptions>().AppConnectionString);
        await lockConnection.OpenAsync(cancellationToken);
        if (!await TryLockAsync(lockConnection, tenantId, cancellationToken))
        {
            return null;
        }

        try
        {
            return await services.GetRequiredService<AutomationRetention>().RunAsync(cancellationToken);
        }
        finally
        {
            await ReleaseLockAsync(lockConnection, tenantId, cancellationToken);
        }
    }

    private async Task ProcessRunAsync(
        ITenantScopeFactory tenantScopes, Guid tenantId, ModulixDbContext db, Guid runId, Tally tally, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var token = Guid.NewGuid();
        var claimed = await db.AutomationRuns
            .Where(r => r.Id == runId && (r.Status == AutomationRunStatus.Pending || r.Status == AutomationRunStatus.Running)
                && r.NextAttemptAt <= now && (r.LeaseUntil == null || r.LeaseUntil < now))
            .ExecuteUpdateAsync(
                set => set.SetProperty(r => r.LeaseToken, token).SetProperty(r => r.LeaseUntil, now + Lease), cancellationToken);
        if (claimed != 1)
        {
            return;
        }

        var run = await db.AutomationRuns.AsNoTracking().SingleAsync(r => r.Id == runId, cancellationToken);
        try
        {
            await ProcessClaimedAsync(tenantScopes, tenantId, db, run, token, tally, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Beklenmeyen hata çalışmayı sonsuza dek yeniden denetmez: başarısız işaretlenir (ileti yazılmaz, kayıt değeri taşıyabilir);
            // yetkili çalışmayı "Yeniden dene" ile kuyruğa alabilir.
            logger.LogError("Tenant {TenantId} otomasyon çalışması {RunId} beklenmeyen hatayla bitti ({ErrorType}).", tenantId, runId, exception.GetType().Name);
            await FinishAsync(db, run, token, AutomationRunStatus.Failed, $"Beklenmeyen hata ({exception.GetType().Name}).", tally, CancellationToken.None);
        }
    }

    private async Task ProcessClaimedAsync(
        ITenantScopeFactory tenantScopes, Guid tenantId, ModulixDbContext db, AutomationRun run, Guid token, Tally tally, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var rule = await db.AutomationRules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == run.RuleId, cancellationToken);
        var version = await db.AutomationRuleVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == run.RuleVersionId, cancellationToken);
        if (rule is null || version is null)
        {
            await FinishAsync(db, run, token, AutomationRunStatus.Failed, "Kural ya da sürümü bulunamadı.", tally, cancellationToken);
            return;
        }

        if (rule.Status != AutomationRuleStatus.Active)
        {
            await FinishAsync(db, run, token, AutomationRunStatus.Cancelled, "Kural kapatıldığı için çalışma iptal edildi.", tally, cancellationToken);
            return;
        }

        var definition = version.Definition;
        var granted = AutomationRuleIdentity.Permissions(run.EntityKey, definition, catalog);
        await using var ruleScope = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        var services = ruleScope.ServiceProvider;
        services.GetRequiredService<EventHandlingScope>().Begin(CauseOf(run, now), CoreModule.Subscriptions.Automation, granted);

        var access = services.GetRequiredService<ICurrentAccess>();
        if (await ClosedModuleAsync(access, rule, definition, cancellationToken) is { } closed)
        {
            await FinishAsync(db, run, token, AutomationRunStatus.Failed, $"'{closed}' modülü bu firmada kapalı.", tally, cancellationToken);
            return;
        }

        if (run.Status == AutomationRunStatus.Pending)
        {
            var outcome = await EvaluateAsync(db, services, run, definition, cancellationToken);
            tally.Evaluated++;
            if (outcome.Terminal is { } terminal)
            {
                await FinishAsync(db, run, token, terminal.Status, terminal.Note, tally, cancellationToken);
                return;
            }

            var steps = definition.Actions
                .Select((a, i) => AutomationRunStep.Create(
                    tenantId, run.Id, i + 1, a.Id, a.Action, AutomationRunKeys.Step(run.DedupeKey, a.Id)))
                .ToList();

            // Adımların yazılması ve durumun "Running" olması birlikte: araya giren bir çökme yarım adım kümesi bırakmaz.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.AutomationRunSteps.AddRange(steps);
            await db.SaveChangesAsync(cancellationToken);
            var started = await db.AutomationRuns
                .Where(r => r.Id == run.Id && r.LeaseToken == token)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, AutomationRunStatus.Running), cancellationToken);
            if (started != 1)
            {
                // Kira başka işleyiciye geçti; adımlar da geri alınır.
                return;
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await ExecuteStepsAsync(db, services, run, rule, version, definition, token, tally, cancellationToken);
    }

    // Tetikleyen olayın yerine geçen zarf: kural kaynaklı değişiklik bu olaya bağlanır (iz, neden, derinlik + 1).
    private static EventEnvelope CauseOf(AutomationRun run, DateTimeOffset now) =>
        new(run.EventId ?? run.Id, run.TenantId, DomainEventTypes.RecordUpdated, 1, run.EntityKey, run.RecordId, null, Guid.Empty,
            ExecutionSource.BackgroundJob, run.CorrelationId, null, run.Depth, new JsonObject(), now);

    private async Task<string?> ClosedModuleAsync(
        ICurrentAccess access, AutomationRule rule, AutomationDefinition definition, CancellationToken cancellationToken)
    {
        var required = new List<string> { rule.ModuleKey };
        foreach (var step in definition.Actions)
        {
            if (catalog.FindAutomationAction(step.Action) is { } found)
            {
                required.Add(found.ModuleKey);
                required.AddRange(found.Action.Definition.RequiredModules);
            }
        }

        foreach (var moduleKey in required.Distinct(StringComparer.Ordinal))
        {
            if (!await access.IsModuleEnabledAsync(moduleKey, cancellationToken))
            {
                return catalog.Find(moduleKey)?.DisplayName ?? moduleKey;
            }
        }

        return null;
    }

    private sealed record EvaluationOutcome((AutomationRunStatus Status, string Note)? Terminal);

    /// <summary>Kaydı kuralın kimliğiyle okur ve koşulu değerlendirir; sağlanmazsa ya da değerlendirilemezse bitiş durumunu verir.</summary>
    private async Task<EvaluationOutcome> EvaluateAsync(
        ModulixDbContext db, IServiceProvider services, AutomationRun run, AutomationDefinition definition, CancellationToken cancellationToken)
    {
        var loaded = await LoadFactsAsync(db, services, run, cancellationToken);
        if (loaded.Failure is { } failure)
        {
            return new EvaluationOutcome(failure);
        }

        var day = await services.GetRequiredService<ITenantClock>().GetAsync(cancellationToken);
        var explanation = await ConditionEvaluator.EvaluateAsync(definition.Condition, loaded.Fields!, loaded.Facts!, day, cancellationToken);
        return explanation.Passed
            ? new EvaluationOutcome(null)
            : new EvaluationOutcome((AutomationRunStatus.NotMatched, "Koşul sağlanmadı."));
    }

    private sealed record LoadedFacts(
        AutomationFieldSet? Fields, IAutomationFacts? Facts, (AutomationRunStatus Status, string Note)? Failure);

    private async Task<LoadedFacts> LoadFactsAsync(ModulixDbContext db, IServiceProvider services, AutomationRun run, CancellationToken cancellationToken)
    {
        var fields = await services.GetRequiredService<IAutomationCatalog>().GetFieldSetAsync(run.EntityKey, cancellationToken);
        if (fields is null)
        {
            return new LoadedFacts(null, null, (AutomationRunStatus.Failed, "Kayıt türü artık kural için kullanılamıyor."));
        }

        var record = await services.GetRequiredService<IAutomationRecordReader>().ReadAsync(new RecordRef(run.EntityKey, run.RecordId), cancellationToken);
        if (!record.IsSuccess)
        {
            return record.Error!.Code == RecordErrorCode.NotFound
                ? new LoadedFacts(null, null, (AutomationRunStatus.Cancelled, "Kayıt bulunamadı (silinmiş olabilir)."))
                : new LoadedFacts(null, null, (AutomationRunStatus.Failed, "Kayıt kuralın kimliğiyle okunamadı."));
        }

        // Eski/yeni değer koşulları için olayın farkı; kuralın kimliğiyle görülemeyen hassas alanlar çıkarılır (alan yetkisi).
        JsonObject? changes = null;
        if (run.EventId is { } eventId)
        {
            var payload = await db.OutboxMessages.AsNoTracking()
                .Where(m => m.Id == eventId && m.TenantId == run.TenantId)
                .Select(m => m.Payload)
                .SingleOrDefaultAsync(cancellationToken);
            if (payload is not null && JsonNode.Parse(payload) is JsonObject diff)
            {
                var permissions = await services.GetRequiredService<ICurrentAccess>().GetPermissionsAsync(cancellationToken);
                foreach (var hidden in catalog.HiddenFields(run.EntityKey, permissions))
                {
                    diff.Remove(hidden);
                }

                changes = diff;
            }
        }

        var facts = new RecordFacts(fields, record.Value!.Id, record.Value.Values, changes, services.GetRequiredService<IAutomationRelationCounter>());
        return new LoadedFacts(fields, facts, null);
    }

    private async Task ExecuteStepsAsync(
        ModulixDbContext db,
        IServiceProvider services,
        AutomationRun run,
        AutomationRule rule,
        AutomationRuleVersion version,
        AutomationDefinition definition,
        Guid token,
        Tally tally,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var step = await db.AutomationRunSteps.AsNoTracking()
                .Where(s => s.RunId == run.Id && s.Status == AutomationStepStatus.Pending)
                .OrderBy(s => s.StepNo)
                .FirstOrDefaultAsync(cancellationToken);
            if (step is null)
            {
                await FinishAsync(db, run, token, AutomationRunStatus.Succeeded, null, tally, cancellationToken);
                return;
            }

            // Adımlar arasında kural kapatıldı ya da acil durdurma açıldıysa kalanlar çalışmaz.
            if (await StopReasonAsync(db, services, rule.Id, cancellationToken) is { } stop)
            {
                await FinishAsync(db, run, token, AutomationRunStatus.Cancelled, stop, tally, cancellationToken);
                return;
            }

            var attempts = step.Attempts + 1;
            var firstAttempt = step.StartedAt is null;
            var startedAt = step.StartedAt ?? time.GetUtcNow();
            await db.AutomationRunSteps.Where(s => s.Id == step.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.Attempts, attempts).SetProperty(s => s.StartedAt, startedAt), cancellationToken);

            var result = await RunStepAsync(db, services, run, version, definition, step, startedAt, firstAttempt, cancellationToken);
            tally.StepsRun++;
            var now = time.GetUtcNow();
            switch (result.Status)
            {
                case AutomationActionStatus.Waiting when result.ResumeAt is { } resumeAt:
                    // Kalıcı akış (Dilim 4c-4): çalışma kirasını bırakır ve bekleme anına kadar uyur; bekleme deneme hakkından sayılmaz.
                    var waitAt = resumeAt > now ? resumeAt : now;
                    await db.AutomationRunSteps.Where(s => s.Id == step.Id)
                        .ExecuteUpdateAsync(
                            set => set
                                .SetProperty(s => s.Attempts, step.Attempts)
                                .SetProperty(s => s.WaitingUntil, waitAt)
                                .SetProperty(s => s.Summary, Truncate(result.Summary, AutomationRunStep.SummaryMaxLength)),
                            cancellationToken);
                    await db.AutomationRuns.Where(r => r.Id == run.Id && r.LeaseToken == token)
                        .ExecuteUpdateAsync(
                            set => set.SetProperty(r => r.NextAttemptAt, waitAt).SetProperty(r => r.LeaseToken, (Guid?)null).SetProperty(r => r.LeaseUntil, (DateTimeOffset?)null),
                            cancellationToken);
                    tally.Waiting++;
                    return;

                case AutomationActionStatus.Succeeded or AutomationActionStatus.Skipped:
                    var effectKey = result.Effect?.EntityKey;
                    Guid? effectId = result.Effect?.Id;
                    var summary = Truncate(result.Summary, AutomationRunStep.SummaryMaxLength);
                    var stepStatus = result.Status == AutomationActionStatus.Succeeded ? AutomationStepStatus.Succeeded : AutomationStepStatus.Skipped;
                    await db.AutomationRunSteps.Where(s => s.Id == step.Id)
                        .ExecuteUpdateAsync(
                            set => set
                                .SetProperty(s => s.Status, stepStatus)
                                .SetProperty(s => s.CompletedAt, now)
                                .SetProperty(s => s.Summary, summary)
                                .SetProperty(s => s.WaitingUntil, (DateTimeOffset?)null)
                                .SetProperty(s => s.EffectEntityKey, effectKey)
                                .SetProperty(s => s.EffectRecordId, effectId),
                            cancellationToken);
                    if (result.StopFlow)
                    {
                        // Akış bilerek bitti (ör. onay reddedildi): kalan adımlar hata değil, "atlandı".
                        await db.AutomationRunSteps.Where(s => s.RunId == run.Id && s.Status == AutomationStepStatus.Pending)
                            .ExecuteUpdateAsync(
                                set => set
                                    .SetProperty(s => s.Status, AutomationStepStatus.Skipped)
                                    .SetProperty(s => s.CompletedAt, now)
                                    .SetProperty(s => s.Summary, "Akış önceki adımda durduğu için çalışmadı."),
                                cancellationToken);
                        await FinishAsync(db, run, token, AutomationRunStatus.Succeeded, summary, tally, cancellationToken);
                        return;
                    }

                    continue;

                default:
                    var retry = result.Retryable && attempts < AutomationRetryPolicy.MaxAttempts;
                    if (retry)
                    {
                        // Aynı adım, aynı idempotency anahtarıyla sonra yeniden denenir; çalışma kirasını bırakır.
                        var next = now + AutomationRetryPolicy.Delay(attempts);
                        await db.AutomationRunSteps.Where(s => s.Id == step.Id)
                            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Summary, Truncate(result.Summary, AutomationRunStep.SummaryMaxLength)), cancellationToken);
                        await db.AutomationRuns.Where(r => r.Id == run.Id && r.LeaseToken == token)
                            .ExecuteUpdateAsync(
                                set => set.SetProperty(r => r.NextAttemptAt, next).SetProperty(r => r.LeaseToken, (Guid?)null).SetProperty(r => r.LeaseUntil, (DateTimeOffset?)null),
                                cancellationToken);
                        tally.Retrying++;
                        return;
                    }

                    await db.AutomationRunSteps.Where(s => s.Id == step.Id)
                        .ExecuteUpdateAsync(
                            set => set
                                .SetProperty(s => s.Status, AutomationStepStatus.Failed)
                                .SetProperty(s => s.CompletedAt, now)
                                .SetProperty(s => s.Summary, Truncate(result.Summary, AutomationRunStep.SummaryMaxLength)),
                            cancellationToken);
                    await db.AutomationRunSteps.Where(s => s.RunId == run.Id && s.Status == AutomationStepStatus.Pending)
                        .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, AutomationStepStatus.Cancelled).SetProperty(s => s.CompletedAt, now), cancellationToken);
                    await FinishAsync(
                        db, run, token, AutomationRunStatus.Failed, $"{step.Action} adımı başarısız: {Truncate(result.Summary, 200)}", tally, cancellationToken);
                    return;
            }
        }
    }

    private async Task<string?> StopReasonAsync(ModulixDbContext db, IServiceProvider services, Guid ruleId, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return "Otomasyon platform anahtarıyla durduruldu.";
        }

        if (await services.GetRequiredService<ITenantSettings>().GetValueAsync(CoreModule.SettingKeys.AutomationEnabled, cancellationToken)
            == CoreModule.SettingValues.No)
        {
            return "Otomasyon firma ayarıyla durduruldu.";
        }

        var status = await db.AutomationRules.AsNoTracking().Where(r => r.Id == ruleId).Select(r => (AutomationRuleStatus?)r.Status).SingleOrDefaultAsync(cancellationToken);
        return status == AutomationRuleStatus.Active ? null : "Kural kapatıldığı için çalışma iptal edildi.";
    }

    /// <summary>
    /// Tek adımı kuralın kimliğiyle çalıştırır. Beklenmeyen hata geçici sayılır (aksiyon idempotent olduğundan yeniden deneme güvenlidir);
    /// iletisi kayıt değeri taşıyabileceği için yalnızca hata türü yazılır.
    /// </summary>
    private async Task<AutomationActionResult> RunStepAsync(
        ModulixDbContext db,
        IServiceProvider services,
        AutomationRun run,
        AutomationRuleVersion version,
        AutomationDefinition definition,
        AutomationRunStep step,
        DateTimeOffset startedAt,
        bool firstAttempt,
        CancellationToken cancellationToken)
    {
        try
        {
            if (definition.Actions.FirstOrDefault(a => a.Id == step.StepId) is not { } stepDefinition
                || catalog.FindAutomationAction(step.Action) is not { } registration)
            {
                return new AutomationActionResult(AutomationActionStatus.Failed, "Aksiyon artık tanımlı değil.");
            }

            var loaded = await LoadFactsAsync(db, services, run, cancellationToken);
            if (loaded.Failure is { } failure)
            {
                return failure.Status == AutomationRunStatus.Cancelled
                    ? new AutomationActionResult(AutomationActionStatus.Skipped, failure.Note)
                    : new AutomationActionResult(AutomationActionStatus.Failed, failure.Note);
            }

            // Adım koşulu (dallanma, Dilim 4c-5): adıma sıra geldiğinde bir kez, kaydın güncel değerleriyle. Bekleyen ya da yeniden denenen
            // adım koşulu yeniden sormaz (başlamış adım yarıda dal değiştirmez).
            if (firstAttempt && stepDefinition.When is { } when)
            {
                var day = await services.GetRequiredService<ITenantClock>().GetAsync(cancellationToken);
                var check = await ConditionEvaluator.EvaluateAsync(when, loaded.Fields!, loaded.Facts!, day, cancellationToken);
                if (!check.Passed)
                {
                    return new AutomationActionResult(
                        AutomationActionStatus.Skipped, $"Adım koşulu sağlanmadı, adım atlandı ({ConditionText.Describe(when, loaded.Fields!)}).");
                }
            }

            var resolved = AutomationParameterResolver.Resolve(stepDefinition, loaded.Fields!, loaded.Facts!, registration.Action.Definition);
            if (AutomationParameterResolver.MissingRequired(registration.Action.Definition, resolved.Values) is { Count: > 0 } missing)
            {
                return new AutomationActionResult(
                    AutomationActionStatus.Skipped, $"Zorunlu parametre bu kayıtta boş, aksiyon yapılmadı: {string.Join(", ", missing)}.");
            }

            var handler = (IAutomationAction)services.GetRequiredService(registration.Action.Handler);
            var request = new AutomationActionRequest(step.Action, resolved.Values, new RecordRef(run.EntityKey, run.RecordId), step.IdempotencyKey)
            {
                StepStartedAt = startedAt,
                FlowStartedAt = run.CreatedAt,
            };
            var context = new AutomationCallContext(AutomationCallerKind.Automation, Guid.Empty, run.CorrelationId, run.EventId, run.Depth)
            {
                RuleVersionId = version.Id,
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.StepTimeoutSeconds)));
            var result = await handler.ExecuteAsync(request, context, timeout.Token);

            // Aksiyonun kuyruğa yazdığı e-posta gibi bekleyen yazımlar aksiyon kapsamında kaydedilir.
            await services.GetRequiredService<ModulixDbContext>().SaveChangesAsync(timeout.Token);
            return result.Status is AutomationActionStatus.Planned || (result.Status is AutomationActionStatus.Waiting && result.ResumeAt is null)
                ? new AutomationActionResult(AutomationActionStatus.Failed, "Aksiyon beklenmeyen bir sonuç döndü.")
                : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Otomasyon adımı hata verdi: çalışma {RunId}, adım {StepNo}, aksiyon {Action}, hata türü {ErrorType}.",
                run.Id, step.StepNo, step.Action, exception.GetType().Name);
            return new AutomationActionResult(AutomationActionStatus.Failed, $"Aksiyon hata verdi ({exception.GetType().Name}).") { Retryable = true };
        }
    }

    /// <summary>Çalışmayı bitirir (kira belirteci şartıyla); kalan bekleyen adımlar iptal olur.</summary>
    private async Task FinishAsync(
        ModulixDbContext db, AutomationRun run, Guid token, AutomationRunStatus status, string? note, Tally tally, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var text = note is null ? null : Truncate(note, AutomationRun.NoteMaxLength);
        var updated = await db.AutomationRuns
            .Where(r => r.Id == run.Id && r.LeaseToken == token)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.Status, status)
                    .SetProperty(r => r.CompletedAt, now)
                    .SetProperty(r => r.Note, text)
                    .SetProperty(r => r.LeaseToken, (Guid?)null)
                    .SetProperty(r => r.LeaseUntil, (DateTimeOffset?)null),
                cancellationToken);
        if (updated != 1)
        {
            return;
        }

        await db.AutomationRunSteps.Where(s => s.RunId == run.Id && s.Status == AutomationStepStatus.Pending)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, AutomationStepStatus.Cancelled).SetProperty(s => s.CompletedAt, now), cancellationToken);
        switch (status)
        {
            case AutomationRunStatus.NotMatched:
                tally.NotMatched++;
                break;
            case AutomationRunStatus.Failed:
                tally.Failed++;
                break;
            case AutomationRunStatus.Cancelled:
                tally.Cancelled++;
                break;
        }
    }

    /// <summary>Bitmemiş bütün çalışmaları ve bekleyen adımlarını iptal eder (firma anahtarı kapalı); iptal edilen çalışma sayısı.</summary>
    private async Task<int> CancelAllOpenAsync(ModulixDbContext db, string note, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await db.AutomationRunSteps
            .Where(s => s.Status == AutomationStepStatus.Pending
                && db.AutomationRuns.Any(r => r.Id == s.RunId && (r.Status == AutomationRunStatus.Pending || r.Status == AutomationRunStatus.Running)))
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, AutomationStepStatus.Cancelled).SetProperty(s => s.CompletedAt, now), cancellationToken);
        return await db.AutomationRuns
            .Where(r => r.Status == AutomationRunStatus.Pending || r.Status == AutomationRunStatus.Running)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.Status, AutomationRunStatus.Cancelled)
                    .SetProperty(r => r.CompletedAt, now)
                    .SetProperty(r => r.Note, note)
                    .SetProperty(r => r.LeaseToken, (Guid?)null)
                    .SetProperty(r => r.LeaseUntil, (DateTimeOffset?)null),
                cancellationToken);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static async Task<bool> TryLockAsync(SqlConnection connection, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = @name, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; SELECT @r;";
        command.Parameters.AddWithValue("@name", LockResource(tenantId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) >= 0;
    }

    private static async Task ReleaseLockAsync(SqlConnection connection, Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @name, @LockOwner = N'Session';";
            command.Parameters.AddWithValue("@name", LockResource(tenantId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException)
        {
            // Bağlantı kapanınca oturum kilidi zaten düşer.
        }
    }

    private sealed class Tally
    {
        public int Evaluated { get; set; }

        public int NotMatched { get; set; }

        public int StepsRun { get; set; }

        public int Retrying { get; set; }

        public int Failed { get; set; }

        public int Cancelled { get; set; }

        public int Scanned { get; set; }

        public int Waiting { get; set; }

        public AutomationRound ToRound() => new(Evaluated, NotMatched, StepsRun, Retrying, Failed, Cancelled, Scanned, Waiting);
    }
}
