using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Hr;
using Modulix.Shared.Records;
using static Modulix.Modules.Hr.HrModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0042 bölüm 23d: duyuru ve blog yazısında yorum ve beğeni. Mutlu yol (yorum, beğeni aç/kapa, sayılar, gizleme ve gösterme, yazarın kendi
/// yorumunu silmesi), doğrulama (boş/uzun yorum, bilinmeyen eylem, genel uçtan yazma ve silme engeli), görünürlük (görülmeyen içeriğe yorum ve
/// beğeni yok, yorumları da görünmez), tenant izolasyonu ve yetki (okur gizleyemez, başkasının yorumunu silemez, izinsiz kullanıcı).
/// </summary>
public class HrContentDiscussionTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Announcements = "/api/records/hr.announcement";
    private const string Posts = "/api/records/hr.blog-post";
    private const string AnnouncementComments = "/api/records/hr.announcement-comment";
    private const string BlogComments = "/api/records/hr.blog-comment";

    [Fact]
    public async Task Readers_comment_and_like_and_managers_moderate()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);
        using var colleague = await LoginAsync(s.Colleague);

        var announcement = await PostAsync(hr, Announcements, new JsonObject
        {
            ["title"] = "Yaz şenliği", ["body"] = "Cuma günü.", ["isPublished"] = true, ["publishOn"] = "2026-01-01",
        });
        var mine = await CommentAsync(staff, "announcements", announcement.Id, "Harika!");
        var theirs = await CommentAsync(colleague, "announcements", announcement.Id, "Katılacağım.");
        Assert.Equal((s.Staff.Id.ToString(), "Harika!"), (Text(mine, "authorUserId"), Text(mine, "body")));

        var liked = await LikeAsync(staff, "announcements", announcement.Id);
        Assert.Equal((1, true), (Number(liked, "likeCount"), Flag(liked, "likedByMe")));
        await LikeAsync(colleague, "announcements", announcement.Id);
        var unliked = await LikeAsync(staff, "announcements", announcement.Id);
        Assert.Equal((1, false), (Number(unliked, "likeCount"), Flag(unliked, "likedByMe")));
        Assert.Equal(2, Number(await GetAsync(staff, $"{Announcements}/{announcement.Id}"), "commentCount"));

        // Yönetici gizler: okur metni görmez, sayı düşer; yönetici metni görür; yeniden gösterilir.
        var hidden = await ActAsync(hr, "announcement-comments", theirs, "hide");
        Assert.True(Flag(hidden!, "isHidden"));
        Assert.Equal(s.Hr.Id.ToString(), Text(hidden!, "hiddenByUserId"));
        var seen = await ListAsync(staff, AnnouncementComments, $"contentId={announcement.Id}");
        Assert.Null(Text(Assert.Single(seen, c => c.Id == theirs.Id), "body"));
        Assert.Equal("Katılacağım.", Text(Assert.Single(await ListAsync(hr, AnnouncementComments, $"contentId={announcement.Id}"), c => c.Id == theirs.Id), "body"));
        Assert.Equal(1, Number(await GetAsync(staff, $"{Announcements}/{announcement.Id}"), "commentCount"));
        await ActAsync(hr, "announcement-comments", (await GetAsync(hr, $"{AnnouncementComments}/{theirs.Id}")), "show");

        // Yazar kendi yorumunu siler.
        Assert.Null(await ActAsync(staff, "announcement-comments", mine, "delete"));
        Assert.Equal([theirs.Id], (await ListAsync(staff, AnnouncementComments, $"contentId={announcement.Id}")).Select(c => c.Id));

        // Blog yazısında da aynı.
        var post = await PostAsync(hr, Posts, new JsonObject { ["title"] = "Yeni ofis", ["body"] = "Taşınıyoruz.", ["isPublished"] = true });
        await CommentAsync(staff, "blog-posts", post.Id, "Nerede?");
        Assert.Equal(1, Number(await LikeAsync(staff, "blog-posts", post.Id), "likeCount"));
        Assert.Single(await ListAsync(colleague, BlogComments, $"contentId={post.Id}"));
    }

    [Fact]
    public async Task Invalid_comments_and_direct_writes_are_rejected()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);
        var announcement = await PostAsync(hr, Announcements, new JsonObject
        {
            ["title"] = "Duyuru", ["body"] = "Metin", ["isPublished"] = true, ["publishOn"] = "2026-01-01",
        });

        foreach (var body in new[] { "   ", new string('a', 2001) })
        {
            var rejected = await staff.PostAsJsonAsync($"/api/hr/announcements/{announcement.Id}/comments", new WriteCommentRequest(body));
            Assert.True((await ValidationProblemAsync(rejected)).Errors.ContainsKey("body"));
        }

        var comment = await CommentAsync(staff, "announcements", announcement.Id, "Tamam");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostActionAsync(hr, "announcement-comments", comment, "pin")).StatusCode);

        // Genel kayıt uçlarından yorum ve beğeni yazılamaz, değiştirilemez, silinemez.
        Assert.Equal(HttpStatusCode.Conflict, (await hr.PostAsJsonAsync(AnnouncementComments,
            new RecordWriteRequest(new JsonObject { ["contentId"] = announcement.Id.ToString(), ["body"] = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await hr.PutAsJsonAsync($"{AnnouncementComments}/{comment.Id}",
            new RecordWriteRequest(new JsonObject { ["isHidden"] = true }, comment.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await hr.DeleteAsync($"{AnnouncementComments}/{comment.Id}?version={comment.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await hr.PostAsJsonAsync("/api/records/hr.announcement-like",
            new RecordWriteRequest(new JsonObject { ["contentId"] = announcement.Id.ToString() }))).StatusCode);
    }

    [Fact]
    public async Task Hidden_content_other_tenants_and_unauthorized_users_cannot_take_part()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var hr = await LoginAsync(a.Hr);
        using var hrB = await LoginAsync(b.Hr);
        using var staff = await LoginAsync(a.Staff);
        using var colleague = await LoginAsync(a.Colleague);
        using var outsider = await LoginAsync(a.Outsider);

        var draft = await PostAsync(hr, Announcements, new JsonObject { ["title"] = "Taslak", ["body"] = "Metin", ["isPublished"] = false });
        var draftComment = await CommentAsync(hr, "announcements", draft.Id, "Yönetici notu");
        var post = await PostAsync(hr, Posts, new JsonObject { ["title"] = "Taslak yazı", ["body"] = "Metin", ["isPublished"] = false });
        var published = await PostAsync(hr, Announcements, new JsonObject
        {
            ["title"] = "Yayında", ["body"] = "Metin", ["isPublished"] = true, ["publishOn"] = "2026-01-01",
        });
        var comment = await CommentAsync(colleague, "announcements", published.Id, "Benim yorumum");

        // Görülmeyen içerik: yorum ve beğeni 404, yorumları listede yok.
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync($"/api/hr/announcements/{draft.Id}/comments", new WriteCommentRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync($"/api/hr/announcements/{draft.Id}/like", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync($"/api/hr/blog-posts/{post.Id}/comments", new WriteCommentRequest("x"))).StatusCode);
        Assert.DoesNotContain(await ListAsync(staff, AnnouncementComments, null), c => c.Id == draftComment.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{AnnouncementComments}/{draftComment.Id}")).StatusCode);

        // Yetki: okur gizleyemez ve başkasının yorumunu silemez; izinsiz kullanıcı yorum yazamaz ve listeleyemez.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(staff, "announcement-comments", comment, "hide")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(staff, "announcement-comments", comment, "delete")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync($"/api/hr/announcements/{published.Id}/comments", new WriteCommentRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(AnnouncementComments)).StatusCode);

        // B tenant'ı A'nın içeriğine yorum yazamaz, beğenemez, yorumlarını göremez ve yönetemez.
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.PostAsJsonAsync($"/api/hr/announcements/{published.Id}/comments", new WriteCommentRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.PostAsJsonAsync($"/api/hr/announcements/{published.Id}/like", new { })).StatusCode);
        Assert.Empty(await ListAsync(hrB, AnnouncementComments, null));
        Assert.Equal(HttpStatusCode.NotFound, (await PostActionAsync(hrB, "announcement-comments", comment, "delete")).StatusCode);
        Assert.Equal(1, Number(await GetAsync(hr, $"{Announcements}/{published.Id}"), "commentCount"));
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, ModulixUser Hr, ModulixUser Staff, ModulixUser Colleague, ModulixUser Outsider);

    /// <summary>İK (yönetici rolü), iki okur (duyuru ve blog okuma) ve izinsiz kullanıcı.</summary>
    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "hr", enabled: true);
        var hr = await factory.CreateUserAsync(tenant);
        var staff = await factory.CreateUserAsync(tenant);
        var colleague = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, hr);
        await factory.CreateRoleAsync(tenant.Id, [AnnouncementsRead, BlogRead], Scope.Own, staff, colleague);
        return new Setup(tenant.Id, hr, staff, colleague, outsider);
    }

    private static async Task<RecordResponse> CommentAsync(HttpClient client, string route, Guid contentId, string body)
    {
        var response = await client.PostAsJsonAsync($"/api/hr/{route}/{contentId}/comments", new WriteCommentRequest(body));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<RecordResponse> LikeAsync(HttpClient client, string route, Guid contentId)
    {
        var response = await client.PostAsJsonAsync($"/api/hr/{route}/{contentId}/like", new { });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static Task<HttpResponseMessage> PostActionAsync(HttpClient client, string route, RecordResponse comment, string action) =>
        client.PostAsJsonAsync($"/api/hr/{route}/{comment.Id}/actions", new RunHrActionRequest(action, comment.Version, null));

    private static async Task<RecordResponse?> ActAsync(HttpClient client, string route, RecordResponse comment, string action)
    {
        var response = await PostActionAsync(client, route, comment, action);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<RecordResponse>();
    }

    private static async Task<RecordResponse> GetAsync(HttpClient client, string url) => (await client.GetFromJsonAsync<RecordResponse>(url))!;

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string url, string? query) =>
        (await client.GetFromJsonAsync<RecordPageResponse>(query is null ? $"{url}?pageSize=100" : $"{url}?{query}&pageSize=100"))!.Items;

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static string? Text(RecordResponse record, string field) => record.Values[field]?.GetValue<string>();

    private static int Number(RecordResponse record, string field) => record.Values[field]!.GetValue<int>();

    private static bool Flag(RecordResponse record, string field) => record.Values[field]!.GetValue<bool>();

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
