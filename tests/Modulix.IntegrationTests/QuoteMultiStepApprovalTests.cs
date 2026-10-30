using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Crm;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Settings;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>ADR-0019 dilim 10: sıralı/paralel çok adımlı onay, vekâlet ve onay hatırlatması.</summary>
public class QuoteMultiStepApprovalTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] ApproverPermissions = [QuotesRead, QuotesApprove, CompaniesRead];

    [Fact]
    public async Task Sequential_steps_are_enforced_by_the_api_and_the_quote_is_approved_only_after_the_last_step()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var a = await LoginAsync(s.ApproverA);
        using var b = await LoginAsync(s.ApproverB);
        await PostAsync<QuoteApprovalRuleDto>(admin, "/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("Müdür", MinGrossTotal: 1m, ApproverId: s.ApproverA.Id, Step: 1));
        await PostAsync<QuoteApprovalRuleDto>(admin, "/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("Direktör", MinGrossTotal: 1m, ApproverId: s.ApproverB.Id, Step: 2));

        var quote = await SubmittedQuoteAsync(admin, s.CompanyId);
        var requirement = await RequirementAsync(admin, quote.Id);
        Assert.Equal((2, 1), (requirement.Steps.Count, requirement.CurrentStep));

        // 2. adımın onaylayanı 1. adım bitmeden karar veremez; 1. adımın onaylayanı verir ama teklif hâlâ onay bekler.
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(b, quote.Id, "approve")).StatusCode);
        var first = await Try(a, quote.Id, "approve");
        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        Assert.Equal("Pending", (await first.Content.ReadFromJsonAsync<QuoteDto>())!.ApprovalStatus);
        var afterFirst = await RequirementAsync(b, quote.Id);
        Assert.True(afterFirst.Steps[0].Completed);
        Assert.Equal(2, afterFirst.CurrentStep);
        Assert.True(afterFirst.CanDecide);
        Assert.Contains((await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{quote.Id}/history"))!, h => h.Kind == "StepApproved" && h.Reason!.Contains("Adım 1/2"));

        // Aynı kişinin ikinci onayı adımı ilerletmez (A artık 2. adımda yetkisiz).
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(a, quote.Id, "approve")).StatusCode);

        // Sunum hâlâ engelli; 2. adım onaylanınca onay tamamlanır ve sunum açılır.
        Assert.Equal(HttpStatusCode.Conflict, (await Try(admin, quote.Id, "present")).StatusCode);
        var second = await Try(b, quote.Id, "approve");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("Approved", (await second.Content.ReadFromJsonAsync<QuoteDto>())!.ApprovalStatus);
        Assert.Equal(HttpStatusCode.OK, (await Try(admin, quote.Id, "present")).StatusCode);
    }

    [Fact]
    public async Task Parallel_named_approvers_in_one_step_must_all_approve_and_a_rejection_ends_the_flow()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var a = await LoginAsync(s.ApproverA);
        using var b = await LoginAsync(s.ApproverB);
        await PostAsync<QuoteApprovalRuleDto>(admin, "/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("Satış", MinGrossTotal: 1m, ApproverId: s.ApproverA.Id));
        await PostAsync<QuoteApprovalRuleDto>(admin, "/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("Finans", MinGrossTotal: 1m, ApproverId: s.ApproverB.Id));

        var quote = await SubmittedQuoteAsync(admin, s.CompanyId);
        Assert.Equal("Pending", (await (await Try(b, quote.Id, "approve")).Content.ReadFromJsonAsync<QuoteDto>())!.ApprovalStatus);
        var step = Assert.Single((await RequirementAsync(admin, quote.Id)).Steps);
        Assert.Equal([s.ApproverB.Id], step.ApprovedBy);
        Assert.False(step.Completed);

        // Diğer onaylayan reddederse akış biter; yeni gönderimde sayaç sıfırdan başlar.
        Assert.Equal(HttpStatusCode.OK, (await Try(a, quote.Id, "reject-approval", "Marj düşük")).StatusCode);
        Assert.Equal("Rejected", (await GetQuoteAsync(admin, quote.Id)).ApprovalStatus);
        Assert.Equal(HttpStatusCode.OK, (await Try(admin, quote.Id, "submit-for-approval")).StatusCode);
        Assert.Empty(Assert.Single((await RequirementAsync(admin, quote.Id)).Steps).ApprovedBy);
    }

    [Fact]
    public async Task A_delegate_approves_on_behalf_of_the_named_approver_only_while_the_delegation_is_active()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var b = await LoginAsync(s.ApproverB);
        await PostAsync<QuoteApprovalRuleDto>(admin, "/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("Müdür", MinGrossTotal: 1m, ApproverId: s.ApproverA.Id));

        // Vekâlet yokken B yetkisiz; vekâlet yönetim izni ister ve kişi kendine vekâlet veremez.
        var quote = await SubmittedQuoteAsync(admin, s.CompanyId);
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(b, quote.Id, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.PostAsJsonAsync("/api/crm/quote-approval-delegations", new SaveQuoteApprovalDelegationRequest(s.ApproverA.Id, s.ApproverB.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/crm/quote-approval-delegations", new SaveQuoteApprovalDelegationRequest(s.ApproverA.Id, s.ApproverA.Id))).StatusCode);

        // Süresi bitmiş vekâlet sayılmaz.
        var expired = await PostAsync<QuoteApprovalDelegationDto>(admin, "/api/crm/quote-approval-delegations",
            new SaveQuoteApprovalDelegationRequest(s.ApproverA.Id, s.ApproverB.Id, DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal(HttpStatusCode.Forbidden, (await Try(b, quote.Id, "approve")).StatusCode);

        // Geçerli vekâlet: B, A adına onaylar; tarihçe vekâleti söyler, onay A için sayılır.
        await PostAsync<QuoteApprovalDelegationDto>(admin, "/api/crm/quote-approval-delegations", new SaveQuoteApprovalDelegationRequest(s.ApproverA.Id, s.ApproverB.Id, Note: "İzin"));
        var requirement = await RequirementAsync(b, quote.Id);
        Assert.True(requirement.CanDecide);
        Assert.True(requirement.ActingAsDelegate);
        var approve = await Try(b, quote.Id, "approve");
        Assert.True(approve.StatusCode == HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        Assert.Equal("Approved", (await approve.Content.ReadFromJsonAsync<QuoteDto>())!.ApprovalStatus);
        Assert.Contains((await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{quote.Id}/history"))!, h => h.Kind == "Approved" && h.Reason!.Contains("vekâleten"));
        Assert.Equal(expired.Id, expired.Id);
    }

    [Fact]
    public async Task Approval_reminders_go_once_to_the_current_step_approvers_and_their_delegates()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        await PostAsync<QuoteApprovalRuleDto>(admin, "/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("Müdür", MinGrossTotal: 1m, ApproverId: s.ApproverA.Id, Step: 1));
        await PostAsync<QuoteApprovalRuleDto>(admin, "/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("Direktör", MinGrossTotal: 1m, ApproverId: s.ApproverB.Id, Step: 2));
        await PostAsync<QuoteApprovalDelegationDto>(admin, "/api/crm/quote-approval-delegations", new SaveQuoteApprovalDelegationRequest(s.ApproverA.Id, s.Admin.Id));
        await SetSettingAsync(admin, "crm.quote.approval-reminder-hours", "2");
        var quote = await SubmittedQuoteAsync(admin, s.CompanyId);
        var runner = factory.Services.GetRequiredService<QuoteExpiryRunner>();

        // Henüz 2 saat geçmedi: hatırlatma yok.
        Assert.Equal(0, (await runner.RunTenantAsync(s.TenantId, CancellationToken.None)).ApprovalRemindersSent);

        // Gönderim kaydı geriye alınınca: 1. adımın onaylayanına (A) ve vekiline (yönetici) birer e-posta; B'ye (2. adım) gitmez. İkinci tur tekrar göndermez.
        await BackdateSubmissionAsync(s.TenantId, quote.Id, TimeSpan.FromHours(3));
        Assert.Equal(1, (await runner.RunTenantAsync(s.TenantId, CancellationToken.None)).ApprovalRemindersSent);
        Assert.Equal(0, (await runner.RunTenantAsync(s.TenantId, CancellationToken.None)).ApprovalRemindersSent);
        var reference = $"crm.quote-approval-reminder:{quote.Id:N}";
        Assert.Equal(1, await QueuedCountAsync(reference, s.ApproverA.Email!));
        Assert.Equal(1, await QueuedCountAsync(reference, s.Admin.Email!));
        Assert.Equal(0, await QueuedCountAsync(reference, s.ApproverB.Email!));
        Assert.Contains((await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{quote.Id}/history"))!, h => h.Kind == "ApprovalReminderSent");
    }

    // ---- yardımcılar

    private async Task BackdateSubmissionAsync(Guid tenantId, Guid quoteId, TimeSpan by)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var at = DateTimeOffset.UtcNow - by;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE crm.QuoteEvents SET CreatedAt = {at} WHERE QuoteId = {quoteId} AND Kind = 'SubmittedForApproval'");
    }

    private async Task<int> QueuedCountAsync(string reference, string address)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        return await db.EmailMessages.CountAsync(e => e.Reference == reference && e.ToAddress == address);
    }

    private static async Task<QuoteApprovalRequirementDto> RequirementAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<QuoteApprovalRequirementDto>($"/api/crm/quotes/{id}/approval-requirements"))!;

    private static async Task<QuoteDto> GetQuoteAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;

    private static async Task<HttpResponseMessage> Try(HttpClient client, Guid id, string action, string? reason = null) =>
        await client.PostAsJsonAsync($"/api/crm/quotes/{id}/{action}", new QuoteTransitionRequest((await GetQuoteAsync(client, id)).Version, reason));

    private static async Task<QuoteDto> SubmittedQuoteAsync(HttpClient client, Guid companyId)
    {
        var quote = await PostAsync<QuoteDto>(client, "/api/crm/quotes", new SaveQuoteRequest("Teklif", companyId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        await PostAsync<QuoteLineDto>(client, "/api/crm/quote-lines", new SaveQuoteLineRequest(quote.Id, "Kalem", "Free", null, 1m, 500m, null, "None", 0m, 20m, null, "Standard"));
        var response = await Try(client, quote.Id, "submit-for-approval");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<QuoteDto>())!;
    }

    private static async Task SetSettingAsync(HttpClient client, string key, string value)
    {
        var response = await client.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { [key] = new(value, 0) }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(ModulixUser Admin, ModulixUser ApproverA, ModulixUser ApproverB, Guid CompanyId, Guid TenantId)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var approverA = await factory.CreateUserAsync(tenant);
        var approverB = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, ApproverPermissions, Scope.All, approverA, approverB);
        using var client = await LoginAsync(admin);
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        return (admin, approverA, approverB, company.Id, tenant.Id);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
