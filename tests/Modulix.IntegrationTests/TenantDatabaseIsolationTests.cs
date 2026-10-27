using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Organization;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Tenancy;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0009 (Faz 3): tenant iş verisi tenant başına ayrı fiziksel veritabanındadır. Bu testler izolasyonun mantıksal
/// değil fiziksel olduğunu, çözümlemenin güvenli olduğunu ve kurulumun idempotent/yeniden denenebilir olduğunu kanıtlar.
/// </summary>
public class TenantDatabaseIsolationTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Each_tenant_gets_a_distinct_physical_database()
    {
        var a = (await factory.CreateTenantAsync()).Id;
        var b = (await factory.CreateTenantAsync()).Id;

        var dbA = await factory.TenantDatabaseNameAsync(a);
        var dbB = await factory.TenantDatabaseNameAsync(b);

        Assert.False(string.IsNullOrWhiteSpace(dbA));
        Assert.False(string.IsNullOrWhiteSpace(dbB));
        Assert.NotEqual(dbA, dbB);
        Assert.StartsWith(TenantDatabaseNaming.Prefix, dbA);
    }

    [Fact]
    public async Task Tenant_A_record_is_written_to_A_database_and_not_to_B()
    {
        var a = (await factory.CreateTenantAsync()).Id;
        var b = (await factory.CreateTenantAsync()).Id;
        var company = await factory.CreateCompanyAsync(a, "A Şirketi");

        var dbA = (await factory.TenantDatabaseNameAsync(a))!;
        var dbB = (await factory.TenantDatabaseNameAsync(b))!;

        // Fiziksel kanıt: kayıt A'nın veritabanında vardır, B'nin veritabanında yoktur (oturum tenant'ı: A).
        var inA = await factory.ScalarInTenantDatabaseAsync<int>(
            dbA, a, "SELECT COUNT(*) FROM [core].[Companies] WHERE [Id] = @id", ("@id", company.Id));
        var inB = await factory.ScalarInTenantDatabaseAsync<int>(
            dbB, a, "SELECT COUNT(*) FROM [core].[Companies] WHERE [Id] = @id", ("@id", company.Id));

        Assert.Equal(1, inA);
        Assert.Equal(0, inB);
    }

    [Fact]
    public async Task Tenant_B_record_is_written_to_B_database()
    {
        var b = (await factory.CreateTenantAsync()).Id;
        var company = await factory.CreateCompanyAsync(b, "B Şirketi");

        var dbB = (await factory.TenantDatabaseNameAsync(b))!;
        var inB = await factory.ScalarInTenantDatabaseAsync<int>(
            dbB, b, "SELECT COUNT(*) FROM [core].[Companies] WHERE [Id] = @id", ("@id", company.Id));

        Assert.Equal(1, inB);
    }

    [Fact]
    public async Task A_user_cannot_read_another_tenants_records()
    {
        var a = (await factory.CreateTenantAsync()).Id;
        var b = (await factory.CreateTenantAsync()).Id;
        await factory.CreateCompanyAsync(a, "Yalnızca A");

        // B'nin bağlamında A'nın kaydı görünmez (farklı veritabanı + RLS).
        using var scope = factory.OpenScope(b);
        var visible = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>()
            .Companies.CountAsync(c => c.Name == "Yalnızca A");

        Assert.Equal(0, visible);
    }

    [Fact]
    public async Task Switching_tenant_in_a_new_scope_targets_the_other_database()
    {
        var a = (await factory.CreateTenantAsync()).Id;
        var b = (await factory.CreateTenantAsync()).Id;
        await factory.CreateCompanyAsync(a, "A");
        await factory.CreateCompanyAsync(b, "B");

        using (var scopeA = factory.OpenScope(a))
        {
            var names = await scopeA.ServiceProvider.GetRequiredService<ModulixDbContext>().Companies.Select(c => c.Name).ToListAsync();
            Assert.Contains("A", names);
            Assert.DoesNotContain("B", names);
        }

        using (var scopeB = factory.OpenScope(b))
        {
            var names = await scopeB.ServiceProvider.GetRequiredService<ModulixDbContext>().Companies.Select(c => c.Name).ToListAsync();
            Assert.Contains("B", names);
            Assert.DoesNotContain("A", names);
        }
    }

    [Fact]
    public void Without_a_tenant_the_resolver_does_not_fall_back_to_a_real_database()
    {
        using var scope = factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantConnectionResolver>();

        // Aktif tenant yoksa gerçek bir iş veritabanına düşülmez: var olmayan, ayrılmış "nöbetçi" veritabanı adı döner.
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(resolver.GetConnectionString(null));
        Assert.Equal(TenantDatabaseNaming.NoActiveTenantCatalog, builder.InitialCatalog);
        Assert.NotEqual("ModulixVerify", builder.InitialCatalog);
    }

    [Fact]
    public void Resolving_an_unknown_tenant_is_rejected()
    {
        using var scope = factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantConnectionResolver>();

        Assert.Throws<InvalidOperationException>(() => resolver.GetConnectionString(Guid.NewGuid()));
    }

    [Fact]
    public async Task Provisioning_is_idempotent_and_does_not_create_a_second_database()
    {
        var tenant = await factory.CreateTenantAsync();
        var databaseName = await factory.TenantDatabaseNameAsync(tenant.Id);

        using var scope = factory.Services.CreateScope();
        var again = await factory.Provisioning(scope).PrepareAsync(tenant.Id, CancellationToken.None);

        // Aynı veritabanı adı döner; yeni veritabanı oluşturulmaz.
        Assert.Equal(databaseName, again);
    }

    [Fact]
    public async Task Provisioned_tenant_database_passes_health_check()
    {
        var tenant = await factory.CreateTenantAsync();

        using var scope = factory.Services.CreateScope();
        Assert.True(await factory.Provisioning(scope).IsHealthyAsync(tenant.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Migration_runner_reports_per_tenant_and_is_safe_to_repeat()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();

        using var scope = factory.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<TenantMigrationRunner>();

        var first = await runner.RunAsync([a.Slug, b.Slug], TenantMigrationRunner.DefaultMaxConcurrency, CancellationToken.None);
        Assert.Equal(0, first.Failed);
        Assert.True(first.Succeeded >= 2);
        Assert.All(first.Results, r => Assert.True(r.Succeeded, r.Detail));

        // İkinci çalıştırma güvenlidir (bekleyen migration yoktur); yine başarılı raporlanır.
        var second = await runner.RunAsync([a.Slug, b.Slug], TenantMigrationRunner.DefaultMaxConcurrency, CancellationToken.None);
        Assert.Equal(0, second.Failed);
    }
}
