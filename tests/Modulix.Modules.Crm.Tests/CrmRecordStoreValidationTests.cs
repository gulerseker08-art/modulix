using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Tests;

/// <summary>CRM kayıt depolarının alan doğrulaması (veritabanı gerektirmez).</summary>
public class CrmRecordStoreValidationTests
{
    private static readonly CrmStoreContext NoData = new(new NoModuleData(), new NoMembers(), new NoChoiceLists(), new NoTenant(), new NoAccess(), new NoPermissions(), []);

    internal static CrmStoreContext NewContext() => new(new NoModuleData(), new NoMembers(), new NoChoiceLists(), new NoTenant(), new NoAccess(), new NoPermissions(), []);

    [Fact]
    public void Create_requires_required_fields()
    {
        var errors = new CompanyRecordStore(NoData).Validate(RecordOperation.Create, new JsonObject());

        Assert.Equal(["name"], errors.Select(e => e.Field));
    }

    [Fact]
    public void Partial_update_does_not_require_missing_fields()
    {
        var errors = new CompanyRecordStore(NoData).Validate(RecordOperation.Update, new JsonObject { ["city"] = "İzmir" });

        Assert.Empty(errors);
    }

    [Fact]
    public void Required_field_cannot_be_cleared_on_update()
    {
        var errors = new ContactRecordStore(NoData).Validate(RecordOperation.Update, new JsonObject { ["lastName"] = "  " });

        Assert.Equal(["lastName"], errors.Select(e => e.Field));
    }

    [Theory]
    [InlineData("email", "gecersiz")]
    [InlineData("email", "a@b@c")]
    [InlineData("website", 5)]
    [InlineData("unknown", "x")]
    public void Invalid_company_values_are_rejected(string field, object value)
    {
        var values = new JsonObject { ["name"] = "Acme", [field] = JsonValue.Create(value) };

        var errors = new CompanyRecordStore(NoData).Validate(RecordOperation.Create, values);

        Assert.Equal([field], errors.Select(e => e.Field));
    }

    [Fact]
    public void Text_longer_than_the_limit_is_rejected()
    {
        var values = new JsonObject { ["name"] = new string('a', 201) };

        var error = Assert.Single(new CompanyRecordStore(NoData).Validate(RecordOperation.Create, values));

        Assert.Equal("En fazla 200 karakter olabilir.", error.Message);
    }

    [Theory]
    [InlineData("stage", "\"Won\"", true)]
    [InlineData("stage", "\"won\"", true)]
    [InlineData("stage", "\"5\"", false)]
    [InlineData("stage", "\"Kazanıldı\"", false)]
    [InlineData("stage", "null", true)]
    [InlineData("stage", "\"\"", true)]
    [InlineData("amount", "1500.25", true)]
    [InlineData("amount", "1500.255", false)]
    [InlineData("amount", "-1", false)]
    [InlineData("amount", "\"100\"", false)]
    [InlineData("currency", "\"usd\"", true)]
    [InlineData("currency", "\"US\"", false)]
    [InlineData("expectedCloseDate", "\"2026-12-31\"", true)]
    [InlineData("expectedCloseDate", "\"31.12.2026\"", false)]
    [InlineData("companyId", "\"not-a-guid\"", false)]
    [InlineData("companyId", "\"00000000-0000-0000-0000-000000000000\"", false)]
    [InlineData("probability", "0", true)]
    [InlineData("probability", "100", true)]
    [InlineData("probability", "101", false)]
    [InlineData("probability", "50.5", false)]
    [InlineData("probability", "null", true)]
    [InlineData("lostReason", "\"Fiyat yüksek\"", true)]
    [InlineData("ownerId", "\"not-a-guid\"", false)]
    [InlineData("ownerId", "null", true)]
    public void Opportunity_field_formats(string field, string json, bool valid)
    {
        var values = new JsonObject
        {
            ["title"] = "Fırsat",
            ["companyId"] = Guid.NewGuid().ToString(),
        };
        values[field] = JsonNode.Parse(json);

        var errors = new OpportunityRecordStore(NoData).Validate(RecordOperation.Create, values);

        Assert.Equal(valid, errors.Count == 0);
    }

    [Theory]
    [InlineData("\"2026-11-02T09:30:00+03:00\"", true)]
    [InlineData("\"2026-11-02T06:30:00Z\"", true)]
    [InlineData("\"yarın\"", false)]
    public void Activity_due_time_must_be_iso_8601(string json, bool valid)
    {
        var values = new JsonObject { ["subject"] = "Ara", ["dueAt"] = JsonNode.Parse(json) };

        var errors = new ActivityRecordStore(NoData).Validate(RecordOperation.Create, values);

        Assert.Equal(valid, errors.Count == 0);
    }

    [Theory]
    [InlineData("status", "\"Customer\"", true)]
    [InlineData("status", "null", true)]
    [InlineData("status", "\"Müşteri\"", false)]
    [InlineData("source", "\"Referral\"", true)]
    [InlineData("source", "null", true)]
    [InlineData("source", "\"1\"", false)]
    [InlineData("tags", "\"öncelikli, ihracat\"", true)]
    [InlineData("address", "\"Atatürk Cad. No:1\"", true)]
    public void Company_field_formats(string field, string json, bool valid)
    {
        var values = new JsonObject { ["name"] = "Acme" };
        values[field] = JsonNode.Parse(json);

        var errors = new CompanyRecordStore(NoData).Validate(RecordOperation.Create, values);

        Assert.Equal(valid, errors.Count == 0);
    }

    [Fact]
    public void Stores_require_module_permissions()
    {
        IRecordStore store = new CompanyRecordStore(NoData);

        Assert.Equal(CrmModule.Permissions.CompaniesRead, store.PermissionFor(RecordOperation.Read));
        Assert.Equal(CrmModule.Permissions.CompaniesWrite, store.PermissionFor(RecordOperation.Create));
        Assert.Equal(CrmModule.Permissions.CompaniesWrite, store.PermissionFor(RecordOperation.Delete));
        Assert.Equal(CrmModule.ModuleKey, store.FeatureKey.Split('.')[0]);
    }

    private sealed class NoPermissions : ICurrentAccess
    {
        public Task<bool> IsModuleEnabledAsync(string moduleKey, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<IReadOnlySet<string>> GetPermissionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task<RecordScope> GetScopeAsync(CancellationToken cancellationToken) => Task.FromResult(RecordScope.All);
    }

    private sealed class NoAccess : IRecordAccess
    {
        public Task<IReadOnlySet<Guid>?> GetOwnersAsync(string entityKey, CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<Guid>?>(null);

        public Task<bool> CanAsync(string entityKey, RecordOperation operation, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class NoMembers : ITenantMembers
    {
        public Task<IReadOnlyList<TenantMember>> ListActiveAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Doğrulama üyelere erişmemeli.");

        public Task<bool> IsActiveMemberAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Doğrulama üyelere erişmemeli.");
    }

    // Doğrulama yalnızca anahtarın biçimine bakar; listede olup olmadığını depo yazarken firmanın listesiyle denetler.
    private sealed class NoChoiceLists : IChoiceLists
    {
        public Task<ChoiceList> GetAsync(string listKey, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Doğrulama listelere erişmemeli.");
    }

    private sealed class NoTenant : ITenantContextAccessor
    {
        public TenantContext? Current => null;
    }

    private sealed class NoModuleData : IModuleData
    {
        public DbSet<TEntity> Set<TEntity>()
            where TEntity : class, ITenantEntity => throw new InvalidOperationException("Doğrulama veritabanına erişmemeli.");
    }
}
