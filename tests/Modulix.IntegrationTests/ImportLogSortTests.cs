using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Imports;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Imports;
using static Modulix.Core.Modules.CoreModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// İçe aktarma günlüğü başlığa göre sıralama (<c>sort</c> anahtarı, ADR-0036): artan/azalan, varsayılan en yeni önce, geçersiz anahtar reddi,
/// tenant izolasyonu ve yetki (izni olmayan sıralı listeyi açamaz).
/// </summary>
public class ImportLogSortTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string Hash = new('c', ImportRun.FileHashLength);

    [Fact]
    public async Task Runs_sort_by_a_column_in_both_directions_and_default_to_newest_first()
    {
        var tenant = await factory.CreateTenantAsync();
        var reader = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadImports], reader);
        await SeedRunAsync(tenant.Id, reader.Id, "b.csv", created: 5, createdAt: DateTimeOffset.UtcNow.AddHours(-3));
        await SeedRunAsync(tenant.Id, reader.Id, "c.csv", created: 1, createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        await SeedRunAsync(tenant.Id, reader.Id, "a.csv", created: 9, createdAt: DateTimeOffset.UtcNow.AddHours(-1));
        using var client = await LoginAsync(reader);

        Assert.Equal(["a.csv", "c.csv", "b.csv"], await FilesAsync(client, null));
        Assert.Equal(["a.csv", "b.csv", "c.csv"], await FilesAsync(client, "fileName"));
        Assert.Equal(["c.csv", "b.csv", "a.csv"], await FilesAsync(client, "-fileName"));
        Assert.Equal(["c.csv", "b.csv", "a.csv"], await FilesAsync(client, "createdCount"));
        Assert.Equal(["a.csv", "b.csv", "c.csv"], await FilesAsync(client, "-createdCount"));
        Assert.Equal(["b.csv", "c.csv", "a.csv"], await FilesAsync(client, "createdAt"));
        Assert.Equal(["a.csv", "c.csv", "b.csv"], await FilesAsync(client, "-createdAt"));
    }

    [Fact]
    public async Task An_unknown_sort_key_is_rejected()
    {
        var tenant = await factory.CreateTenantAsync();
        var reader = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadImports], reader);
        using var client = await LoginAsync(reader);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/api/imports?sort=userName")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync("/api/imports?sort=fileName%20desc")).StatusCode);
    }

    [Fact]
    public async Task A_sorted_list_never_shows_another_tenants_runs()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var readerA = await factory.CreateUserAsync(a);
        var runnerB = await factory.CreateUserAsync(b);
        await factory.CreateRoleAsync(a.Id, [ReadImports], readerA);
        await SeedRunAsync(a.Id, readerA.Id, "yalniz-a.csv", created: 1);
        await SeedRunAsync(b.Id, runnerB.Id, "yalniz-b.csv", created: 2);
        using var client = await LoginAsync(readerA);

        Assert.Equal(["yalniz-a.csv"], await FilesAsync(client, "-fileName"));
    }

    [Fact]
    public async Task Without_import_permission_a_sorted_list_is_forbidden()
    {
        var tenant = await factory.CreateTenantAsync();
        var nobody = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadAudit], nobody);
        using var client = await LoginAsync(nobody);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/imports?sort=fileName")).StatusCode);
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/imports?sort=fileName")).StatusCode);
    }

    private static async Task<List<string>> FilesAsync(HttpClient client, string? sort)
    {
        var url = sort is null ? "/api/imports?pageSize=50" : $"/api/imports?pageSize=50&sort={Uri.EscapeDataString(sort)}";
        var page = await client.GetFromJsonAsync<ImportRunPageDto>(url);
        return [.. page!.Items.Select(r => r.FileName)];
    }

    private async Task SeedRunAsync(Guid tenantId, Guid userId, string fileName, int created, DateTimeOffset? createdAt = null)
    {
        var now = createdAt ?? DateTimeOffset.UtcNow;
        var run = ImportRun.Create(
            tenantId, userId, "crm.lead", fileName, 4096, Hash, 1,
            ImportRunMode.Apply, ImportFailurePolicy.SkipInvalidRows, Guid.NewGuid().ToString("N"), now);
        run.Start(created, now);
        run.Complete(created, 0, 0, 0, note: null, now.AddSeconds(5));

        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync();
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
