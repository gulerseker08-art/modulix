using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Infrastructure.Persistence;

/// <summary>Modüllerin kendi tablolarına erişimi; isteğin <see cref="ModulixDbContext"/>'ini paylaşır (aynı işlem).</summary>
internal sealed class ModuleData(ModulixDbContext db) : IModuleData
{
    public DbSet<TEntity> Set<TEntity>()
        where TEntity : class, ITenantEntity => db.Set<TEntity>();
}

/// <summary>
/// EF modeli bağlam türü başına bir kez kurar ve önbelleğe alır. Model kayıtlı modüllere bağlı olduğu için
/// anahtara modül listesi de girer; modülsüz kurulan bir bağlam modüllü olanın modelini bozmaz.
/// </summary>
internal sealed class ModuleModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is ModulixDbContext modulix
            ? (context.GetType(), modulix.ModelKey, designTime)
            : (context.GetType(), designTime);
}

/// <summary><c>sp_getapplock</c> ile işlem kapsamlı kilit (<see cref="ITransactionLock"/>); 15 saniyede alınamazsa hata verir.</summary>
internal sealed class SqlTransactionLock(ModulixDbContext db) : ITransactionLock
{
    public async Task AcquireAsync(string resource, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = {0}, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000; IF @r < 0 THROW 51000, 'Kaynak kilidi alinamadi.', 1;",
            [resource], cancellationToken);
        db.ChangeTracker.Clear();
    }
}
