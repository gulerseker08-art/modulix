using Microsoft.EntityFrameworkCore.Design;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Api.Development;

/// <summary>
/// Yalnızca 'dotnet ef migrations add' içindir. Modüllerin tabloları modele girsin diye API projesindedir
/// (Infrastructure modülleri tanımaz). Bu komut veritabanına bağlanmaz; bu yüzden gerçek bir şifre gerekmez.
/// Migration'ları uygulamak için 'dotnet ef database update' değil, scripts\db-up.ps1
/// (içinde: dotnet run --project src/Modulix.Api -- migrate) kullanılır.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ModulixDbContext>
{
    private const string DesignTimeConnectionString =
        "Server=localhost,14333;Database=Modulix;User Id=design-time;Password=not-used;TrustServerCertificate=True";

    public ModulixDbContext CreateDbContext(string[] args) =>
        PersistenceSetup.CreateContext(DesignTimeConnectionString, ModuleCatalog.Create(ModulixModules.Create()));
}
