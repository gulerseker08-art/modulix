namespace Modulix.Infrastructure.Tenancy;

/// <summary>
/// Tenant'ın veritabanı bağlantısını seçer (talimat Bölüm 3, kural 8). Faz 1'de tüm tenant'lar
/// tek veritabanındadır; Faz 3'te bir tenant'a ayrı veritabanı verilirse yalnızca bu arayüzün
/// uygulaması değişir.
/// </summary>
public interface ITenantConnectionResolver
{
    string GetConnectionString(Guid? tenantId);
}

internal sealed class SharedDatabaseConnectionResolver(string connectionString) : ITenantConnectionResolver
{
    public string GetConnectionString(Guid? tenantId) => connectionString;
}
