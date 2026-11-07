using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Approvals;
using Modulix.Shared.Auth;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0027 Dilim 4a: genel onay. Çok adımlı talep, dört göz, eşzamanlı karar, sahipsiz adım, tenant izolasyonu (API ve Row-Level Security),
/// kişi bazlı görünürlük, izinler, denetim kaydı ve e-posta bildirimi.
/// </summary>
public class ApprovalTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] Requester = [CoreModule.Permissions.RequestApprovals];
    private static readonly string[] Decider = [CoreModule.Permissions.DecideApprovals];

    private sealed record Team(Tenant Tenant, ModulixUser Admin, ModulixUser Requester, ModulixUser First, ModulixUser Second);

    [Fact]
    public async Task Request_moves_through_steps_and_ends_approved_with_audit_and_email()
    {
        Team team = await SetUpAsync();
        using HttpClient requester = await LoginAsync(team.Requester);
        using HttpClient first = await LoginAsync(team.First);
        using HttpClient second = await LoginAsync(team.Second);
        ApprovalDefinitionDto definition = await CreateDefinitionAsync(team);

        ApprovalRequestDto opened = await OpenAsync(requester, definition, 250_000m);
        Assert.Equal("Pending", opened.Status);
        Assert.Equal(["Active", "Waiting"], opened.Steps.Select(s => s.Status));
        Assert.True(opened.CanWithdraw);

        // İkinci onaycı sırası gelmeden karar veremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostDecisionAsync(second, opened, "approve")).StatusCode);

        ApprovalRequestPageDto waiting = await GetAsync<ApprovalRequestPageDto>(first, "/api/approvals/requests?view=waiting");
        Assert.Equal(opened.Id, Assert.Single(waiting.Items).Id);

        ApprovalRequestDto afterFirst = await ReadAsync<ApprovalRequestDto>(await PostDecisionAsync(first, opened, "approve"), HttpStatusCode.OK);
        Assert.Equal(["Approved", "Active"], afterFirst.Steps.Select(s => s.Status));

        ApprovalRequestDto done = await ReadAsync<ApprovalRequestDto>(await PostDecisionAsync(second, afterFirst, "approve"), HttpStatusCode.OK);
        Assert.Equal("Approved", done.Status);
        Assert.NotNull(done.CompletedAt);

        ApprovalOverviewDto overview = await GetAsync<ApprovalOverviewDto>(requester, "/api/approvals/overview");
        Assert.Equal(1, overview.ApprovedLast30Days);
        Assert.Equal(0, overview.MyOpen);

        using IServiceScope scope = factory.OpenScope(team.Tenant.Id);
        ModulixDbContext db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.True(await db.AuditLog.CountAsync(a => a.RecordId == opened.Id && a.EntityKey == CoreModule.AuditedEntityKeys.ApprovalRequest) >= 3);
        Assert.True(await db.EmailMessages.CountAsync(m => m.Reference == $"core.approval:{opened.Id}") >= 3);
    }

    [Fact]
    public async Task Small_amount_skips_the_threshold_step_and_definition_change_does_not_touch_open_requests()
    {
        Team team = await SetUpAsync();
        using HttpClient admin = await LoginAsync(team.Admin);
        using HttpClient requester = await LoginAsync(team.Requester);
        ApprovalDefinitionDto definition = await CreateDefinitionAsync(team);

        ApprovalRequestDto small = await OpenAsync(requester, definition, 1_000m);
        Assert.Single(small.Steps);

        ApprovalRequestDto big = await OpenAsync(requester, definition, 500_000m);
        ApprovalDefinitionDto changed = await ReadAsync<ApprovalDefinitionDto>(await admin.PutAsJsonAsync(
            $"/api/approvals/definitions/{definition.Id}",
            new SaveApprovalDefinitionRequest(definition.Name, null, "TRY", [definition.Steps[0]], definition.Version)), HttpStatusCode.OK);
        Assert.Equal(2, changed.Revision);

        ApprovalRequestDto reloaded = await GetAsync<ApprovalRequestDto>(requester, $"/api/approvals/requests/{big.Id}");
        Assert.Equal(2, reloaded.Steps.Count);
        Assert.Equal(1, reloaded.DefinitionRevision);
    }

    [Fact]
    public async Task Rejection_needs_a_reason_and_closes_the_request()
    {
        Team team = await SetUpAsync();
        using HttpClient requester = await LoginAsync(team.Requester);
        using HttpClient first = await LoginAsync(team.First);
        ApprovalRequestDto opened = await OpenAsync(requester, await CreateDefinitionAsync(team), 250_000m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostDecisionAsync(first, opened, "reject")).StatusCode);

        ApprovalRequestDto rejected = await ReadAsync<ApprovalRequestDto>(await PostDecisionAsync(first, opened, "reject", "Bütçe yok"), HttpStatusCode.OK);
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal(["Rejected", "Cancelled"], rejected.Steps.Select(s => s.Status));
        Assert.Equal(HttpStatusCode.Conflict, (await PostDecisionAsync(first, rejected, "approve")).StatusCode);
    }

    [Fact]
    public async Task Requester_cannot_decide_own_request_even_as_the_step_approver()
    {
        Team team = await SetUpAsync();
        await factory.CreateRoleAsync(team.Tenant.Id, [.. Requester, .. Decider], team.First);
        using HttpClient first = await LoginAsync(team.First);
        ApprovalDefinitionDto definition = await CreateDefinitionAsync(team);

        // Tek onaycı talep sahibiyse süreç sahipsiz kalır: talep açılmaz.
        HttpResponseMessage response = await first.PostAsJsonAsync(
            "/api/approvals/requests", new OpenApprovalRequest(definition.Id, "Kendi talebim", null, 250_000m, "TRY"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Two_simultaneous_decisions_advance_the_step_once()
    {
        Team team = await SetUpAsync();
        using HttpClient requester = await LoginAsync(team.Requester);
        using HttpClient one = await LoginAsync(team.First);
        using HttpClient two = await LoginAsync(team.First);
        ApprovalRequestDto opened = await OpenAsync(requester, await CreateDefinitionAsync(team), 250_000m);

        HttpResponseMessage[] results = await Task.WhenAll(PostDecisionAsync(one, opened, "approve"), PostDecisionAsync(two, opened, "approve"));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        ApprovalRequestDto reloaded = await GetAsync<ApprovalRequestDto>(requester, $"/api/approvals/requests/{opened.Id}");
        Assert.Equal(["Approved", "Active"], reloaded.Steps.Select(s => s.Status));
    }

    [Fact]
    public async Task Departed_approver_shows_as_orphaned_and_manager_reassigns_the_step()
    {
        Team team = await SetUpAsync();
        using HttpClient admin = await LoginAsync(team.Admin);
        using HttpClient requester = await LoginAsync(team.Requester);
        using HttpClient second = await LoginAsync(team.Second);
        ApprovalRequestDto opened = await OpenAsync(requester, await CreateDefinitionAsync(team), 250_000m);

        await factory.DisableMembershipAsync(team.First.Id, team.Tenant.Id);

        ApprovalOverviewDto overview = await GetAsync<ApprovalOverviewDto>(admin, "/api/approvals/overview");
        Assert.Equal(1, overview.Orphaned);
        ApprovalRequestDto seen = await GetAsync<ApprovalRequestDto>(admin, $"/api/approvals/requests/{opened.Id}");
        ApprovalStepDto step = seen.Steps[0];
        Assert.True(step.IsOrphaned);

        HttpResponseMessage toRequester = await admin.PostAsJsonAsync(
            $"/api/approvals/requests/{opened.Id}/reassign", new ReassignApprovalStepRequest(step.Id, team.Requester.Id, null, seen.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, toRequester.StatusCode);

        ApprovalRequestDto reassigned = await ReadAsync<ApprovalRequestDto>(await admin.PostAsJsonAsync(
            $"/api/approvals/requests/{opened.Id}/reassign", new ReassignApprovalStepRequest(step.Id, team.Second.Id, null, seen.Version)), HttpStatusCode.OK);
        Assert.False(reassigned.Steps[0].IsOrphaned);

        ApprovalRequestDto approved = await ReadAsync<ApprovalRequestDto>(await PostDecisionAsync(second, reassigned, "approve"), HttpStatusCode.OK);
        Assert.Equal("Approved", approved.Steps[0].Status);
    }

    [Fact]
    public async Task Requester_can_withdraw_only_own_pending_request()
    {
        Team team = await SetUpAsync();
        using HttpClient requester = await LoginAsync(team.Requester);
        using HttpClient first = await LoginAsync(team.First);
        ApprovalRequestDto opened = await OpenAsync(requester, await CreateDefinitionAsync(team), 250_000m);

        ApprovalRequestDto withdrawn = await ReadAsync<ApprovalRequestDto>(
            await requester.PostAsJsonAsync($"/api/approvals/requests/{opened.Id}/withdraw", new { }), HttpStatusCode.OK);
        Assert.Equal("Withdrawn", withdrawn.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await requester.PostAsJsonAsync($"/api/approvals/requests/{opened.Id}/withdraw", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await first.PostAsJsonAsync($"/api/approvals/requests/{opened.Id}/withdraw", new { })).StatusCode);
    }

    [Fact]
    public async Task Users_see_only_requests_they_take_part_in()
    {
        Team team = await SetUpAsync();
        ModulixUser outsider = await factory.CreateUserAsync(team.Tenant);
        await factory.CreateRoleAsync(team.Tenant.Id, Decider, outsider);
        using HttpClient requester = await LoginAsync(team.Requester);
        using HttpClient stranger = await LoginAsync(outsider);
        using HttpClient admin = await LoginAsync(team.Admin);
        ApprovalRequestDto opened = await OpenAsync(requester, await CreateDefinitionAsync(team), 250_000m);

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/approvals/requests/{opened.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostDecisionAsync(stranger, opened, "approve")).StatusCode);
        Assert.Empty((await GetAsync<ApprovalRequestPageDto>(stranger, "/api/approvals/requests?view=all")).Items);
        Assert.Single((await GetAsync<ApprovalRequestPageDto>(admin, "/api/approvals/requests?view=all")).Items);
    }

    [Fact]
    public async Task Tenant_cannot_reach_another_tenants_definitions_or_requests()
    {
        Team a = await SetUpAsync();
        Team b = await SetUpAsync();
        using HttpClient requesterA = await LoginAsync(a.Requester);
        using HttpClient adminB = await LoginAsync(b.Admin);
        ApprovalDefinitionDto definition = await CreateDefinitionAsync(a);
        ApprovalRequestDto opened = await OpenAsync(requesterA, definition, 250_000m);

        Assert.Empty(await GetAsync<List<ApprovalDefinitionDto>>(adminB, "/api/approvals/definitions?activeOnly=false") is { Count: > 0 } own ? own.Where(d => d.Id == definition.Id) : []);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/approvals/requests/{opened.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PutAsJsonAsync($"/api/approvals/definitions/{definition.Id}/active", new SetApprovalDefinitionActiveRequest(false))).StatusCode);

        // B'nin tanımına A'nın kişisi yazılamaz.
        HttpResponseMessage foreign = await adminB.PostAsJsonAsync("/api/approvals/definitions", new SaveApprovalDefinitionRequest(
            "Yabancı onaycı", null, null, [new ApprovalStepDefinitionDto("Adım", 1, a.First.Id, null, null, null)]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);

        // Row-Level Security: B oturumuyla ham sorgu A'nın talebini göremez.
        int visible = await factory.ScalarAsync<int>(b.Tenant.Id, "SELECT COUNT(*) FROM core.ApprovalRequests WHERE Id = @id", ("id", opened.Id));
        Assert.Equal(0, visible);
    }

    [Fact]
    public async Task Permissions_are_enforced_on_every_write()
    {
        Team team = await SetUpAsync();
        ModulixUser nobody = await factory.CreateUserAsync(team.Tenant);
        using HttpClient none = await LoginAsync(nobody);
        using HttpClient requester = await LoginAsync(team.Requester);
        using HttpClient first = await LoginAsync(team.First);
        ApprovalDefinitionDto definition = await CreateDefinitionAsync(team);

        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync("/api/approvals/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.PostAsJsonAsync("/api/approvals/definitions", new SaveApprovalDefinitionRequest("X", null, null, []))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await first.PostAsJsonAsync("/api/approvals/requests", new OpenApprovalRequest(definition.Id, "X", null, 1m, "TRY"))).StatusCode);

        ApprovalRequestDto opened = await OpenAsync(requester, definition, 250_000m);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostDecisionAsync(requester, opened, "approve")).StatusCode);
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

    private async Task<ApprovalDefinitionDto> CreateDefinitionAsync(Team team)
    {
        using HttpClient admin = await LoginAsync(team.Admin);
        return await ReadAsync<ApprovalDefinitionDto>(await admin.PostAsJsonAsync("/api/approvals/definitions", new SaveApprovalDefinitionRequest(
            $"Satın alma {Guid.NewGuid():N}"[..24], "Bütçe dışı alımlar", "TRY",
            [
                new ApprovalStepDefinitionDto("Yönetici", 1, team.First.Id, null, null, 48),
                new ApprovalStepDefinitionDto("Genel müdür", 2, team.Second.Id, null, 100_000m, null),
            ])), HttpStatusCode.Created);
    }

    private static async Task<ApprovalRequestDto> OpenAsync(HttpClient client, ApprovalDefinitionDto definition, decimal amount) =>
        await ReadAsync<ApprovalRequestDto>(await client.PostAsJsonAsync(
            "/api/approvals/requests", new OpenApprovalRequest(definition.Id, "Depo rafı alımı", "Yeni raf sistemi", amount, "TRY")), HttpStatusCode.Created);

    private static Task<HttpResponseMessage> PostDecisionAsync(HttpClient client, ApprovalRequestDto request, string action, string? comment = null) =>
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
