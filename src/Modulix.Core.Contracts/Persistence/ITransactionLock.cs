namespace Modulix.Core.Contracts.Persistence;

/// <summary>
/// Açık işlem süresince bir kaynağa (ör. bir siparişin teslimat kayıtları) tek yazar garantisi verir. Kilit işlemle birlikte bırakılır; kilidi bekleyen istek
/// öncekinin yazdığını okuyabilsin diye alındıktan sonra bağlamın izlenen kayıtları temizlenir (kilitten önce okunan kayıtlar bayat olabilir).
/// Yalnızca <c>RunAtomicAsync</c> gibi açık bir işlem içinde çağrılır.
/// </summary>
public interface ITransactionLock
{
    /// <param name="resource">Kaynak adı; tenant'a özgü kimlik içermelidir (kilit veritabanı genelindedir).</param>
    Task AcquireAsync(string resource, CancellationToken cancellationToken);
}
