using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using static Modulix.Modules.Hr.HrModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0042 dilim 6: duyurular. Mutlu yol (yazma, yayınlama, sabitlenmişin üstte olması, okundu ve okuyan sayısı, okuyanlar listesi), doğrulama
/// (zorunlu alanlar, bitiş başlangıçtan önce, okundu kaydı doğrudan yazılamaz ve değişmez), hedefleme (taslak, ileri tarihli, süresi geçmiş ve başka
/// departmana hedeflenmiş duyuru okura görünmez ve okundu işaretlenemez), tenant izolasyonu ve yetki (okur yazamaz, okuyanları göremez).
/// </summary>
public class HrAnnouncementTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Employees = "/api/records/hr.employee";
    private const string People = "/api/records/core.person";
    private const string Announcements = "/api/records/hr.announcement";
    private const string Reads = "/api/records/hr.announcement-read";

    [Fact]
    public async Task Published_announcements_are_read_pinned_first_and_read_counts_are_tracked()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);

        var older = await PostAsync(hr, Announcements, Announcement("Yemekhane", "Menü değişti.\nAfiyet olsun.", published: true, publishOn: "2026-01-05"));
        var pinned = await PostAsync(hr, Announcements, Announcement("Bayram tatili", "Ofis kapalı.", published: true, publishOn: "2026-01-01", pinned: true));
        Assert.Equal((s.Hr.Id.ToString(), 0, false), (Text(pinned, "authorUserId"), Number(pinned, "readCount"), Flag(pinned, "readByMe")));

        // Sabitlenmiş duyuru üstte; ardından yayın günü yeni olan.
        var feed = await ListAsync(staff, Announcements, "active=true");
        Assert.Equal([pinned.Id, older.Id], feed.Select(a => a.Id));
        Assert.All(feed, a => Assert.False(Flag(a, "readByMe")));

        // Okundu işaretlenir; ikinci kez yeni kayıt yazmaz.
        var read = await MarkReadAsync(staff, pinned.Id);
        Assert.Equal((true, 1), (Flag(read, "readByMe"), Number(read, "readCount")));
        Assert.Equal(1, Number(await MarkReadAsync(staff, pinned.Id), "readCount"));
        Assert.Equal(2, Number(await MarkReadAsync(hr, pinned.Id), "readCount"));

        // İK kimin okuduğunu görür.
        var readers = await ListAsync(hr, Reads, $"announcementId={pinned.Id}");
        Assert.Equal(new[] { s.Hr.Id.ToString(), s.Staff.Id.ToString() }.Order(), readers.Select(r => Text(r, "userId")).Order());

        // Taslak varsayılan olarak bugünden yayın günü alır; okura görünmez.
        var draft = await PostAsync(hr, Announcements, new JsonObject { ["title"] = "Taslak", ["body"] = "Henüz değil." });
        Assert.Equal((DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), false), (Text(draft, "publishOn"), Flag(draft, "isPublished")));
        Assert.Contains(draft.Id, (await ListAsync(hr, Announcements, "isPublished=false")).Select(a => a.Id));
    }

    [Fact]
    public async Task Invalid_announcements_and_direct_read_writes_are_rejected()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);

        var missing = await ValidationProblemAsync(await hr.PostAsJsonAsync(Announcements, new RecordWriteRequest(new JsonObject { ["title"] = " " })));
        Assert.True(missing.Errors.ContainsKey("title"));
        Assert.True(missing.Errors.ContainsKey("body"));

        var backwards = Announcement("Ters", "Metin", published: true, publishOn: "2026-02-10");
        backwards["expiresOn"] = "2026-02-01";
        Assert.True((await ValidationProblemAsync(await hr.PostAsJsonAsync(Announcements, new RecordWriteRequest(backwards)))).Errors.ContainsKey("expiresOn"));

        var unknownDepartment = Department(Announcement("Bilinmeyen", "Metin", published: true, publishOn: "2026-02-10"), Guid.NewGuid());
        Assert.True((await ValidationProblemAsync(await hr.PostAsJsonAsync(Announcements, new RecordWriteRequest(unknownDepartment)))).Errors
            .ContainsKey("audienceBusinessUnitId"));

        // Kapalı departman yeni hedef olarak seçilemez.
        var closed = await HrDepartments.CreateAsync(hr, "Kapanan birim");
        await HrDepartments.DeactivateAsync(hr, closed);
        var closedTarget = Department(Announcement("Kapalı", "Metin", published: true, publishOn: "2026-02-10"), closed);
        Assert.True((await ValidationProblemAsync(await hr.PostAsJsonAsync(Announcements, new RecordWriteRequest(closedTarget)))).Errors
            .ContainsKey("audienceBusinessUnitId"));

        // Okundu kaydı yalnızca duyuru açılınca yazılır ve değişmez.
        var announcement = await PostAsync(hr, Announcements, Announcement("Duyuru", "Metin", published: true, publishOn: "2026-01-01"));
        var direct = await hr.PostAsJsonAsync(Reads, new RecordWriteRequest(new JsonObject { ["announcementId"] = announcement.Id.ToString() }));
        Assert.Equal(HttpStatusCode.Conflict, direct.StatusCode);
        await MarkReadAsync(hr, announcement.Id);
        var row = Assert.Single(await ListAsync(hr, Reads, $"announcementId={announcement.Id}"));
        Assert.Equal(HttpStatusCode.Conflict, (await hr.PutAsJsonAsync($"{Reads}/{row.Id}",
            new RecordWriteRequest(new JsonObject { ["announcementId"] = announcement.Id.ToString() }, row.Version))).StatusCode);

        // Duyuru silinince okundu kayıtları da silinir.
        var current = await GetAsync(hr, $"{Announcements}/{announcement.Id}");
        Assert.Equal(HttpStatusCode.NoContent, (await hr.DeleteAsync($"{Announcements}/{announcement.Id}?version={current.Version}")).StatusCode);
        Assert.Empty(await ListAsync(hr, Reads, $"announcementId={announcement.Id}"));
    }

    [Fact]
    public async Task Readers_only_see_published_current_announcements_targeted_at_them()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);

        var all = await PostAsync(hr, Announcements, Announcement("Herkese", "Metin", published: true, publishOn: "2026-01-01"));
        var mine = await PostAsync(hr, Announcements, Department(Announcement("Merkez", "Metin", published: true, publishOn: "2026-01-02"), s.Center));
        var other = await PostAsync(hr, Announcements, Department(Announcement("Yönetim", "Metin", published: true, publishOn: "2026-01-03"), s.Management));
        var draft = await PostAsync(hr, Announcements, Announcement("Taslak", "Metin", published: false, publishOn: "2026-01-01"));
        var future = await PostAsync(hr, Announcements, Announcement("İleride", "Metin", published: true, publishOn: "2099-01-01"));
        var expiredValues = Announcement("Eski", "Metin", published: true, publishOn: "2025-01-01");
        expiredValues["expiresOn"] = "2025-01-31";
        var expired = await PostAsync(hr, Announcements, expiredValues);

        // Okur, sorgu süzgeci vermeden de yalnızca kendisine yönelik yayındaki duyuruları görür.
        Assert.Equal([mine.Id, all.Id], (await ListAsync(staff, Announcements, null)).Select(a => a.Id));
        foreach (var hidden in new[] { other, draft, future, expired })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Announcements}/{hidden.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync($"/api/hr/announcements/{hidden.Id}/read", new { })).StatusCode);
        }

        // Yönetici bütün duyuruları ve görünümleri görür.
        Assert.Equal(6, (await ListAsync(hr, Announcements, null)).Count);
        Assert.Equal([expired.Id], (await ListAsync(hr, Announcements, "expired=true")).Select(a => a.Id));
        Assert.Equal([other.Id, mine.Id, all.Id], (await ListAsync(hr, Announcements, "active=true")).Select(a => a.Id));
    }

    [Fact]
    public async Task Readers_cannot_write_or_see_readers_and_tenants_are_isolated()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var hrA = await LoginAsync(a.Hr);
        using var hrB = await LoginAsync(b.Hr);
        using var staff = await LoginAsync(a.Staff);
        using var outsider = await LoginAsync(a.Outsider);

        var announcement = await PostAsync(hrA, Announcements, Announcement("A duyurusu", "Metin", published: true, publishOn: "2026-01-01"));
        await MarkReadAsync(staff, announcement.Id);

        // Okur yazamaz, değiştiremez, silemez ve okuyanları göremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync(Announcements,
            new RecordWriteRequest(Announcement("Benim", "Metin", published: true, publishOn: "2026-01-01")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PutAsJsonAsync($"{Announcements}/{announcement.Id}",
            new RecordWriteRequest(new JsonObject { ["title"] = "Değişti" }, announcement.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.DeleteAsync($"{Announcements}/{announcement.Id}?version={announcement.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync(Reads)).StatusCode);

        // İzinsiz kullanıcı duyuruları göremez ve okundu işaretleyemez.
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Announcements)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync($"/api/hr/announcements/{announcement.Id}/read", new { })).StatusCode);

        // B tenant'ı A'nın duyurusunu ve okundu kayıtlarını görmez.
        Assert.Empty(await ListAsync(hrB, Announcements, null));
        Assert.Empty(await ListAsync(hrB, Reads, null));
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"{Announcements}/{announcement.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.PostAsJsonAsync($"/api/hr/announcements/{announcement.Id}/read", new { })).StatusCode);
        Assert.Equal(1, Number(await GetAsync(hrA, $"{Announcements}/{announcement.Id}"), "readCount"));
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, ModulixUser Hr, ModulixUser Staff, ModulixUser Outsider, Guid Center, Guid Finance, Guid Management);

    /// <summary>
    /// İK (yönetici rolü), Core'da Merkez › Finans departmanındaki çalışan (yalnızca duyuru okuma izni) ve izinsiz kullanıcı. Üst departmana (Merkez)
    /// hedeflenen duyuru alt departmandakine de görünür; Yönetim departmanına hedeflenen görünmez.
    /// </summary>
    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "hr", enabled: true);
        var hrUser = await factory.CreateUserAsync(tenant);
        var staffUser = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, hrUser);
        await factory.CreateRoleAsync(tenant.Id, [AnnouncementsRead], Scope.Own, staffUser);

        using var hr = await LoginAsync(hrUser);
        var center = await HrDepartments.CreateAsync(hr, "Merkez ofis");
        var finance = await HrDepartments.CreateAsync(hr, "Finans", center);
        var management = await HrDepartments.CreateAsync(hr, "Yönetim");
        await HrDepartments.MoveAsync(hr, factory, staffUser.Id, tenant.Id, finance);
        var person = await PostAsync(hr, People, new JsonObject { ["firstName"] = "Sinan", ["lastName"] = "Okur" });
        await PostAsync(hr, Employees, new JsonObject
        {
            ["personId"] = person.Id.ToString(), ["hireDate"] = "2020-01-01", ["userId"] = staffUser.Id.ToString(),
        });
        return new Setup(tenant.Id, hrUser, staffUser, outsider, center, finance, management);
    }

    private static JsonObject Announcement(string title, string body, bool published, string publishOn, bool pinned = false) => new()
    {
        ["title"] = title, ["body"] = body, ["isPublished"] = published, ["publishOn"] = publishOn, ["isPinned"] = pinned,
    };

    private static JsonObject Department(JsonObject values, Guid department)
    {
        values["audienceBusinessUnitId"] = department.ToString();
        return values;
    }

    private static async Task<RecordResponse> MarkReadAsync(HttpClient client, Guid id)
    {
        var response = await client.PostAsJsonAsync($"/api/hr/announcements/{id}/read", new { });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static string? Text(RecordResponse record, string field) => record.Values[field]?.GetValue<string>();

    private static int Number(RecordResponse record, string field) => record.Values[field]!.GetValue<int>();

    private static bool Flag(RecordResponse record, string field) => record.Values[field]!.GetValue<bool>();

    private async Task MakeAdministratorAsync(Guid tenantId, ModulixUser user)
    {
        using var scope = factory.OpenScope(tenantId);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
        Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(user.Id, tenantId), [system.Id], CancellationToken.None));
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<RecordResponse> GetAsync(HttpClient client, string url) => (await client.GetFromJsonAsync<RecordResponse>(url))!;

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string url, string? query) =>
        (await client.GetFromJsonAsync<RecordPageResponse>(query is null ? url : $"{url}?{query}"))!.Items;

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
