using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Modulix.Api.Tenancy;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;

namespace Modulix.Api.Development;

/// <summary>
/// Yalnızca geliştirme ortamı için örnek veri: 2 tenant, 3 kullanıcı. Tekrar çalıştırmak güvenlidir.
/// Acme'de CRM ve Lojistik, Beta'da yalnızca Lojistik açıktır; her tenant'ta tüm izinleri olan
/// bir "Yönetici" rolü vardır ve örnek kullanıcılar bu role atanır. Acme'de birkaç örnek CRM kaydı vardır.
///   dotnet run --project src/Modulix.Api -- seed
/// </summary>
public static class DemoDataSeeder
{
    /// <summary>Yalnızca yerel geliştirme şifresi; gerçek ortamda bu kullanıcılar oluşturulmaz.</summary>
    public const string DemoPassword = "Modulix.Demo1";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();

        var acme = await EnsureTenantAsync(db, "Acme Lojistik", "acme", cancellationToken);
        var beta = await EnsureTenantAsync(db, "Beta Taşımacılık", "beta", cancellationToken);

        var ayse = await EnsureUserAsync(users, "ayse@acme.demo", "Ayşe Yılmaz");
        var mehmet = await EnsureUserAsync(users, "mehmet@beta.demo", "Mehmet Kaya");
        var deniz = await EnsureUserAsync(users, "deniz@ortak.demo", "Deniz Demir");

        await EnsureMembershipAsync(db, acme, ayse, cancellationToken);
        await EnsureMembershipAsync(db, beta, mehmet, cancellationToken);
        await EnsureMembershipAsync(db, acme, deniz, cancellationToken);
        await EnsureMembershipAsync(db, beta, deniz, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var entitlements = scope.ServiceProvider.GetRequiredService<IModuleEntitlements>();
        await entitlements.SetEnabledAsync(acme.Id, "crm", enabled: true, cancellationToken);
        await entitlements.SetEnabledAsync(acme.Id, "logistics", enabled: true, cancellationToken);
        await entitlements.SetEnabledAsync(beta.Id, "logistics", enabled: true, cancellationToken);

        await EnsureAdministratorsAsync(services, acme, cancellationToken);
        await EnsureAdministratorsAsync(services, beta, cancellationToken);

        await EnsureCrmSamplesAsync(services, acme, ayse, cancellationToken);
    }

    /// <summary>Acme için birkaç CRM kaydı. Kayıt boru hattından geçer (audit: Ayşe). Tenant'ta firma varsa dokunmaz.</summary>
    private static async Task EnsureCrmSamplesAsync(IServiceProvider services, Tenant tenant, ModulixUser user, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var membershipId = await db.Memberships.Where(m => m.TenantId == tenant.Id && m.UserId == user.Id).Select(m => m.Id).SingleAsync(ct);
        scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current = new TenantContext(tenant.Id, user.Id, membershipId);

        var pipeline = scope.ServiceProvider.GetRequiredService<IRecordPipeline>();
        var existing = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.Company, PageSize: 1), ct);
        if (!existing.IsSuccess || existing.Value!.TotalCount > 0)
        {
            return;
        }

        async Task<Guid> CreateAsync(string entityKey, JsonObject values)
        {
            var result = await pipeline.CreateAsync(new RecordCommand(entityKey, null, values, Source: ExecutionSource.Import), ct);
            return result.IsSuccess
                ? result.Value!.Id
                : throw new InvalidOperationException($"Örnek CRM kaydı oluşturulamadı ({entityKey}): {result.Error!.Message}");
        }

        var anka = await CreateAsync(CrmModule.Entities.Company, new JsonObject
        {
            ["name"] = "Anka Gıda A.Ş.", ["city"] = "İstanbul", ["phone"] = "0212 555 01 01", ["email"] = "info@anka.demo",
            ["status"] = "Customer", ["industry"] = "Gıda", ["source"] = "Referral", ["tags"] = "öncelikli, soğuk zincir",
        });
        var delta = await CreateAsync(CrmModule.Entities.Company, new JsonObject
        {
            ["name"] = "Delta Yapı Ltd.", ["city"] = "Ankara", ["taxNumber"] = "1234567890", ["industry"] = "İnşaat", ["source"] = "Event",
        });
        var selin = await CreateAsync(CrmModule.Entities.Contact, new JsonObject
        {
            ["firstName"] = "Selin", ["lastName"] = "Aydın", ["companyId"] = anka.ToString(), ["title"] = "Satın Alma Müdürü",
        });
        var depo = await CreateAsync(CrmModule.Entities.Opportunity, new JsonObject
        {
            ["title"] = "Depo taşımacılığı sözleşmesi", ["companyId"] = anka.ToString(), ["contactId"] = selin.ToString(),
            ["stage"] = "Proposal", ["amount"] = 450000m, ["currency"] = "TRY", ["probability"] = 60,
        });
        await CreateAsync(CrmModule.Entities.Opportunity, new JsonObject
        {
            ["title"] = "Şantiye malzeme sevkiyatı", ["companyId"] = delta.ToString(), ["stage"] = "Lead",
        });
        await CreateAsync(CrmModule.Entities.Activity, new JsonObject
        {
            ["type"] = "Meeting", ["subject"] = "Teklif sunumu", ["companyId"] = anka.ToString(), ["opportunityId"] = depo.ToString(),
            ["dueAt"] = DateTimeOffset.UtcNow.Date.AddDays(3).AddHours(7).ToString("O"),
        });
        await CreateAsync(CrmModule.Entities.Activity, new JsonObject
        {
            ["type"] = "Call", ["subject"] = "Delta'yı arayıp ihtiyacı netleştir", ["companyId"] = delta.ToString(),
            ["dueAt"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
        });
    }

    /// <summary>Tenant'ta tüm izinleri olan "Yönetici" rolünü oluşturur ve tenant'ın tüm üyelerine atar.</summary>
    private static async Task EnsureAdministratorsAsync(IServiceProvider services, Tenant tenant, CancellationToken ct)
    {
        using var scope = services.CreateScope();

        // Roller tenant verisidir; yazmak için o tenant'ın bağlamı gerekir.
        scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current =
            new TenantContext(tenant.Id, Guid.Empty, Guid.Empty);

        var catalog = scope.ServiceProvider.GetRequiredService<ModuleCatalog>();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();

        const string name = "Yönetici";
        var allPermissions = catalog.Modules.SelectMany(m => m.Permissions).Select(p => p.Key).ToList();
        var existing = (await roles.ListAsync(ct)).SingleOrDefault(r => r.Name == name);
        var admin = existing is null
            ? await roles.CreateAsync(name, allPermissions, ct)
            : await roles.UpdateAsync(existing.Id, name, allPermissions, ct) ?? existing;

        var membershipIds = await db.Memberships.Where(m => m.TenantId == tenant.Id).Select(m => m.Id).ToListAsync(ct);
        foreach (var membershipId in membershipIds)
        {
            var assigned = await roles.GetMembershipRolesAsync(membershipId, ct) ?? [];
            if (assigned.All(r => r.Id != admin.Id))
            {
                await roles.SetMembershipRolesAsync(membershipId, [.. assigned.Select(r => r.Id), admin.Id], ct);
            }
        }
    }

    private static async Task<Tenant> EnsureTenantAsync(ModulixDbContext db, string name, string slug, CancellationToken ct)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Slug == slug, ct);
        if (tenant is null)
        {
            tenant = new Tenant(name, slug);
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);
        }

        return tenant;
    }

    private static async Task<ModulixUser> EnsureUserAsync(UserManager<ModulixUser> users, string email, string displayName)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is not null)
        {
            return user;
        }

        user = new ModulixUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
        };

        var result = await users.CreateAsync(user, DemoPassword);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Örnek kullanıcı oluşturulamadı ({email}): {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }

        return user;
    }

    private static async Task EnsureMembershipAsync(ModulixDbContext db, Tenant tenant, ModulixUser user, CancellationToken ct)
    {
        var exists = await db.Memberships.AnyAsync(m => m.TenantId == tenant.Id && m.UserId == user.Id, ct);
        if (!exists)
        {
            db.Memberships.Add(new Membership(tenant.Id, user.Id));
        }
    }
}
