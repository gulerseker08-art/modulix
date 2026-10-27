using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Tenancy;

/// <summary>
/// Platform ve tenant veritabanı bağlantıları (ADR-0009). Uygulama (app) ve migration (migrator) için ayrı şablonlar
/// tutulur; tenant bağlantıları şablonun yalnızca veritabanı adı değiştirilerek türetilir.
/// </summary>
public sealed class PlatformDatabaseOptions(string appConnectionString, string migrationsConnectionString)
{
    /// <summary>Platform veritabanına uygulama bağlantısı; tenant app bağlantıları için de şablondur.</summary>
    public string AppConnectionString { get; } = !string.IsNullOrWhiteSpace(appConnectionString)
        ? appConnectionString
        : throw new ArgumentException("Platform app bağlantısı boş olamaz.", nameof(appConnectionString));

    /// <summary>Platform veritabanına migration bağlantısı; tenant DB oluşturma/migration için de şablondur.</summary>
    public string MigrationsConnectionString { get; } = !string.IsNullOrWhiteSpace(migrationsConnectionString)
        ? migrationsConnectionString
        : throw new ArgumentException("Platform migration bağlantısı boş olamaz.", nameof(migrationsConnectionString));
}

/// <summary>
/// Tenant→veritabanı eşleştirmesini platform defterinden okur ve kısa süreli önbellekler (ADR-0009, "Güvenli tenant
/// çözümleme"). Önbellek üyelik iptali/askıya alma gibi değişikliklerde <see cref="Invalidate"/> ile geçersizleşir;
/// ayrıca kısa ömürlüdür, böylece geçersizleştirme kaçsa bile en geç TTL sonunda düzelir.
/// </summary>
public interface ITenantDatabaseRegistry
{
    /// <summary>Tenant'ın veritabanı kaydını döndürür; tenant yoksa veya veritabanı atanmadıysa <c>null</c>.</summary>
    TenantDatabaseInfo? Find(Guid tenantId);

    void Invalidate(Guid tenantId);
}

internal sealed class TenantDatabaseRegistry(PlatformDatabaseOptions platform, IMemoryCache cache) : ITenantDatabaseRegistry
{
    /// <summary>Üyelik iptali/askıya alma kaçan geçersizleştirmede bile en geç bu süre sonunda görünür olur.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private static string Key(Guid tenantId) => $"tenant-db:{tenantId}";

    public TenantDatabaseInfo? Find(Guid tenantId)
    {
        if (cache.TryGetValue<TenantDatabaseInfo?>(Key(tenantId), out var cached))
        {
            return cached;
        }

        var info = Read(tenantId);
        cache.Set(Key(tenantId), info, CacheDuration);
        return info;
    }

    public void Invalidate(Guid tenantId) => cache.Remove(Key(tenantId));

    private TenantDatabaseInfo? Read(Guid tenantId)
    {
        using var connection = new SqlConnection(platform.AppConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT [DatabaseName], [ProvisioningStatus] FROM [{DatabaseSchemas.Platform}].[Tenants] WHERE [Id] = @id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = tenantId;
        command.Parameters.Add(parameter);

        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }

        return new TenantDatabaseInfo(tenantId, reader.GetString(0), (TenantProvisioningStatus)reader.GetInt32(1));
    }
}

/// <summary>
/// Tenant'ın veritabanı bağlantısını çözer (ADR-0009). Aktif tenant yoksa <b>varsayılan iş veritabanına düşmez</b>:
/// kullanılamaz bir "nöbetçi" bağlantı döner (var olmayan ayrılmış bir veritabanı adı). Böylece minimal API'de servis
/// bağımlılıkları (ör. kayıt boru hattı) uç nokta süzgecinden önce oluşturulsa bile tenant DbContext'i kurulabilir;
/// gerçek veri erişimi aktif tenant gerektiren uç nokta süzgeciyle (403) engellenir. Bu nöbetçi bağlantıyla yapılan bir
/// sorgu bağlanamaz (açık hata). Tenant bilinmiyorsa (defterde yok) işlem sertçe reddedilir.
/// İstemci veritabanı adı veya bağlantı seçemez.
/// </summary>
public interface ITenantConnectionResolver
{
    /// <summary>Tenant'ın uygulama (app) bağlantısı; aktif tenant yoksa kullanılamaz nöbetçi bağlantı; tenant bilinmiyorsa hata.</summary>
    string GetConnectionString(Guid? tenantId);
}

internal sealed class TenantConnectionResolver(PlatformDatabaseOptions platform, ITenantDatabaseRegistry registry) : ITenantConnectionResolver
{
    public string GetConnectionString(Guid? tenantId)
    {
        if (tenantId is not { } id || id == Guid.Empty)
        {
            // Varsayılan iş veritabanına düşmeyiz; kullanılamaz nöbetçi bağlantı döneriz (yalnızca kurulum için; sorgu bağlanamaz).
            return new SqlConnectionStringBuilder(platform.AppConnectionString)
            {
                InitialCatalog = TenantDatabaseNaming.NoActiveTenantCatalog,
                ConnectTimeout = 2,
            }.ConnectionString;
        }

        var info = registry.Find(id)
            ?? throw new InvalidOperationException($"Tenant veritabanı bulunamadı ({id}); işlem reddedildi (ADR-0009).");

        return TenantDatabaseNaming.ConnectionFor(platform.AppConnectionString, info.DatabaseName);
    }
}
