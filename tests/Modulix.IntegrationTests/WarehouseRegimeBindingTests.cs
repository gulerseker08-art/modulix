using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Customs;
using Modulix.Shared.Records;
using Modulix.Shared.Sectors;
using static Modulix.Modules.Warehouse.WarehouseModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0032 dilim 5c (rejim–profil bağlaması ve beyannamede tescil no denetimi, K7, K16): mutlu yol (uyarı düzeyi kaydı engellemez, tenant sıkılaştırınca
/// engeller, yeni profil sürümü hemen uygulanır), doğrulama (yayında olmayan/pasif/olmayan profil, tekrar eden rejim), tenant izolasyonu ve yetki.
/// Profiller global olduğundan her test kendi rastgele kodlu profilini oluşturur.
/// </summary>
public class WarehouseRegimeBindingTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>, IAsyncLifetime
{
    // Profiller global ve silinemez; testlerin bıraktıkları paylaşılan veritabanında tenant'ların profil seçimini kirletmesin diye sonunda pasife alınır.
    private readonly List<(Guid ActorId, Guid ProfileId)> createdProfiles = [];

    private const string Bindings = "/api/records/warehouse.regime-binding";
    private const string Declarations = "/api/records/warehouse.customs-declaration";
    private const string Parties = "/api/records/core.party";
    private const string ProfilesUrl = "/api/platform/regime-profiles";
    private const string Regime = "R7100";

    [Fact]
    public async Task Bound_regime_checks_the_registration_no_warns_by_default_blocks_when_tightened_and_follows_new_versions()
    {
        using var platform = await PlatformClientAsync();
        var profile = await PublishedProfileAsync(platform, "Warn");
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var importer = await PartyAsync(admin, "Profil İthalat", "ithalatci-malik");

        // Bağlama yokken kural yoktur: her numara geçer.
        var none = await CheckAsync(admin, "ne-olursa");
        Assert.False(none.HasRule);
        await PostAsync(admin, Declarations, Declaration("ESKI-1", importer));

        var binding = await PostAsync(admin, Bindings, new JsonObject { ["regimeKey"] = Regime, ["profileId"] = profile.ToString() });
        Assert.Equal("Inherit", binding.Values["enforcement"]!.GetValue<string>());
        Assert.Equal([binding.Id], (await ListAsync(admin, Bindings, $"regimeKey={Regime}")).Select(i => i.Id));

        // Uyarı düzeyi: ihlal denetimde görünür ama kayıt engellenmez; uygun numara ihlalsizdir.
        var warn = await CheckAsync(admin, "ABC");
        Assert.Equal((true, "Warn", 1, false), (warn.HasRule, warn.Enforcement, warn.VersionNumber, warn.Verified));
        Assert.NotEmpty(warn.Violations);
        Assert.Empty((await CheckAsync(admin, "123456")).Violations);
        var lenient = await PostAsync(admin, Declarations, Declaration("ABC", importer));
        await PostAsync(admin, Declarations, Declaration("123456", importer));

        // Tenant sıkılaştırır: yeni ve değişen numaralar engellenir; numarası değişmeyen eski beyannamenin başka alanları düzenlenebilir.
        var tightened = await PutAsync(admin, Bindings, binding, new JsonObject { ["enforcement"] = "Block" });
        Assert.Equal("Block", (await CheckAsync(admin, "ABC")).Enforcement);
        var rejected = await ValidationProblemAsync(await admin.PostAsJsonAsync(Declarations, new RecordWriteRequest(Declaration("XYZ", importer))));
        Assert.Contains("registrationNo", rejected.Errors.Keys);
        Assert.Contains("rejim profili", rejected.Errors["registrationNo"][0]);
        var edited = await PutAsync(admin, Declarations, lenient, new JsonObject { ["description"] = "Numarası değişmedi" });
        Assert.Equal("Numarası değişmedi", edited.Values["description"]!.GetValue<string>());
        var changedNo = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Declarations}/{lenient.Id}",
            new RecordWriteRequest(new JsonObject { ["registrationNo"] = "BOZUK" }, edited.Version)));
        Assert.Contains("registrationNo", changedNo.Errors.Keys);
        await PutAsync(admin, Declarations, edited, new JsonObject { ["registrationNo"] = "654321" });

        // Platform yeni sürümü yayınlar: kural hemen uygulanır (altı hane yerine sekiz hane).
        await PublishNewVersionAsync(platform, profile, @"\d{8}", 8, 8, "12345678", "Warn");
        var next = await CheckAsync(admin, "123456");
        Assert.Equal((2, "Block"), (next.VersionNumber, next.Enforcement));
        Assert.NotEmpty(next.Violations);
        await PostAsync(admin, Declarations, Declaration("87654321", importer));

        // Profil pasife alınınca kural uygulanmaz ve seçilemez; bağlama silinince de kural kalkar.
        await platform.PutAsJsonAsync($"{ProfilesUrl}/{profile}/active", new SetActiveRequest(false));
        Assert.False((await CheckAsync(admin, "ABC")).HasRule);
        Assert.DoesNotContain(await admin.GetFromJsonAsync<List<PublishedRegimeProfileDto>>("/api/warehouse/regime/profiles") ?? [], p => p.ProfileId == profile);
        await platform.PutAsJsonAsync($"{ProfilesUrl}/{profile}/active", new SetActiveRequest(true));
        Assert.True((await CheckAsync(admin, "ABC")).HasRule);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Bindings}/{tightened.Id}?version={tightened.Version}")).StatusCode);
        Assert.False((await CheckAsync(admin, "ABC")).HasRule);
    }

    [Fact]
    public async Task Tenant_cannot_loosen_a_blocking_profile()
    {
        using var platform = await PlatformClientAsync();
        var profile = await PublishedProfileAsync(platform, "Block");
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var importer = await PartyAsync(admin, "Sıkı İthalat", "ithalatci-malik");

        // Tenant "Warn" seçse de etkin düzey profilin düzeyidir (Block); "Inherit" de aynıdır.
        await PostAsync(admin, Bindings, new JsonObject { ["regimeKey"] = Regime, ["profileId"] = profile.ToString(), ["enforcement"] = "Warn" });
        Assert.Equal("Block", (await CheckAsync(admin, "ABC")).Enforcement);
        var rejected = await ValidationProblemAsync(await admin.PostAsJsonAsync(Declarations, new RecordWriteRequest(Declaration("ABC", importer))));
        Assert.Contains("registrationNo", rejected.Errors.Keys);
        await PostAsync(admin, Declarations, Declaration("123456", importer));
    }

    [Fact]
    public async Task Binding_needs_a_published_active_profile_a_known_regime_and_is_unique_per_regime()
    {
        using var platform = await PlatformClientAsync();
        var published = await PublishedProfileAsync(platform, "Warn");
        var draftOnly = (await (await platform.PostAsJsonAsync(ProfilesUrl, new CreateRegimeProfileRequest(NewCode(), "Taslak profil"))).Content.ReadFromJsonAsync<RegimeProfileSummaryDto>())!;
        createdProfiles.Add((platformActor, draftOnly.Id));
        await platform.PutAsJsonAsync($"{ProfilesUrl}/{draftOnly.Id}/draft", Content(@"\d{6}", 6, 6, "123456", "Warn"));
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        async Task<ValidationProblemDetails> Reject(JsonObject values) =>
            await ValidationProblemAsync(await admin.PostAsJsonAsync(Bindings, new RecordWriteRequest(values)));

        // Olmayan profil, yalnızca taslağı olan profil: "bulunamadı"; bilinmeyen rejim; geçersiz düzey; eksik profil.
        Assert.Contains("profileId", (await Reject(new JsonObject { ["regimeKey"] = Regime, ["profileId"] = Guid.NewGuid().ToString() })).Errors.Keys);
        Assert.Contains("profileId", (await Reject(new JsonObject { ["regimeKey"] = Regime, ["profileId"] = draftOnly.Id.ToString() })).Errors.Keys);
        Assert.Contains("regimeKey", (await Reject(new JsonObject { ["regimeKey"] = "R9999", ["profileId"] = published.ToString() })).Errors.Keys);
        Assert.Contains("enforcement", (await Reject(new JsonObject { ["regimeKey"] = Regime, ["profileId"] = published.ToString(), ["enforcement"] = "Belki" })).Errors.Keys);
        Assert.Contains("profileId", (await Reject(new JsonObject { ["regimeKey"] = Regime })).Errors.Keys);

        // Rejim başına tek bağlama.
        await PostAsync(admin, Bindings, new JsonObject { ["regimeKey"] = Regime, ["profileId"] = published.ToString() });
        Assert.Equal(["Bu rejim zaten bir profile bağlı; mevcut bağlamayı düzenleyin."], (await Reject(new JsonObject { ["regimeKey"] = Regime, ["profileId"] = published.ToString() })).Errors["regimeKey"]);

        // Tenant yalnızca yayındaki etkin profilleri görür; taslak-only profil listede yoktur.
        var visible = (await admin.GetFromJsonAsync<List<PublishedRegimeProfileDto>>("/api/warehouse/regime/profiles"))!;
        Assert.Contains(visible, p => p.ProfileId == published && p.RegistrationNo is { Pattern: @"\d{6}" });
        Assert.DoesNotContain(visible, p => p.ProfileId == draftOnly.Id);
    }

    [Fact]
    public async Task Bindings_are_per_tenant_and_need_their_own_permissions()
    {
        using var platform = await PlatformClientAsync();
        var profile = await PublishedProfileAsync(platform, "Block");
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var importerB = await PartyAsync(adminB, "B İthalat", "ithalatci-malik");

        // A bağlar; B'nin rejimi bağlı değildir: kural B'nin beyannamelerine uygulanmaz ve B A'nın bağlamasını görmez.
        var bindingA = await PostAsync(adminA, Bindings, new JsonObject { ["regimeKey"] = Regime, ["profileId"] = profile.ToString() });
        Assert.Empty(await ListAsync(adminB, Bindings, null));
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Bindings}/{bindingA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"{Bindings}/{bindingA.Id}?version={bindingA.Version}")).StatusCode);
        Assert.False((await CheckAsync(adminB, "ABC")).HasRule);
        await PostAsync(adminB, Declarations, Declaration("ABC", importerB));

        // Aynı profile B de kendi bağlamasını bağımsız kurar.
        var bindingB = await PostAsync(adminB, Bindings, new JsonObject { ["regimeKey"] = Regime, ["profileId"] = profile.ToString() });
        Assert.NotEqual(bindingA.Id, bindingB.Id);

        // Yetki: bağlama okuma/yazma ayrı izinlerdir; beyanname izni bağlamayı, bağlama izni beyannameyi açmaz. Denetim ucu beyanname okuma ister.
        using var reader = await LoginAsync(a.RegimeReader);
        using var writer = await LoginAsync(a.RegimeWriter);
        using var declarationsOnly = await LoginAsync(a.DeclarationsOnly);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Bindings}/{bindingA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/warehouse/regime/profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{Bindings}/{bindingA.Id}", new RecordWriteRequest(new JsonObject { ["enforcement"] = "Block" }, bindingA.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Bindings}/{bindingA.Id}?version={bindingA.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/warehouse/regime/check?regimeKey=R7100&registrationNo=1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await writer.PutAsJsonAsync($"{Bindings}/{bindingA.Id}", new RecordWriteRequest(new JsonObject { ["enforcement"] = "Block" }, bindingA.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await declarationsOnly.GetAsync(Bindings)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await declarationsOnly.GetAsync("/api/warehouse/regime/profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await declarationsOnly.GetAsync("/api/warehouse/regime/check?regimeKey=R7100&registrationNo=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await declarationsOnly.GetAsync("/api/warehouse/regime/check?regimeKey=R7100")).StatusCode);
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/warehouse/regime/profiles")).StatusCode);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (actor, profile) in createdProfiles)
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<Modulix.Core.Customs.IRegimeProfileAdmin>().SetActiveAsync(actor, profile, false, CancellationToken.None);
        }
    }

    private static string NewCode() => $"t-{Guid.NewGuid():N}"[..14];

    private static RegimeProfileContentDto Content(string pattern, int min, int max, string example, string enforcement) =>
        new(1, "7100", new RegistrationNoRuleDto(pattern, min, max, example, enforcement));

    private async Task<Guid> PublishedProfileAsync(HttpClient platform, string enforcement)
    {
        var created = await platform.PostAsJsonAsync(ProfilesUrl, new CreateRegimeProfileRequest(NewCode(), "Test profili"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<RegimeProfileSummaryDto>())!.Id;
        createdProfiles.Add((platformActor, id));
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync($"{ProfilesUrl}/{id}/draft", Content(@"\d{6}", 6, 6, "123456", enforcement))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await platform.PostAsJsonAsync($"{ProfilesUrl}/{id}/publish", new PublishRegimeProfileRequest(false, null))).StatusCode);
        return id;
    }

    private static async Task PublishNewVersionAsync(HttpClient platform, Guid id, string pattern, int min, int max, string example, string enforcement)
    {
        Assert.Equal(HttpStatusCode.OK, (await platform.PutAsJsonAsync($"{ProfilesUrl}/{id}/draft", Content(pattern, min, max, example, enforcement))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await platform.PostAsJsonAsync($"{ProfilesUrl}/{id}/publish", new PublishRegimeProfileRequest(false, null))).StatusCode);
    }

    private static JsonObject Declaration(string number, Guid importer) => new()
    {
        ["registrationNo"] = number, ["registrationDate"] = "2026-03-10", ["importerPartyId"] = importer.ToString(), ["regimeKey"] = Regime,
    };

    private Guid platformActor;

    private async Task<HttpClient> PlatformClientAsync()
    {
        var user = await factory.CreateUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
            var stored = (await users.FindByIdAsync(user.Id.ToString()))!;
            stored.IsPlatformAdministrator = true;
            Assert.True((await users.UpdateAsync(stored)).Succeeded);
        }

        platformActor = user.Id;
        return await LoginAsync(user);
    }

    private async Task<(ModulixUser Admin, ModulixUser RegimeReader, ModulixUser RegimeWriter, ModulixUser DeclarationsOnly)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var regimeReader = await factory.CreateUserAsync(tenant);
        var regimeWriter = await factory.CreateUserAsync(tenant);
        var declarationsOnly = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [RegimesRead], Scope.All, regimeReader);
        await factory.CreateRoleAsync(tenant.Id, [RegimesRead, RegimesWrite], Scope.All, regimeWriter);
        await factory.CreateRoleAsync(tenant.Id, [DeclarationsRead], Scope.All, declarationsOnly);
        return (admin, regimeReader, regimeWriter, declarationsOnly);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<RegistrationNoCheckDto> CheckAsync(HttpClient client, string number)
    {
        var response = await client.GetAsync($"/api/warehouse/regime/check?regimeKey={Regime}&registrationNo={Uri.EscapeDataString(number)}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RegistrationNoCheckDto>())!;
    }

    private static async Task<Guid> PartyAsync(HttpClient client, string name, params string[] roles) =>
        (await PostAsync(client, Parties, new JsonObject
        {
            ["name"] = name,
            ["taxNumber"] = Guid.NewGuid().ToString("N")[..10],
            ["relationshipRoles"] = new JsonArray([.. roles.Select(r => (JsonNode?)JsonValue.Create(r))]),
        })).Id;

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string url, string? query)
    {
        var response = await client.GetAsync($"{url}?pageSize=50{(query is null ? string.Empty : "&" + query)}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordPageResponse>())!.Items;
    }

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<RecordResponse> PutAsync(HttpClient client, string url, RecordResponse record, JsonObject values)
    {
        var response = await client.PutAsJsonAsync($"{url}/{record.Id}", new RecordWriteRequest(values, record.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
