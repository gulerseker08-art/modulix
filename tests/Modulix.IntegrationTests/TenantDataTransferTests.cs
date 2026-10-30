using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Infrastructure.Tenancy;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0020 Dilim 3: bir tenant'ın bütün verisi TenantId ile eksiksiz dışa aktarılır ve silinir; diğer tenant'lar
/// etkilenmez, kullanıcı hesapları kalır.
/// </summary>
public class TenantDataTransferTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Export_writes_only_the_tenants_rows_as_json_files_with_a_manifest()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(a);
        var companyA = await factory.CreateCompanyAsync(a.Id, $"A Şirketi {Guid.NewGuid():N}");
        var companyB = await factory.CreateCompanyAsync(b.Id, $"B Şirketi {Guid.NewGuid():N}");
        var directory = Path.Combine(Path.GetTempPath(), "modulix-export-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var scope = factory.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<ITenantDataTransfer>().ExportAsync(a.Id, directory, CancellationToken.None);

            Assert.Equal(a.Slug, result.Slug);
            Assert.Contains(result.Tables, t => t is { Schema: "core", Table: "Companies", Rows: 1 });
            Assert.Contains(result.Tables, t => t is { Schema: "platform", Table: "Memberships", Rows: 1 });
            Assert.Contains(result.Tables, t => t.Schema == "crm"); // modül tabloları kataloğdan bulunur

            var companies = await File.ReadAllTextAsync(Path.Combine(directory, "core.Companies.json"));
            Assert.Contains(companyA.Name, companies);
            Assert.DoesNotContain(companyB.Name, companies);

            var memberships = await File.ReadAllTextAsync(Path.Combine(directory, "platform.Memberships.json"));
            Assert.Contains(user.Id.ToString(), memberships);

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json")));
            Assert.Equal(a.Id.ToString(), manifest.RootElement.GetProperty("tenantId").GetString());
            Assert.Equal(result.Tables.Count, manifest.RootElement.GetProperty("tables").GetArrayLength());

            using var tenant = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "tenant.json")));
            Assert.Equal(a.Slug, tenant.RootElement.GetProperty("slug").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Delete_removes_every_row_of_the_tenant_but_keeps_other_tenants_and_user_accounts()
    {
        var a = await factory.CreateTenantAsync();
        var b = await factory.CreateTenantAsync();
        var user = await factory.CreateUserAsync(a, b);
        await factory.MakeAdministratorAsync(a.Id, user); // rol ataması: üyeliğe yabancı anahtarla bağlı satır
        var companyA = await factory.CreateCompanyAsync(a.Id, "Silinecek");
        var companyB = await factory.CreateCompanyAsync(b.Id, "Kalacak");

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ITenantDataTransfer>().DeleteAsync(a.Id, CancellationToken.None);

        Assert.True(result.TotalRows >= 4, "şirket, üyelik, rol ve tenant satırları silinmeli");
        Assert.Contains(result.Tables, t => t is { Schema: "platform", Table: "Tenants", Rows: 1 });

        const string companies = "SELECT COUNT(*) FROM [core].[Companies] WHERE [TenantId] = @t AND [Id] = @id";
        Assert.Equal(0, await factory.ScalarAsync<int>(a.Id, companies, ("@t", a.Id), ("@id", companyA.Id)));
        Assert.Equal(1, await factory.ScalarAsync<int>(b.Id, companies, ("@t", b.Id), ("@id", companyB.Id)));
        Assert.Equal(0, await factory.ScalarAsync<int>(null, "SELECT COUNT(*) FROM [platform].[Tenants] WHERE [Id] = @t", ("@t", a.Id)));
        Assert.Equal(0, await factory.ScalarAsync<int>(null, "SELECT COUNT(*) FROM [platform].[Memberships] WHERE [TenantId] = @t", ("@t", a.Id)));
        Assert.Equal(1, await factory.ScalarAsync<int>(null, "SELECT COUNT(*) FROM [platform].[Memberships] WHERE [TenantId] = @t", ("@t", b.Id)));
        Assert.Equal(1, await factory.ScalarAsync<int>(null, "SELECT COUNT(*) FROM [platform].[Users] WHERE [Id] = @u", ("@u", user.Id)));
    }

    [Fact]
    public async Task Unknown_tenant_is_rejected()
    {
        using var scope = factory.Services.CreateScope();
        var transfer = scope.ServiceProvider.GetRequiredService<ITenantDataTransfer>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => transfer.DeleteAsync(Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transfer.ExportAsync(Guid.NewGuid(), Path.GetTempPath(), CancellationToken.None));
    }
}
