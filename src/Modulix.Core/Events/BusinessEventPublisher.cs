using System.Diagnostics;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Records;

namespace Modulix.Core.Events;

/// <summary>
/// İş olayı yayıncısı (ADR-0039). Olayı kayıt olaylarıyla aynı outbox'a, aynı zarfla yazar: tür ve sürüm modülün beyanından, tenant
/// oturumdan, kişi ve kaynak denetim kaydıyla aynı, correlation isteğin izi; bir abonenin içinden yayınlanıyorsa tetikleyen olaya
/// bağlanır (<see cref="EventHandlingScope"/>: causation, derinlik + 1, kaynak arka plan işi). Çağıran komut, durum değişikliğini ve
/// yayını <c>IRecordPipeline.RunAtomicAsync</c> içinde yapar; yayıncı iş birimini kaydeder, işlemi dış kapsam onaylar.
/// Beyansız tür, beyan dışı alan, boş tenant ya da boş kayıt kimliği programlama hatasıdır ve istisnayla reddedilir.
/// </summary>
public sealed class BusinessEventPublisher(
    ITenantContextAccessor tenantAccessor,
    ModuleCatalog catalog,
    IDomainEventOutbox outbox,
    IUnitOfWork unitOfWork,
    EventHandlingScope? handling = null) : IBusinessEventPublisher
{
    public async Task PublishAsync(BusinessEvent businessEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(businessEvent);
        var tenant = tenantAccessor.Current
            ?? throw new InvalidOperationException($"'{businessEvent.EventType}' olayı tenant bağlamı olmadan yayınlanamaz.");
        if (businessEvent.RecordId == Guid.Empty)
        {
            throw new ArgumentException($"'{businessEvent.EventType}' olayının kayıt kimliği boş.", nameof(businessEvent));
        }

        var (_, definition) = catalog.FindBusinessEvent(businessEvent.EventType)
            ?? throw new InvalidOperationException($"Beyan edilmemiş iş olayı: '{businessEvent.EventType}'. Modül IModule.BusinessEvents ile beyan etmeli.");

        var undeclared = businessEvent.Payload.Select(p => p.Key).Where(k => !definition.Fields.Contains(k, StringComparer.Ordinal)).ToList();
        if (undeclared.Count > 0)
        {
            throw new ArgumentException(
                $"'{businessEvent.EventType}' olayında beyan edilmemiş alan: {string.Join(", ", undeclared)}.", nameof(businessEvent));
        }

        var cause = handling?.Event;
        outbox.Add(new DomainEvent(tenant.TenantId, definition.EventType, definition.EntityKey, businessEvent.RecordId, (JsonObject)businessEvent.Payload.DeepClone())
        {
            SchemaVersion = definition.SchemaVersion,
            Sequence = businessEvent.RecordVersion,
            CompanyId = businessEvent.CompanyId ?? cause?.CompanyId,
            UserId = tenant.UserId,
            Source = cause is null ? businessEvent.Source : ExecutionSource.BackgroundJob,
            CorrelationId = cause?.CorrelationId ?? Activity.Current?.TraceId.ToString(),
            CausationId = cause?.Id,
            Depth = cause is null ? 0 : cause.Depth + 1,
            Reason = businessEvent.Reason,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
