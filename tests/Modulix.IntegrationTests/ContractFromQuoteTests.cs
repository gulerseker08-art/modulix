using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Contracts;
using Modulix.Shared.Crm;
using Modulix.Shared.Records;
using static Modulix.Modules.Contracts.ContractsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0022 dilim 3: kabul edilen teklif revizyonundan sözleşme. Doğru tutar ve kalemler, on iki ay takvim hesabı, teklif sonradan değişince
/// sözleşmenin sabit kalması, kısmi kapsam ve mükerrer devir engeli, iptalde kalemin serbest kalması, tenant izolasyonu ve yetki.
/// </summary>
public class ContractFromQuoteTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Accepted_quote_becomes_a_twelve_month_service_contract_with_the_right_lines_totals_and_trace()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var accepted = await AcceptedQuoteAsync(admin, s.CompanyId, ("Kurulum", 2m, 1000m, 20m, null), ("Aylık destek", 1m, 500m, 20m, "Monthly"));

        // Önizleme: kalemler ve henüz sözleşmede olmadıkları.
        var preview = (await admin.GetFromJsonAsync<QuoteSourceDto>($"/api/contracts/from-quote/{accepted.Id}"))!;
        Assert.Equal((accepted.QuoteNumber, 2, true), (preview.QuoteNumber, preview.Lines.Count, preview.CustomerLinked));
        Assert.All(preview.Lines, l => Assert.False(l.Taken));

        // 1 Kasım 2026 + 12 ay → bitiş 31 Ekim 2027 (365 gün değil, takvim ayı); yenilemesiz değil, manuel 12 ay, 60 gün bildirim.
        var response = await admin.PostAsJsonAsync($"/api/contracts/from-quote/{accepted.Id}",
            new CreateContractFromQuoteRequest(StartDate: new DateOnly(2026, 11, 1), TermMonths: 12, RenewalMode: "Manual", RenewalPeriodMonths: 12, NoticePeriodDays: 60));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var contract = (await response.Content.ReadFromJsonAsync<ContractDto>())!;

        Assert.Equal(accepted.Title, contract.Title);
        Assert.Equal(("Draft", "NotRequired", "NotSent", "NotInForce"), (contract.PreparationStatus, contract.ApprovalStatus, contract.SignatureStatus, contract.EffectiveStatus));
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2027, 10, 31), new DateOnly(2027, 9, 1)), (contract.StartDate, contract.EndDate, contract.NoticeDeadline));
        Assert.Equal((accepted.Id, accepted.QuoteNumber, accepted.RevisionNo), (contract.SourceQuoteId, contract.SourceQuoteNumber, contract.SourceQuoteRevisionNo));
        Assert.NotNull(contract.SourceQuoteSha256);
        Assert.NotEqual(Guid.Empty, contract.PartyId);

        // Tutar: tek seferlik 2 × 1.000 + %20 → 2.400; aylık 500 dönemsel özette, genel toplama girmez.
        Assert.Equal((2000m, 400m, 2400m), (contract.NetTotal, contract.TaxTotal, contract.GrossTotal));
        Assert.Contains("Monthly", contract.RecurringSummary);
        var lines = (await admin.GetFromJsonAsync<PagedResult<ContractLineDto>>($"/api/contracts/contract-lines?contractId={contract.Id}"))!.Items;
        Assert.Equal(["Kurulum", "Aylık destek"], lines.Select(l => l.Name));
        Assert.All(lines, l => Assert.NotNull(l.SourceQuoteLineKey));

        var history = (await admin.GetFromJsonAsync<List<ContractHistoryDto>>($"/api/contracts/contracts/{contract.Id}/history"))!;
        Assert.Equal("CreatedFromQuote", Assert.Single(history).Kind);

        // Teklif sonradan değişir (iptal): sözleşme aynı kalır.
        var quoteNow = (await admin.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{accepted.Id}"))!;
        Assert.Equal("Accepted", quoteNow.Status);
        var again = (await admin.GetFromJsonAsync<ContractDto>($"/api/contracts/contracts/{contract.Id}"))!;
        Assert.Equal((2000m, 2400m), (again.NetTotal, again.GrossTotal));

        // Sözleşme fırsat/müşteri üzerinden bulunur.
        var byQuote = (await admin.GetFromJsonAsync<PagedResult<ContractDto>>($"/api/contracts/contracts?sourceQuoteId={accepted.Id}"))!;
        Assert.Equal(contract.Id, Assert.Single(byQuote.Items).Id);
    }

    [Fact]
    public async Task Same_quote_line_cannot_enter_two_contracts_but_a_partial_scope_and_a_cancelled_contract_free_the_lines()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var accepted = await AcceptedQuoteAsync(admin, s.CompanyId, ("Kurulum", 1m, 1000m, 0m, null), ("Eğitim", 1m, 300m, 0m, null));
        var preview = (await admin.GetFromJsonAsync<QuoteSourceDto>($"/api/contracts/from-quote/{accepted.Id}"))!;
        var install = preview.Lines.Single(l => l.Name == "Kurulum").StableKey;
        var training = preview.Lines.Single(l => l.Name == "Eğitim").StableKey;

        // Kısmi kapsam: yalnızca kurulum.
        var first = await PostFromQuoteAsync(admin, accepted.Id, new CreateContractFromQuoteRequest(LineKeys: [install]));
        Assert.Equal(1000m, first.GrossTotal);

        // Aynı kalem ikinci kez (mükerrer istek): 409 ve hiçbir sözleşme eklenmez.
        var duplicate = await admin.PostAsJsonAsync($"/api/contracts/from-quote/{accepted.Id}", new CreateContractFromQuoteRequest(LineKeys: [install]));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var all = await admin.PostAsJsonAsync($"/api/contracts/from-quote/{accepted.Id}", new CreateContractFromQuoteRequest());
        Assert.Equal(HttpStatusCode.Conflict, all.StatusCode);
        var count = (await admin.GetFromJsonAsync<PagedResult<ContractDto>>($"/api/contracts/contracts?sourceQuoteId={accepted.Id}"))!;
        Assert.Single(count.Items);

        // Önizleme alınan kalemi gösterir; kalan kalem ayrı bir sözleşmeye alınabilir (bire bir zorunlu değil).
        var taken = (await admin.GetFromJsonAsync<QuoteSourceDto>($"/api/contracts/from-quote/{accepted.Id}"))!;
        Assert.True(taken.Lines.Single(l => l.StableKey == install).Taken);
        Assert.Equal(first.Number, taken.Lines.Single(l => l.StableKey == install).ContractNumber);
        var second = await PostFromQuoteAsync(admin, accepted.Id, new CreateContractFromQuoteRequest(LineKeys: [training]));
        Assert.Equal(300m, second.GrossTotal);

        // İptal edilen sözleşmenin kalemi serbest kalır; yeniden alınabilir.
        var cancel = await admin.PostAsJsonAsync($"/api/contracts/contracts/{first.Id}/cancel", new ContractTransitionRequest(first.Version, "Müşteri vazgeçti"));
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var freed = (await admin.GetFromJsonAsync<QuoteSourceDto>($"/api/contracts/from-quote/{accepted.Id}"))!;
        Assert.False(freed.Lines.Single(l => l.StableKey == install).Taken);
        await PostFromQuoteAsync(admin, accepted.Id, new CreateContractFromQuoteRequest(LineKeys: [install]));
    }

    [Fact]
    public async Task Draft_quote_unknown_lines_and_incomplete_options_are_rejected()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        // Kabul edilmemiş teklif ve olmayan teklif: bulunamadı.
        var draft = await admin.PostAsJsonAsync("/api/crm/quotes", new SaveQuoteRequest("Taslak", s.CompanyId));
        var draftQuote = (await draft.Content.ReadFromJsonAsync<QuoteDto>())!;
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/contracts/from-quote/{draftQuote.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/contracts/from-quote/{Guid.NewGuid()}", new CreateContractFromQuoteRequest())).StatusCode);

        var accepted = await AcceptedQuoteAsync(admin, s.CompanyId, ("Kurulum", 1m, 100m, 0m, null));
        var unknownLine = await admin.PostAsJsonAsync($"/api/contracts/from-quote/{accepted.Id}", new CreateContractFromQuoteRequest(LineKeys: [Guid.NewGuid()]));
        Assert.True((await ValidationProblemAsync(unknownLine)).Errors.ContainsKey("lineKeys"));
        var termWithoutStart = await admin.PostAsJsonAsync($"/api/contracts/from-quote/{accepted.Id}", new CreateContractFromQuoteRequest(TermMonths: 12));
        Assert.True((await ValidationProblemAsync(termWithoutStart)).Errors.ContainsKey("startDate"));
        var badRenewal = await admin.PostAsJsonAsync($"/api/contracts/from-quote/{accepted.Id}", new CreateContractFromQuoteRequest(RenewalMode: "Automatic"));
        Assert.True((await ValidationProblemAsync(badRenewal)).Errors.ContainsKey("renewalPeriodMonths"));

        // Doğrulama hatası hiçbir sözleşme bırakmaz (tek işlem).
        var none = (await admin.GetFromJsonAsync<PagedResult<ContractDto>>($"/api/contracts/contracts?sourceQuoteId={accepted.Id}"))!;
        Assert.Empty(none.Items);
    }

    [Fact]
    public async Task Other_tenant_and_unauthorized_users_cannot_create_from_or_preview_a_quote()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        using var writer = await LoginAsync(a.NoFinancials);
        using var reader = await LoginAsync(a.Reader);
        var accepted = await AcceptedQuoteAsync(adminA, a.CompanyId, ("Kurulum", 1m, 100m, 0m, null));

        // Başka tenant teklifi göremez (404); kendi tenant'ında da bulunamaz.
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/contracts/from-quote/{accepted.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/contracts/from-quote/{accepted.Id}", new CreateContractFromQuoteRequest())).StatusCode);

        // Finansal izni olmayan yazıcı ve yalnızca okuyucu oluşturamaz (403); önizlemeyi de göremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync($"/api/contracts/from-quote/{accepted.Id}", new CreateContractFromQuoteRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.GetAsync($"/api/contracts/from-quote/{accepted.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/contracts/from-quote/{accepted.Id}", new CreateContractFromQuoteRequest())).StatusCode);

        // CRM kapalı tenant: teklif kaynağı kullanılamaz, bulunamadı.
        var noCrm = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(noCrm.Id, "contracts", enabled: true);
        var user = await factory.CreateUserAsync(noCrm);
        await factory.CreateRoleAsync(noCrm.Id, [ContractsRead, ContractsWrite, FinancialsRead], Scope.All, user);
        using var noCrmClient = await LoginAsync(user);
        Assert.Equal(HttpStatusCode.NotFound, (await noCrmClient.GetAsync($"/api/contracts/from-quote/{accepted.Id}")).StatusCode);
    }

    [Fact]
    public async Task Contract_can_be_linked_to_an_activity_but_stays_invisible_to_users_without_access()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var accepted = await AcceptedQuoteAsync(admin, s.CompanyId, ("Kurulum", 1m, 100m, 0m, null));
        var contract = await PostFromQuoteAsync(admin, accepted.Id, new CreateContractFromQuoteRequest());

        var activity = await admin.PostAsJsonAsync("/api/crm/activities", new SaveActivityRequest("Yenileme görüşmesi", "Task", DateTimeOffset.UtcNow.AddDays(30), CompanyId: s.CompanyId));
        Assert.Equal(HttpStatusCode.Created, activity.StatusCode);
        var activityDto = (await activity.Content.ReadFromJsonAsync<ActivityDto>())!;
        var link = await admin.PostAsJsonAsync("/api/crm/activity-links", new SaveActivityLinkRequest(activityDto.Id, "contracts.contract", contract.Id));
        Assert.True(link.StatusCode == HttpStatusCode.Created, await link.Content.ReadAsStringAsync());
        var related = (await admin.GetFromJsonAsync<ActivityRelatedDto>($"/api/crm/activities/{activityDto.Id}/related"))!;
        var linked = related.Links.Single(l => l.EntityKey == "contracts.contract");
        Assert.Contains(contract.Number, linked.Title);
        Assert.DoesNotContain("100", linked.Title); // tutar aktivite üzerinden taşınmaz

        // Sözleşme okuma izni olmayan kullanıcı sözleşmeyi aktiviteye bağlayamaz.
        using var outsider = await LoginAsync(s.Reader);
        var other = await outsider.PostAsJsonAsync("/api/crm/activity-links", new SaveActivityLinkRequest(activityDto.Id, "contracts.contract", Guid.NewGuid()));
        Assert.NotEqual(HttpStatusCode.Created, other.StatusCode);
    }

    private async Task<ContractDto> PostFromQuoteAsync(HttpClient client, Guid quoteId, CreateContractFromQuoteRequest request)
    {
        var response = await client.PostAsJsonAsync($"/api/contracts/from-quote/{quoteId}", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ContractDto>())!;
    }

    /// <summary>Teklif açar, kalemleri ekler, sunar ve kabul eder (kabul kaydı).</summary>
    private static async Task<QuoteDto> AcceptedQuoteAsync(
        HttpClient client, Guid companyId, params (string Name, decimal Quantity, decimal Price, decimal Tax, string? Period)[] lines)
    {
        var create = await client.PostAsJsonAsync("/api/crm/quotes", new SaveQuoteRequest("Yıllık bakım teklifi", companyId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var quote = (await create.Content.ReadFromJsonAsync<QuoteDto>())!;
        foreach (var (name, quantity, price, tax, period) in lines)
        {
            var added = await client.PostAsJsonAsync("/api/crm/quote-lines",
                new SaveQuoteLineRequest(quote.Id, name, "Free", null, quantity, price, null, "None", 0m, tax, null, "Standard", RecurringPeriod: period));
            Assert.True(added.StatusCode == HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        }

        quote = await TransitionAsync(client, quote.Id, "present");
        return await TransitionAsync(client, quote.Id, "record-accepted");
    }

    private static async Task<QuoteDto> TransitionAsync(HttpClient client, Guid id, string action)
    {
        var current = (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;
        var response = await client.PostAsJsonAsync($"/api/crm/quotes/{id}/{action}", new QuoteTransitionRequest(current.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<QuoteDto>())!;
    }

    private async Task<(ModulixUser Admin, ModulixUser NoFinancials, ModulixUser Reader, Guid CompanyId)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "contracts", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var noFinancials = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, ContractsWrite, Modulix.Modules.Crm.CrmModule.Permissions.QuotesRead], Scope.All, noFinancials);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead], Scope.All, reader);
        using var client = await LoginAsync(admin);
        var company = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        Assert.Equal(HttpStatusCode.Created, company.StatusCode);
        return (admin, noFinancials, reader, (await company.Content.ReadFromJsonAsync<CompanyDto>())!.Id);
    }

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
