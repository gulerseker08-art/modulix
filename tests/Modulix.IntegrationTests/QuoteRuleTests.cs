using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>ADR-0019 dilim 4: yapılandırılabilir iç onay kuralları, onaylayan kısıtı ve revizyon karşılaştırma.</summary>
public class QuoteRuleTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] RepPermissions = [QuotesRead, QuotesWrite, CompaniesRead];
    private static readonly string[] ApproverPermissions = [QuotesRead, QuotesApprove, CompaniesRead];

    [Fact]
    public async Task Rule_management_needs_its_own_permission_and_validates_conditions()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var rep = await LoginAsync(s.Rep);

        Assert.Equal(HttpStatusCode.Forbidden, (await rep.PostAsJsonAsync("/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("Kural", MinDiscountPercent: 10m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.GetAsync("/api/crm/quote-approval-rules")).StatusCode);

        // Koşulsuz kural olmaz; aralık dışı eşik reddedilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("Boş"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("Yüzde", MinDiscountPercent: 100m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("Kullanıcı", MinDiscountPercent: 5m, ApproverId: Guid.NewGuid()))).StatusCode);

        var created = await PostAsync<QuoteApprovalRuleDto>(admin, "/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("Yüksek indirim", MinDiscountPercent: 10m));
        Assert.Equal(10m, created.MinDiscountPercent);

        // Başka tenant kuralları göremez.
        var other = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(other.Id, "crm", enabled: true);
        var stranger = await factory.CreateUserAsync(other);
        await factory.CreateRoleAsync(other.Id, [QuoteRulesManage], Scope.All, stranger);
        using var foreign = await LoginAsync(stranger);
        var theirs = (await foreign.GetFromJsonAsync<CrmPage<QuoteApprovalRuleDto>>("/api/crm/quote-approval-rules"))!;
        Assert.Empty(theirs.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/crm/quote-approval-rules/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_matching_rule_blocks_presentation_until_approved_and_the_submission_records_rule_name_and_version()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var approver = await LoginAsync(s.ApproverA);
        var rule = await PostAsync<QuoteApprovalRuleDto>(admin, "/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("Yüksek indirim", MinDiscountPercent: 10m));

        var small = await NewQuoteAsync(admin, s.CompanyId);
        await AddLineAsync(admin, small.Id, 1000m, discountKind: "Percent", discount: 5m);
        Assert.False((await Requirement(admin, small.Id)).Required);
        await PresentAsync(admin, small.Id);   // eşik altı: onaysız sunulur

        var big = await NewQuoteAsync(admin, s.CompanyId);
        await AddLineAsync(admin, big.Id, 1000m, quantity: 2m, discountKind: "Percent", discount: 20m);
        var requirement = await Requirement(admin, big.Id);
        Assert.True(requirement.Required);
        Assert.Equal(rule.Name, Assert.Single(requirement.Matches).Name);

        var blocked = await admin.PostAsJsonAsync($"/api/crm/quotes/{big.Id}/present", new QuoteTransitionRequest((await GetQuoteAsync(admin, big.Id)).Version));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        await TransitionAsync(admin, big.Id, "submit-for-approval");
        var history = (await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{big.Id}/history"))!;
        var submitted = history.Single(h => h.Kind == "SubmittedForApproval");
        Assert.Contains("Yüksek indirim", submitted.Reason);
        Assert.Contains($"sürüm {rule.Version}", submitted.Reason);

        await TransitionAsync(approver, big.Id, "approve");
        Assert.Equal("Presented", (await PresentAsync(admin, big.Id)).Status);
    }

    [Fact]
    public async Task Currency_scoped_amount_rules_do_not_match_other_currencies_and_inactive_rules_are_ignored()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        await PostAsync<QuoteApprovalRuleDto>(admin, "/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("USD tutar", MinGrossTotal: 100m, Currency: "USD"));
        await PostAsync<QuoteApprovalRuleDto>(admin, "/api/crm/quote-approval-rules", new SaveQuoteApprovalRuleRequest("Pasif", MinGrossTotal: 1m, IsActive: false));

        var quote = await NewQuoteAsync(admin, s.CompanyId);   // TRY
        await AddLineAsync(admin, quote.Id, 5000m);
        Assert.False((await Requirement(admin, quote.Id)).Required);
    }

    [Fact]
    public async Task A_named_approver_is_enforced_by_the_api_and_shown_in_the_action_list()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var approverA = await LoginAsync(s.ApproverA);
        using var approverB = await LoginAsync(s.ApproverB);
        await PostAsync<QuoteApprovalRuleDto>(admin, "/api/crm/quote-approval-rules",
            new SaveQuoteApprovalRuleRequest("Yönetici onayı", MinGrossTotal: 1m, ApproverId: s.ApproverA.Id));

        var quote = await NewQuoteAsync(admin, s.CompanyId);
        await AddLineAsync(admin, quote.Id, 500m);
        await TransitionAsync(admin, quote.Id, "submit-for-approval");

        // Onay izni olsa da atanmamış kullanıcı karar veremez: API reddeder, eylem listesi nedeni söyler.
        var denied = await approverB.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/approve", new QuoteTransitionRequest((await GetQuoteAsync(approverB, quote.Id)).Version));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var actions = (await approverB.GetFromJsonAsync<List<QuoteActionStateDto>>($"/api/crm/quotes/{quote.Id}/actions"))!;
        Assert.Equal("approver-restricted", actions.Single(a => a.Action == "approve").Code);
        Assert.False((await Requirement(approverB, quote.Id)).CanDecide);

        Assert.True((await Requirement(approverA, quote.Id)).CanDecide);
        Assert.Equal("Approved", (await TransitionAsync(approverA, quote.Id, "approve")).ApprovalStatus);
    }

    [Fact]
    public async Task Revisions_are_compared_line_by_line_without_costs_and_only_within_a_family()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var v1 = await NewQuoteAsync(admin, s.CompanyId);
        var kept = await AddLineAsync(admin, v1.Id, 100m, cost: 60m);
        var changed = await AddLineAsync(admin, v1.Id, 200m, quantity: 1m);
        var removed = await AddLineAsync(admin, v1.Id, 50m);
        await PresentAsync(admin, v1.Id);

        var revise = await admin.PostAsJsonAsync($"/api/crm/quotes/{v1.Id}/revise", new ReviseQuoteRequest("Fiyat güncellendi"));
        var v2 = (await revise.Content.ReadFromJsonAsync<QuoteDto>())!;
        var v2Lines = (await admin.GetFromJsonAsync<CrmPage<QuoteLineDto>>($"/api/crm/quote-lines?quoteId={v2.Id}"))!.Items;
        var v2Changed = v2Lines.Single(l => l.StableKey == changed.StableKey);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/crm/quote-lines/{v2Changed.Id}", ToSave(v2Changed) with { Quantity = 4m, UnitPrice = 210m })).StatusCode);
        var v2Removed = v2Lines.Single(l => l.StableKey == removed.StableKey);
        Assert.True((await admin.DeleteAsync($"/api/crm/quote-lines/{v2Removed.Id}?version={v2Removed.Version}")).IsSuccessStatusCode);
        await AddLineAsync(admin, v2.Id, 75m);

        var response = await admin.GetAsync($"/api/crm/quotes/{v2.Id}/compare?with={v1.Id}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var comparison = (await response.Content.ReadFromJsonAsync<QuoteComparisonDto>())!;
        Assert.Equal((1, 2), (comparison.LeftRevision, comparison.RightRevision));
        Assert.Equal("Unchanged", comparison.Lines.Single(l => l.StableKey == kept.StableKey).Change);
        var line = comparison.Lines.Single(l => l.StableKey == changed.StableKey);
        Assert.Equal("Changed", line.Change);
        Assert.Contains(line.Fields, f => f.Field == "quantity" && f.Before == "1" && f.After == "4");
        Assert.Equal("Removed", comparison.Lines.Single(l => l.StableKey == removed.StableKey).Change);
        Assert.Equal(1, comparison.Lines.Count(l => l.Change == "Added"));
        Assert.Contains(comparison.Totals, t => t.Field == "grossTotal");

        // Maliyet hiçbir biçimde dönmez.
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("unitCost", raw, StringComparison.OrdinalIgnoreCase);

        // Aynı aile dışı ve aynı sürümle karşılaştırma reddedilir.
        var other = await NewQuoteAsync(admin, s.CompanyId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.GetAsync($"/api/crm/quotes/{v2.Id}/compare?with={other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.GetAsync($"/api/crm/quotes/{v2.Id}/compare?with={v2.Id}")).StatusCode);

        // Başka tenant'ın kullanıcısı teklifleri göremez.
        var otherTenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(otherTenant.Id, "crm", enabled: true);
        var stranger = await factory.CreateUserAsync(otherTenant);
        await factory.CreateRoleAsync(otherTenant.Id, [QuotesRead], Scope.All, stranger);
        using var foreign = await LoginAsync(stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/crm/quotes/{v2.Id}/compare?with={v1.Id}")).StatusCode);
    }

    // ---- yardımcılar

    private static SaveQuoteLineRequest ToSave(QuoteLineDto l) => new(
        l.QuoteId, l.Name, l.Kind, l.ProductId, l.Quantity, l.UnitPrice, l.UnitId, l.DiscountKind, l.DiscountValue, l.TaxRatePercent, l.TaxCode,
        l.Role, l.IsSelected, l.ParentStableKey, l.RecurringPeriod, l.Description, l.GroupName, l.Position, l.UnitCost, l.Version);

    private static async Task<QuoteApprovalRequirementDto> Requirement(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<QuoteApprovalRequirementDto>($"/api/crm/quotes/{id}/approval-requirements"))!;

    private static async Task<QuoteDto> NewQuoteAsync(HttpClient client, Guid companyId) =>
        await PostAsync<QuoteDto>(client, "/api/crm/quotes", new SaveQuoteRequest("Teklif", companyId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));

    private static async Task<QuoteLineDto> AddLineAsync(
        HttpClient client, Guid quoteId, decimal price, decimal quantity = 1m, string discountKind = "None", decimal discount = 0m, decimal? cost = null) =>
        await PostAsync<QuoteLineDto>(client, "/api/crm/quote-lines",
            new SaveQuoteLineRequest(quoteId, "Kalem", "Free", null, quantity, price, null, discountKind, discount, 0m, null, "Standard", UnitCost: cost));

    private static async Task<QuoteDto> GetQuoteAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;

    private static async Task<QuoteDto> PresentAsync(HttpClient client, Guid id) => await TransitionAsync(client, id, "present");

    private static async Task<QuoteDto> TransitionAsync(HttpClient client, Guid id, string action)
    {
        var response = await client.PostAsJsonAsync($"/api/crm/quotes/{id}/{action}", new QuoteTransitionRequest((await GetQuoteAsync(client, id)).Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<QuoteDto>())!;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(ModulixUser Admin, ModulixUser Rep, ModulixUser ApproverA, ModulixUser ApproverB, Guid CompanyId)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var rep = await factory.CreateUserAsync(tenant);
        var approverA = await factory.CreateUserAsync(tenant);
        var approverB = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, RepPermissions, Scope.All, rep);
        await factory.CreateRoleAsync(tenant.Id, ApproverPermissions, Scope.All, approverA, approverB);
        using var client = await LoginAsync(admin);
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        return (admin, rep, approverA, approverB, company.Id);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
