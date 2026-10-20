namespace Modulix.Core.Contracts.Tenancy;

/// <summary>
/// Tenant'a ait kayıt. Bu arayüzü uygulayan her entity iki katmanla korunur (ADR-0002):
/// <list type="number">
///   <item>Uygulama: EF Core global query filter yalnızca aktif tenant'ın satırlarını döndürür;
///   kaydederken <see cref="TenantId"/> aktif tenant'tan otomatik atanır, başkası yazılamaz.</item>
///   <item>Veritabanı: SQL Server Row-Level Security, filtre unutulsa veya atlansa bile başka
///   tenant'ın satırını göstermez ve yazdırmaz. Tablonun migration'ında
///   <c>TenantIsolationSql.AddTable</c> çağrılmalıdır.</item>
/// </list>
/// </summary>
public interface ITenantEntity
{
    Guid TenantId { get; }
}
