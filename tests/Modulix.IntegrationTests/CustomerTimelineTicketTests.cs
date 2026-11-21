using System.Net;
using System.Net.Http.Json;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Support;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// Müşteri zaman çizelgesinin destek talebi kaynağı (ADR-0053, dilim 2c): müşterinin talepleri CRM kayıtlarıyla aynı akışta görünür. Kaynak
/// Destek modülündedir; satırlar talebin kendi izni, veri kapsamı ve lisansıyla süzülür.
/// </summary>
public class CustomerTimelineTicketTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string TicketsSource = "support.tickets";

    [Fact]
    public async Task Timeline_includes_the_customers_tickets_next_to_crm_records()
    {
        var (_, admin) = await TenantWithAdminAsync(support: true);
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Destek A.Ş.");
        var opportunity = await client.PostAsJsonAsync("/api/crm/opportunities",
            new SaveOpportunityRequest("Bakım fırsatı", company.Id, Stage: "Lead", Amount: 100m, Currency: "TRY"));
        Assert.Equal(HttpStatusCode.Created, opportunity.StatusCode);
        var ticket = await CreateTicketAsync(client, company.PartyId!.Value, "Cihaz açılmıyor");

        // Başka müşterinin talebi çizelgeye girmez.
        var other = await CreateCompanyAsync(client, "Başka Ltd.");
        await CreateTicketAsync(client, other.PartyId!.Value, "İlgisiz talep");

        var timeline = (await client.GetFromJsonAsync<CustomerTimelineDto>($"/api/crm/companies/{company.Id}/timeline"))!;
        Assert.Contains(TicketsSource, timeline.Sources);
        Assert.Equal([SupportAccess.TicketEntityKey, CrmEntityKeys.Opportunity], timeline.Items.Select(i => i.EntityKey));

        // Talep fırsattan sonra açıldı: en üstteki satırdır.
        var row = timeline.Items[0];
        Assert.Equal(ticket.Id, row.RecordId);
        Assert.Equal("Cihaz açılmıyor", row.Title);
        Assert.Equal(ticket.Number, row.Reference);
        Assert.Equal(ticket.Status, row.Status);

        // İmleç talep satırını da kapsar.
        var first = (await client.GetFromJsonAsync<CustomerTimelineDto>($"/api/crm/companies/{company.Id}/timeline?take=1"))!;
        Assert.Equal(ticket.Id, Assert.Single(first.Items).RecordId);
        var before = Uri.EscapeDataString(first.NextBefore!.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var second = (await client.GetFromJsonAsync<CustomerTimelineDto>($"/api/crm/companies/{company.Id}/timeline?take=1&before={before}"))!;
        Assert.Equal(CrmEntityKeys.Opportunity, Assert.Single(second.Items).EntityKey);
        Assert.Null(second.NextBefore);
    }

    [Fact]
    public async Task Timeline_has_no_ticket_source_when_the_support_module_is_off()
    {
        var (_, admin) = await TenantWithAdminAsync(support: false);
        using var client = await LoginAsync(admin);
        var company = await CreateCompanyAsync(client, "Modülsüz A.Ş.");

        var timeline = (await client.GetFromJsonAsync<CustomerTimelineDto>($"/api/crm/companies/{company.Id}/timeline"))!;
        Assert.DoesNotContain(TicketsSource, timeline.Sources);
        Assert.Empty(timeline.Items);
    }

    [Fact]
    public async Task Timeline_never_shows_another_tenants_tickets()
    {
        var (_, adminA) = await TenantWithAdminAsync(support: true);
        var (_, adminB) = await TenantWithAdminAsync(support: true);
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        var companyA = await CreateCompanyAsync(clientA, "A Tenant A.Ş.");
        await CreateTicketAsync(clientA, companyA.PartyId!.Value, "A'nın talebi");
        var companyB = await CreateCompanyAsync(clientB, "B Tenant A.Ş.");

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/crm/companies/{companyA.Id}/timeline")).StatusCode);
        var own = (await clientB.GetFromJsonAsync<CustomerTimelineDto>($"/api/crm/companies/{companyB.Id}/timeline"))!;
        Assert.Contains(TicketsSource, own.Sources);
        Assert.Empty(own.Items);
    }

    [Fact]
    public async Task Timeline_ticket_rows_follow_the_ticket_permission_and_data_scope()
    {
        var (tenant, admin) = await TenantWithAdminAsync(support: true);
        using var adminClient = await LoginAsync(admin);
        var company = await CreateCompanyAsync(adminClient, "Yetki A.Ş.");
        await CreateTicketAsync(adminClient, company.PartyId!.Value, "Yöneticinin talebi");

        // Talep okuma izni yok: kaynak da satır da yok.
        var crmOnly = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], Scope.All, crmOnly);
        using var crmOnlyClient = await LoginAsync(crmOnly);
        var withoutTickets = (await crmOnlyClient.GetFromJsonAsync<CustomerTimelineDto>($"/api/crm/companies/{company.Id}/timeline"))!;
        Assert.DoesNotContain(TicketsSource, withoutTickets.Sources);
        Assert.Empty(withoutTickets.Items);

        // İzin var, kapsam "kendi": kaynak okunur; sahipsiz talep kuyrukta herkese görünür (Destek kuralı), başkasının üstlendiği talep gelmez.
        var ownOnly = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, CompaniesWrite, SupportAccess.TicketsRead], Scope.Own, ownOnly);
        using var ownOnlyClient = await LoginAsync(ownOnly);
        var ownCompany = await CreateCompanyAsync(ownOnlyClient, "Temsilcinin firması");
        var adminTicket = await CreateTicketAsync(adminClient, ownCompany.PartyId!.Value, "Yöneticinin açtığı talep");
        var queued = (await ownOnlyClient.GetFromJsonAsync<CustomerTimelineDto>($"/api/crm/companies/{ownCompany.Id}/timeline"))!;
        Assert.Equal(adminTicket.Id, Assert.Single(queued.Items).RecordId);
        var taken = await adminClient.PostAsJsonAsync($"/api/support/tickets/{adminTicket.Id}/take", new TicketActionRequest(adminTicket.Version));
        Assert.True(taken.StatusCode == HttpStatusCode.OK, await taken.Content.ReadAsStringAsync());
        var scoped = (await ownOnlyClient.GetFromJsonAsync<CustomerTimelineDto>($"/api/crm/companies/{ownCompany.Id}/timeline"))!;
        Assert.Contains(TicketsSource, scoped.Sources);
        Assert.Empty(scoped.Items);

        // Kapsam "tümü": talep satırı gelir.
        var reader = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, SupportAccess.TicketsRead], Scope.All, reader);
        using var readerClient = await LoginAsync(reader);
        var visible = (await readerClient.GetFromJsonAsync<CustomerTimelineDto>($"/api/crm/companies/{company.Id}/timeline"))!;
        Assert.Equal([SupportAccess.TicketEntityKey], visible.Items.Select(i => i.EntityKey));
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin)> TenantWithAdminAsync(bool support)
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        if (support)
        {
            await factory.SetModuleAsync(tenant.Id, "support", enabled: true);
        }

        var admin = await factory.CreateUserAsync(tenant);
        await factory.MakeAdministratorAsync(tenant.Id, admin);
        return (tenant, admin);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<CompanyDto> CreateCompanyAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompanyDto>())!;
    }

    private static async Task<TicketDto> CreateTicketAsync(HttpClient client, Guid partyId, string subject)
    {
        var response = await client.PostAsJsonAsync("/api/support/tickets", new SaveTicketRequest(Subject: subject, PartyId: partyId));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDto>())!;
    }
}
