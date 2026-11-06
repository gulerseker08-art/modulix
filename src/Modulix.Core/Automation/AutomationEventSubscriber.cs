using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Automation;

/// <summary>Tetikleyicisi eşleşebilecek, yayınlanmış ve açık kural sürümü.</summary>
public sealed record ActiveAutomationRule(AutomationRule Rule, AutomationRuleVersion Version);

/// <summary>
/// Çalışma deposu (ADR-0027). Uygulaması altyapıdadır; abone ve yönetim servisleri veritabanına bunun üzerinden erişir.
/// Tüm sorgular aktif tenant ile sınırlıdır (sorgu süzgeci + Row-Level Security).
/// </summary>
public interface IAutomationRunStore
{
    /// <summary>Kayıt türü için yayınlanmış ve açık kurallar; öncelik sırasıyla (küçük önce), en çok <see cref="AutomationLimits.MaxRulesPerEvent"/>.</summary>
    Task<IReadOnlyList<ActiveAutomationRule>> ActiveRulesAsync(string entityKey, CancellationToken cancellationToken);

    /// <summary>Bu tekilleştirme anahtarıyla çalışma var mı?</summary>
    Task<bool> ExistsAsync(string dedupeKey, CancellationToken cancellationToken);

    /// <summary>Aynı kural, kayıt ve iz (correlation) için çalışma var mı? Kural kaynaklı olayın aynı kuralı aynı kayıtta yeniden tetiklemesini (döngü) keser.</summary>
    Task<bool> ChainExistsAsync(Guid ruleId, Guid recordId, string correlationId, CancellationToken cancellationToken);

    /// <summary>Çalışmayı, aboneyi çalıştıran işleme ekler (teslimin işaretiyle birlikte yazılır).</summary>
    void Add(AutomationRun run);
}

/// <summary>
/// Kayıt olaylarını (<c>RecordCreated</c>, <c>RecordUpdated</c>) kural çalışmasına çevirir (<c>core.automation</c>, ADR-0027 Dilim 2).
/// <list type="bullet">
///   <item>Yalnızca ucuz iş yapar: kayda bakmaz, koşul değerlendirmez, aksiyon çalıştırmaz. Tetikleyicisi olayın farkıyla
///   eşleşen her kural için bir <see cref="AutomationRunStatus.Pending"/> çalışma yazar; koşul ve aksiyonlar işleyicide, kuralın
///   kendi kimliğiyle değerlendirilir.</item>
///   <item>Teslim en az bir kezdir: aynı olay yeniden gelirse tekilleştirme anahtarı ikinci çalışmayı engeller.</item>
///   <item>Zincirleme: kural kaynaklı bir olayın <see cref="EventEnvelope.Depth"/> değeri taşınır; azami derinliği
///   <see cref="EventHandlingScope.MaxDepth"/> dağıtıcıda zaten sınırlar. Aboneliğin izni yoktur, bu yüzden hassas alanların
///   değeri olay farkından çıkarılmış gelir (o alanlara bağlı tetikleyiciler eşleşmez).</item>
/// </list>
/// </summary>
public sealed class AutomationEventSubscriber(IAutomationRunStore store, TimeProvider time) : IEventSubscriber
{
    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        // Toplu içe aktarma kural çalıştırmaz (ADR-0033); firma tür başına açarsa olayda bu neden bulunmaz.
        if (envelope.Reason == EventReasons.Import)
        {
            return;
        }

        var rules = await store.ActiveRulesAsync(envelope.EntityKey, cancellationToken);
        if (rules.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        foreach (var (rule, version) in rules)
        {
            if ((version.ActiveFrom is { } from && now < from) || (version.ActiveUntil is { } until && now > until))
            {
                continue;
            }

            if (!AutomationTriggerMatcher.Matches(version.Definition.Trigger, envelope.EntityKey, envelope.EventType, envelope.Payload))
            {
                continue;
            }

            var dedupe = AutomationRunKeys.Dedupe(envelope.TenantId, envelope.Id.ToString("N"), version.Id, envelope.EntityKey, envelope.RecordId);
            if (await store.ExistsAsync(dedupe, cancellationToken)
                || (envelope.Depth > 0 && envelope.CorrelationId is { } trace
                    && await store.ChainExistsAsync(rule.Id, envelope.RecordId, trace, cancellationToken)))
            {
                continue;
            }

            store.Add(AutomationRun.Create(
                envelope.TenantId, rule.Id, version.Id, envelope.EntityKey, envelope.RecordId, AutomationRunOrigin.Event, envelope.Id,
                dedupe, envelope.CorrelationId, envelope.Depth, null, now));
        }
    }
}
