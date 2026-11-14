using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Files;
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

    /// <summary>
    /// Bölüm 23a: duyuru ekleri (ADR-0040 dosyaları) ve ana sayfa kutucuğunun okunmamış sayısı. Ek, duyuruyu görebilen okura açılır; başka departmana
    /// hedeflenmiş duyurunun eki okura yoktur; okur ek yükleyemez; başka tenant göremez. Okunmamış süzgeci kullanıcı başınadır.
    /// </summary>
    [Fact]
    public async Task Attachments_follow_announcement_visibility_and_unread_counts_are_per_user()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var hr = await LoginAsync(a.Hr);
        using var staff = await LoginAsync(a.Staff);
        using var outsider = await LoginAsync(a.Outsider);
        using var hrB = await LoginAsync(b.Hr);

        var mine = await PostAsync(hr, Announcements, Department(Announcement("Finans", "Metin", published: true, publishOn: "2026-01-02"), a.Finance));
        var other = await PostAsync(hr, Announcements, Department(Announcement("Yönetim", "Metin", published: true, publishOn: "2026-01-03"), a.Management));
        await PostAsync(hr, Announcements, Announcement("Herkese", "Metin", published: true, publishOn: "2026-01-01"));
        var plan = await UploadAsync(hr, mine.Id, "plan.pdf", Pdf);
        var hidden = await UploadAsync(hr, other.Id, "gizli.pdf", Pdf);

        // Okur kendisine yönelik duyurunun ekini listeler ve indirir; başka departmanınkini göremez.
        Assert.Single((await staff.GetFromJsonAsync<List<StoredFileDto>>($"{Files}?entityKey=hr.announcement&recordId={mine.Id}"))!);
        Assert.Equal(Pdf, await staff.GetByteArrayAsync($"{Files}/{plan.Id}/content"));
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Files}?entityKey=hr.announcement&recordId={other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Files}/{hidden.Id}/content")).StatusCode);

        // Okur ek yükleyemez ve silemez; izinsiz kullanıcı ve başka tenant eke ulaşamaz.
        using (var form = Form(mine.Id, "benim.pdf", Pdf))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsync(Files, form)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await staff.DeleteAsync($"{Files}/{plan.Id}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await outsider.GetAsync($"{Files}/{plan.Id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"{Files}/{plan.Id}/content")).StatusCode);

        // Okunmamış süzgeci: okur iki duyuru görür, birini okuyunca bir kalır; yöneticinin okuması okurunkini etkilemez.
        Assert.Equal(2, (await ListAsync(staff, Announcements, "active=true&unread=true")).Count);
        await MarkReadAsync(staff, mine.Id);
        Assert.Equal(1, (await ListAsync(staff, Announcements, "active=true&unread=true")).Count);
        Assert.Equal(3, (await ListAsync(hr, Announcements, "active=true&unread=true")).Count);
        Assert.Empty(await ListAsync(hrB, Announcements, "unread=true"));
    }

    /// <summary>
    /// Bölüm 23c: birden çok departmana ve kişiye hedefleme. Okur hedeflerden birine uyuyorsa görür (departmanı, üst departmanı ya da kendisi); yalnızca
    /// kişi hedefi güncellenince departman hedefleri kalır; başka tenant'ın kullanıcısı, kapalı departman, tekrarlı ve biçimsiz liste reddedilir.
    /// </summary>
    [Fact]
    public async Task Announcements_can_target_several_departments_and_people()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var hr = await LoginAsync(a.Hr);
        using var staff = await LoginAsync(a.Staff);

        var both = await PostAsync(hr, Announcements, Targets(Announcement("İki departman", "Metin", published: true, publishOn: "2026-01-01"),
            [a.Finance, a.Management], []));
        var person = await PostAsync(hr, Announcements, Targets(Announcement("Yönetim ve Sinan", "Metin", published: true, publishOn: "2026-01-02"),
            [a.Management], [a.Staff.Id]));
        var others = await PostAsync(hr, Announcements, Targets(Announcement("Yalnız yönetim", "Metin", published: true, publishOn: "2026-01-03"),
            [a.Management], [a.Hr.Id]));

        Assert.Equal(new[] { a.Finance, a.Management }.Order(), Ids(both, "audienceBusinessUnitIds").Order());
        Assert.Null(Text(both, "audienceBusinessUnitId"));
        Assert.EndsWith("· 1 kişi", Text(person, "audienceName"));
        Assert.Equal([person.Id, both.Id], (await ListAsync(staff, Announcements, null)).Select(x => x.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Announcements}/{others.Id}")).StatusCode);

        // Yalnızca kişi hedefi değişince departman hedefleri kalır; kişi kaldırılınca okur artık göremez.
        var updated = await PutAsync(hr, $"{Announcements}/{person.Id}", new JsonObject { ["audienceUserIds"] = new JsonArray() });
        Assert.Equal([a.Management], Ids(updated, "audienceBusinessUnitIds"));
        Assert.Empty(Ids(updated, "audienceUserIds"));
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Announcements}/{person.Id}")).StatusCode);

        // Doğrulama: başka tenant'ın kullanıcısı, kapalı departman, tekrar ve dizi olmayan değer.
        var closed = await HrDepartments.CreateAsync(hr, "Kapanacak");
        await HrDepartments.DeactivateAsync(hr, closed);
        foreach (var (values, field) in new (JsonObject, string)[]
        {
            (Targets(Announcement("x", "y", true, "2026-01-01"), [], [b.Staff.Id]), "audienceUserIds"),
            (Targets(Announcement("x", "y", true, "2026-01-01"), [closed], []), "audienceBusinessUnitIds"),
            (Targets(Announcement("x", "y", true, "2026-01-01"), [a.Finance, a.Finance], []), "audienceBusinessUnitIds"),
            (new JsonObject { ["title"] = "x", ["body"] = "y", ["audienceUserIds"] = a.Staff.Id.ToString() }, "audienceUserIds"),
        })
        {
            var rejected = await hr.PostAsJsonAsync(Announcements, new RecordWriteRequest(values));
            Assert.True((await ValidationProblemAsync(rejected)).Errors.ContainsKey(field), field);
        }

        // Okur hedef değiştiremez; B tenant'ı A'nın duyurusunu görmez.
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PutAsJsonAsync($"{Announcements}/{both.Id}",
            new RecordWriteRequest(new JsonObject { ["audienceUserIds"] = new JsonArray() }, both.Version))).StatusCode);
        using var hrB = await LoginAsync(b.Hr);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"{Announcements}/{both.Id}")).StatusCode);
    }

    // --- yardımcılar ---

    private static JsonObject Targets(JsonObject values, Guid[] units, Guid[] users)
    {
        values["audienceBusinessUnitIds"] = new JsonArray([.. units.Select(u => (JsonNode?)JsonValue.Create(u.ToString()))]);
        values["audienceUserIds"] = new JsonArray([.. users.Select(u => (JsonNode?)JsonValue.Create(u.ToString()))]);
        return values;
    }

    private static async Task<RecordResponse> PutAsync(HttpClient client, string url, JsonObject values)
    {
        var current = (await client.GetFromJsonAsync<RecordResponse>(url))!;
        var response = await client.PutAsJsonAsync(url, new RecordWriteRequest(values, current.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static IReadOnlyList<Guid> Ids(RecordResponse record, string field) =>
        [.. record.Values[field]!.AsArray().Select(n => Guid.Parse(n!.GetValue<string>()))];

    private const string Files = "/api/files";

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n");

    private static MultipartFormDataContent Form(Guid announcementId, string fileName, byte[] content)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent
        {
            { new StringContent("hr.announcement"), "entityKey" },
            { new StringContent(announcementId.ToString()), "recordId" },
            { file, "file", fileName },
        };
    }

    private static async Task<StoredFileDto> UploadAsync(HttpClient client, Guid announcementId, string fileName, byte[] content)
    {
        using var form = Form(announcementId, fileName, content);
        var response = await client.PostAsync(Files, form);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<StoredFileDto>())!;
    }


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
