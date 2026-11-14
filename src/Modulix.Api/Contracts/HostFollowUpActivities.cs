using System.Text.Json.Nodes;
using Modulix.Modules.Crm.Contracts.Activities;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Infrastructure.Platform;

namespace Modulix.Api.Contracts;

/// <summary>
/// Takip aktivitesi köprüsü (ana bilgisayar). Kullanıcı isteğinde aktivite çağıranın kimliği ve yetkisiyle açılır (CRM uygulaması doğrudan çağrılır; yetki yoksa hata sonuçla döner).
/// Sözleşme zamanlanmış işi (<c>contracts.scheduler</c> sistem kimliği) aktivite izni taşıyamaz (bir aboneliğin yalnızca kendi modülünün izinleri olabilir); bu durumda aktivite ayrı bir
/// kapsamda, CRM'in "crm.follow-up" sistem kimliğiyle (yalnızca aktivite izinleri) açılır. Yükseltme yalnızca izin verilen sistem kimlikleri içindir; sıradan kullanıcı bu yoldan yetki kazanmaz.
/// Sistem yolunda aktivite açılır ancak çağıranın kayıt görünürlüğü olmadığından kayda bağlanamaz (sözleşme numarası ve başlığı konu ve açıklamadadır).
/// </summary>
internal sealed class HostFollowUpActivities(IServiceProvider services, EventHandlingScope handling, ITenantContextAccessor tenant, ITenantScopeFactory scopes, TimeProvider time) : IFollowUpActivities
{
    private static readonly HashSet<string> ElevatedSystemIdentities = new(StringComparer.Ordinal) { "contracts.scheduler", "orders.scheduler" };

    public async Task<FollowUpResult> CreateAsync(FollowUpRequest request, CancellationToken cancellationToken)
    {
        if (services.GetKeyedService<IFollowUpActivities>("direct") is not { } direct)
        {
            return new FollowUpResult(null, "Aktivite modülü kurulu değil.", Unavailable: true);
        }

        if (handling.Subscription is not { } subscription || !ElevatedSystemIdentities.Contains(subscription) || tenant.Current is not { } current)
        {
            return await direct.CreateAsync(request, cancellationToken);
        }

        await using var scope = scopes.Create(new TenantContext(current.TenantId, Guid.Empty, Guid.Empty));
        var cause = new EventEnvelope(
            Guid.NewGuid(), current.TenantId, DomainEventTypes.RecordDeleted, 1, "crm.activity", Guid.Empty, null, Guid.Empty,
            ExecutionSource.Import, null, null, 0, new JsonObject(), time.GetUtcNow());
        scope.ServiceProvider.GetRequiredService<EventHandlingScope>().Begin(cause, "crm.follow-up");
        return scope.ServiceProvider.GetKeyedService<IFollowUpActivities>("direct") is { } system
            ? await system.CreateAsync(request, cancellationToken)
            : new FollowUpResult(null, "Aktivite modülü kurulu değil.", Unavailable: true);
    }
}
