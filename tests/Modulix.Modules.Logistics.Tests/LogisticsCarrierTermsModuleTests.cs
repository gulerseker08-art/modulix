using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Shared.Logistics;

namespace Modulix.Modules.Logistics.Tests;

/// <summary>ADR-0038 dilim 3b: taşıyıcı anlaşması beyanları (veritabanı gerektirmez).</summary>
public class LogisticsCarrierTermsModuleTests
{
    private static readonly IModule Module = new LogisticsModule();

    [Fact]
    public void The_carrier_terms_form_is_tabbed_with_identity_and_validity_on_the_first_tab()
    {
        var form = Assert.Single(LogisticsForms.All, f => f.EntityKey == LogisticsModule.Entities.CarrierTerms);

        Assert.True(form.Tabs.Count >= 2);
        Assert.Equal("genel", form.Tabs[0].Key);
        Assert.All(form.Tabs, tab => Assert.Contains(form.Sections, s => s.Tab == tab.Key));
        Assert.Contains(form.Sections, s => s.Key == FormDefinition.ExtrasSection);
        var firstTab = form.Sections.Where(s => s.Tab == form.Tabs[0].Key).Select(s => s.Key).ToHashSet();
        Assert.All(form.Fields.Where(f => f is { Required: true, Fixed: true }), f => Assert.Contains(f.Section, firstTab));
        Assert.All(form.Fields, f => Assert.Contains(f.Span, FormSpans.Allowed));
        Assert.Contains(form.EntityKey, Module.CustomizableEntities.Select(c => c.EntityKey));
    }

    [Fact]
    public void Carrier_terms_use_the_carrier_permissions_and_reference_the_party()
    {
        var audited = Assert.Single(Module.AuditedEntities, a => a.EntityKey == LogisticsModule.Entities.CarrierTerms);

        Assert.Equal(LogisticsModule.Permissions.CarriersRead, audited.ReadPermission);
        Assert.Contains(Module.SharedRecordAccess, a => a.EntityKey == SharedEntities.Party && a.ReadPermission == LogisticsModule.Permissions.CarriersRead);
        Assert.Contains(Module.SharedRecordReferences, r => r.EntityKey == LogisticsModule.Entities.CarrierTerms && r.Field == "carrierPartyId");
    }

    [Fact]
    public void Client_access_constants_match_the_server_module()
    {
        Assert.Equal(LogisticsModule.Permissions.CarriersRead, LogisticsAccess.CarriersRead);
        Assert.Equal(LogisticsModule.Permissions.CarriersWrite, LogisticsAccess.CarriersWrite);
        Assert.Equal(LogisticsModule.Entities.CarrierTerms, LogisticsAccess.CarrierTermsEntityKey);
        Assert.Equal(LogisticsLookupDefinitions.TermsCarrier, LogisticsAccess.TermsCarrierLookup);
    }
}
