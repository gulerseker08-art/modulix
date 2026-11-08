using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Platform;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0028 Dilim 7c: sağlayıcıdan bağımsız ödeme kaydı. Finans rolü yazar; kayıt erişimi değiştirmez ve hiçbir kayıt silinmez
/// (iptal gerekçesiyle kalır). Bir firmanın kaydı başka firmanın listesinde görünmez; firma kullanıcısı bu uçlara giremez.
/// </summary>
public partial class PlatformAdministrationTests
{
    private static CreateBillingRequest Bill(int daysUntilDue = 14, decimal amount = 1500m) => new(
        new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), amount, "TRY", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(daysUntilDue), "FTR-2026-0001");

    private static async Task<BillingRecordDto> CreateBillAsync(HttpClient finance, Guid tenantId, CreateBillingRequest request)
    {
        var response = await finance.PostAsJsonAsync($"/api/platform/tenants/{tenantId}/billing", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BillingRecordDto>())!;
    }

    [Fact]
    public async Task Billing_records_are_created_paid_and_audited_without_touching_access()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead], user);
        using var tenantClient = await LoginAsync(user);
        using var finance = await LoginAsync(await CreatePlatformMemberAsync(PlatformRole.Finance));

        var open = await CreateBillAsync(finance, tenant.Id, Bill());
        Assert.Equal((BillingStatusDto.Pending, "TRY", false), (open.Status, open.Currency, open.IsOverdue));
        var late = await CreateBillAsync(finance, tenant.Id, Bill(daysUntilDue: -3));
        Assert.True(late.IsOverdue);

        var paid = (await (await finance.PostAsJsonAsync($"/api/platform/billing/{open.Id}/paid", new MarkBillingPaidRequest(null, "EFT-778"))).Content.ReadFromJsonAsync<BillingRecordDto>())!;
        Assert.Equal((BillingStatusDto.Paid, "EFT-778"), (paid.Status, paid.Reference));
        Assert.NotNull(paid.PaidAt);

        // Vadesi geçmiş kayıt bile erişimi kapatmaz.
        Assert.Equal(HttpStatusCode.OK, (await tenantClient.GetAsync("/api/crm/companies")).StatusCode);

        var list = (await finance.GetFromJsonAsync<List<BillingRecordDto>>($"/api/platform/billing?tenantId={tenant.Id}"))!;
        Assert.Equal(2, list.Count);
        var pending = (await finance.GetFromJsonAsync<List<BillingRecordDto>>($"/api/platform/billing?tenantId={tenant.Id}&status=Pending"))!;
        Assert.Equal(late.Id, Assert.Single(pending).Id);

        using var scope = factory.Services.CreateScope();
        var actions = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().PlatformAuditLog.AsNoTracking()
            .Where(a => a.TargetTenantId == tenant.Id).Select(a => a.Action).ToListAsync();
        Assert.Equal(2, actions.Count(a => a == PlatformAuditActions.BillingCreated));
        Assert.Contains(PlatformAuditActions.BillingPaid, actions);
    }

    [Fact]
    public async Task Billing_validation_and_state_rules_are_enforced_and_nothing_is_deleted()
    {
        var tenant = await factory.CreateTenantAsync();
        using var finance = await LoginAsync(await CreatePlatformMemberAsync(PlatformRole.Finance));
        var url = $"/api/platform/tenants/{tenant.Id}/billing";

        Assert.Equal(HttpStatusCode.BadRequest, (await finance.PostAsJsonAsync(url, Bill(amount: 0m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await finance.PostAsJsonAsync(url, Bill() with { PeriodEnd = new DateOnly(2026, 10, 1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await finance.PostAsJsonAsync(url, Bill() with { Currency = "TL" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await finance.PostAsJsonAsync($"/api/platform/tenants/{Guid.NewGuid()}/billing", Bill())).StatusCode);

        var record = await CreateBillAsync(finance, tenant.Id, Bill());
        Assert.Equal(HttpStatusCode.BadRequest, (await finance.PostAsJsonAsync($"/api/platform/billing/{record.Id}/cancel", new CancelBillingRequest(" "))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await finance.PostAsJsonAsync($"/api/platform/billing/{record.Id}/cancel", new CancelBillingRequest("Yanlış dönem"))).StatusCode);

        // İptal edilen kayıt ödendi yapılamaz, tekrar iptal edilemez; kayıt silinmedi, gerekçesiyle duruyor.
        Assert.Equal(HttpStatusCode.Conflict, (await finance.PostAsJsonAsync($"/api/platform/billing/{record.Id}/paid", new MarkBillingPaidRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await finance.PostAsJsonAsync($"/api/platform/billing/{record.Id}/cancel", new CancelBillingRequest("Tekrar"))).StatusCode);
        var kept = Assert.Single((await finance.GetFromJsonAsync<List<BillingRecordDto>>($"/api/platform/billing?tenantId={tenant.Id}"))!);
        Assert.Equal((BillingStatusDto.Cancelled, "Yanlış dönem"), (kept.Status, kept.CancelReason));
        Assert.Equal(HttpStatusCode.NotFound, (await finance.PostAsJsonAsync($"/api/platform/billing/{Guid.NewGuid()}/paid", new MarkBillingPaidRequest())).StatusCode);
    }

    [Fact]
    public async Task Billing_records_stay_with_their_tenant_and_only_finance_writes()
    {
        var tenantA = await factory.CreateTenantAsync();
        var tenantB = await factory.CreateTenantAsync();
        var tenantUser = await factory.CreateUserAsync(tenantA);
        using var tenantClient = await LoginAsync(tenantUser);
        using var finance = await LoginAsync(await CreatePlatformMemberAsync(PlatformRole.Finance));
        using var support = await LoginAsync(await CreatePlatformMemberAsync(PlatformRole.Support));
        using var operations = await LoginAsync(await CreatePlatformMemberAsync(PlatformRole.Operations));

        var recordA = await CreateBillAsync(finance, tenantA.Id, Bill());

        // Firma süzgeci: B'nin listesinde A'nın kaydı yok.
        Assert.Empty((await finance.GetFromJsonAsync<List<BillingRecordDto>>($"/api/platform/billing?tenantId={tenantB.Id}"))!);
        Assert.Contains(
            (await finance.GetFromJsonAsync<List<BillingRecordDto>>($"/api/platform/billing?tenantId={tenantA.Id}"))!, r => r.Id == recordA.Id);

        // Okuma her platform rolüne açık; yazma yalnızca Finans (ve Owner). Firma kullanıcısı platform uçlarına giremez.
        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync($"/api/platform/billing?tenantId={tenantA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PostAsJsonAsync($"/api/platform/tenants/{tenantA.Id}/billing", Bill())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await operations.PostAsJsonAsync($"/api/platform/billing/{recordA.Id}/paid", new MarkBillingPaidRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.GetAsync($"/api/platform/billing?tenantId={tenantA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.PostAsJsonAsync($"/api/platform/tenants/{tenantA.Id}/billing", Bill())).StatusCode);
        Assert.Equal(BillingStatusDto.Pending, (await finance.GetFromJsonAsync<List<BillingRecordDto>>($"/api/platform/billing?tenantId={tenantA.Id}"))!.Single().Status);
    }
}
