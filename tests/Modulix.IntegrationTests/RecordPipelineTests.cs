using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Tenancy;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;

namespace Modulix.IntegrationTests;

/// <summary>
/// Görev 7: kayıt boru hattı v0. Tüm yazma işlemleri /api/records üzerinden boru hattından geçer;
/// kayıt, audit ve outbox olayı birlikte yazılır, başka tenant'ın kaydı "yok" görünür.
/// </summary>
public class RecordPipelineTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/records/core.tenant-setting";

    [Fact]
    public async Task Crud_through_pipeline_writes_audit_and_outbox()
    {
        var (client, tenantId, userId) = await LoggedInClientAsync();
        using var _ = client;
        var key = UniqueKey();

        var create = await client.PostAsJsonAsync(Url, Write(new JsonObject { ["key"] = key, ["value"] = "ilk" }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<RecordResponse>())!;
        Assert.Equal(1, created.Version);

        var update = await client.PutAsJsonAsync($"{Url}/{created.Id}", Write(new JsonObject { ["value"] = "ikinci" }, version: 1));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<RecordResponse>())!;
        Assert.Equal(2, updated.Version);
        Assert.Equal("ikinci", updated.Values["value"]!.GetValue<string>());

        var read = await client.GetFromJsonAsync<RecordResponse>($"{Url}/{created.Id}");
        Assert.Equal("ikinci", read!.Values["value"]!.GetValue<string>());

        var list = await client.GetFromJsonAsync<RecordPageResponse>(Url);
        Assert.Contains(list!.Items, i => i.Id == created.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Url}/{created.Id}?version=2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Url}/{created.Id}")).StatusCode);

        using var scope = factory.OpenScope(tenantId);
        var db = Db(scope);
        var audit = await db.AuditLog.Where(a => a.RecordId == created.Id).OrderBy(a => a.OccurredAt).ThenBy(a => a.Action).ToListAsync();
        Assert.Equal(new[] { AuditAction.Created, AuditAction.Updated, AuditAction.Deleted }, audit.Select(a => a.Action).ToArray());
        Assert.All(audit, a => Assert.Equal(userId, a.UserId));
        Assert.All(audit, a => Assert.Equal(tenantId, a.TenantId));

        var changes = JsonNode.Parse(audit[1].Changes)!.AsObject();
        Assert.Equal("ilk", changes["value"]!["old"]!.GetValue<string>());
        Assert.Equal("ikinci", changes["value"]!["new"]!.GetValue<string>());
        Assert.False(changes.ContainsKey("key"));

        // Olay zarfı (ADR-0007): sıra numarası kaydın yeni sürümü (silmede son sürüm + 1), kişi ve iz denetim kaydıyla aynı.
        var events = await db.OutboxMessages.Where(o => o.RecordId == created.Id).OrderBy(o => o.Sequence).ToListAsync();
        Assert.Equal(
            [DomainEventTypes.RecordCreated, DomainEventTypes.RecordUpdated, DomainEventTypes.RecordDeleted],
            events.Select(e => e.EventType));
        Assert.Equal([1L, 2L, 3L], events.Select(e => e.Sequence!.Value));
        Assert.All(events, e => Assert.Equal(userId, e.UserId));
        Assert.All(events, e => Assert.Equal(1, e.SchemaVersion));
        Assert.All(events, e => Assert.Equal(ExecutionSource.Api, e.Source));
        Assert.All(events, e => Assert.Null(e.DispatchedAt));
        Assert.Equal(audit.Select(a => a.CorrelationId), events.Select(e => e.CorrelationId));
    }

    /// <summary>Ç1.1: olay kaydın kendisiyle aynı işlemde yazılır; işlem geri alınınca olay da yoktur.</summary>
    [Fact]
    public async Task Rolled_back_transaction_writes_no_event()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CoreModule.Permissions.ManageSettings], user);
        var membershipId = await factory.GetMembershipIdAsync(user.Id, tenant.Id);

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current = new TenantContext(tenant.Id, user.Id, membershipId);
            var db = Db(scope);
            await using var transaction = await db.Database.BeginTransactionAsync();
            var result = await scope.ServiceProvider.GetRequiredService<IRecordPipeline>().CreateAsync(
                new RecordCommand("core.tenant-setting", null, new JsonObject { ["key"] = UniqueKey(), ["value"] = "geri alınacak" }),
                CancellationToken.None);
            Assert.True(result.IsSuccess);
            Assert.True(await db.OutboxMessages.AnyAsync(o => o.TenantId == tenant.Id));

            // Onaylanmadan kapanır: geri alınır.
        }

        using var check = factory.OpenScope(tenant.Id);
        Assert.False(await Db(check).OutboxMessages.AnyAsync(o => o.TenantId == tenant.Id));
        Assert.False(await Db(check).AuditLog.AnyAsync(a => a.EntityKey == "core.tenant-setting"));
    }

    [Fact]
    public async Task Other_tenant_record_looks_like_not_found()
    {
        var (owner, _, _) = await LoggedInClientAsync();
        using var __ = owner;
        var created = await CreateAsync(owner, UniqueKey(), "gizli");

        var (stranger, _, _) = await LoggedInClientAsync();
        using var ___ = stranger;

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Url}/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.PutAsJsonAsync($"{Url}/{created.Id}", Write(new JsonObject { ["value"] = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"{Url}/{created.Id}")).StatusCode);

        var list = await stranger.GetFromJsonAsync<RecordPageResponse>(Url);
        Assert.DoesNotContain(list!.Items, i => i.Id == created.Id);

        var stillThere = await owner.GetFromJsonAsync<RecordResponse>($"{Url}/{created.Id}");
        Assert.Equal("gizli", stillThere!.Values["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_values_return_field_errors_and_write_nothing()
    {
        var (client, tenantId, _) = await LoggedInClientAsync();
        using var _ = client;

        var response = await client.PostAsJsonAsync(Url, Write(new JsonObject { ["key"] = "Geçersiz Anahtar!", ["renk"] = "mavi" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var errors = body["errors"]!.AsObject();
        Assert.True(errors.ContainsKey("key"));
        Assert.True(errors.ContainsKey("value"));
        Assert.True(errors.ContainsKey("renk"));
        Assert.Equal("ValidationFailed", body["code"]!.GetValue<string>());

        // Kurulumdaki rol ataması da denetim kaydına düşer (Y1); burada yalnızca ayar kayıtlarına bakılır.
        using var scope = factory.OpenScope(tenantId);
        Assert.Equal(0, await Db(scope).AuditLog.CountAsync(a => a.EntityKey == "core.tenant-setting"));
    }

    [Fact]
    public async Task Stale_version_is_rejected_with_conflict()
    {
        var (client, _, _) = await LoggedInClientAsync();
        using var _ = client;
        var created = await CreateAsync(client, UniqueKey(), "a");
        await client.PutAsJsonAsync($"{Url}/{created.Id}", Write(new JsonObject { ["value"] = "b" }, version: 1));

        var stale = await client.PutAsJsonAsync($"{Url}/{created.Id}", Write(new JsonObject { ["value"] = "c" }, version: 1));
        var staleDelete = await client.DeleteAsync($"{Url}/{created.Id}?version=1");

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
        var current = await client.GetFromJsonAsync<RecordResponse>($"{Url}/{created.Id}");
        Assert.Equal("b", current!.Values["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Duplicate_key_is_rejected_with_conflict()
    {
        var (client, _, _) = await LoggedInClientAsync();
        using var _ = client;
        var key = UniqueKey();
        await CreateAsync(client, key, "a");

        var duplicate = await client.PostAsJsonAsync(Url, Write(new JsonObject { ["key"] = key, ["value"] = "b" }));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Unknown_entity_returns_not_found()
    {
        var (client, _, _) = await LoggedInClientAsync();
        using var _ = client;

        var response = await client.GetAsync("/api/records/crm.yok");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Without_active_tenant_records_are_forbidden()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(a, b);
        using var client = factory.CreateBrowserClient();
        await LoginAsync(client, user.Email!);

        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_user_cannot_use_records()
    {
        using var client = factory.CreateBrowserClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Url)).StatusCode);
    }

    [Fact]
    public async Task Application_cannot_change_or_delete_audit_log()
    {
        var (client, tenantId, _) = await LoggedInClientAsync();
        using var _ = client;
        await CreateAsync(client, UniqueKey(), "a");

        using var scope = factory.OpenScope(tenantId);
        var db = Db(scope);
        Assert.True(await db.AuditLog.AnyAsync());

        await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE [core].[AuditLog] SET [Changes] = {0}", "degisti"));
        await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM [core].[AuditLog]"));
    }

    private async Task<(HttpClient Client, Guid TenantId, Guid UserId)> LoggedInClientAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);

        // Ayar yazmak core.settings.manage ister (Y1); okuma izni gerektirmez.
        await factory.CreateRoleAsync(tenant.Id, [CoreModule.Permissions.ManageSettings], user);
        var client = factory.CreateBrowserClient();
        var me = await LoginAsync(client, user.Email!);
        Assert.Equal(tenant.Id, me.ActiveTenantId);
        return (client, tenant.Id, user.Id);
    }

    private static async Task<MeResponse> LoginAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MeResponse>())!;
    }

    private static async Task<RecordResponse> CreateAsync(HttpClient client, string key, string value)
    {
        var response = await client.PostAsJsonAsync(Url, Write(new JsonObject { ["key"] = key, ["value"] = value }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static RecordWriteRequest Write(JsonObject values, long? version = null) => new(values, version);

    // Her test kendi firmasını açar; anahtar tanımlı bir ayar olmalıdır (Y6).
    private static string UniqueKey() => CoreModule.SettingKeys.Address;

    private static ModulixDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
}
