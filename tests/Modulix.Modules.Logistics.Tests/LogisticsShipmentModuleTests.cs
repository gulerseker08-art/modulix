using Modulix.Core.Contracts.Modules;
using Modulix.Core.Modules;
using Modulix.Modules.Logistics.Domain;
using Modulix.Modules.Logistics.Lifecycle;
using Modulix.Shared.Logistics;

namespace Modulix.Modules.Logistics.Tests;

/// <summary>
/// ADR-0038 dilim 4: taşıma talebi ve sevkiyat. Geçiş tablosu durum listesiyle tutarlıdır (bilinmeyen durum yok, teslim durumlarına komutla
/// geçilmez, iptal teslimden önce ve gerekçeli, onay ayrı izin); formlar sekmelidir; taşıma türü listesi kategorili; arayüz sabitleri sunucuyla aynı.
/// </summary>
public class LogisticsShipmentModuleTests
{
    private static readonly IModule Module = new LogisticsModule();

    [Fact]
    public void Transitions_use_known_statuses_and_keep_delivery_for_the_delivery_command()
    {
        foreach (var t in LogisticsTransitions.All)
        {
            var statuses = t.EntityKey == LogisticsModule.Entities.TransportRequest ? TransportRequest.Statuses.All : Shipment.Statuses.All;
            Assert.All(t.From, from => Assert.Contains(from, statuses));
            Assert.Contains(t.To, statuses);
            Assert.DoesNotContain(t.From, from => from == t.To);
        }

        // Teslim durumlarına bu dilimin komutlarıyla geçilmez (teslim kanıtıyla gelir, dilim 6); yoldaki ya da kapanan sevkiyat iptal edilmez.
        string[] delivered = [Shipment.Statuses.Delivered, Shipment.Statuses.Partial, Shipment.Statuses.Failed];
        Assert.DoesNotContain(LogisticsTransitions.All, t => delivered.Contains(t.To));
        var cancel = LogisticsTransitions.Find(LogisticsModule.Entities.Shipment, LogisticsActions.Cancel)!;
        Assert.True(cancel.RequiresReason);
        Assert.DoesNotContain(Shipment.Statuses.InTransit, cancel.From);

        // Onay ve ret ayrı izindir; operasyon şablonunda yoktur, müdür şablonundadır.
        Assert.Equal(LogisticsModule.Permissions.RequestsApprove, LogisticsTransitions.Find(LogisticsModule.Entities.TransportRequest, LogisticsActions.Approve)!.Permission);
        Assert.True(LogisticsTransitions.Find(LogisticsModule.Entities.TransportRequest, LogisticsActions.Reject)!.RequiresReason);
        Assert.DoesNotContain(LogisticsModule.Permissions.RequestsApprove, Module.RoleTemplates.Single(t => t.Key == "logistics.operations").Permissions);
        Assert.Contains(LogisticsModule.Permissions.RequestsApprove, Module.RoleTemplates.Single(t => t.Key == "logistics.manager").Permissions);
    }

    [Fact]
    public void Request_and_shipment_forms_are_tabbed_and_the_transport_mode_list_has_categories()
    {
        var catalog = ModuleCatalog.Create(new LogisticsModule());

        foreach (var entity in new[] { LogisticsModule.Entities.TransportRequest, LogisticsModule.Entities.Shipment })
        {
            var form = catalog.FindForm(entity)!.Value.Form;
            Assert.True(form.Tabs.Count >= 2);
            Assert.Contains(form.Fields, f => f.Key == "cargoDescription" && f.Required);
        }

        var modes = catalog.FindChoiceList(LogisticsModule.Lists.TransportMode)!.Value.List;
        Assert.Equal([LogisticsModule.TransportModeCategories.Full, LogisticsModule.TransportModeCategories.Partial], modes.Categories.Select(c => c.Key));
    }

    [Fact]
    public void Client_constants_match_the_server()
    {
        Assert.Equal(LogisticsModule.Entities.TransportRequest, LogisticsAccess.TransportRequestEntityKey);
        Assert.Equal(LogisticsModule.Entities.Shipment, LogisticsAccess.ShipmentEntityKey);
        Assert.Equal(LogisticsModule.Permissions.RequestsRead, LogisticsAccess.RequestsRead);
        Assert.Equal(LogisticsModule.Permissions.RequestsWrite, LogisticsAccess.RequestsWrite);
        Assert.Equal(LogisticsModule.Permissions.RequestsApprove, LogisticsAccess.RequestsApprove);
        Assert.Equal(LogisticsModule.Permissions.ShipmentsRead, LogisticsAccess.ShipmentsRead);
        Assert.Equal(LogisticsModule.Permissions.ShipmentsWrite, LogisticsAccess.ShipmentsWrite);
        Assert.Equal(LogisticsModule.Lists.TransportMode, LogisticsAccess.TransportModeList);
        Assert.Equal(LogisticsLookupDefinitions.RequestCustomer, LogisticsAccess.RequestCustomerLookup);
        Assert.Equal(LogisticsLookupDefinitions.ShipmentReceiver, LogisticsAccess.ShipmentReceiverLookup);
        Assert.Equal(TransportRequest.Statuses.All, LogisticsAccess.RequestStatuses.Select(s => s.Key));
        Assert.Equal(Shipment.Statuses.All, LogisticsAccess.ShipmentStatuses.Select(s => s.Key));
        Assert.Equal(LogisticsModule.ShipmentStatusLabels.Values, LogisticsAccess.ShipmentStatuses.Select(s => s.Label));
    }
}
