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
            var statuses = t.EntityKey switch
            {
                LogisticsModule.Entities.TransportRequest => TransportRequest.Statuses.All,
                LogisticsModule.Entities.Shipment => Shipment.Statuses.All,
                _ => Trip.Statuses.All,
            };
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
    public void Shipments_are_planned_only_by_trip_assignment_and_trips_close_through_their_own_commands()
    {
        // Dilim 5: elle "Planlandı" eylemi yoktur; "Planlandı" sefer atamasıdır.
        Assert.DoesNotContain(LogisticsTransitions.All, t => t.EntityKey == LogisticsModule.Entities.Shipment && t.To == Shipment.Statuses.Planned);

        // Sefer: onay planlamadan, çıkış hazırdan, tamamlama yoldan; iptal gerekçeli ve yoldan önce.
        Assert.Equal([Trip.Statuses.Planning], LogisticsTransitions.Find(LogisticsModule.Entities.Trip, LogisticsActions.Confirm)!.From);
        Assert.Equal([Trip.Statuses.Ready], LogisticsTransitions.Find(LogisticsModule.Entities.Trip, LogisticsActions.Start)!.From);
        Assert.Equal([Trip.Statuses.InProgress], LogisticsTransitions.Find(LogisticsModule.Entities.Trip, LogisticsActions.Complete)!.From);
        var cancel = LogisticsTransitions.Find(LogisticsModule.Entities.Trip, LogisticsActions.Cancel)!;
        Assert.True(cancel.RequiresReason);
        Assert.DoesNotContain(Trip.Statuses.InProgress, cancel.From);
        Assert.All(LogisticsTransitions.All.Where(t => t.EntityKey == LogisticsModule.Entities.Trip), t => Assert.Equal(LogisticsModule.Permissions.TripsWrite, t.Permission));

        // Belge kuralı firma ayarıdır; varsayılan uyarı (ADR-0038 bölüm 9 madde 3).
        var setting = Module.Settings.Single(x => x.Key == LogisticsModule.SettingKeys.CredentialCheck);
        Assert.Equal(LogisticsModule.CredentialCheckModes.Warn, setting.DefaultValue);
        Assert.Equal([LogisticsModule.CredentialCheckModes.Warn, LogisticsModule.CredentialCheckModes.Block], setting.Options.Select(o => o.Value));

        // Sefer yetkisi iki rol şablonunda da var.
        Assert.All(Module.RoleTemplates, t => Assert.Contains(LogisticsModule.Permissions.TripsWrite, t.Permissions));
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

        var trip = catalog.FindForm(LogisticsModule.Entities.Trip)!.Value.Form;
        Assert.True(trip.Tabs.Count >= 2);
        Assert.Contains(trip.Fields, f => f.Key == "plannedStart" && f.Required);

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
        Assert.Equal(LogisticsModule.Entities.Trip, LogisticsAccess.TripEntityKey);
        Assert.Equal(LogisticsModule.Entities.TripStop, LogisticsAccess.TripStopEntityKey);
        Assert.Equal(LogisticsModule.Permissions.TripsRead, LogisticsAccess.TripsRead);
        Assert.Equal(LogisticsModule.Permissions.TripsWrite, LogisticsAccess.TripsWrite);
        Assert.Equal(LogisticsLookupDefinitions.TripVehicle, LogisticsAccess.TripVehicleLookup);
        Assert.Equal(LogisticsLookupDefinitions.TripTrailer, LogisticsAccess.TripTrailerLookup);
        Assert.Equal(LogisticsLookupDefinitions.TripCarrier, LogisticsAccess.TripCarrierLookup);
        Assert.Equal(Trip.Statuses.All, LogisticsAccess.TripStatuses.Select(s => s.Key));
        Assert.Equal(LogisticsModule.TripStatusLabels.Values, LogisticsAccess.TripStatuses.Select(s => s.Label));
        Assert.Equal(TripStop.Types.All, LogisticsAccess.StopTypes.Select(s => s.Key));
        Assert.Equal(LogisticsModule.StopTypeLabels.Values, LogisticsAccess.StopTypes.Select(s => s.Label));
    }
}
