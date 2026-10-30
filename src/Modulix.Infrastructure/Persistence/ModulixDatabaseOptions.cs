namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// Tek paylaşımlı veritabanının (ADR-0002, ADR-0020) iki bağlantısı: uygulama (<c>modulix_app</c>, yalnızca veri) ve
/// migration (<c>modulix_migrator</c>, şema değiştirir). Uygulama süreci veri erişimi için yalnızca ilkini kullanır;
/// ikincisi 'migrate' komutu içindir. Tenant başına bağlantı veya veritabanı yoktur; izolasyon satır düzeyindedir.
/// </summary>
public sealed class ModulixDatabaseOptions(string appConnectionString, string migrationsConnectionString)
{
    public string AppConnectionString { get; } = !string.IsNullOrWhiteSpace(appConnectionString)
        ? appConnectionString
        : throw new ArgumentException("Uygulama bağlantısı boş olamaz.", nameof(appConnectionString));

    public string MigrationsConnectionString { get; } = !string.IsNullOrWhiteSpace(migrationsConnectionString)
        ? migrationsConnectionString
        : throw new ArgumentException("Migration bağlantısı boş olamaz.", nameof(migrationsConnectionString));
}
