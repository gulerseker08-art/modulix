using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Contracts.Persistence;

/// <summary>
/// Yüksek hacimli, kayıt boru hattından geçmeyen günlük satırı (ör. kampanya teslimat kuyruğu). Her satır için denetim kaydı ve olay yazmak anlamsızdır;
/// satırın durum geçişleri bir üst kaydın (çalıştırma) denetimli eylemleriyle izlenir. Yalnızca bu işaret arayüzünü uygulayan entity
/// <see cref="IJournalWriter"/> ile yazılabilir; ana veri ve iş kayıtları boru hattından geçer.
/// </summary>
public interface IJournalEntity : ITenantEntity;

/// <summary>
/// Günlük satırlarını toplu ekler ve izlenen değişiklikleri kaydeder (modüller <see cref="IModuleData"/> ile kaydetmez). Tenant filtresi ve RLS
/// geçerlidir. Çağıran, bu işlem sırasında bağlamda başka bekleyen değişiklik bırakmamalıdır.
/// </summary>
public interface IJournalWriter
{
    void Add(IJournalEntity entity);

    Task SaveAsync(CancellationToken cancellationToken);
}
