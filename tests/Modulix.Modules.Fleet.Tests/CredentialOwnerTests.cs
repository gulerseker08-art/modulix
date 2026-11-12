using Modulix.Core.Contracts.Credentials;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Modules;
using Modulix.Shared.Credentials;

namespace Modulix.Modules.Fleet.Tests;

/// <summary>
/// ADR-0038 dilim 2a: süreli belge sahibi bildirimi. Filo aracı "vehicle" kategorisinde sahip olarak kaydolur; Core'un belge türü listesi bütün
/// kategorileri taşır ve her değer bir kategoriye bağlıdır; geçersiz bildirim açılışta reddedilir; arayüz anahtarları sunucuyla aynıdır.
/// </summary>
public class CredentialOwnerTests
{
    private const string Read = "fleet.vehicles.read";

    [Fact]
    public void Fleet_registers_the_vehicle_as_a_credential_owner_with_vehicle_types_available()
    {
        var catalog = ModuleCatalog.Create(new FleetModule());

        var owner = catalog.FindCredentialOwner(FleetModule.Entities.Vehicle)!.Value;
        Assert.Equal((FleetModule.ModuleKey, CredentialCategories.Vehicle), (owner.ModuleKey, owner.Owner.Category));
        Assert.Null(catalog.FindCredentialOwner("fleet.maintenance"));

        var types = catalog.FindChoiceList(CredentialEntities.TypeList)!.Value;
        Assert.Equal(CoreModule.ModuleKey, types.ModuleKey);
        Assert.Equal(CredentialEntities.Credential, types.List.EntityKey);
        Assert.Equal(CredentialCategories.All, types.List.Categories.Select(c => c.Key));
        Assert.Contains(types.List.Values, v => v.Category == CredentialCategories.Vehicle && v.Key == "Inspection");
        Assert.NotNull(catalog.FindForm(CredentialEntities.Credential));
    }

    [Fact]
    public void Credentials_expose_an_expiry_trigger_and_core_offers_a_notify_action()
    {
        var catalog = ModuleCatalog.Create(new FleetModule());

        var entity = catalog.FindAutomationEntity(CredentialEntities.Credential)!.Value;
        Assert.Equal(CoreModule.ModuleKey, entity.ModuleKey);
        Assert.NotNull(entity.Entity.RecordSource);
        Assert.Equal(Modulix.Core.Contracts.Automation.AutomationFieldType.Date, catalog.FindAutomationField("core.credential.validTo")!.Type);
        Assert.Contains(catalog.AutomationActions, a => a.Action.Definition.Key == Modulix.Core.Automation.NotifyAutomation.NotifyAction);
        Assert.NotNull(catalog.FindLookup(Modulix.Core.Automation.NotifyAutomation.RecipientLookup));
    }

    [Theory]
    [InlineData("fleet.vehicle", "spaceship")]
    [InlineData("crm.company", CredentialCategories.Party)]
    [InlineData("fleet.vehicle", CredentialCategories.Vehicle, true)]
    [InlineData("fleet.vehicle", CredentialCategories.Vehicle, false, "crm.companies.read")]
    public void Invalid_or_duplicate_owner_declarations_stop_the_catalog(string entityKey, string category, bool duplicate = false, string permission = Read)
    {
        CredentialOwner owner = new(entityKey, category, "Araç", permission);
        CredentialOwner[] owners = duplicate ? [owner, owner] : [owner];

        var error = Assert.Throws<InvalidOperationException>(() => ModuleCatalog.Create(new OwnerModule(owners)));
        Assert.Contains("süreli belge sahibi", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_keys_match_the_server()
    {
        Assert.Equal(CredentialEntities.Credential, CredentialAccess.EntityKey);
        Assert.Equal(CredentialEntities.TypeList, CredentialAccess.TypeList);
        Assert.Equal(CredentialCategories.Vehicle, CredentialAccess.Vehicle);
        Assert.Equal(CredentialCategories.Driver, CredentialAccess.Driver);
        Assert.Equal(CredentialStatuses.All, CredentialAccess.Statuses.Select(s => s.Key));
    }

    private sealed class OwnerModule(IReadOnlyList<CredentialOwner> owners) : IModule
    {
        public string Key => "fleet";

        public string DisplayName => "Sahte filo";

        public IReadOnlyList<PermissionDefinition> Permissions => [new(Read, "Araçları görüntüler", "Araçlar")];

        public IReadOnlyList<CredentialOwner> CredentialOwners => owners;
    }
}
