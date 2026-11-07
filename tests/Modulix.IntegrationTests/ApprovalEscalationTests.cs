using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Approvals;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Approvals;
using Modulix.Shared.Auth;
using Modulix.Shared.Notifications;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0027 Dilim 4b: bildirim kutusu, süre aşımı eskalasyonu ve onay vekâleti. Mutlu yol, hata, kişi ve tenant izolasyonu, yetki.
/// </summary>
public class ApprovalEscalationTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] Requester = [CoreModule.Permissions.RequestApprovals];
    private static readonly string[] Decider = [CoreModule.Permissions.DecideApprovals];

    private sealed record Team(Tenant Tenant, ModulixUser Admin, ModulixUser Requester, ModulixUser First, ModulixUser Second);

    [Fact]
    public async Task Approver_gets_one_notification_and_only_sees_own_inbox()
    {
        Team team = await SetUpAsync();
        using HttpClient requester = await LoginAsync(team.Requester);
        using HttpClient first = await LoginAsync(team.First);
        using HttpClient second = await LoginAsync(team.Second);
        ApprovalRequestDto opened = await OpenAsync(requester, await CreateDefinitionAsync(team, escalateTo: null));

        NotificationPageDto inbox = await GetAsync<NotificationPageDto>(first, "/api/notifications");
        NotificationDto item = Assert.Single(inbox.Items);
        Assert.Equal(1, inbox.UnreadCount);
        Assert.Equal($"onaylar?talep={opened.Id}", item.Link);
        Assert.DoesNotContain(opened.Title, item.Title); // Metin kaydın ayrıntısını taşımaz; erişim kalkınca sızmaz.

        // Başkasının bildirimi görünmez ve okundu yapılamaz.
        Assert.Empty((await GetAsync<NotificationPageDto>(second, "/api/notifications")).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await second.PostAsJsonAsync($"/api/notifications/{item.Id}/read", new { })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await first.PostAsJsonAsync($"/api/notifications/{item.Id}/read", new { })).StatusCode);
        Assert.Equal(0, (await GetAsync<UnreadNotificationCountDto>(first, "/api/notifications/unread-count")).Count);

        // Sonuç talep sahibine düşer.
        await ReadAsync<ApprovalRequestDto>(await DecideAsync(first, opened, "reject", "Bütçe yok"), HttpStatusCode.OK);
        Assert.Contains((await GetAsync<NotificationPageDto>(requester, "/api/notifications")).Items, n => n.Kind == "approval.result");
    }

    [Fact]
    public async Task Overdue_step_opens_to_the_escalation_approver_once()
    {
        Team team = await SetUpAsync();
        using HttpClient requester = await LoginAsync(team.Requester);
        using HttpClient first = await LoginAsync(team.First);
        using HttpClient second = await LoginAsync(team.Second);
        ApprovalRequestDto opened = await OpenAsync(requester, await CreateDefinitionAsync(team, escalateTo: team.Second.Id));

        // Süre dolmadan eskalasyon onaycısı karar veremez ve talebi göremez.
        Assert.Equal(0, await RunAsync(team));
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(second, opened, "approve")).StatusCode);

        await ExpireAsync(team, opened.Id);
        Assert.Equal(1, await RunAsync(team));
        Assert.Equal(0, await RunAsync(team)); // İkinci tur aynı adımı işlemez.

        NotificationPageDto inbox = await GetAsync<NotificationPageDto>(second, "/api/notifications");
        Assert.Single(inbox.Items, n => n.Kind == "approval.overdue");

        ApprovalRequestDto seen = await GetAsync<ApprovalRequestDto>(second, $"/api/approvals/requests/{opened.Id}");
        Assert.True(seen.CanDecide);
        Assert.True(seen.Steps[0].IsEscalated);
        ApprovalRequestDto done = await ReadAsync<ApprovalRequestDto>(await DecideAsync(second, seen, "approve"), HttpStatusCode.OK);
        Assert.Equal("Approved", done.Status);

        using IServiceScope scope = factory.OpenScope(team.Tenant.Id);
        ModulixDbContext db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.True(await db.AuditLog.AnyAsync(a => a.RecordId == opened.Id && a.Subscription == "core.approval-escalation"));
    }

    [Fact]
    public async Task Overdue_step_without_a_target_only_reminds()
    {
        Team team = await SetUpAsync();
        using HttpClient requester = await LoginAsync(team.Requester);
        using HttpClient first = await LoginAsync(team.First);
        ApprovalRequestDto opened = await OpenAsync(requester, await CreateDefinitionAsync(team, escalateTo: null));

        await ExpireAsync(team, opened.Id);
        Assert.Equal(1, await RunAsync(team));

        Assert.Contains((await GetAsync<NotificationPageDto>(first, "/api/notifications")).Items, n => n.Kind == "approval.overdue");
        Assert.Empty((await GetAsync<NotificationPageDto>(requester, "/api/notifications")).Items.Where(n => n.Kind == "approval.overdue"));
    }

    [Fact]
    public async Task Delegate_decides_for_the_principal_and_loses_it_when_revoked()
    {
        Team team = await SetUpAsync();
        using HttpClient requester = await LoginAsync(team.Requester);
        using HttpClient first = await LoginAsync(team.First);
        using HttpClient second = await LoginAsync(team.Second);
        ApprovalDefinitionDto definition = await CreateDefinitionAsync(team, escalateTo: null);

        ApprovalDelegationDto delegation = await ReadAsync<ApprovalDelegationDto>(
            await first.PostAsJsonAsync("/api/approvals/delegations", Delegate(team.Second.Id)), HttpStatusCode.Created);
        Assert.True(delegation.IsActive);

        ApprovalRequestDto opened = await OpenAsync(requester, definition);
        Assert.Contains((await GetAsync<NotificationPageDto>(second, "/api/notifications")).Items, n => n.Kind == "approval.waiting");
        Assert.Single((await GetAsync<ApprovalRequestPageDto>(second, "/api/approvals/requests?view=waiting")).Items);

        ApprovalRequestDto decided = await ReadAsync<ApprovalRequestDto>(await DecideAsync(second, opened, "approve"), HttpStatusCode.OK);
        Assert.NotNull(decided.Steps[0].OnBehalfOfName);
        Assert.Equal("Approved", decided.Status);

        await ReadAsync<ApprovalDelegationDto>(await first.PostAsJsonAsync($"/api/approvals/delegations/{delegation.Id}/revoke", new { }), HttpStatusCode.OK);
        ApprovalRequestDto next = await OpenAsync(requester, definition);
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(second, next, "approve")).StatusCode);
    }

    [Fact]
    public async Task Delegation_cannot_bypass_four_eyes()
    {
        Team team = await SetUpAsync();
        await factory.CreateRoleAsync(team.Tenant.Id, [.. Requester, .. Decider], team.Second);
        using HttpClient first = await LoginAsync(team.First);
        using HttpClient second = await LoginAsync(team.Second);
        ApprovalDefinitionDto definition = await CreateDefinitionAsync(team, escalateTo: null);

        // İkinci kişi birinci kişinin vekili ama talep sahibi: kendi talebine vekâleten de karar veremez.
        await ReadAsync<ApprovalDelegationDto>(await first.PostAsJsonAsync("/api/approvals/delegations", Delegate(team.Second.Id)), HttpStatusCode.Created);
        ApprovalRequestDto own = await OpenAsync(second, definition);
        Assert.False(own.CanDecide);
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(second, own, "approve")).StatusCode);
    }

    [Fact]
    public async Task Delegation_is_validated_and_limited_to_the_principal()
    {
        Team team = await SetUpAsync();
        Team other = await SetUpAsync();
        using HttpClient first = await LoginAsync(team.First);
        using HttpClient second = await LoginAsync(team.Second);
        using HttpClient requester = await LoginAsync(team.Requester);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await first.PostAsJsonAsync("/api/approvals/delegations", Delegate(team.First.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await first.PostAsJsonAsync("/api/approvals/delegations", Delegate(other.First.Id))).StatusCode);

        ApprovalDelegationDto delegation = await ReadAsync<ApprovalDelegationDto>(
            await first.PostAsJsonAsync("/api/approvals/delegations", Delegate(team.Second.Id)), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await first.PostAsJsonAsync("/api/approvals/delegations", Delegate(team.Second.Id))).StatusCode);

        // Vekil geri alamaz, başkası adına vekâlet açılamaz, karar izni olmayan vekâlet açamaz.
        Assert.Equal(HttpStatusCode.NotFound, (await second.PostAsJsonAsync($"/api/approvals/delegations/{delegation.Id}/revoke", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await second.PostAsJsonAsync("/api/approvals/delegations", Delegate(team.Requester.Id) with { PrincipalUserId = team.First.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.PostAsJsonAsync("/api/approvals/delegations", Delegate(team.Second.Id))).StatusCode);
    }

    [Fact]
    public async Task Tenants_cannot_read_each_others_notifications_or_delegations()
    {
        Team a = await SetUpAsync();
        Team b = await SetUpAsync();
        using HttpClient requesterA = await LoginAsync(a.Requester);
        using HttpClient firstA = await LoginAsync(a.First);
        using HttpClient adminB = await LoginAsync(b.Admin);
        await OpenAsync(requesterA, await CreateDefinitionAsync(a, escalateTo: null));
        ApprovalDelegationDto delegation = await ReadAsync<ApprovalDelegationDto>(
            await firstA.PostAsJsonAsync("/api/approvals/delegations", Delegate(a.Second.Id)), HttpStatusCode.Created);

        Assert.Empty(await GetAsync<List<ApprovalDelegationDto>>(adminB, "/api/approvals/delegations"));
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/approvals/delegations/{delegation.Id}/revoke", new { })).StatusCode);
        int visible = await factory.ScalarAsync<int>(b.Tenant.Id, "SELECT COUNT(*) FROM core.Notifications WHERE UserId = @id", ("id", a.First.Id));
        Assert.Equal(0, visible);
    }

    private static CreateApprovalDelegationRequest Delegate(Guid userId) =>
        new(null, userId, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7), "İzin");

    private async Task<int> RunAsync(Team team) =>
        await factory.Services.GetRequiredService<ApprovalEscalationRunner>().RunTenantAsync(team.Tenant.Id, CancellationToken.None);

    private async Task ExpireAsync(Team team, Guid requestId)
    {
        using IServiceScope scope = factory.OpenScope(team.Tenant.Id);
        ModulixDbContext db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        DateTimeOffset past = DateTimeOffset.UtcNow.AddHours(-1);
        await db.ApprovalRequestSteps.Where(s => s.RequestId == requestId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DueAt, past));
    }

    private async Task<Team> SetUpAsync()
    {
        Tenant tenant = await factory.CreateTenantAsync();
        ModulixUser admin = await factory.CreateUserAsync(tenant);
        ModulixUser requester = await factory.CreateUserAsync(tenant);
        ModulixUser first = await factory.CreateUserAsync(tenant);
        ModulixUser second = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        await factory.CreateRoleAsync(tenant.Id, Requester, requester);
        await factory.CreateRoleAsync(tenant.Id, Decider, first, second);
        return new Team(tenant, admin, requester, first, second);
    }

    private async Task<ApprovalDefinitionDto> CreateDefinitionAsync(Team team, Guid? escalateTo)
    {
        using HttpClient admin = await LoginAsync(team.Admin);
        return await ReadAsync<ApprovalDefinitionDto>(await admin.PostAsJsonAsync("/api/approvals/definitions", new SaveApprovalDefinitionRequest(
            $"Masraf {Guid.NewGuid():N}"[..20], null, null, [new ApprovalStepDefinitionDto("Yönetici", 1, team.First.Id, null, null, 24, escalateTo)])), HttpStatusCode.Created);
    }

    private static async Task<ApprovalRequestDto> OpenAsync(HttpClient client, ApprovalDefinitionDto definition) =>
        await ReadAsync<ApprovalRequestDto>(await client.PostAsJsonAsync(
            "/api/approvals/requests", new OpenApprovalRequest(definition.Id, "Gizli proje masrafı", null, null, null)), HttpStatusCode.Created);

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, ApprovalRequestDto request, string action, string? comment = null) =>
        client.PostAsJsonAsync($"/api/approvals/requests/{request.Id}/{action}", new DecideApprovalRequest(comment, request.Version));

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        HttpClient client = factory.CreateBrowserClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url) => await ReadAsync<T>(await client.GetAsync(url), HttpStatusCode.OK);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
