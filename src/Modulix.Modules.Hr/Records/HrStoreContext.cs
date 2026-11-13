using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Modules.Hr.Records;

/// <summary>Kayıt depolarının ihtiyaç duyduğu servisler. Tenant filtresi ve RLS <see cref="IModuleData"/> üzerinden uygulanır.</summary>
internal sealed class HrStoreContext(
    IModuleData data, IChoiceLists choiceLists, ITenantMembers members, ITenantContextAccessor tenant, ICurrentAccess access, HrWorkflowGate gate)
{
    public HrWorkflowGate Gate => gate;

    /// <summary>İsteği yapan kullanıcı (aktif tenant yoksa null).</summary>
    public Guid? CurrentUserId => tenant.Current?.UserId;

    public ICurrentAccess Access => access;

    public IModuleData Data => data;

    public IChoiceLists ChoiceLists => choiceLists;

    public ITenantMembers Members => members;
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
/// İş akışı kapısı (Lojistik kalıbı): durum yalnızca komutla değişir. Komut kapıyı açar, boru hattından güncelleme ister; depo kapı açıkken
/// durumu, gerekçeyi ve kararı verenin bilgisini yazar. Kapı kapalıyken genel uçtan gelen durum değeri yok sayılır.
/// </summary>
internal sealed class HrWorkflowGate
{
    /// <summary>Açıksa boru hattının bu işlem için istediği izin (eylemin izni).</summary>
    public string? Permission { get; private set; }

    public string? Status { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>Gönderimde yazılacak onaylayan (masraf beyanı; gönderim anındaki yönetici).</summary>
    public Guid? ApproverUserId { get; private set; }

    public bool IsOpen => Permission is not null;

    public IDisposable Open(string permission, string status, string? reason, Guid? approverUserId = null)
    {
        Permission = permission;
        Status = status;
        Reason = reason;
        ApproverUserId = approverUserId;
        return new Closer(this);
    }

    private sealed class Closer(HrWorkflowGate gate) : IDisposable
    {
        public void Dispose()
        {
            gate.Permission = null;
            gate.Status = null;
            gate.Reason = null;
            gate.ApproverUserId = null;
        }
    }
}
