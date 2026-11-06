using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Customs;
using Modulix.Shared.Sectors;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0032 dilim 5a (rejim profili, K16, K7): platform paketinin yaşam döngüsü (oluştur, taslak, yayın, yerini bırakma), doğrulama
/// (içerik, kod, yayın koşulları) ve yetki. Profil global olduğundan tenant verisi içermez; "tenant izolasyonu" tenant kullanıcısının
/// platform uçlarına erişememesidir. Profil kodları rastgele üretilir; testler birbirini etkilemez.
/// </summary>
public class RegimeProfileTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/platform/regime-profiles";

    private static RegimeProfileContentDto Content(string pattern = @"\d{8}[A-Z]{2}\d{8}", string enforcement = "Warn", string? example = "25341300IM00123456") =>
        new(1, "7100", new RegistrationNoRuleDto(pattern, 18, 18, example, enforcement));

    [Fact]
    public async Task Profile_goes_from_draft_to_published_and_a_new_publication_supersedes_the_previous_one()
    {
        var admin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(admin);
        var profile = await CreateAsync(platform);

        // Yeni profilin yayını da taslağı da yoktur.
        Assert.Equal((null, false, false), (profile.PublishedNumber, profile.HasDraft, profile.PublishedVerified));

        var draft = await SaveDraftAsync(platform, profile.Id, Content());
        Assert.Equal(("Draft", 1), (draft.State, draft.Number));
        Assert.Equal("Warn", draft.Content.RegistrationNo!.Enforcement);

        // Taslak değiştirilir (aynı numara), yeni taslak açılmaz.
        var edited = await SaveDraftAsync(platform, profile.Id, Content(enforcement: "Block"));
        Assert.Equal((draft.Id, "Block"), (edited.Id, edited.Content.RegistrationNo!.Enforcement));

        // Doğrulanmamış yayın: not şart değil, ama "Doğrulanmadı" olarak işaretlenir.
        var published = await PublishAsync(platform, profile.Id, verified: false, note: null);
        Assert.Equal(("Published", 1, false), (published.State, published.Number, published.IsVerified));
        var afterFirst = (await platform.GetFromJsonAsync<RegimeProfileDetailDto>($"{Url}/{profile.Id}"))!;
        Assert.Equal((1, false, false), (afterFirst.Summary.PublishedNumber, afterFirst.Summary.PublishedVerified, afterFirst.Summary.HasDraft));

        // İkinci sürüm: taslak 2 numaralı açılır; yayınlanınca birinci "yerini bırakır" ve geçmişte kalır.
        var second = await SaveDraftAsync(platform, profile.Id, Content(pattern: @"\d{8}[A-Z]{2}\d{8}", example: "25341300EX00123456"));
        Assert.Equal(2, second.Number);
        var verified = await PublishAsync(platform, profile.Id, verified: true, note: "Uzman X, 6 Kasım 2026 görüşmesi");
        Assert.Equal(("Published", 2, true), (verified.State, verified.Number, verified.IsVerified));

        var detail = (await platform.GetFromJsonAsync<RegimeProfileDetailDto>($"{Url}/{profile.Id}"))!;
        Assert.Equal([2, 1], detail.Versions.Select(v => v.Number));
        Assert.Equal(["Published", "Superseded"], detail.Versions.Select(v => v.State));
        Assert.Equal((2, true, false), (detail.Summary.PublishedNumber, detail.Summary.PublishedVerified, detail.Summary.HasDraft));
        Assert.Equal("Uzman X, 6 Kasım 2026 görüşmesi", detail.Versions[0].VerificationNote);
        Assert.Equal("Block", detail.Versions[1].Content.RegistrationNo!.Enforcement);

        // Taslak silinebilir; yayın etkilenmez.
        await SaveDraftAsync(platform, profile.Id, Content());
        Assert.Equal(HttpStatusCode.NoContent, (await platform.DeleteAsync($"{Url}/{profile.Id}/draft")).StatusCode);
        var final = (await platform.GetFromJsonAsync<RegimeProfileDetailDto>($"{Url}/{profile.Id}"))!;
        Assert.Equal((2, false), (final.Summary.PublishedNumber, final.Summary.HasDraft));

        // Pasife alınan profil listede kalır.
        var inactive = await platform.PutAsJsonAsync($"{Url}/{profile.Id}/active", new SetActiveRequest(false));
        Assert.False((await inactive.Content.ReadFromJsonAsync<RegimeProfileSummaryDto>())!.IsActive);
        Assert.Contains(await platform.GetFromJsonAsync<List<RegimeProfileSummaryDto>>(Url) ?? [], p => p.Id == profile.Id && !p.IsActive);

        // Platform denetim kaydı.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var actions = await db.PlatformAuditLog.AsNoTracking().Where(a => a.ActorUserId == admin.Id).Select(a => a.Action).ToListAsync();
        Assert.Contains("regime-profile.created", actions);
        Assert.Contains("regime-profile.draft-saved", actions);
        Assert.Contains("regime-profile.published", actions);
        Assert.Contains("regime-profile.draft-discarded", actions);
    }

    [Fact]
    public async Task Invalid_codes_content_and_publication_steps_are_rejected()
    {
        var admin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(admin);
        var profile = await CreateAsync(platform);

        // Kod biçimi ve tekrarı; ad zorunlu.
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync(Url, new CreateRegimeProfileRequest("Büyük Harf", "Ad"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync(Url, new CreateRegimeProfileRequest(NewCode(), "  "))).StatusCode);
        var duplicate = await platform.PostAsJsonAsync(Url, new CreateRegimeProfileRequest(profile.Code, "Aynı kod"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // Geçersiz içerik taslak olarak bile kaydedilmez: bozuk desen, örnek kurala uymuyor, uzunluk sırası, uygulama düzeyi, sürüm.
        async Task<HttpStatusCode> DraftAsync(RegimeProfileContentDto content) => (await platform.PutAsJsonAsync($"{Url}/{profile.Id}/draft", content)).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await DraftAsync(Content(pattern: "([bozuk")));
        Assert.Equal(HttpStatusCode.BadRequest, await DraftAsync(Content(example: "yanlis-ornek")));
        Assert.Equal(HttpStatusCode.BadRequest, await DraftAsync(new RegimeProfileContentDto(1, "7100", new RegistrationNoRuleDto(null, 20, 10, null, "Warn"))));
        Assert.Equal(HttpStatusCode.BadRequest, await DraftAsync(Content(enforcement: "Belki")));
        Assert.Equal(HttpStatusCode.BadRequest, await DraftAsync(new RegimeProfileContentDto(9, "7100", null)));
        var detail = (await platform.GetFromJsonAsync<RegimeProfileDetailDto>($"{Url}/{profile.Id}"))!;
        Assert.Empty(detail.Versions);

        // Taslak yokken yayın ve silme; doğrulanmış yayın not ister; çok uzun not.
        Assert.Equal(HttpStatusCode.Conflict, (await platform.PostAsJsonAsync($"{Url}/{profile.Id}/publish", new PublishRegimeProfileRequest(false, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.DeleteAsync($"{Url}/{profile.Id}/draft")).StatusCode);
        await SaveDraftAsync(platform, profile.Id, Content());
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync($"{Url}/{profile.Id}/publish", new PublishRegimeProfileRequest(true, "  "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync($"{Url}/{profile.Id}/publish", new PublishRegimeProfileRequest(false, new string('n', 501)))).StatusCode);
        var stillDraft = (await platform.GetFromJsonAsync<RegimeProfileDetailDto>($"{Url}/{profile.Id}"))!;
        Assert.Equal((null, true), (stillDraft.Summary.PublishedNumber, stillDraft.Summary.HasDraft));

        // Bilinmeyen profil.
        Assert.Equal(HttpStatusCode.NotFound, (await platform.GetAsync($"{Url}/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PutAsJsonAsync($"{Url}/{Guid.NewGuid()}/draft", Content())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.PostAsJsonAsync($"{Url}/{Guid.NewGuid()}/publish", new PublishRegimeProfileRequest(false, null))).StatusCode);
    }

    [Fact]
    public async Task Only_platform_administrators_manage_profiles()
    {
        var admin = await CreatePlatformAdministratorAsync();
        using var platform = await LoginAsync(admin);
        var profile = await CreateAsync(platform);
        await SaveDraftAsync(platform, profile.Id, Content());

        // Tenant kullanıcısı ne okuyabilir ne yazabilir; oturumsuz çağrı oturum ister. Hiçbiri profili değiştirmez.
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        using var tenantClient = await LoginAsync(user);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.GetAsync($"{Url}/{profile.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.PostAsJsonAsync(Url, new CreateRegimeProfileRequest(NewCode(), "Yetkisiz"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.PutAsJsonAsync($"{Url}/{profile.Id}/draft", Content(enforcement: "Block"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.PostAsJsonAsync($"{Url}/{profile.Id}/publish", new PublishRegimeProfileRequest(false, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.DeleteAsync($"{Url}/{profile.Id}/draft")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.PutAsJsonAsync($"{Url}/{profile.Id}/active", new SetActiveRequest(false))).StatusCode);
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url)).StatusCode);

        var detail = (await platform.GetFromJsonAsync<RegimeProfileDetailDto>($"{Url}/{profile.Id}"))!;
        Assert.Equal(("Warn", true, true), (detail.Versions.Single().Content.RegistrationNo!.Enforcement, detail.Summary.HasDraft, detail.Summary.IsActive));
    }

    private static string NewCode() => $"t-{Guid.NewGuid():N}"[..14];

    private static async Task<RegimeProfileSummaryDto> CreateAsync(HttpClient platform)
    {
        var response = await platform.PostAsJsonAsync(Url, new CreateRegimeProfileRequest(NewCode(), "Test profili"));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RegimeProfileSummaryDto>())!;
    }

    private static async Task<RegimeProfileVersionDto> SaveDraftAsync(HttpClient platform, Guid id, RegimeProfileContentDto content)
    {
        var response = await platform.PutAsJsonAsync($"{Url}/{id}/draft", content);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RegimeProfileVersionDto>())!;
    }

    private static async Task<RegimeProfileVersionDto> PublishAsync(HttpClient platform, Guid id, bool verified, string? note)
    {
        var response = await platform.PostAsJsonAsync($"{Url}/{id}/publish", new PublishRegimeProfileRequest(verified, note));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RegimeProfileVersionDto>())!;
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
