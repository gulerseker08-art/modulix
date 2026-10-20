using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Contracts.Persistence;

/// <summary>
/// Modüllerin kendi entity'lerine erişimi; yalnızca modülün kayıt depoları (<c>IRecordStore</c>) kullanır.
/// <list type="bullet">
///   <item>Yalnızca <see cref="ITenantEntity"/> açılır: sorgu filtresi ve Row-Level Security her sorguda geçerlidir.</item>
///   <item>Kaydetme yoktur: değişiklikleri kayıt boru hattı audit ve olayla birlikte, tek seferde yazar.</item>
///   <item>Modelin kendisi modül projesindeki <c>IEntityTypeConfiguration</c> sınıflarından kurulur; her
///   tablo modül anahtarıyla aynı adlı şemada olmalıdır (ör. "crm"), aksi halde uygulama açılmaz.</item>
/// </list>
/// </summary>
public interface IModuleData
{
    DbSet<TEntity> Set<TEntity>()
        where TEntity : class, ITenantEntity;
}
