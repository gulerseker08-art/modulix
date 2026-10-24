using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Users;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Users;
using static Modulix.Core.Modules.CoreModule.Permissions;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Y5b: kayıt geçmişi (kaydı görebilen herkes görür) ve oturum olayları (doğru firmaların denetim kaydına yazılır).
/// </summary>
public class RecordHistoryAndSignInTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string UserAgent = "ModulixTest/1.0";

    [Fact]
    public async Task Record_history_is_visible_to_anyone_who_can_read_the_record()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var editor = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, CompaniesWrite], editor);
        using var editorClient = await LoginAsync(editor);
        var created = (await (await editorClient.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Anka Gıda")))
            .Content.ReadFromJsonAsync<CompanyDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await editorClient.PutAsJsonAsync($"/api/crm/companies/{created.Id}",
            new SaveCompanyRequest("Anka Gıda", Status: CompanyStatus.Customer, Version: created.Version))).StatusCode);

        // Yalnızca firma okuma izni: denetim izni olmadan geçmiş görünür, değerler açık.
        var reader = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], reader);
        using var readerClient = await LoginAsync(reader);
        var url = $"/api/records/crm.company/{created.Id}/history";
        var history = (await readerClient.GetFromJsonAsync<AuditPageResponse>(url))!;
        Assert.Equal(2, history.TotalCount);
        Assert.Equal(AuditOperation.Updated, history.Items[0].Action);
        Assert.Equal(AuditOperation.Created, history.Items[1].Action);
        Assert.All(history.Items, item => Assert.False(item.ValuesHidden));
        Assert.Equal("Anka Gıda", history.Items[0].RecordTitle);
        var status = Assert.Single(history.Items[0].Changes, c => c.Field == "status");
        Assert.Equal(("Durum", "Potansiyel", "Müşteri"), (status.Label, status.Old, status.New));
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.GetAsync("/api/audit")).StatusCode);

        // Kaydı göremeyen: izni yoksa 403, başka firmadaysa veya kayıt yoksa 404.
        var auditor = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [ReadAudit], auditor);
        using var auditorClient = await LoginAsync(auditor);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditorClient.GetAsync(url)).StatusCode);

        var other = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(other.Id, "crm", enabled: true);
        var stranger = await factory.CreateUserAsync(other);
        await factory.CreateRoleAsync(other.Id, [CompaniesRead], stranger);
        using var strangerClient = await LoginAsync(stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await strangerClient.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await readerClient.GetAsync($"/api/records/crm.company/{Guid.NewGuid()}/history")).StatusCode);
    }

    [Fact]
    public async Task Sign_in_events_go_to_the_right_tenants()
    {
        var first = await factory.CreateTenantAsync();
        var second = await factory.CreateTenantAsync();
        var unrelated = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(first, second);
        await factory.CreateUserAsync(unrelated);

        // Hatalı şifre: kişinin üye olduğu tüm firmalara.
        using var client = Browser();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, "Yanlis.Sifre1"))).StatusCode);
        foreach (var tenant in new[] { first, second })
        {
            var failed = Assert.Single(await EventsAsync(tenant.Id, user.Id));
            Assert.Equal(AuditAction.SignInFailed, failed.Action);
            Assert.Contains("WrongPassword", failed.Changes);
            Assert.Contains(UserAgent, failed.Changes);
        }

        Assert.Empty(await EventsAsync(unrelated.Id, user.Id));

        // Kayıtlı olmayan e-posta hiçbir firmaya yazılmaz.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest($"yok-{Guid.NewGuid():N}@test.modulix", "Yanlis.Sifre1"))).StatusCode);
        Assert.Single(await EventsAsync(first.Id, user.Id));

        // Başarılı giriş: iki firma varken kişi seçer; yalnızca seçilen firmaya yazılır.
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password))).StatusCode);
        Assert.Single(await EventsAsync(first.Id, user.Id));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/active-tenant", new SelectTenantRequest(first.Id))).StatusCode);
        Assert.Equal(AuditAction.SignedIn, (await EventsAsync(first.Id, user.Id))[^1].Action);
        Assert.Single(await EventsAsync(second.Id, user.Id));

        // Denetim kaydı ekranında okunur.
        var auditor = await factory.CreateUserAsync(first);
        await factory.CreateRoleAsync(first.Id, [ReadAudit], auditor);
        using var auditorClient = await LoginAsync(auditor);
        var page = (await auditorClient.GetFromJsonAsync<AuditPageResponse>($"/api/audit?entityKey=core.account&userId={user.Id}"))!;
        Assert.Equal([AuditOperation.SignedIn, AuditOperation.SignInFailed], page.Items.Select(i => i.Action));
        var item = page.Items[1];
        Assert.Equal("Hesap", item.EntityName);
        Assert.Equal(user.Email, item.RecordTitle);
        Assert.Equal("Test Kullanıcı", item.UserName);
        Assert.Contains(item.Changes, c => c.Label == "Neden" && c.New == "Hatalı şifre");
        Assert.Contains(item.Changes, c => c.Label == "Tarayıcı" && c.New == UserAgent);
    }

    [Fact]
    public async Task Lockout_is_recorded_once_and_later_attempts_as_failed()
    {
        var tenant = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(tenant);
        using var client = Browser();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, "Yanlis.Sifre1"))).StatusCode);
        }

        Assert.Equal([.. Enumerable.Repeat(HttpStatusCode.Unauthorized, 4), HttpStatusCode.Locked, HttpStatusCode.Locked], statuses);
        var events = await EventsAsync(tenant.Id, user.Id);
        Assert.Equal(
            [.. Enumerable.Repeat(AuditAction.SignInFailed, 4), AuditAction.LockedOut, AuditAction.SignInFailed],
            events.Select(e => e.Action));
        Assert.Contains(AccountAuditReasons.Locked, events[^1].Changes);
    }

    [Fact]
    public async Task Password_reset_and_invitation_acceptance_are_recorded()
    {
        var tenant = await factory.CreateTenantAsync();
        var member = await factory.CreateUserAsync(tenant);
        var email = $"davet-{Guid.NewGuid():N}@test.modulix";
        string resetToken, invitationToken;
        Guid invitedId, membershipId;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
            resetToken = await users.GeneratePasswordResetTokenAsync((await users.FindByIdAsync(member.Id.ToString()))!);

            var invited = new ModulixUser { UserName = email, Email = email, DisplayName = "Davetli Kişi" };
            Assert.True((await users.CreateAsync(invited)).Succeeded);
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var membership = Membership.CreateInvited(tenant.Id, invited.Id, awaitsAcceptance: true, DateTimeOffset.UtcNow);
            db.Memberships.Add(membership);
            await db.SaveChangesAsync();
            (invitedId, membershipId) = (invited.Id, membership.Id);
            invitationToken = await users.GenerateUserTokenAsync(
                invited, AccountTokens.InvitationProvider, AccountTokens.InvitationPurpose(membership.Id));
        }

        using var client = Browser();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/reset-password",
            new ResetPasswordRequest(member.Id, resetToken, "Yeni.Sifre.2026"))).StatusCode);
        Assert.Equal(AuditAction.PasswordReset, Assert.Single(await EventsAsync(tenant.Id, member.Id)).Action);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/accept-invitation",
            new AcceptInvitationRequest(membershipId, invitationToken, "Yeni.Sifre.2026"))).StatusCode);
        var accepted = Assert.Single(await EventsAsync(tenant.Id, invitedId));
        Assert.Equal(AuditAction.InvitationAccepted, accepted.Action);
        Assert.Contains(email, accepted.Changes);
    }

    private async Task<List<(AuditAction Action, string Changes)>> EventsAsync(Guid tenantId, Guid userId)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        return (await db.AuditLog.AsNoTracking()
                .Where(a => a.EntityKey == CoreModule.AuditedEntityKeys.Account && a.RecordId == userId)
                .OrderBy(a => a.OccurredAt).ThenBy(a => a.Id)
                .Select(a => new { a.Action, a.Changes })
                .ToListAsync())
            .Select(a => (a.Action, a.Changes))
            .ToList();
    }

    private HttpClient Browser()
    {
        var client = factory.CreateBrowserClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = Browser();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
