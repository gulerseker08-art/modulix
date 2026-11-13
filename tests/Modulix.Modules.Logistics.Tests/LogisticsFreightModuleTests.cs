using Modulix.Core.Contracts.Modules;
using Modulix.Modules.Logistics;
using Modulix.Shared.Logistics;

namespace Modulix.Modules.Logistics.Tests;

/// <summary>ADR-0038 dilim 7a: navlun ve hakediş hassas alan beyanları ve form yerleşimi (veritabanı gerekmez).</summary>
public class LogisticsFreightModuleTests
{
    private static IModule Module { get; } = new LogisticsModule();

    [Fact]
    public void Freight_and_settlement_amounts_and_currencies_are_sensitive_fields_behind_the_freight_permission()
    {
        Assert.Equal(LogisticsModule.Permissions.FreightRead, LogisticsAccess.FreightRead);
        Assert.Contains(Module.Permissions, p => p.Key == LogisticsModule.Permissions.FreightRead);

        var shipment = Assert.Single(Module.SensitiveFields, f => f.EntityKey == LogisticsModule.Entities.Shipment);
        var trip = Assert.Single(Module.SensitiveFields, f => f.EntityKey == LogisticsModule.Entities.Trip);
        Assert.Equal(LogisticsModule.Permissions.FreightRead, shipment.Permission);
        Assert.Equal(LogisticsModule.Permissions.FreightRead, trip.Permission);

        // Para birimi tutarla birlikte korunur: izinsiz kişi mevcut tutarın para birimini değiştiremesin.
        Assert.Equal(["freightAmount", "freightCurrency"], shipment.Fields);
        Assert.Equal(["settlementAmount", "settlementCurrency"], trip.Fields);

        // Talep navlun taşımaz (onayda kopyalanırsa yetkisiz onaylayanda kaybolurdu).
        Assert.DoesNotContain(Module.SensitiveFields, f => f.EntityKey == LogisticsModule.Entities.TransportRequest);
    }

    [Fact]
    public void Freight_fields_are_in_the_default_shipment_and_trip_forms_but_not_in_the_request_form()
    {
        var shipmentForm = Assert.Single(Module.Forms, f => f.EntityKey == LogisticsModule.Entities.Shipment);
        var tripForm = Assert.Single(Module.Forms, f => f.EntityKey == LogisticsModule.Entities.Trip);
        var requestForm = Assert.Single(Module.Forms, f => f.EntityKey == LogisticsModule.Entities.TransportRequest);

        Assert.All(new[] { "freightAmount", "freightCurrency" }, key => Assert.Contains(shipmentForm.Fields, f => f.Key == key));
        Assert.All(new[] { "settlementAmount", "settlementCurrency" }, key => Assert.Contains(tripForm.Fields, f => f.Key == key));
        Assert.DoesNotContain(requestForm.Fields, f => f.Key.StartsWith("freight", StringComparison.Ordinal));

        // Her alanın bölümü formda tanımlıdır (yerleşim bozulmasın).
        foreach (var form in new[] { shipmentForm, tripForm })
        {
            Assert.All(form.Fields, field => Assert.Contains(form.Sections, s => s.Key == field.Section));
        }
    }

    [Fact]
    public void Only_the_manager_role_template_carries_the_freight_permission()
    {
        Assert.Contains(Module.RoleTemplates, r => r.Key == "logistics.manager" && r.Permissions.Contains(LogisticsModule.Permissions.FreightRead));
        Assert.DoesNotContain(Module.RoleTemplates, r => r.Key == "logistics.operations" && r.Permissions.Contains(LogisticsModule.Permissions.FreightRead));
    }
}
