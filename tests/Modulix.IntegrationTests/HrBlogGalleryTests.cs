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
/// ADR-0042 dilim 8: blog ve galeri. Mutlu yol (taslak, yayın, yayın anı, ekler ve fotoğraflar ADR-0040 dosyalarıyla), doğrulama (zorunlu alanlar,
/// uzunluk, tarih), görünürlük (taslak yazı/albüm ve dosyaları okura 404), yetki (okur yazamaz, dosya ekleyemez ve silemez; izinsiz kullanıcı) ve
/// tenant izolasyonu.
/// </summary>
public class HrBlogGalleryTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Posts = "/api/records/hr.blog-post";
    private const string Albums = "/api/records/hr.photo-album";
    private const string Files = "/api/files";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n");

    [Fact]
    public async Task Posts_are_published_to_readers_with_their_attachments()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);

        var draft = await PostAsync(hr, Posts, new JsonObject { ["title"] = "Yeni ofis", ["summary"] = "Taşınıyoruz.", ["body"] = "Ayrıntılar.\nİkinci satır." });
        Assert.Equal((false, (string?)null, s.Hr.Id.ToString()), (Flag(draft, "isPublished"), Text(draft, "publishedAt"), Text(draft, "authorUserId")));
        var attachment = await UploadAsync(hr, "hr.blog-post", draft.Id, "plan.pdf", Pdf);

        // Taslak ve dosyası okura yoktur.
        Assert.Empty(await ListAsync(staff, Posts, null));
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Posts}/{draft.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Files}?entityKey=hr.blog-post&recordId={draft.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Files}/{attachment.Id}/content")).StatusCode);

        // Yayınlanınca görünür; yayın anı ilk yayında yazılır ve taslağa alınıp yeniden yayınlansa da değişmez.
        var published = await PutAsync(hr, $"{Posts}/{draft.Id}", new JsonObject { ["isPublished"] = true });
        var publishedAt = Text(published, "publishedAt");
        Assert.NotNull(publishedAt);
        await PutAsync(hr, $"{Posts}/{draft.Id}", new JsonObject { ["isPublished"] = false });
        Assert.Equal(publishedAt, Text(await PutAsync(hr, $"{Posts}/{draft.Id}", new JsonObject { ["isPublished"] = true }), "publishedAt"));

        var visible = Assert.Single(await ListAsync(staff, Posts, null));
        Assert.Equal("Ayrıntılar.\nİkinci satır.", Text(visible, "body"));
        Assert.Single((await staff.GetFromJsonAsync<List<StoredFileDto>>($"{Files}?entityKey=hr.blog-post&recordId={draft.Id}"))!);
        Assert.Equal(Pdf, await staff.GetByteArrayAsync($"{Files}/{attachment.Id}/content"));

        // Okur ek ekleyemez, silemez; yazıyı değiştiremez.
        using (var form = Form("hr.blog-post", draft.Id, "benim.pdf", [.. Pdf, 0x20]))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsync(Files, form)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await staff.DeleteAsync($"{Files}/{attachment.Id}?version={attachment.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PutAsJsonAsync($"{Posts}/{draft.Id}",
            new RecordWriteRequest(new JsonObject { ["title"] = "Değişti" }, visible.Version))).StatusCode);
    }

    [Fact]
    public async Task Albums_show_photos_only_when_published()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);

        var album = await PostAsync(hr, Albums, new JsonObject { ["title"] = "Yılbaşı", ["eventDate"] = "2026-12-31" });
        var photo = await UploadAsync(hr, "hr.photo-album", album.Id, "kutlama.png", Png);
        Assert.Equal("image/png", photo.ContentType);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Files}?entityKey=hr.photo-album&recordId={album.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Files}/{photo.Id}/content")).StatusCode);

        var published = await PutAsync(hr, $"{Albums}/{album.Id}", new JsonObject { ["isPublished"] = true });
        Assert.NotNull(Text(published, "publishedAt"));
        var older = await PostAsync(hr, Albums, new JsonObject { ["title"] = "Piknik", ["eventDate"] = "2026-06-01", ["isPublished"] = true });
        Assert.Equal([album.Id, older.Id], (await ListAsync(staff, Albums, "isPublished=true")).Select(a => a.Id));
        Assert.Equal(Png, await staff.GetByteArrayAsync($"{Files}/{photo.Id}/content"));

        using (var form = Form("hr.photo-album", album.Id, "benim.png", [.. Png, 0x01]))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsync(Files, form)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await staff.DeleteAsync($"{Files}/{photo.Id}?version={photo.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync(Albums, new RecordWriteRequest(new JsonObject { ["title"] = "Benim" }))).StatusCode);
    }

    [Fact]
    public async Task Invalid_posts_and_albums_are_rejected()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);

        var missing = await ValidationProblemAsync(await hr.PostAsJsonAsync(Posts, new RecordWriteRequest(new JsonObject { ["summary"] = "Yalnızca özet" })));
        Assert.True(missing.Errors.ContainsKey("title"));
        Assert.True(missing.Errors.ContainsKey("body"));
        var tooLong = await ValidationProblemAsync(await hr.PostAsJsonAsync(Posts,
            new RecordWriteRequest(new JsonObject { ["title"] = "Uzun", ["body"] = new string('a', 20_001) })));
        Assert.True(tooLong.Errors.ContainsKey("body"));

        var album = await ValidationProblemAsync(await hr.PostAsJsonAsync(Albums,
            new RecordWriteRequest(new JsonObject { ["eventDate"] = "31.12.2026", ["isPublished"] = "evet" })));
        Assert.True(album.Errors.ContainsKey("title"));
        Assert.True(album.Errors.ContainsKey("eventDate"));
        Assert.True(album.Errors.ContainsKey("isPublished"));
    }

    [Fact]
    public async Task Tenants_and_unauthorised_users_cannot_reach_posts_albums_or_their_files()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var hrA = await LoginAsync(a.Hr);
        using var hrB = await LoginAsync(b.Hr);
        using var outsider = await LoginAsync(a.Outsider);

        var post = await PostAsync(hrA, Posts, new JsonObject { ["title"] = "A yazısı", ["body"] = "Metin", ["isPublished"] = true });
        var album = await PostAsync(hrA, Albums, new JsonObject { ["title"] = "A albümü", ["isPublished"] = true });
        var photo = await UploadAsync(hrA, "hr.photo-album", album.Id, "a.png", Png);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Posts)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Albums)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await outsider.GetAsync($"{Files}/{photo.Id}/content")).StatusCode);

        Assert.Empty(await ListAsync(hrB, Posts, null));
        Assert.Empty(await ListAsync(hrB, Albums, null));
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"{Posts}/{post.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"{Albums}/{album.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"{Files}/{photo.Id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.DeleteAsync($"{Files}/{photo.Id}?version={photo.Version}")).StatusCode);
        Assert.Equal(Png, await hrA.GetByteArrayAsync($"{Files}/{photo.Id}/content"));
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, ModulixUser Hr, ModulixUser Staff, ModulixUser Outsider);

    /// <summary>İK (yönetici rolü), yalnızca blog ve galeri okuma izni olan çalışan ve izinsiz kullanıcı.</summary>
    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "hr", enabled: true);
        var hrUser = await factory.CreateUserAsync(tenant);
        var staffUser = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, hrUser);
        await factory.CreateRoleAsync(tenant.Id, [BlogRead, GalleryRead], Scope.Own, staffUser);
        return new Setup(tenant.Id, hrUser, staffUser, outsider);
    }

    private static MultipartFormDataContent Form(string entityKey, Guid recordId, string fileName, byte[] content)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent
        {
            { new StringContent(entityKey), "entityKey" },
            { new StringContent(recordId.ToString()), "recordId" },
            { file, "file", fileName },
        };
    }

    private static async Task<StoredFileDto> UploadAsync(HttpClient client, string entityKey, Guid recordId, string fileName, byte[] content)
    {
        using var form = Form(entityKey, recordId, fileName, content);
        var response = await client.PostAsync(Files, form);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<StoredFileDto>())!;
    }

    private static string? Text(RecordResponse record, string field) => record.Values[field]?.GetValue<string>();

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

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string url, string? query) =>
        (await client.GetFromJsonAsync<RecordPageResponse>(query is null ? url : $"{url}?{query}"))!.Items;

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<RecordResponse> PutAsync(HttpClient client, string url, JsonObject values)
    {
        var current = (await client.GetFromJsonAsync<RecordResponse>(url))!;
        var response = await client.PutAsJsonAsync(url, new RecordWriteRequest(values, current.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
