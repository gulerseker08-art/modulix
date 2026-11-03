namespace Modulix.Core.Contracts.Records;

/// <summary>
/// İsteği yapan kullanıcının erişim bilgisi (ADR-0010). Modüller bağlı kayıt doğrularken ve ortak kaydın görünürlüğüne
/// karar verirken kullanır; boru hattının uyguladığı kurallarla aynı kaynaktan gelir. Aktif tenant yoksa erişim yoktur.
/// </summary>
public interface IRecordAccess
{
    /// <summary>
    /// Veri kapsamı (Y8): null ise tüm kayıtlar; aksi halde yalnızca sorumlusu bu kullanıcılardan biri olan kayıtlar.
    /// Kapsam kayıt türüne göre sorulur (ADR-0028): <paramref name="entityKey"/> kapsamı süzülecek kayıt türünün anahtarıdır
    /// (ör. <c>crm.opportunities</c>). Aktif tenant yoksa boş küme.
    /// </summary>
    Task<IReadOnlySet<Guid>?> GetOwnersAsync(string entityKey, CancellationToken cancellationToken);

    /// <summary>Kullanıcının kayıt türünde işlem izni (modül tenant için açık ve izin rollerinde) var mı?</summary>
    Task<bool> CanAsync(string entityKey, RecordOperation operation, CancellationToken cancellationToken);
}
