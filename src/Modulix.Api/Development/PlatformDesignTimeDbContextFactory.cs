using Microsoft.EntityFrameworkCore.Design;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Api.Development;

/// <summary>
/// Yalnızca 'dotnet ef migrations add ... --context PlatformDbContext' içindir (ADR-0009). Veritabanına bağlanmaz;
/// gerçek bir şifre gerekmez. Migration'ları uygulamak için scripts\db-up.ps1 (dotnet run -- migrate) kullanılır.
/// </summary>
public sealed class PlatformDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    private const string DesignTimeConnectionString =
        "Server=localhost,14333;Database=Modulix;User Id=design-time;Password=not-used;TrustServerCertificate=True";

    public PlatformDbContext CreateDbContext(string[] args) =>
        PersistenceSetup.CreatePlatformContext(DesignTimeConnectionString);
}
