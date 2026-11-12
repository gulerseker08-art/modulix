using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Fleet;
using Modulix.Core.Contracts.Persistence;

namespace Modulix.Modules.Logistics.Records;

/// <summary>
/// Kayıt depolarının ihtiyaç duyduğu servisler. Tenant filtresi ve RLS <see cref="IModuleData"/> üzerinden uygulanır. Araçlar Filo
/// modülündedir; lojistik araç tablosuna dokunmaz, <see cref="IVehicleDirectory"/> ile okur (ADR-0038 K1).
/// </summary>
internal sealed class LogisticsStoreContext(IModuleData data, IChoiceLists choiceLists, IVehicleDirectory vehicles, LogisticsWorkflowGate gate)
{
    public LogisticsWorkflowGate Gate => gate;

    public IModuleData Data => data;

    public IChoiceLists ChoiceLists => choiceLists;

    public IVehicleDirectory Vehicles => vehicles;
}

/// <summary>Liste filtresi: değeri okuyup sorguya uygular. Değer geçersizse null döner (doğrulama hatası olur).</summary>
internal delegate IQueryable<TEntity>? RecordFilter<TEntity>(IQueryable<TEntity> source, string value);

/// <summary>Filtre değerlerini okuma yardımcıları.</summary>
internal static class FilterValue
{
    public static Guid? Id(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    public static DateOnly? Day(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) ? day : null;
}

/// <summary>
/// İş akışı kapısı (ADR-0038 K7): durum ve gerekçe alanlarını yalnızca iş akışı komutu yazar. Komut kapıyı eylemin izniyle açar, kayıt boru
/// hattından güncelleme yapar (izin, audit, olay, sürüm denetimi depoda), sonra kapatır. Kapı kapalıyken genel uçtan gelen durum değeri yok
/// sayılır; sevkiyat kapı kapalıyken oluşturulamaz (yalnızca talep onayıyla doğar).
/// </summary>
internal sealed class LogisticsWorkflowGate
{
    /// <summary>Açıksa boru hattının bu işlem için istediği izin (eylemin izni, ör. talep onayı).</summary>
    public string? Permission { get; private set; }

    /// <summary>Yazılacak durum ve gerekçe; oluşturmada boş (kayıt varsayılan durumla doğar).</summary>
    public string? Status { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>Geçişle bağlanan kayıt (talep onayında açılan sevkiyat).</summary>
    public Guid? LinkedId { get; private set; }

    public bool IsOpen => Permission is not null;

    public IDisposable Open(string permission, string? status, string? reason, Guid? linkedId = null)
    {
        Permission = permission;
        Status = status;
        Reason = reason;
        LinkedId = linkedId;
        return new Closer(this);
    }

    private sealed class Closer(LogisticsWorkflowGate gate) : IDisposable
    {
        public void Dispose()
        {
            gate.Permission = null;
            gate.Status = null;
            gate.Reason = null;
            gate.LinkedId = null;
        }
    }
}
