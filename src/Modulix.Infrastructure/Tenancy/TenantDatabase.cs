using Microsoft.Data.SqlClient;
using Modulix.Core.Platform;

namespace Modulix.Infrastructure.Tenancy;

/// <summary>
/// Bir tenant'ın iş veritabanının platform defterindeki kaydı (ADR-0009): hangi veritabanı ve kurulum durumu.
/// Bağlantı cümlesi bundan ve şablon bağlantıdan türetilir; kimlik bilgisi tenant başına saklanmaz.
/// </summary>
public sealed record TenantDatabaseInfo(Guid TenantId, string DatabaseName, TenantProvisioningStatus Status);

/// <summary>
/// Güvenli tenant veritabanı adı üretir ve şablon bağlantıdan tenant bağlantısı türetir (ADR-0009).
/// Veritabanı adları parametrelenemediği için ad üretimi sabit, güvenli bir karakter kümesiyle sınırlıdır;
/// böylece <c>CREATE DATABASE [ad]</c> enjeksiyona açık olmaz.
/// </summary>
public static class TenantDatabaseNaming
{
    /// <summary>Normal tenant veritabanlarının ön eki.</summary>
    public const string Prefix = "modulix_tenant_";

    /// <summary>
    /// Aktif tenant yokken kullanılan, var olmayan ayrılmış veritabanı adı (ADR-0009): tenant DbContext'i kurulabilsin
    /// diye döner ama bu bağlantıyla sorgu bağlanamaz; hiçbir gerçek iş veritabanına düşülmez.
    /// </summary>
    public const string NoActiveTenantCatalog = "modulix_no_active_tenant";

    /// <summary>Benzersiz, güvenli bir veritabanı adı üretir (ör. <c>modulix_tenant_ab12...</c>).</summary>
    public static string Create() => Prefix + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Şablon bağlantının (platform) yalnızca veritabanı adını <paramref name="databaseName"/> ile değiştirir.
    /// Sunucu, kimlik doğrulama ve havuz ayarları şablondan gelir; tenant başına kimlik bilgisi yoktur.
    /// </summary>
    public static string ConnectionFor(string templateConnectionString, string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        var builder = new SqlConnectionStringBuilder(templateConnectionString)
        {
            InitialCatalog = databaseName,
        };
        return builder.ConnectionString;
    }
}
