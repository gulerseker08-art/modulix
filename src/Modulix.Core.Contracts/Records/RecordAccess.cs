namespace Modulix.Core.Contracts.Records;

/// <summary>
/// İsteği yapan kullanıcının erişim bilgisi (ADR-0010). Modüller bağlı kayıt doğrularken ve ortak kaydın görünürlüğüne
/// karar verirken kullanır; boru hattının uyguladığı kurallarla aynı kaynaktan gelir. Aktif tenant yoksa erişim yoktur.
/// </summary>
public interface IRecordAccess
{
    /// <summary>
    /// Veri kapsamı (Y8): null ise tüm kayıtlar; aksi halde yalnızca sorumlusu bu kullanıcılardan biri olan kayıtlar.
    /// Aktif tenant yoksa boş küme.
    /// </summary>
    Task<IReadOnlySet<Guid>?> GetOwnersAsync(CancellationToken cancellationToken);

    /// <summary>Kullanıcının kayıt türünde işlem izni (modül tenant için açık ve izin rollerinde) var mı?</summary>
    Task<bool> CanAsync(string entityKey, RecordOperation operation, CancellationToken cancellationToken);

    /// <summary>
    /// Kullanıcının tüm rolleri "kapsam dışı ortak kayıtları gizle" diyor mu (ADR-0026 K8)? Evetse kapsam dışı ortak firma ve
    /// kişiler asgari kimlikle bile görünmez. Yönetici, rolü olmayan kişi ve olay işleyicisi için hayırdır.
    /// </summary>
    Task<bool> HidesOutOfScopeSharedRecordsAsync(CancellationToken cancellationToken) => Task.FromResult(false);
}
