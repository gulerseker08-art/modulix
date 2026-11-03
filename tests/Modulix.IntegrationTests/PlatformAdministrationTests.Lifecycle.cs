using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Platform;
using Modulix.Shared.Users;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0028 Dilim 1: tenant durumları (deneme, askı), hak koşulları (kaynak, bitiş, not) ve platform denetim kaydı.
/// Askıdaki firma kapalı gibi erişimi kaybeder; gerekçe zorunludur; her işlem firmalar arası platform kaydına yazılır;
/// firma yöneticisi bu uçlara erişemez; bir firmanın kaydı başka firmanın görünümüne sızmaz.
/// </summary>
public partial class PlatformAdministrationTests
{
    [Fact]
    public async Task Suspending_a_tenant_stops_access_until_it_is_reopened_and_every_step_is_recorded()
    {
        var tenant = await factory.CreateTenantAsync();
        var member = await factory.CreateUserAsync(tenant);
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);
        using var memberClient = await LoginAsync(member);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync("/api/tenant/current")).StatusCode);

        var suspend = await client.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/suspend", new TenantReasonRequest("Ödeme gecikti"));
        Assert.Equal(HttpStatusCode.NoContent, suspend.StatusCode);

        // Üye bir sonraki isteğinde erişimi kaybeder; yönetim ekranı durumu Askıda gösterir ve davet gönderilemez.
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.GetAsync("/api/tenant/current")).StatusCode);
        var suspended = await client.GetFromJsonAsync<PlatformTenantPageResponse>($"/api/platform/tenants?search={tenant.Slug}&status=Suspended");
        Assert.Equal(TenantState.Suspended, Assert.Single(suspended!.Items).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/administrators",
            new InviteTenantAdministratorRequest($"x-{Guid.NewGuid():N}@test.modulix", "Baska"))).StatusCode);

        // Askıdaki firma tekrar askıya alınamaz.
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/suspend", new TenantReasonRequest("Yine"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/platform/tenants/{tenant.Id}/reopen", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync("/api/tenant/current")).StatusCode);

        var audit = await client.GetFromJsonAsync<PlatformAuditPageResponse>($"/api/platform/audit?tenantId={tenant.Id}");
        Assert.Equal(["tenant.reopened", "tenant.suspended"], audit!.Items.Select(i => i.Action).Order(StringComparer.Ordinal));
        var suspendedEntry = audit.Items.Single(i => i.Action == "tenant.suspended");
        Assert.Equal("Ödeme gecikti", suspendedEntry.Reason);
        Assert.Equal(platformAdmin.Id, suspendedEntry.ActorUserId);
        Assert.Equal(platformAdmin.DisplayName, suspendedEntry.ActorName);
        Assert.Contains("Suspended", suspendedEntry.Details);

        // Firmanın kendi denetim kaydında da durum değişikliği görünür (core.tenant).
        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.Equal(2, await db.AuditLog.AsNoTracking().CountAsync(a => a.EntityKey == "core.tenant" && a.RecordId == tenant.Id));
    }

    [Fact]
    public async Task Trial_tenant_works_like_an_active_one_and_activation_is_only_for_trials()
    {
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);
        var slug = $"d-{Guid.NewGuid():N}"[..20];
        var email = $"deneme-{Guid.NewGuid():N}@test.modulix";

        var created = await client.PostAsJsonAsync("/api/platform/tenants", new CreateTenantRequest(
            "Deneme Firması", slug, ["crm"], [], email, "Deneme Yonetici", Trial: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tenantId = (await created.Content.ReadFromJsonAsync<CreateTenantResponse>())!.Tenant.Tenant.Id;
        Assert.Equal(TenantState.Trial, (await client.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{tenantId}"))!.Tenant.Status);

        // Deneme firmasının yöneticisi daveti kabul edip firmaya girer: erişim Active ile aynıdır.
        var link = await ReadInvitationAsync(email);
        using var invited = factory.CreateBrowserClient();
        var accepted = await invited.PostAsJsonAsync("/api/auth/accept-invitation", new AcceptInvitationRequest(link.Id, link.Token, NewPassword));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await invited.GetAsync("/api/tenant/current")).StatusCode);
        var listed = await client.GetFromJsonAsync<PlatformTenantPageResponse>("/api/platform/tenants?search=" + slug + "&status=Trial");
        Assert.Single(listed!.Items);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/platform/tenants/{tenantId}/activate", null)).StatusCode);
        Assert.Equal(TenantState.Active, (await client.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{tenantId}"))!.Tenant.Status);
        Assert.Equal(HttpStatusCode.OK, (await invited.GetAsync("/api/tenant/current")).StatusCode);

        // Etkin firma deneme değildir.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/platform/tenants/{tenantId}/activate", null)).StatusCode);

        var audit = await client.GetFromJsonAsync<PlatformAuditPageResponse>($"/api/platform/audit?tenantId={tenantId}");
        Assert.Equal(["tenant.activated", "tenant.created"], audit!.Items.Select(i => i.Action).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Suspend_needs_a_reason_and_invalid_transitions_and_terms_are_rejected()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);

        var missing = await client.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/suspend", new TenantReasonRequest("   "));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("reason", await missing.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/platform/tenants/{tenant.Id}/suspend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"/api/platform/tenants/{Guid.NewGuid()}/suspend", new TenantReasonRequest("x"))).StatusCode);

        // Yalnızca açık firma askıya alınır; kapalı firma askıya alınamaz.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/platform/tenants/{tenant.Id}/close", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/suspend", new TenantReasonRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/platform/tenants/{tenant.Id}/reopen", null)).StatusCode);

        // Hak koşulları: istisna gerekçesiz olmaz, bitiş gelecekte olmalı, notun sınırı var.
        var url = $"/api/platform/tenants/{tenant.Id}/modules/crm";
        var noNote = await client.PutAsJsonAsync(url, new SetTenantModuleRequest(true, LicenseSource.Exception));
        Assert.Equal(HttpStatusCode.BadRequest, noNote.StatusCode);
        Assert.Contains("note", await noNote.Content.ReadAsStringAsync());
        var past = await client.PutAsJsonAsync(url, new SetTenantModuleRequest(true, LicenseSource.Trial, DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
        Assert.Contains("validUntil", await past.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync(url, new SetTenantModuleRequest(true, LicenseSource.Manual, null, new string('a', 501)))).StatusCode);

        // Geçersiz koşullar hiçbir şeyi değiştirmez: hak ve denetim kayıtları olduğu gibi kalır.
        var detail = await client.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{tenant.Id}");
        var crm = Assert.Single(detail!.Entitlements!, e => e.ModuleKey == "crm");
        Assert.Equal(LicenseSource.Manual, crm.Source);
        Assert.Null(crm.ValidUntil);
        var audit = await client.GetFromJsonAsync<PlatformAuditPageResponse>($"/api/platform/audit?tenantId={tenant.Id}&action=tenant.module-changed");
        Assert.Empty(audit!.Items);
    }

    [Fact]
    public async Task Entitlement_terms_are_stored_shown_and_changing_them_on_an_open_module_is_audited()
    {
        var tenant = await factory.CreateTenantAsync();
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);
        var url = $"/api/platform/tenants/{tenant.Id}/modules/crm";
        var until = DateTimeOffset.UtcNow.AddDays(30);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync(url, new SetTenantModuleRequest(true, LicenseSource.Trial, until, "30 günlük deneme"))).StatusCode);
        var first = Assert.Single((await client.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{tenant.Id}"))!.Entitlements!, e => e.ModuleKey == "crm");
        Assert.True(first.IsEnabled);
        Assert.Equal(LicenseSource.Trial, first.Source);
        Assert.Equal("30 günlük deneme", first.Note);
        Assert.Equal(until.ToUnixTimeSeconds(), first.ValidUntil!.Value.ToUnixTimeSeconds());

        // Modül zaten açıkken koşulları değiştirmek (süre uzatma) yok sayılmaz ve denetlenir.
        var longer = until.AddDays(30);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync(url, new SetTenantModuleRequest(true, LicenseSource.Exception, longer, "Müşteri talebi, satış onayı"))).StatusCode);
        var second = Assert.Single((await client.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{tenant.Id}"))!.Entitlements!, e => e.ModuleKey == "crm");
        Assert.Equal(LicenseSource.Exception, second.Source);
        Assert.Equal(longer.ToUnixTimeSeconds(), second.ValidUntil!.Value.ToUnixTimeSeconds());

        var audit = await client.GetFromJsonAsync<PlatformAuditPageResponse>($"/api/platform/audit?tenantId={tenant.Id}&action=tenant.module-changed");
        Assert.Equal(2, audit!.Items.Count);
        Assert.Contains(audit.Items, i => i.Reason == "Müşteri talebi, satış onayı");
        Assert.All(audit.Items, i => Assert.Contains("terms", i.Details));

        // Koşulsuz yeniden açma mevcut koşulları korur; kapatmak koşulları siler ama veri ve kayıt satırı kalır.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(url, new SetTenantModuleRequest(true))).StatusCode);
        Assert.Equal(LicenseSource.Exception,
            Assert.Single((await client.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{tenant.Id}"))!.Entitlements!, e => e.ModuleKey == "crm").Source);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(url, new SetTenantModuleRequest(false))).StatusCode);
        Assert.False(Assert.Single((await client.GetFromJsonAsync<PlatformTenantDetailResponse>($"/api/platform/tenants/{tenant.Id}"))!.Entitlements!, e => e.ModuleKey == "crm").IsEnabled);
    }

    [Fact]
    public async Task Platform_audit_is_scoped_to_the_tenant_asked_for_and_does_not_leak_between_tenants()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync($"/api/platform/tenants/{a.Id}/suspend", new TenantReasonRequest("Yalnızca A"))).StatusCode);

        var forA = await client.GetFromJsonAsync<PlatformAuditPageResponse>($"/api/platform/audit?tenantId={a.Id}");
        var forB = await client.GetFromJsonAsync<PlatformAuditPageResponse>($"/api/platform/audit?tenantId={b.Id}");
        Assert.Single(forA!.Items);
        Assert.Empty(forB!.Items);
        Assert.All(forA.Items, i => Assert.Equal(a.Id, i.TargetTenantId));

        // A'nın durum değişikliği yalnızca A'nın kendi denetim kaydına yazılır (RLS); B'nin bağlamında görünmez.
        using (var scopeB = factory.OpenScope(b.Id))
        {
            var dbB = scopeB.ServiceProvider.GetRequiredService<ModulixDbContext>();
            Assert.Equal(0, await dbB.AuditLog.AsNoTracking().CountAsync(x => x.EntityKey == "core.tenant"));
        }

        // B'nin üyesi A'nın askıya alınmasından etkilenmez.
        var memberOfB = await factory.CreateUserAsync(b);
        using var memberClient = await LoginAsync(memberOfB);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync("/api/tenant/current")).StatusCode);
    }

    [Fact]
    public async Task Tenant_administrators_and_anonymous_callers_cannot_use_the_new_platform_endpoints()
    {
        var tenant = await factory.CreateTenantAsync();
        var tenantAdmin = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, tenantAdmin);
        using var tenantClient = await LoginAsync(tenantAdmin);
        using var anonymous = factory.CreateBrowserClient();

        foreach (var action in new[] { "suspend", "activate", "close", "reopen" })
        {
            var url = $"/api/platform/tenants/{tenant.Id}/{action}";
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.PostAsJsonAsync(url, new TenantReasonRequest("x"))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(url, new TenantReasonRequest("x"))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.GetAsync($"/api/platform/audit?tenantId={tenant.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/platform/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantClient.PutAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/modules/crm", new SetTenantModuleRequest(true, LicenseSource.Plan))).StatusCode);

        // Hiçbiri durumu değiştirmedi.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.Equal(Modulix.Core.Platform.TenantStatus.Active, (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.Id)).Status);
        Assert.Equal(0, await db.PlatformAuditLog.AsNoTracking().CountAsync(e => e.TargetTenantId == tenant.Id));
    }

    [Fact]
    public async Task Platform_audit_is_append_only_for_the_application_role()
    {
        var tenant = await factory.CreateTenantAsync();
        var platformAdmin = await CreatePlatformAdministratorAsync();
        using var client = await LoginAsync(platformAdmin);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync($"/api/platform/tenants/{tenant.Id}/suspend", new TenantReasonRequest("Silinemez"))).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE platform.PlatformAuditLog SET Reason = N'x' WHERE TargetTenantId = {tenant.Id}"));
        await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.ExecuteSqlAsync($"DELETE FROM platform.PlatformAuditLog WHERE TargetTenantId = {tenant.Id}"));
        Assert.Equal(1, await db.PlatformAuditLog.AsNoTracking().CountAsync(e => e.TargetTenantId == tenant.Id));
    }
}
