using Modulix.Core.Contracts.Modules;
using Modulix.Modules.Logistics;
using Modulix.Modules.Logistics.Domain;
using Modulix.Shared.Logistics;

namespace Modulix.Modules.Logistics.Tests;

/// <summary>ADR-0038 dilim 6: teslim kanıtı ve iş olayı beyanları (veritabanı gerekmez).</summary>
public class LogisticsDeliveryModuleTests
{
    private static IModule Module { get; } = new LogisticsModule();

    [Fact]
    public void Delivery_outcomes_match_the_shared_constants_and_every_outcome_has_a_label()
    {
        Assert.Equal(DeliveryProof.Outcomes.All, LogisticsAccess.DeliveryOutcomes.Select(o => o.Key));
        Assert.All(DeliveryProof.Outcomes.All, key => Assert.True(LogisticsModule.DeliveryOutcomeLabels.ContainsKey(key)));
        Assert.Equal(LogisticsModule.Entities.DeliveryProof, LogisticsAccess.DeliveryProofEntityKey);
    }

    [Fact]
    public void Delivery_events_are_declared_with_the_module_key_version_one_and_the_shipment_record()
    {
        var delivered = Assert.Single(Module.BusinessEvents, e => e.EventType == LogisticsModule.Events.ShipmentDelivered);
        var failed = Assert.Single(Module.BusinessEvents, e => e.EventType == LogisticsModule.Events.ShipmentDeliveryFailed);

        Assert.All(new[] { delivered, failed }, e =>
        {
            Assert.StartsWith(LogisticsModule.ModuleKey + ".", e.EventType);
            Assert.Equal(1, e.SchemaVersion);
            Assert.Equal(LogisticsModule.Entities.Shipment, e.EntityKey);
        });

        // Başarısız teslim teslim alan, hasar ve eksik taşımaz; teslim neden taşımaz (alanlar birbirine karışmaz).
        Assert.DoesNotContain("receivedBy", failed.Fields);
        Assert.Contains("failureReason", failed.Fields);
        Assert.DoesNotContain("failureReason", delivered.Fields);
        Assert.All(new[] { "receivedBy", "damaged", "shortage", "proofId", "outcome", "receivedAt" }, f => Assert.Contains(f, delivered.Fields));
    }

    [Fact]
    public void Delivery_states_stay_unreachable_from_the_generic_action_table_and_the_proof_is_audited()
    {
        // Teslim durumlarına yalnızca teslim komutu geçer; geçiş tablosunda "teslim" hedefi olmamalı.
        Assert.DoesNotContain(Lifecycle.LogisticsTransitions.All, t =>
            t.To is Shipment.Statuses.Delivered or Shipment.Statuses.Partial or Shipment.Statuses.Failed);
        Assert.Contains(Module.AuditedEntities, a => a.EntityKey == LogisticsModule.Entities.DeliveryProof && a.ReadPermission == LogisticsModule.Permissions.ShipmentsRead);
    }
}
