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
/// ADR-0021 dilim 2: durum geçişleri, sürüm dondurma, onay ve karşılaştırma. Mutlu yol (inceleme → hazır → onay → onaylandı), doğrulama
/// (eksik tarih, gerekçe, eşzamanlılık), tenant izolasyonu (eylem ve sürüm uçları) ve yetki (onay izni, kendi onayı, finansal maske).
/// </summary>
public class ContractLifecycleTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Parties = "/api/records/" + SharedEntities.Party;

    [Fact]
    public async Task Review_ready_and_approval_flow_freezes_the_version_and_blocks_edits_until_reopened()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var approver = await LoginAsync(s.Approver);
        var contract = await NewContractAsync(admin, s.PartyId, dated: true);
        var line = await PostAsync<ContractLineDto>(admin, "/api/contracts/contract-lines", new SaveContractLineRequest(contract.Id, "Kurulum", Quantity: 2m, UnitPrice: 1000m, TaxRatePercent: 20m));
        contract = (await admin.GetFromJsonAsync<ContractDto>($"/api/contracts/contracts/{contract.Id}"))!; // kalem eklenince başlık sürümü artar

        // Eylem listesi: yetkili ve duruma uygun olanlar.
        var actions = (await admin.GetFromJsonAsync<List<ContractActionDto>>($"/api/contracts/contracts/{contract.Id}/actions"))!;
        Assert.True(actions.Single(a => a.Action == "send-to-review").Allowed);
        Assert.False(actions.Single(a => a.Action == "submit-for-approval").Allowed);

        contract = await RunAsync(admin, contract.Id, "send-to-review", contract.Version);
        Assert.Equal("InReview", contract.PreparationStatus);
        contract = await RunAsync(admin, contract.Id, "mark-ready", contract.Version);
        Assert.Equal("ReadyToPublish", contract.PreparationStatus);

        // Yayıma hazır içerik düzenlenemez.
        var edit = await admin.PutAsJsonAsync($"/api/contracts/contracts/{contract.Id}", Save(contract) with { Title = "Değişti" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
        var editLine = await admin.PutAsJsonAsync($"/api/contracts/contract-lines/{line.Id}", new SaveContractLineRequest(contract.Id, "Kurulum", Quantity: 3m, UnitPrice: 1000m, TaxRatePercent: 20m, Version: line.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, editLine.StatusCode);

        contract = await RunAsync(admin, contract.Id, "submit-for-approval", contract.Version);
        Assert.Equal("Pending", contract.ApprovalStatus);
        var versions = (await admin.GetFromJsonAsync<List<ContractVersionDto>>($"/api/contracts/contracts/{contract.Id}/versions"))!;
        var frozen = Assert.Single(versions);
        Assert.Equal((1, "Approval", "Frozen", 64), (frozen.VersionNo, frozen.Kind, frozen.Status, frozen.Sha256.Length));
        var detail = (await admin.GetFromJsonAsync<ContractVersionDto>($"/api/contracts/contracts/{contract.Id}/versions/1"))!;
        Assert.Equal(2400m, detail.Snapshot!.GrossTotal);
        Assert.Equal(1000m, Assert.Single(detail.Snapshot.Lines).UnitPrice);

        // Onaylayan onaylar; sürüm durumu da yazılır; tarihçe geçişleri gösterir.
        contract = await RunAsync(approver, contract.Id, "approve", contract.Version);
        Assert.Equal("Approved", contract.ApprovalStatus);
        versions = (await admin.GetFromJsonAsync<List<ContractVersionDto>>($"/api/contracts/contracts/{contract.Id}/versions"))!;
        Assert.Equal("Approved", Assert.Single(versions).Status);
        var history = (await admin.GetFromJsonAsync<List<ContractHistoryDto>>($"/api/contracts/contracts/{contract.Id}/history"))!;
        Assert.Equal(["Created", "SentToReview", "MarkedReady", "SubmittedForApproval", "Approved"], history.Select(h => h.Kind));
        Assert.Contains(history, h => h.Kind == "Approved" && h.FromStatus!.Contains("Pending") && h.ToStatus!.Contains("Approved"));

        // Onaylı içeriği düzenlemek için taslağa dönülür: onay geçersiz kalır, yeni revizyon açılır, eski sürüm geçersiz işaretlenir.
        contract = await RunAsync(admin, contract.Id, "reopen-draft", contract.Version, "Fiyat güncellenecek");
        Assert.Equal(("Draft", "NotRequired", 2), (contract.PreparationStatus, contract.ApprovalStatus, contract.CurrentVersionNo));
        versions = (await admin.GetFromJsonAsync<List<ContractVersionDto>>($"/api/contracts/contracts/{contract.Id}/versions"))!;
        Assert.Equal("Superseded", Assert.Single(versions).Status);
        var retitle = await admin.PutAsJsonAsync($"/api/contracts/contracts/{contract.Id}", Save(contract) with { Title = "Yıllık bakım (güncel)" });
        Assert.Equal(HttpStatusCode.OK, retitle.StatusCode);

        // Dondurulmuş sürüm güncel taslakla karşılaştırılır.
        var diff = (await admin.GetFromJsonAsync<ContractCompareDto>($"/api/contracts/contracts/{contract.Id}/versions/1/compare?with=0"))!;
        Assert.Contains(diff.Header, f => f.Field == "title" && f.After == "Yıllık bakım (güncel)");
    }

    [Fact]
    public async Task Missing_dates_reason_and_stale_version_are_rejected_by_the_api()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var contract = await NewContractAsync(admin, s.PartyId, dated: false);
        contract = await RunAsync(admin, contract.Id, "send-to-review", contract.Version);

        var ready = await admin.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/mark-ready", new ContractTransitionRequest(contract.Version));
        Assert.Equal(HttpStatusCode.Conflict, ready.StatusCode);
        Assert.Contains("Başlangıç tarihi", await ready.Content.ReadAsStringAsync());

        var unknown = await admin.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/approve", new ContractTransitionRequest(contract.Version));
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);

        var noReason = await admin.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/cancel", new ContractTransitionRequest(contract.Version));
        Assert.Equal(["Gerekçe zorunludur."], (await ValidationProblemAsync(noReason)).Errors["reason"]);

        var stale = await admin.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/cancel", new ContractTransitionRequest(contract.Version + 7, "Vazgeçildi"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        contract = await RunAsync(admin, contract.Id, "cancel", contract.Version, "Müşteri vazgeçti");
        Assert.Equal("Cancelled", contract.EffectiveStatus);
        var afterCancel = await admin.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/send-to-review", new ContractTransitionRequest(contract.Version));
        Assert.Equal(HttpStatusCode.Conflict, afterCancel.StatusCode);
    }

    [Fact]
    public async Task Other_tenant_cannot_run_actions_or_read_versions()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var contract = await NewContractAsync(adminA, a.PartyId, dated: true);
        contract = await RunAsync(adminA, contract.Id, "send-to-review", contract.Version);
        contract = await RunAsync(adminA, contract.Id, "mark-ready", contract.Version);
        contract = await RunAsync(adminA, contract.Id, "submit-for-approval", contract.Version);

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/approve", new ContractTransitionRequest(contract.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/contracts/contracts/{contract.Id}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/contracts/contracts/{contract.Id}/versions/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/contracts/contracts/{contract.Id}/actions")).StatusCode);
        Assert.Equal("Pending", (await adminA.GetFromJsonAsync<ContractDto>($"/api/contracts/contracts/{contract.Id}"))!.ApprovalStatus);
    }

    [Fact]
    public async Task Approval_needs_its_own_permission_self_approval_is_blocked_and_financials_are_masked_in_versions()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var approver = await LoginAsync(s.Approver);
        using var reader = await LoginAsync(s.Reader);
        var contract = await NewContractAsync(admin, s.PartyId, dated: true);
        await PostAsync<ContractLineDto>(admin, "/api/contracts/contract-lines", new SaveContractLineRequest(contract.Id, "Kurulum", UnitPrice: 500m));
        contract = (await admin.GetFromJsonAsync<ContractDto>($"/api/contracts/contracts/{contract.Id}"))!;
        contract = await RunAsync(admin, contract.Id, "send-to-review", contract.Version);
        contract = await RunAsync(admin, contract.Id, "mark-ready", contract.Version);
        contract = await RunAsync(admin, contract.Id, "submit-for-approval", contract.Version);

        // Onay izni olmayan okuyucu: eylem listesinde onay yok, çağrı 403.
        var readerActions = (await reader.GetFromJsonAsync<List<ContractActionDto>>($"/api/contracts/contracts/{contract.Id}/actions"))!;
        Assert.DoesNotContain(readerActions, a => a.Action is "approve" or "reject-approval");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/approve", new ContractTransitionRequest(contract.Version))).StatusCode);

        // Onaya gönderen (admin, onay izni de var) kendi gönderdiğini onaylayamaz; ayar varsayılan kapalıdır.
        var self = await admin.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/approve", new ContractTransitionRequest(contract.Version));
        Assert.True((await ValidationProblemAsync(self)).Errors.ContainsKey("approvalStatus"));
        var adminActions = (await admin.GetFromJsonAsync<List<ContractActionDto>>($"/api/contracts/contracts/{contract.Id}/actions"))!;
        Assert.Equal("self-approval", adminActions.Single(a => a.Action == "approve").Code);

        // Finansal izni olmayan okuyucu sürümü görür, tutarlar maskelidir; onaylayan reddedince gerekçe tarihçeye yazılır.
        var masked = (await reader.GetFromJsonAsync<ContractVersionDto>($"/api/contracts/contracts/{contract.Id}/versions/1"))!;
        Assert.Equal(0m, masked.Snapshot!.GrossTotal);
        Assert.Equal(0m, Assert.Single(masked.Snapshot.Lines).UnitPrice);
        Assert.Equal(500m, (await approver.GetFromJsonAsync<ContractVersionDto>($"/api/contracts/contracts/{contract.Id}/versions/1"))!.Snapshot!.GrossTotal);

        contract = await RunAsync(approver, contract.Id, "reject-approval", contract.Version, "Fiyat yüksek");
        Assert.Equal(("Rejected", "Fiyat yüksek"), (contract.ApprovalStatus, contract.StatusReason));
        Assert.Equal("Rejected", Assert.Single((await admin.GetFromJsonAsync<List<ContractVersionDto>>($"/api/contracts/contracts/{contract.Id}/versions"))!).Status);
    }

    private static async Task<ContractDto> RunAsync(HttpClient client, Guid id, string action, long version, string? reason = null)
    {
        var response = await client.PostAsJsonAsync($"/api/contracts/contracts/{id}/{action}", new ContractTransitionRequest(version, reason));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ContractDto>())!;
    }

    private static Task<ContractDto> NewContractAsync(HttpClient client, Guid partyId, bool dated) =>
        PostAsync<ContractDto>(client, "/api/contracts/contracts", new SaveContractRequest(
            "Yıllık bakım", partyId, StartDate: dated ? new DateOnly(2026, 11, 1) : null, EndDate: dated ? new DateOnly(2027, 10, 31) : null));

    private static SaveContractRequest Save(ContractDto c) => new(
        c.Title, c.PartyId, c.Type, c.Description, c.PersonId, c.OwnerId, c.PreviousContractId, c.StartDate, c.EndDate, c.IsIndefinite, c.Currency, c.PriceIncludesTax,
        c.ContractValue, c.PaymentTerms, c.RenewalMode, c.RenewalPeriodMonths, c.NoticePeriodDays, c.Confidentiality, c.Version);

    private async Task<(ModulixUser Admin, ModulixUser Approver, ModulixUser Reader, Guid PartyId)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "contracts", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var approver = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, FinancialsRead, ContractsApprove], Scope.All, approver);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead], Scope.All, reader);
        using var client = await LoginAsync(admin);
        var party = await PostRecordAsync(client, Parties, new JsonObject { ["name"] = "Anka Lojistik A.Ş." });
        return (admin, approver, reader, party.Id);
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
