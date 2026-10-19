using Microsoft.EntityFrameworkCore;

namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// Core ve platform tablolarının DbContext'i. Modüller bu sınıfa erişemez; kayıtlara
/// yalnızca kayıt boru hattı üzerinden ulaşır (talimat Bölüm 3, kural 1).
/// </summary>
public class ModulixDbContext(DbContextOptions<ModulixDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Tablolar sonraki görevlerde eklenir (Görev 4: tenant, user, membership).
        base.OnModelCreating(modelBuilder);
    }
}
