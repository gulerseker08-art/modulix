namespace Modulix.Core.Contracts.Communication;

/// <summary>
/// Pazarlama iznini reddeden kayıt (ADR-0025, dilim 7): bir yanıtla gelen "bir daha yazmayın" talebinin uygulanması. İzin kaydının sahibi çekirdektir; modüller
/// tercih tablosuna yazmaz, bu sözleşmeyle bildirir. Kaynak "abonelikten çıkma" (kişinin kendi talebi) olarak kaydedilir; zaten reddedilmişse değişiklik yazılmaz.
/// </summary>
public interface IOptOutRecorder
{
    /// <returns>Yeni ret yazıldıysa true; zaten reddedilmişti ya da adres geçersizse false.</returns>
    Task<bool> RecordAsync(CommunicationChannel channel, string address, string reason, CancellationToken cancellationToken);
}
