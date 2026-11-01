using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Contracts;
using Modulix.Shared.Records;
using Modulix.Shared.Support;
using static Modulix.Modules.Contracts.ContractsModule.Permissions;
using static Modulix.Modules.Support.SupportModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0024 dilim 5: talebin ilişkili kayıtları (sözleşme bağı çağıranın yetkisiyle doğrulanır, müşteri uyuşması, modül kapalıyken etiket okunur kalır), sözleşmeye bağlı SLA politikası
/// ve modül kapatma davranışı. Her özellik mutlu yol, doğrulama, yetki ve tenant izolasyonuyla sınanır.
/// </summary>
public class SupportIntegrationTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] AgentPermissions = [TicketsRead, TicketsCreate, TicketsWrite, TicketsTake, TicketsResolve, MessagesReply, NotesWrite, InternalRead];
    private static readonly string[] LeadPermissions = [.. AgentPermissions, TicketsAssign, TicketsClose, TicketsReopen, TicketsCancel, ContractsRead];
    private static readonly string[] ManagerPermissions = [.. LeadPermissions, SettingsManage, SlaManage, SlaRecalculate, "core.settings.manage"];

    private sealed record Setup(Tenant Tenant, Guid PartyId, Guid OtherPartyId, ModulixUser Manager, ModulixUser Lead, ModulixUser Agent, ModulixUser Agent2, ModulixUser Viewer, ModulixUser ContractAdmin);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "support", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "contracts", enabled: true);
        var manager = await factory.CreateUserAsync(tenant);
        var lead = await factory.CreateUserAsync(tenant);
        var agent = await factory.CreateUserAsync(tenant);
        var agent2 = await factory.CreateUserAsync(tenant);
        var viewer = await factory.CreateUserAsync(tenant);
        var contractAdmin = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, ManagerPermissions, Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, LeadPermissions, Scope.All, lead);
        // Sözleşme okuma izni olmayan temsilci.
        await factory.CreateRoleAsync(tenant.Id, AgentPermissions, Scope.Own, agent);
        // Sözleşme okuma izni var ama kapsamı "kendi": başkasının oluşturduğu sözleşmeyi göremez.
        await factory.CreateRoleAsync(tenant.Id, [.. AgentPermissions, ContractsRead], Scope.Own, agent2);
        await factory.CreateRoleAsync(tenant.Id, [TicketsRead], Scope.All, viewer);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, ContractsWrite], Scope.All, contractAdmin);

        Guid partyId, otherPartyId;
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var party = new Party { Name = "Entegrasyon Müşterisi A.Ş." };
            var other = new Party { Name = "Başka Müşteri Ltd." };
            db.Set<Party>().AddRange(party, other);
            await db.SaveChangesAsync();
            partyId = party.Id;
            otherPartyId = other.Id;
        }

        return new Setup(tenant, partyId, otherPartyId, manager, lead, agent, agent2, viewer, contractAdmin);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<ContractDto> ContractAsync(HttpClient client, Guid partyId, string title = "Bakım sözleşmesi")
    {
        var response = await client.PostAsJsonAsync("/api/contracts/contracts", new SaveContractRequest(title, partyId, StartDate: new DateOnly(2026, 1, 1), IsIndefinite: true));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ContractDto>())!;
    }

    private static async Task<T> CreateAsync<T>(HttpClient client, string path, object request)
    {
        var response = await client.PostAsJsonAsync($"/api/support/{path}", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<TicketDto> TicketAsync(HttpClient client, Guid? partyId, string subject = "Talep") =>
        CreateAsync<TicketDto>(client, "tickets", partyId is null ? new SaveTicketRequest(Subject: subject, ReporterName: "Telefonla arayan") : new SaveTicketRequest(Subject: subject, PartyId: partyId));

    private static async Task<TicketDto> GetAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<TicketDto>($"/api/support/tickets/{id}"))!;

    private static async Task<List<TicketHistoryDto>> HistoryAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<List<TicketHistoryDto>>($"/api/support/tickets/{id}/history"))!;

    private static Task<HttpResponseMessage> LinkAsync(HttpClient client, Guid ticketId, string entityKey, Guid recordId) =>
        client.PostAsJsonAsync($"/api/support/tickets/{ticketId}/links", new AddTicketLinkRequest(entityKey, recordId));

    private static async Task<List<TicketLinkDto>> LinksAsync(HttpClient client, Guid ticketId) =>
        (await client.GetFromJsonAsync<List<TicketLinkDto>>($"/api/support/tickets/{ticketId}/links"))!;

    private static async Task<List<SlaInstanceDto>> SlaAsync(HttpClient client, Guid ticketId) =>
        (await client.GetFromJsonAsync<List<SlaInstanceDto>>($"/api/support/tickets/{ticketId}/sla"))!;

    // ---- İlişkili kayıtlar

    [Fact]
    public async Task A_contract_of_the_same_customer_can_be_linked_listed_found_from_the_contract_side_and_unlinked()
    {
        var s = await SetUpAsync();
        using var lead = await LoginAsync(s.Lead);
        using var contracts = await LoginAsync(s.ContractAdmin);
        var contract = await ContractAsync(contracts, s.PartyId);
        var ticket = await TicketAsync(lead, s.PartyId, "Sözleşmeli talep");

        var response = await LinkAsync(lead, ticket.Id, "contracts.contract", contract.Id);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var link = Assert.Single(await response.Content.ReadFromJsonAsync<List<TicketLinkDto>>() ?? []);
        Assert.Equal(("contracts.contract", contract.Id), (link.EntityKey, link.RecordId));
        Assert.Equal($"{contract.Number} · {contract.Title}", link.Label);
        Assert.Contains(await HistoryAsync(lead, ticket.Id), h => h.Kind == "LinkAdded" && h.Reason!.Contains(contract.Number));

        // Alan olayı adı: diğer modüller ve abonelikler "support.<TarihçeOlayı>" nedenini görür (RecordUpdated olayının nedeni).
        using (var scope = factory.OpenScope(s.Tenant.Id))
        {
            var reasons = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().OutboxMessages.AsNoTracking().Where(o => o.RecordId == ticket.Id).Select(o => o.Reason).ToListAsync();
            Assert.Contains("support.LinkAdded", reasons);
        }

        // Karşı modülün sayfasındaki "ilişkili talepler" için ters arama.
        var found = (await lead.GetFromJsonAsync<PagedResult<TicketDto>>($"/api/support/tickets?linked=contracts.contract:{contract.Id}"))!;
        Assert.Equal(ticket.Id, Assert.Single(found.Items).Id);
        Assert.Empty((await lead.GetFromJsonAsync<PagedResult<TicketDto>>($"/api/support/tickets?linked=contracts.contract:{Guid.NewGuid()}"))!.Items);

        // Aynı kayıt ikinci kez bağlanmaz.
        Assert.Equal(HttpStatusCode.Conflict, (await LinkAsync(lead, ticket.Id, "contracts.contract", contract.Id)).StatusCode);

        var removed = await lead.DeleteAsync($"/api/support/tickets/{ticket.Id}/links/{link.Id}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty(await LinksAsync(lead, ticket.Id));
        Assert.Contains(await HistoryAsync(lead, ticket.Id), h => h.Kind == "LinkRemoved");
        Assert.Empty((await lead.GetFromJsonAsync<PagedResult<TicketDto>>($"/api/support/tickets?linked=contracts.contract:{contract.Id}"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await lead.DeleteAsync($"/api/support/tickets/{ticket.Id}/links/{link.Id}")).StatusCode);
    }

    [Fact]
    public async Task Links_are_validated_customer_must_match_the_record_must_exist_and_closed_tickets_stay_untouched()
    {
        var s = await SetUpAsync();
        using var lead = await LoginAsync(s.Lead);
        using var contracts = await LoginAsync(s.ContractAdmin);
        var mine = await ContractAsync(contracts, s.PartyId);
        var others = await ContractAsync(contracts, s.OtherPartyId, "Başka müşteri sözleşmesi");
        var ticket = await TicketAsync(lead, s.PartyId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LinkAsync(lead, ticket.Id, "contracts.contract", Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LinkAsync(lead, ticket.Id, "crm.company", mine.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LinkAsync(lead, ticket.Id, "contracts.contract", Guid.Empty)).StatusCode);
        var wrongCustomer = await LinkAsync(lead, ticket.Id, "contracts.contract", others.Id);
        Assert.Equal(HttpStatusCode.Conflict, wrongCustomer.StatusCode);
        Assert.Contains("müşteriye", await wrongCustomer.Content.ReadAsStringAsync());

        // Müşterisiz talebe kayıt bağlanmaz.
        var anonymous = await TicketAsync(lead, null, "Müşterisiz");
        var noParty = await LinkAsync(lead, anonymous.Id, "contracts.contract", mine.Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noParty.StatusCode);
        Assert.Contains("müşteri", await noParty.Content.ReadAsStringAsync());
        Assert.Empty(await LinksAsync(lead, ticket.Id));

        // Kapanmış talebin bağlantıları değişmez.
        Assert.Equal(HttpStatusCode.Created, (await LinkAsync(lead, ticket.Id, "contracts.contract", mine.Id)).StatusCode);
        var current = await GetAsync(lead, ticket.Id);
        Assert.Equal(HttpStatusCode.OK, (await lead.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/cancel", new TicketActionRequest(current.Version, "Vazgeçildi"))).StatusCode);
        var second = await ContractAsync(contracts, s.PartyId, "İkinci sözleşme");
        Assert.Equal(HttpStatusCode.Conflict, (await LinkAsync(lead, ticket.Id, "contracts.contract", second.Id)).StatusCode);
        var link = Assert.Single(await LinksAsync(lead, ticket.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await lead.DeleteAsync($"/api/support/tickets/{ticket.Id}/links/{link.Id}")).StatusCode);
    }

    [Fact]
    public async Task Linking_follows_the_callers_permissions_scope_tenant_and_a_closed_contracts_module_keeps_labels_readable()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var lead = await LoginAsync(s.Lead);
        using var agent = await LoginAsync(s.Agent);
        using var agent2 = await LoginAsync(s.Agent2);
        using var viewer = await LoginAsync(s.Viewer);
        using var contracts = await LoginAsync(s.ContractAdmin);
        using var foreign = await LoginAsync(other.Lead);
        using var foreignContracts = await LoginAsync(other.ContractAdmin);
        var contract = await ContractAsync(contracts, s.PartyId);
        var foreignContract = await ContractAsync(foreignContracts, other.PartyId);
        var ticket = await TicketAsync(lead, s.PartyId);
        var agentTicket = await TicketAsync(agent, s.PartyId, "Temsilci talebi");
        // Atanmamış talepler kuyrukta herkese görünür; kapsam denemesi için talebi sahiplenir.
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsJsonAsync($"/api/support/tickets/{agentTicket.Id}/take", new TicketActionRequest(agentTicket.Version))).StatusCode);
        var agent2Ticket = await TicketAsync(agent2, s.PartyId, "Temsilci 2 talebi");

        // Sözleşme okuma izni olmayan kullanıcı, kendi talebine bile o sözleşmeyi bağlayamaz (var olup olmadığı sızmaz).
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LinkAsync(agent, agentTicket.Id, "contracts.contract", contract.Id)).StatusCode);
        // Okuma izni olsa bile veri kapsamı dışındaki sözleşme bağlanamaz (kapsam karşı modülde de uygulanır).
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LinkAsync(agent2, agent2Ticket.Id, "contracts.contract", contract.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await LinkAsync(lead, agent2Ticket.Id, "contracts.contract", contract.Id)).StatusCode);

        // Talep düzenleme izni yok: 403; kapsam dışı talep: 404; okuyabilen herkes bağlantıları görür.
        Assert.Equal(HttpStatusCode.Forbidden, (await LinkAsync(viewer, ticket.Id, "contracts.contract", contract.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await LinkAsync(agent2, agentTicket.Id, "contracts.contract", contract.Id)).StatusCode);
        Assert.Single(await LinksAsync(viewer, agent2Ticket.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await agent2.GetAsync($"/api/support/tickets/{agentTicket.Id}/links")).StatusCode);

        // Tenant izolasyonu: başka tenant'ın sözleşmesi bağlanamaz, başka tenant talebine ulaşılamaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LinkAsync(lead, ticket.Id, "contracts.contract", foreignContract.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await LinkAsync(foreign, ticket.Id, "contracts.contract", foreignContract.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/support/tickets/{ticket.Id}/links")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.DeleteAsync($"/api/support/tickets/{agent2Ticket.Id}/links/{(await LinksAsync(lead, agent2Ticket.Id)).Single().Id}")).StatusCode);
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/support/tickets/{ticket.Id}/links")).StatusCode);

        // Sözleşme modülü kapanınca yeni bağ kurulamaz, mevcut bağın etiketi talepte okunur kalır; yeniden açılınca her şey yerindedir.
        var existing = (await LinksAsync(lead, agent2Ticket.Id)).Single();
        await factory.SetModuleAsync(s.Tenant.Id, "contracts", enabled: false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LinkAsync(lead, ticket.Id, "contracts.contract", contract.Id)).StatusCode);
        Assert.Equal(existing.Label, (await LinksAsync(lead, agent2Ticket.Id)).Single().Label);
        await factory.SetModuleAsync(s.Tenant.Id, "contracts", enabled: true);
        Assert.Equal(HttpStatusCode.Created, (await LinkAsync(lead, ticket.Id, "contracts.contract", contract.Id)).StatusCode);
    }

    // ---- Sözleşmeye bağlı SLA

    private static Task<HttpResponseMessage> PolicyAsync(HttpClient client, SaveSlaPolicyRequest request) => client.PostAsJsonAsync("/api/support/sla-policies", request);

    [Fact]
    public async Task A_contract_specific_sla_policy_applies_when_the_contract_is_linked_and_falls_back_when_it_is_removed()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var lead = await LoginAsync(s.Lead);
        using var contracts = await LoginAsync(s.ContractAdmin);
        var covered = await ContractAsync(contracts, s.PartyId, "Premium destek sözleşmesi");
        var plain = await ContractAsync(contracts, s.PartyId, "Standart sözleşme");
        await CreateAsync<SlaPolicyDto>(manager, "sla-policies", new SaveSlaPolicyRequest(Name: "Genel", IsDefault: true, ResolutionMinutes: 480));
        var premium = await CreateAsync<SlaPolicyDto>(manager, "sla-policies", new SaveSlaPolicyRequest(Name: "Premium", ContractId: covered.Id, ResolutionMinutes: 60));
        Assert.Equal(covered.Id, premium.ContractId);

        var ticket = await TicketAsync(lead, s.PartyId, "Premium müşteri talebi");
        var before = Assert.Single(await SlaAsync(lead, ticket.Id));
        Assert.Equal(("Genel", 480), (before.PolicyName, before.TargetMinutes));

        // Başka sözleşmeyi bağlamak politikayı değiştirmez; kapsamdaki sözleşmeyi bağlamak değiştirir ve tarihçeye yazar.
        Assert.Equal(HttpStatusCode.Created, (await LinkAsync(lead, ticket.Id, "contracts.contract", plain.Id)).StatusCode);
        Assert.Equal("Genel", Assert.Single(await SlaAsync(lead, ticket.Id)).PolicyName);
        var link = await LinkAsync(lead, ticket.Id, "contracts.contract", covered.Id);
        Assert.Equal(HttpStatusCode.Created, link.StatusCode);
        var after = Assert.Single(await SlaAsync(lead, ticket.Id));
        Assert.Equal(("Premium", 60), (after.PolicyName, after.TargetMinutes));
        Assert.Contains(await HistoryAsync(lead, ticket.Id), h => h.Kind == "LinkAdded" && h.Reason!.Contains("SLA politikası yeniden seçilir"));

        // Bağ kalkınca genel politikaya dönülür.
        var coveredLink = (await LinksAsync(lead, ticket.Id)).Single(l => l.RecordId == covered.Id);
        Assert.Equal(HttpStatusCode.OK, (await lead.DeleteAsync($"/api/support/tickets/{ticket.Id}/links/{coveredLink.Id}")).StatusCode);
        Assert.Equal(("Genel", 480), (Assert.Single(await SlaAsync(lead, ticket.Id)).PolicyName, Assert.Single(await SlaAsync(lead, ticket.Id)).TargetMinutes));

        // Önizleme sözleşmeyi dikkate alır.
        var preview = await manager.PostAsJsonAsync("/api/support/sla/preview", new SlaPreviewRequest("Support", "Normal", PartyId: s.PartyId, ContractId: covered.Id));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Contains("Premium", await preview.Content.ReadAsStringAsync());
        var plainPreview = await manager.PostAsJsonAsync("/api/support/sla/preview", new SlaPreviewRequest("Support", "Normal", PartyId: s.PartyId));
        Assert.Contains("Genel", await plainPreview.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Contract_policies_are_validated_against_the_callers_view_of_the_contract_and_stay_tenant_isolated()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var contracts = await LoginAsync(s.ContractAdmin);
        using var foreignContracts = await LoginAsync(other.ContractAdmin);
        using var viewer = await LoginAsync(s.Viewer);
        var contract = await ContractAsync(contracts, s.PartyId);
        var foreignContract = await ContractAsync(foreignContracts, other.PartyId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PolicyAsync(manager, new SaveSlaPolicyRequest(Name: "Yok", ContractId: Guid.NewGuid(), ResolutionMinutes: 60))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PolicyAsync(manager, new SaveSlaPolicyRequest(Name: "Sızıntı", ContractId: foreignContract.Id, ResolutionMinutes: 60))).StatusCode);
        var mismatch = await PolicyAsync(manager, new SaveSlaPolicyRequest(Name: "Çelişki", PartyId: s.OtherPartyId, ContractId: contract.Id, ResolutionMinutes: 60));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        Assert.Contains("müşteriye", await mismatch.Content.ReadAsStringAsync());
        var fallback = await PolicyAsync(manager, new SaveSlaPolicyRequest(Name: "Yedek", IsDefault: true, ContractId: contract.Id, ResolutionMinutes: 60));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fallback.StatusCode);

        // Doğru müşteriyle birlikte kabul edilir; politikayı yönetme izni olmayan yazamaz.
        Assert.Equal(HttpStatusCode.Created, (await PolicyAsync(manager, new SaveSlaPolicyRequest(Name: "Uyumlu", PartyId: s.PartyId, ContractId: contract.Id, ResolutionMinutes: 60))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PolicyAsync(viewer, new SaveSlaPolicyRequest(Name: "Yetkisiz", ContractId: contract.Id, ResolutionMinutes: 60))).StatusCode);

        // Sözleşme okuma izni olmayan yönetici sözleşmeye bağlı politika tanımlayamaz.
        var noRead = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.Tenant.Id, [TicketsRead, SlaManage], Scope.All, noRead);
        using var blind = await LoginAsync(noRead);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PolicyAsync(blind, new SaveSlaPolicyRequest(Name: "Kör", ContractId: contract.Id, ResolutionMinutes: 60))).StatusCode);
    }

    // ---- Modül kapatma

    [Fact]
    public async Task Closing_the_support_module_closes_its_api_but_keeps_every_ticket_and_link_for_when_it_is_reopened()
    {
        var s = await SetUpAsync();
        using var lead = await LoginAsync(s.Lead);
        using var contracts = await LoginAsync(s.ContractAdmin);
        var contract = await ContractAsync(contracts, s.PartyId);
        var ticket = await TicketAsync(lead, s.PartyId, "Kapanışta korunacak");
        await LinkAsync(lead, ticket.Id, "contracts.contract", contract.Id);

        await factory.SetModuleAsync(s.Tenant.Id, "support", enabled: false);
        Assert.NotEqual(HttpStatusCode.OK, (await lead.GetAsync($"/api/support/tickets/{ticket.Id}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await lead.GetAsync($"/api/support/tickets/{ticket.Id}/links")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Created, (await LinkAsync(lead, ticket.Id, "contracts.contract", contract.Id)).StatusCode);

        await factory.SetModuleAsync(s.Tenant.Id, "support", enabled: true);
        Assert.Equal("Kapanışta korunacak", (await GetAsync(lead, ticket.Id)).Subject);
        Assert.Equal(contract.Id, Assert.Single(await LinksAsync(lead, ticket.Id)).RecordId);
    }
}
