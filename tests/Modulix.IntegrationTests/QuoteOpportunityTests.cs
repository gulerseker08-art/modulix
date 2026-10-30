using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Events;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Settings;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>ADR-0019 dilim 9: fırsatın esas teklifi, sunumda otomatik esas, ayara bağlı tutar eşitleme, esas teklif seçimi ve kuralları.</summary>
public class QuoteOpportunityTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task The_first_presented_quote_becomes_primary_and_the_amount_follows_only_when_the_setting_is_on()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var opportunity = await PostAsync<OpportunityDto>(admin, "/api/crm/opportunities", new SaveOpportunityRequest("Fırsat", s.CompanyId, Amount: 5000m, Currency: "TRY"));

        // Ayar kapalı: esas teklif bağlanır, tutar elle girilmiş haliyle kalır.
        var first = await PresentedQuoteAsync(admin, s.CompanyId, opportunity.Id, 1000m);
        await DrainAsync(s.TenantId);
        var after = await GetOpportunityAsync(admin, opportunity.Id);
        Assert.Equal(first.Id, after.PrimaryQuoteId);
        Assert.Equal(5000m, after.Amount);
        Assert.Contains((await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{first.Id}/history"))!, h => h.Kind == "OpportunitySynced");

        // İkinci (farklı aile) teklif sunulunca esas değişmez.
        var second = await PresentedQuoteAsync(admin, s.CompanyId, opportunity.Id, 2000m);
        await DrainAsync(s.TenantId);
        Assert.Equal(first.Id, (await GetOpportunityAsync(admin, opportunity.Id)).PrimaryQuoteId);

        // Ayar açık + esas teklif elle değiştirilince tutar net toplamla eşitlenir (2 × 2.000 − %10 = 3.600 net).
        await SetSettingAsync(admin, "crm.quote.sync-opportunity-amount", "true");
        var set = await admin.PostAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}/primary-quote", new SetPrimaryQuoteRequest((await GetOpportunityAsync(admin, opportunity.Id)).Version, second.Id));
        Assert.True(set.StatusCode == HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
        var synced = (await set.Content.ReadFromJsonAsync<OpportunityDto>())!;
        Assert.Equal((second.Id, 3600m, "TRY"), (synced.PrimaryQuoteId, synced.Amount, synced.Currency));

        // Esas teklifin yeni revizyonu sunulunca esas yeni revizyona geçer ve tutar onu izler.
        var revise = await admin.PostAsJsonAsync($"/api/crm/quotes/{second.Id}/revise", new ReviseQuoteRequest("Fiyat güncellendi"));
        var v2 = (await revise.Content.ReadFromJsonAsync<QuoteDto>())!;
        var line = (await admin.GetFromJsonAsync<CrmPage<QuoteLineDto>>($"/api/crm/quote-lines?quoteId={v2.Id}"))!.Items.Single();
        Assert.True((await admin.PutAsJsonAsync($"/api/crm/quote-lines/{line.Id}", Save(line) with { UnitPrice = 1500m })).IsSuccessStatusCode);
        await TransitionAsync(admin, v2.Id, "present");
        await DrainAsync(s.TenantId);
        var moved = await GetOpportunityAsync(admin, opportunity.Id);
        Assert.Equal((v2.Id, 2700m), (moved.PrimaryQuoteId, moved.Amount));

        // Teslim tekrarı ikinci bir değişiklik yapmaz.
        await ReplayAllDeliveriesAsync(s.TenantId);
        await DrainAsync(s.TenantId);
        Assert.Equal(moved.Version, (await GetOpportunityAsync(admin, opportunity.Id)).Version);
    }

    [Fact]
    public async Task Primary_quote_selection_is_validated_and_needs_write_permission()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var viewer = await LoginAsync(s.Viewer);
        var opportunity = await PostAsync<OpportunityDto>(admin, "/api/crm/opportunities", new SaveOpportunityRequest("Fırsat", s.CompanyId));
        var other = await PostAsync<OpportunityDto>(admin, "/api/crm/opportunities", new SaveOpportunityRequest("Başka fırsat", s.CompanyId));
        var draft = await PostAsync<QuoteDto>(admin, "/api/crm/quotes", new SaveQuoteRequest("Taslak", s.CompanyId, OpportunityId: opportunity.Id));
        var foreign = await PresentedQuoteAsync(admin, s.CompanyId, other.Id, 100m);
        var presented = await PresentedQuoteAsync(admin, s.CompanyId, opportunity.Id, 100m);
        await DrainAsync(s.TenantId);

        async Task<HttpStatusCode> TrySet(HttpClient client, Guid? quoteId) =>
            (await client.PostAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}/primary-quote",
                new SetPrimaryQuoteRequest((await GetOpportunityAsync(admin, opportunity.Id)).Version, quoteId))).StatusCode;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, await TrySet(admin, draft.Id));     // taslak esas olamaz
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await TrySet(admin, foreign.Id));   // başka fırsatın teklifi
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await TrySet(admin, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, await TrySet(viewer, presented.Id));          // yalnızca okuma izni

        // Kaldırma ve yeniden seçme; sürüm çakışması 409.
        Assert.Equal(HttpStatusCode.OK, await TrySet(admin, null));
        Assert.Null((await GetOpportunityAsync(admin, opportunity.Id)).PrimaryQuoteId);
        Assert.Equal(HttpStatusCode.OK, await TrySet(admin, presented.Id));
        var stale = await admin.PostAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}/primary-quote", new SetPrimaryQuoteRequest(1, null));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // Esas teklif alanı genel güncelleme ucuyla yazılamaz (sistem alanı).
        var current = await GetOpportunityAsync(admin, opportunity.Id);
        var put = await admin.PutAsJsonAsync($"/api/crm/opportunities/{opportunity.Id}", new SaveOpportunityRequest("Fırsat", s.CompanyId, Version: current.Version));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(presented.Id, (await GetOpportunityAsync(admin, opportunity.Id)).PrimaryQuoteId);
    }

    // ---- yardımcılar

    private static SaveQuoteLineRequest Save(QuoteLineDto l) => new(
        l.QuoteId, l.Name, l.Kind, l.ProductId, l.Quantity, l.UnitPrice, l.UnitId, l.DiscountKind, l.DiscountValue, l.TaxRatePercent, l.TaxCode,
        l.Role, l.IsSelected, l.ParentStableKey, l.RecurringPeriod, l.Description, l.GroupName, l.Position, l.UnitCost, l.Version);

    private static async Task<OpportunityDto> GetOpportunityAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<OpportunityDto>($"/api/crm/opportunities/{id}"))!;

    private static async Task<QuoteDto> GetQuoteAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;

    private static async Task<QuoteDto> TransitionAsync(HttpClient client, Guid id, string action)
    {
        var response = await client.PostAsJsonAsync($"/api/crm/quotes/{id}/{action}", new QuoteTransitionRequest((await GetQuoteAsync(client, id)).Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<QuoteDto>())!;
    }

    private static async Task<QuoteDto> PresentedQuoteAsync(HttpClient client, Guid companyId, Guid opportunityId, decimal price)
    {
        var quote = await PostAsync<QuoteDto>(client, "/api/crm/quotes",
            new SaveQuoteRequest("Teklif", companyId, OpportunityId: opportunityId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        await PostAsync<QuoteLineDto>(client, "/api/crm/quote-lines",
            new SaveQuoteLineRequest(quote.Id, "Kalem", "Free", null, 2m, price, null, "Percent", 10m, 20m, null, "Standard"));
        return await TransitionAsync(client, quote.Id, "present");
    }

    private static async Task SetSettingAsync(HttpClient client, string key, string value)
    {
        var response = await client.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { [key] = new(value, 0) }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task DrainAsync(Guid tenantId)
    {
        for (var i = 0; i < 20; i++)
        {
            using var scope = factory.OpenScope(tenantId);
            var round = await scope.ServiceProvider.GetRequiredService<EventDispatcher>().RunOnceAsync(CancellationToken.None);
            if (round.Dispatched == 0 && round.Delivered == 0)
            {
                return;
            }
        }
    }

    private async Task ReplayAllDeliveriesAsync(Guid tenantId)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<Modulix.Infrastructure.Persistence.ModulixDbContext>();
        await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(
            db.Database, "DELETE FROM platform.EventDeliveries; UPDATE platform.OutboxMessages SET DispatchedAt = NULL");
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(ModulixUser Admin, ModulixUser Viewer, Guid CompanyId, Guid TenantId)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var viewer = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [OpportunitiesRead, OpportunityAmountsRead, QuotesRead, CompaniesRead], Scope.All, viewer);
        using var client = await LoginAsync(admin);
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        return (admin, viewer, company.Id, tenant.Id);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
