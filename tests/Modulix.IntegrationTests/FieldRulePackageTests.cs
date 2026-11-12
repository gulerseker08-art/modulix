using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Sectors;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0029 K7/K8: alan kuralı paketleri (taslak → önizleme → yayın → geri alma) ve firma uyarlaması. Paketler platform genelidir;
/// testler birbirini etkilemesin diye her test kendi rastgele kural anahtarlarını ve kendi koşullu kurallarını kullanır.
/// Kurallar henüz kayıt boru hattında uygulanmaz; bu testler tanım, yayın ve birleşimi doğrular.
/// </summary>
public class FieldRulePackageTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Status = "logistics.shipment.status";
    private const string Description = "logistics.shipment.cargoDescription";
    private const string Sentinel = "__alan-kurali-testi__";
    private const string Capacity = "logistics.shipment.weightKg";

    private static string NewPrefix() => $"t{Guid.NewGuid():N}"[..9];

    // Koşul, hiçbir testin ya da komutun üretmediği bir yük tanımıdır: yayınlanan ürün paketi bu veritabanındaki bütün lojistik firmalarına ulaşır;
    // gerçek bir durum (planlama bekliyor, teslim edilemedi…) seçilirse aynı veritabanını paylaşan sevkiyat, sefer ve teslim testleri bozulur.
    private static FieldRuleDto Rule(string key, string field, string effect, string? message = null, string? value = null, bool locked = false, string whenDescription = Sentinel)
    {
        var condition = JsonSerializer.SerializeToElement<ConditionNode>(new ConditionLeaf(Description, AutomationOperator.Eq, [whenDescription]), AutomationJson.Options);
        return new FieldRuleDto(key, field, effect, condition, message, value, locked);
    }

    [Fact]
    public async Task Package_goes_through_draft_preview_publish_and_rollback_and_reaches_the_right_tenants()
    {
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(platformAdmin);
        var prefix = NewPrefix();

        // Katalog: sektör → grup → faaliyet; firma A faaliyeti seçer, firma B seçmez.
        var sector = await CreateNodeAsync(platform, null, "Sector");
        var group = await CreateNodeAsync(platform, sector.Id, "Group");
        var activity = await CreateNodeAsync(platform, group.Id, "Activity");

        var product = await CreatePackageAsync(platform, $"{prefix}-product", "ProductStandard", null);
        var sectorPackage = await CreatePackageAsync(platform, $"{prefix}-sector", "SectorPackage", sector.Id);

        // Taslak → önizleme → yayın.
        var draft = await SaveDraftAsync(platform, product.Id, [Rule($"{prefix}.warn", Capacity, "Warn", "Kapasite aşıldı.")]);
        Assert.NotNull(draft.Draft);
        Assert.Null(draft.Published);
        var preview = (await platform.GetFromJsonAsync<RulePublishPreviewDto>($"/api/platform/rule-packages/{product.Id}/preview"))!;
        Assert.True(preview.CanPublish);
        Assert.Equal([$"{prefix}.warn"], preview.Added);

        var published = await PublishAsync(platform, product.Id);
        Assert.Equal(1, published.Published!.Number);
        Assert.Null(published.Draft);

        // İkinci sürüm: kural değişir; önizleme farkı gösterir; eski sürüm geçersiz kılınır.
        await SaveDraftAsync(platform, product.Id,
            [Rule($"{prefix}.warn", Capacity, "Warn", "Kapasite çok yüksek."), Rule($"{prefix}.hint", Capacity, "SuggestDefault", value: "80")]);
        var second = (await platform.GetFromJsonAsync<RulePublishPreviewDto>($"/api/platform/rule-packages/{product.Id}/preview"))!;
        Assert.Equal([$"{prefix}.hint"], second.Added);
        Assert.Equal([$"{prefix}.warn"], second.Changed);
        Assert.Equal(2, (await PublishAsync(platform, product.Id)).Published!.Number);

        // Geri alma, 1. sürümün kurallarını yeni (3.) sürüm olarak yayınlar.
        var rolledBack = await ReadAsync<RulePackageDto>(await platform.PostAsJsonAsync($"/api/platform/rule-packages/{product.Id}/rollback", new RollbackRulePackageRequest(1)));
        Assert.Equal(3, rolledBack.Published!.Number);
        Assert.Equal([$"{prefix}.warn"], rolledBack.Published.Rules.Select(r => r.Key));
        Assert.Equal("Kapasite aşıldı.", rolledBack.Published.Rules.Single().Message);

        // Sektör paketi, sektörün altındaki bir faaliyeti seçen firmaya ulaşır.
        await SaveDraftAsync(platform, sectorPackage.Id, [Rule($"{prefix}.block", Status, "Block", "Teslim edilemeyen sevkiyat değiştirilemez.")]);
        await PublishAsync(platform, sectorPackage.Id);

        var (_, adminA) = await CreateTenantAdminAsync(withLogistics: true);
        var (_, adminB) = await CreateTenantAdminAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        Assert.Equal(HttpStatusCode.OK, (await clientA.PutAsJsonAsync("/api/sectors/selection", new ActivityIdsRequest([activity.Id]))).StatusCode);

        var effectiveA = (await clientA.GetFromJsonAsync<EffectiveRulesDto>("/api/rules/effective"))!;
        var effectiveB = (await clientB.GetFromJsonAsync<EffectiveRulesDto>("/api/rules/effective"))!;
        Assert.Contains(effectiveA.Rules, r => r.Key == $"{prefix}.warn"); // ürün standardı herkese
        Assert.Contains(effectiveA.Rules, r => r.Key == $"{prefix}.block");
        Assert.Contains(effectiveB.Rules, r => r.Key == $"{prefix}.warn");
        Assert.DoesNotContain(effectiveB.Rules, r => r.Key == $"{prefix}.block");

        // Firma uyarlaması: kendi kuralını ekler, ürün kuralını kapatır; sektör paketi yerinde kalır.
        var save = await clientA.PutAsJsonAsync("/api/rules/adaptation", new SaveRuleAdaptationRequest(
            [Rule($"{prefix}.own", Capacity, "Required")], [$"{prefix}.warn"], 0));
        var adaptation = await ReadAsync<TenantRuleAdaptationDto>(save);
        Assert.Equal(1, adaptation.Revision);
        var after = (await clientA.GetFromJsonAsync<EffectiveRulesDto>("/api/rules/effective"))!;
        Assert.DoesNotContain(after.Rules, r => r.Key == $"{prefix}.warn");
        Assert.Contains(after.Rules, r => r.Key == $"{prefix}.own");
        Assert.Contains(after.Rules, r => r.Key == $"{prefix}.block");

        // Platform denetim kaydı yönetici ile birlikte yazılır.
        using var scope = factory.Services.CreateScope();
        var actions = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().PlatformAuditLog.AsNoTracking()
            .Where(a => a.ActorUserId == platformAdmin.Id).Select(a => a.Action).ToListAsync();
        foreach (var expected in new[] { "rules.package.created", "rules.package.draft-saved", "rules.package.published", "rules.package.rolled-back" })
        {
            Assert.Contains(expected, actions);
        }
    }

    [Fact]
    public async Task Invalid_definitions_conflicts_and_stale_edits_are_rejected()
    {
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(platformAdmin);
        var prefix = NewPrefix();

        // Paket oluşturma kuralları.
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync("/api/platform/rule-packages", new CreateRulePackageRequest("Bozuk Kod", "Ad", "ProductStandard", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync("/api/platform/rule-packages", new CreateRulePackageRequest($"{prefix}-a", "Ad", "TenantAdaptation", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync("/api/platform/rule-packages", new CreateRulePackageRequest($"{prefix}-b", "Ad", "SectorPackage", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync("/api/platform/rule-packages", new CreateRulePackageRequest($"{prefix}-c", "Ad", "SectorPackage", Guid.NewGuid()))).StatusCode);

        var package = await CreatePackageAsync(platform, $"{prefix}-main", "ProductStandard", null);
        Assert.Equal(HttpStatusCode.Conflict, (await platform.PostAsJsonAsync("/api/platform/rule-packages", new CreateRulePackageRequest($"{prefix}-main", "Ad", "ProductStandard", null))).StatusCode);

        // Taslak içeriği: bilinmeyen alan, eksik ileti, çözülemeyen etki; hiçbiri kaydedilmez.
        foreach (var bad in new[]
        {
            Rule($"{prefix}.x", "logistics.shipment.yok", "Hide"),
            Rule($"{prefix}.x", Capacity, "Warn"),
            Rule($"{prefix}.x", Capacity, "Uydurma"),
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync($"/api/platform/rule-packages/{package.Id}/draft", new SaveRuleDraftRequest([bad], null, null))).StatusCode);
        }

        var packages = (await platform.GetFromJsonAsync<List<RulePackageDto>>("/api/platform/rule-packages"))!;
        Assert.Null(packages.Single(p => p.Id == package.Id).Draft);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PostAsJsonAsync($"/api/platform/rule-packages/{package.Id}/publish", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.GetAsync($"/api/platform/rule-packages/{Guid.NewGuid()}/preview")).StatusCode);

        // Eşzamanlı düzenleme: eski revizyonla yazılan taslak çakışır; revizyon verilmeyen de çakışır.
        var draft = await SaveDraftAsync(platform, package.Id, [Rule($"{prefix}.one", Capacity, "Warn", "Bir.")]);
        var revision = draft.Draft!.Revision;
        var edited = await platform.PutAsJsonAsync($"/api/platform/rule-packages/{package.Id}/draft",
            new SaveRuleDraftRequest([Rule($"{prefix}.one", Capacity, "Warn", "İki.")], null, revision));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await platform.PutAsJsonAsync($"/api/platform/rule-packages/{package.Id}/draft",
            new SaveRuleDraftRequest([Rule($"{prefix}.one", Capacity, "Warn", "Eski.")], null, revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await platform.PutAsJsonAsync($"/api/platform/rule-packages/{package.Id}/draft",
            new SaveRuleDraftRequest([Rule($"{prefix}.one", Capacity, "Warn", "Kör.")], null, null))).StatusCode);

        // Yayın: aynı katmanda başka pakette var olan anahtar yayını engeller; önizleme nedenini söyler.
        await PublishAsync(platform, package.Id);
        var other = await CreatePackageAsync(platform, $"{prefix}-other", "ProductStandard", null);
        await SaveDraftAsync(platform, other.Id, [Rule($"{prefix}.one", Capacity, "Warn", "Çakışan anahtar.")]);
        var preview = (await platform.GetFromJsonAsync<RulePublishPreviewDto>($"/api/platform/rule-packages/{other.Id}/preview"))!;
        Assert.False(preview.CanPublish);
        Assert.Contains(preview.Conflicts, c => c.Path == $"{prefix}.one");
        Assert.Equal(HttpStatusCode.Conflict, (await platform.PostAsJsonAsync($"/api/platform/rule-packages/{other.Id}/publish", new { })).StatusCode);
        Assert.Null((await platform.GetFromJsonAsync<List<RulePackageDto>>("/api/platform/rule-packages"))!.Single(p => p.Id == other.Id).Published);

        // Geri alma: yayındaki sürüme, olmayan sürüme ve açık taslak varken geri alınamaz.
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync($"/api/platform/rule-packages/{package.Id}/rollback", new RollbackRulePackageRequest(1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PostAsJsonAsync($"/api/platform/rule-packages/{package.Id}/rollback", new RollbackRulePackageRequest(99))).StatusCode);

        // Taslak silinebilir ve yayındaki sürüm değişmez.
        var discarded = await ReadAsync<RulePackageDto>(await platform.DeleteAsync($"/api/platform/rule-packages/{other.Id}/draft"));
        Assert.Null(discarded.Draft);
    }

    [Fact]
    public async Task Tenant_adaptation_cannot_override_locked_rules_and_is_validated()
    {
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(platformAdmin);
        var prefix = NewPrefix();

        var package = await CreatePackageAsync(platform, $"{prefix}-locked", "ProductStandard", null);
        await SaveDraftAsync(platform, package.Id, [Rule($"{prefix}.approval", Capacity, "RequireApproval", "Yönetici onayı gerekir.", locked: true)]);
        await PublishAsync(platform, package.Id);

        var (tenant, admin) = await CreateTenantAdminAsync(withLogistics: true);
        using var client = await LoginAsync(admin);

        // Kilitli kural geçersiz kılınamaz ve kapatılamaz; ikisi de kaydedilmez.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/rules/adaptation",
            new SaveRuleAdaptationRequest([Rule($"{prefix}.approval", Capacity, "Warn", "Sadece uyarı.")], [], 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/rules/adaptation",
            new SaveRuleAdaptationRequest([], [$"{prefix}.approval"], 0))).StatusCode);

        // Tanım hataları: bilinmeyen alan, eksik ileti.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/rules/adaptation",
            new SaveRuleAdaptationRequest([Rule($"{prefix}.own", "logistics.shipment.yok", "Hide")], [], 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/rules/adaptation",
            new SaveRuleAdaptationRequest([Rule($"{prefix}.own", Capacity, "Block")], [], 0))).StatusCode);

        var current = (await client.GetFromJsonAsync<TenantRuleAdaptationDto>("/api/rules/adaptation"))!;
        Assert.Empty(current.Rules);
        Assert.Equal(0, current.Revision);

        // Geçerli uyarlama kaydedilir; eski revizyonla ikinci yazma çakışır.
        var saved = await ReadAsync<TenantRuleAdaptationDto>(await client.PutAsJsonAsync("/api/rules/adaptation",
            new SaveRuleAdaptationRequest([Rule($"{prefix}.own", Capacity, "Warn", "Kendi uyarım.")], [], 0)));
        Assert.Equal(1, saved.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/rules/adaptation",
            new SaveRuleAdaptationRequest([], [], 0))).StatusCode);

        // Kalkmış bir kuralı kapatmak yalnızca uyarıdır, kaydı engellemez.
        var stale = await client.PutAsJsonAsync("/api/rules/adaptation",
            new SaveRuleAdaptationRequest(saved.Rules, [$"{prefix}.artik-yok"], saved.Revision));
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Contains((await client.GetFromJsonAsync<EffectiveRulesDto>("/api/rules/effective"))!.Warnings, w => w.Path == $"{prefix}.artik-yok");

        // Denetim kaydı firmanın kendisindedir.
        using var scope = factory.OpenScope(tenant.Id);
        var audit = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().AuditLog.AsNoTracking()
            .Where(a => a.EntityKey == "core.rule-adaptation").ToListAsync();
        Assert.Equal(2, audit.Count);
        Assert.All(audit, a => Assert.Equal(admin.Id, a.UserId));
    }

    [Fact]
    public async Task One_tenants_adaptation_is_invisible_to_another_tenant()
    {
        var prefix = NewPrefix();
        var (tenantA, adminA) = await CreateTenantAdminAsync(withLogistics: true);
        var (tenantB, adminB) = await CreateTenantAdminAsync(withLogistics: true);
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);

        Assert.Equal(HttpStatusCode.OK, (await clientA.PutAsJsonAsync("/api/rules/adaptation",
            new SaveRuleAdaptationRequest([Rule($"{prefix}.own", Capacity, "Warn", "A'nın kuralı.")], [], 0))).StatusCode);

        Assert.Empty((await clientB.GetFromJsonAsync<TenantRuleAdaptationDto>("/api/rules/adaptation"))!.Rules);
        Assert.DoesNotContain((await clientB.GetFromJsonAsync<EffectiveRulesDto>("/api/rules/effective"))!.Rules, r => r.Key == $"{prefix}.own");

        // B'nin kendi uyarlaması A'nınkini ezmez (tenant başına tek kayıt).
        Assert.Equal(HttpStatusCode.OK, (await clientB.PutAsJsonAsync("/api/rules/adaptation",
            new SaveRuleAdaptationRequest([Rule($"{prefix}.own", Capacity, "Required")], [], 0))).StatusCode);
        Assert.Equal("Warn", (await clientA.GetFromJsonAsync<TenantRuleAdaptationDto>("/api/rules/adaptation"))!.Rules.Single().Effect);

        // Süzgeç atlansa bile Row-Level Security başka firmanın satırını göstermez.
        using var scopeB = factory.OpenScope(tenantB.Id);
        var rows = await scopeB.ServiceProvider.GetRequiredService<ModulixDbContext>().RuleAdaptations.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.NotEqual(tenantA.Id, tenantB.Id);
    }

    [Fact]
    public async Task Only_authorized_users_reach_packages_and_tenant_rules()
    {
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(platformAdmin);

        var (tenant, admin) = await CreateTenantAdminAsync();
        using var adminClient = await LoginAsync(admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.GetAsync("/api/platform/rule-packages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.PostAsJsonAsync("/api/platform/rule-packages",
            new CreateRulePackageRequest($"{NewPrefix()}-x", "İzinsiz", "ProductStandard", null))).StatusCode);

        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/platform/rule-packages")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/rules/effective")).StatusCode);

        // Özelleştirme izni olmayan firma kullanıcısı etkin kuralları ve uyarlamayı göremez, değiştiremez.
        var rep = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [], rep);
        using var repClient = await LoginAsync(rep);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync("/api/rules/effective")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync("/api/rules/adaptation")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.PutAsJsonAsync("/api/rules/adaptation", new SaveRuleAdaptationRequest([], [], 0))).StatusCode);

        // Platform yöneticisi firma bağlamı olmadan firma kural uçlarına giremez.
        Assert.False((await platform.GetAsync("/api/rules/effective")).IsSuccessStatusCode);
        Assert.False((await platform.PutAsJsonAsync("/api/rules/adaptation", new SaveRuleAdaptationRequest([], [], 0))).IsSuccessStatusCode);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ActivityNodeDto> CreateNodeAsync(HttpClient platform, Guid? parentId, string kind) =>
        await ReadAsync<ActivityNodeDto>(await platform.PostAsJsonAsync("/api/platform/sectors/nodes",
            new CreateActivityNodeRequest($"t-{Guid.NewGuid():N}"[..14], parentId, kind, "Test düğümü", null, 0)));

    private static async Task<RulePackageDto> CreatePackageAsync(HttpClient platform, string code, string layer, Guid? nodeId) =>
        await ReadAsync<RulePackageDto>(await platform.PostAsJsonAsync("/api/platform/rule-packages", new CreateRulePackageRequest(code, "Test paketi", layer, nodeId)));

    private static async Task<RulePackageDto> SaveDraftAsync(HttpClient platform, Guid packageId, List<FieldRuleDto> rules)
    {
        var packages = (await platform.GetFromJsonAsync<List<RulePackageDto>>("/api/platform/rule-packages"))!;
        var revision = packages.Single(p => p.Id == packageId).Draft?.Revision;
        return await ReadAsync<RulePackageDto>(await platform.PutAsJsonAsync($"/api/platform/rule-packages/{packageId}/draft", new SaveRuleDraftRequest(rules, null, revision)));
    }

    private static async Task<RulePackageDto> PublishAsync(HttpClient platform, Guid packageId) =>
        await ReadAsync<RulePackageDto>(await platform.PostAsJsonAsync($"/api/platform/rule-packages/{packageId}/publish", new { }));

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin)> CreateTenantAdminAsync(bool withLogistics = false)
    {
        var tenant = await factory.CreateTenantAsync();
        if (withLogistics)
        {
            await factory.SetModuleAsync(tenant.Id, "logistics", enabled: true);
        }

        var admin = await factory.CreateUserAsync(tenant);
        using var scope = factory.OpenScope(tenant.Id);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
        Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        return (tenant, admin);
    }

    private async Task<ModulixUser> CreatePlatformAdministratorAsync()
    {
        var user = await factory.CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var stored = (await users.FindByIdAsync(user.Id.ToString()))!;
        stored.IsPlatformAdministrator = true;
        Assert.True((await users.UpdateAsync(stored)).Succeeded);
        return user;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
