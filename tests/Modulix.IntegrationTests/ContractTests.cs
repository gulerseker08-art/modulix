using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Contracts;
using Modulix.Shared.Records;
using static Modulix.Modules.Contracts.ContractsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0022 ilk dilim: sözleşme başlığı, taraf ve kalem CRUD'u; sunucu toplamı ve son bildirim tarihi; tarih/ilişki doğrulamaları;
/// tenant izolasyonu; izin (yazma, finansal alan) ve veri kapsamı.
/// </summary>
public class ContractTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Parties = "/api/records/" + SharedEntities.Party;
    private const string People = "/api/records/" + SharedEntities.Person;

    [Fact]
    public async Task Contract_with_parties_and_lines_is_created_and_totals_and_notice_deadline_are_computed_by_the_server()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var created = await PostAsync<ContractDto>(admin, "/api/contracts/contracts", new SaveContractRequest(
            "Yıllık bakım hizmeti", s.PartyId, PersonId: s.PersonId, StartDate: new DateOnly(2026, 11, 1), EndDate: new DateOnly(2027, 10, 31),
            RenewalMode: "Manual", RenewalPeriodMonths: 12, NoticePeriodDays: 60, ContractValue: 50_000m));
        Assert.Matches("^SZ-", created.Number);
        Assert.Equal(("Draft", "NotRequired", "NotSent", "NotInForce"), (created.PreparationStatus, created.ApprovalStatus, created.SignatureStatus, created.EffectiveStatus));
        Assert.Equal(new DateOnly(2027, 9, 1), created.NoticeDeadline);
        Assert.Equal(s.Admin.Id, created.OwnerId);
        Assert.Equal("Service", created.Type);
        Assert.Equal(1, created.CurrentVersionNo);

        // 2 × 1.000, %10 indirim, %20 vergi → net 1.800, vergi 360, toplam 2.160; aylık 500 tek seferlik toplama girmez.
        var oneTime = await PostAsync<ContractLineDto>(admin, "/api/contracts/contract-lines",
            new SaveContractLineRequest(created.Id, "Kurulum", Quantity: 2m, UnitPrice: 1000m, DiscountPercent: 10m, TaxRatePercent: 20m));
        Assert.Equal((1800m, 360m, 2160m), (oneTime.Net, oneTime.Tax, oneTime.Gross));
        await PostAsync<ContractLineDto>(admin, "/api/contracts/contract-lines",
            new SaveContractLineRequest(created.Id, "Aylık destek", UnitPrice: 500m, TaxRatePercent: 20m, BillingPeriod: "Monthly"));

        var loaded = (await admin.GetFromJsonAsync<ContractDto>($"/api/contracts/contracts/{created.Id}"))!;
        Assert.Equal((1800m, 360m, 2160m), (loaded.NetTotal, loaded.TaxTotal, loaded.GrossTotal));
        Assert.Contains("Monthly", loaded.RecurringSummary);
        Assert.Contains("500", loaded.RecurringSummary);
        Assert.True(loaded.Version > created.Version);

        var party = await PostAsync<ContractPartyDto>(admin, "/api/contracts/contract-parties",
            new SaveContractPartyRequest(created.Id, "Signatory", s.PartyId, s.PersonId, Notes: "İmza yetkilisi"));
        Assert.Equal(1, party.Position);

        // Kalem silinince toplam yeniden hesaplanır.
        var remove = await admin.DeleteAsync($"/api/contracts/contract-lines/{oneTime.Id}?version={oneTime.Version}");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        var after = (await admin.GetFromJsonAsync<ContractDto>($"/api/contracts/contracts/{created.Id}"))!;
        Assert.Equal(0m, after.GrossTotal);

        var history = (await admin.GetFromJsonAsync<List<ContractHistoryDto>>($"/api/contracts/contracts/{created.Id}/history"))!;
        Assert.Contains(history, h => h.Kind == "Created" && h.ActorId == s.Admin.Id);

        var list = (await admin.GetFromJsonAsync<PagedResult<ContractDto>>($"/api/contracts/contracts?noticeFrom=2027-09-01&noticeTo=2027-09-01"))!;
        Assert.Contains(list.Items, c => c.Id == created.Id);
    }

    [Fact]
    public async Task Date_renewal_and_relationship_rules_are_enforced_by_the_api()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);

        var endBeforeStart = await admin.PostAsJsonAsync("/api/contracts/contracts",
            new SaveContractRequest("Hatalı", s.PartyId, StartDate: new DateOnly(2026, 11, 1), EndDate: new DateOnly(2026, 10, 1)));
        Assert.Contains("önce olamaz", (await ValidationProblemAsync(endBeforeStart)).Errors["endDate"][0]);

        var indefiniteWithEnd = await admin.PostAsJsonAsync("/api/contracts/contracts",
            new SaveContractRequest("Hatalı", s.PartyId, EndDate: new DateOnly(2027, 10, 1), IsIndefinite: true));
        Assert.True((await ValidationProblemAsync(indefiniteWithEnd)).Errors.ContainsKey("endDate"));

        var renewalWithoutPeriod = await admin.PostAsJsonAsync("/api/contracts/contracts", new SaveContractRequest("Hatalı", s.PartyId, RenewalMode: "Automatic"));
        Assert.True((await ValidationProblemAsync(renewalWithoutPeriod)).Errors.ContainsKey("renewalPeriodMonths"));

        // Başka bir firmanın kişisi müşteri kişisi olamaz.
        var otherParty = await PostRecordAsync(admin, Parties, new JsonObject { ["name"] = "Başka Firma" });
        var otherPerson = await PostRecordAsync(admin, People, new JsonObject { ["firstName"] = "Ayşe", ["lastName"] = "Kaya", ["partyId"] = otherParty.Id.ToString() });
        var foreignPerson = await admin.PostAsJsonAsync("/api/contracts/contracts", new SaveContractRequest("Hatalı", s.PartyId, PersonId: otherPerson.Id));
        Assert.Equal(["Kişi seçilen müşteriyle ilişkili değil."], (await ValidationProblemAsync(foreignPerson)).Errors["personId"]);

        var unknownParty = await admin.PostAsJsonAsync("/api/contracts/contracts", new SaveContractRequest("Hatalı", Guid.NewGuid()));
        Assert.Equal(["Müşteri bulunamadı."], (await ValidationProblemAsync(unknownParty)).Errors["partyId"]);

        var contract = await PostAsync<ContractDto>(admin, "/api/contracts/contracts", new SaveContractRequest("Geçerli", s.PartyId));
        var orphanParty = await admin.PostAsJsonAsync("/api/contracts/contract-parties", new SaveContractPartyRequest(contract.Id, "Guarantor"));
        Assert.Equal(["Taraf ya da kişi seçin."], (await ValidationProblemAsync(orphanParty)).Errors["partyId"]);

        // Eski sürümle güncelleme 409 alır (eşzamanlılık).
        var stale = await admin.PutAsJsonAsync($"/api/contracts/contracts/{contract.Id}", new SaveContractRequest("Geçerli 2", s.PartyId, Version: contract.Version + 5));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Tenants_cannot_see_or_reference_each_others_contracts_and_parties()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);

        var contract = await PostAsync<ContractDto>(adminA, "/api/contracts/contracts", new SaveContractRequest("A'nın sözleşmesi", a.PartyId));
        var line = await PostAsync<ContractLineDto>(adminA, "/api/contracts/contract-lines", new SaveContractLineRequest(contract.Id, "Kalem", UnitPrice: 10m));

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/contracts/contracts/{contract.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/contracts/contract-lines/{line.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/contracts/contracts/{contract.Id}/history")).StatusCode);
        var listB = (await adminB.GetFromJsonAsync<PagedResult<ContractDto>>("/api/contracts/contracts"))!;
        Assert.DoesNotContain(listB.Items, c => c.Id == contract.Id);

        // B, A'nın müşterisiyle sözleşme açamaz; A'nın sözleşmesine kalem ekleyemez (varlığı da belli edilmez).
        var foreign = await adminB.PostAsJsonAsync("/api/contracts/contracts", new SaveContractRequest("Sızıntı", a.PartyId));
        Assert.Equal(["Müşteri bulunamadı."], (await ValidationProblemAsync(foreign)).Errors["partyId"]);
        var foreignLine = await adminB.PostAsJsonAsync("/api/contracts/contract-lines", new SaveContractLineRequest(contract.Id, "Sızıntı", UnitPrice: 1m));
        Assert.Equal(["Sözleşme bulunamadı."], (await ValidationProblemAsync(foreignLine)).Errors["contractId"]);

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"/api/contracts/contracts/{contract.Id}?version={contract.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminA.GetAsync($"/api/contracts/contracts/{contract.Id}")).StatusCode);
    }

    [Fact]
    public async Task Permissions_and_data_scope_are_enforced_and_financial_fields_are_hidden_without_the_permission()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.Reader);
        using var writer = await LoginAsync(s.Writer);
        using var outsider = await LoginAsync(s.Outsider);

        var contract = await PostAsync<ContractDto>(admin, "/api/contracts/contracts", new SaveContractRequest("Gizli tutarlar", s.PartyId, ContractValue: 1234m));
        var line = await PostAsync<ContractLineDto>(admin, "/api/contracts/contract-lines", new SaveContractLineRequest(contract.Id, "Kalem", UnitPrice: 100m, TaxRatePercent: 20m));

        // İzinsiz: modül izni yok → 403; okuyucu yazamaz → 403.
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/contracts/contracts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/contracts/contracts", new SaveContractRequest("Yeni", s.PartyId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/contracts/contracts/{contract.Id}?version={contract.Version}")).StatusCode);

        // Finansal izni olmayan sözleşmeyi ve kalemi görür; tutarlar yanıta hiç girmez.
        var seen = (await reader.GetFromJsonAsync<ContractDto>($"/api/contracts/contracts/{contract.Id}"))!;
        Assert.Null(seen.ContractValue);
        Assert.Null(seen.GrossTotal);
        var raw = await reader.GetStringAsync($"/api/contracts/contract-lines/{line.Id}");
        Assert.DoesNotContain("\"unitPrice\":100", raw);
        Assert.DoesNotContain("\"gross\":120", raw);
        Assert.Equal(120m, (await admin.GetFromJsonAsync<ContractLineDto>($"/api/contracts/contract-lines/{line.Id}"))!.Gross);

        // Kendi kayıtları kapsamındaki yazıcı başkasının sözleşmesini göremez ve ona kalem ekleyemez; kendi açtığını görür.
        var own = await PostAsync<ContractDto>(writer, "/api/contracts/contracts", new SaveContractRequest("Kendi sözleşmem", s.PartyId));
        Assert.Equal(HttpStatusCode.NotFound, (await writer.GetAsync($"/api/contracts/contracts/{contract.Id}")).StatusCode);
        var foreignLine = await writer.PostAsJsonAsync("/api/contracts/contract-lines", new SaveContractLineRequest(contract.Id, "Sızıntı", UnitPrice: null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignLine.StatusCode);

        // Finansal izni olmayan yazıcı fiyat yazamaz (403); fiyatsız kalem açabilir, sunucu tutarı 0 hesaplar.
        var priced = await writer.PostAsJsonAsync("/api/contracts/contract-lines", new SaveContractLineRequest(own.Id, "Yazıcı kalemi", UnitPrice: 999m));
        Assert.Equal(HttpStatusCode.Forbidden, priced.StatusCode);
        var unpriced = await PostAsync<ContractLineDto>(writer, "/api/contracts/contract-lines", new SaveContractLineRequest(own.Id, "Yazıcı kalemi", UnitPrice: null));
        Assert.Null(unpriced.UnitPrice);
        Assert.Equal(0m, (await admin.GetFromJsonAsync<ContractLineDto>($"/api/contracts/contract-lines/{unpriced.Id}"))!.UnitPrice);
        var mine = (await writer.GetFromJsonAsync<PagedResult<ContractDto>>("/api/contracts/contracts"))!;
        Assert.Equal([own.Id], mine.Items.Select(c => c.Id));
    }

    private async Task<(ModulixUser Admin, ModulixUser Reader, ModulixUser Writer, ModulixUser Outsider, Guid PartyId, Guid PersonId)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "contracts", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var writer = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [ContractsRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, ContractsWrite], Scope.Own, writer);
        using var client = await LoginAsync(admin);
        var party = await PostRecordAsync(client, Parties, new JsonObject { ["name"] = "Anka Lojistik A.Ş.", ["taxNumber"] = Guid.NewGuid().ToString("N")[..10] });
        var person = await PostRecordAsync(client, People, new JsonObject { ["firstName"] = "Ali", ["lastName"] = "Yılmaz", ["partyId"] = party.Id.ToString() });
        return (admin, reader, writer, outsider, party.Id, person.Id);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<RecordResponse> PostRecordAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
