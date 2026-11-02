using System.Globalization;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Modules.Marketing.Records;

/// <summary>Kayıt depolarının ihtiyaç duyduğu servisler: veri, tenant üyeleri, seçim listeleri, isteği yapan kullanıcı ve iş akışı kapısı.</summary>
internal sealed class MarketingStoreContext(
    IModuleData data, ITenantMembers members, IChoiceLists choiceLists, ITenantContextAccessor tenant, IRecordAccess access,
    ICurrentAccess permissions, SegmentWorkflowGate gate, TimeProvider time, IServiceProvider services, IRecordCommandContext? command = null)
{
    public IModuleData Data => data;

    public ITenantMembers Members => members;

    public IChoiceLists ChoiceLists => choiceLists;

    public IRecordAccess Access => access;

    public ICurrentAccess Permissions => permissions;

    /// <summary>İş akışı kapısı: durum, sürüm ve hesaplama alanları yalnızca kapı açıkken yazılır.</summary>
    public SegmentWorkflowGate Gate => gate;

    public TimeProvider Time => time;

    public string? CommandReason => command?.Reason;

    public Guid? CurrentUserId => tenant.Current?.UserId;

    /// <summary>Başka bir kaydı (ör. ortak taraf) çağıranın yetkisiyle kayıt boru hattından okur; yoksa ya da okunamıyorsa null.</summary>
    public async Task<RecordData?> ReadRecordAsync(string entityKey, Guid id, CancellationToken cancellationToken)
    {
        var pipeline = (IRecordPipeline)services.GetService(typeof(IRecordPipeline))!;
        var result = await pipeline.GetAsync(new RecordRef(entityKey, id), cancellationToken);
        return result.IsSuccess ? result.Value : null;
    }
}

/// <summary>
/// Segment iş akışı kapısı (istek ömürlü): durum, yayın sürümü ve hesaplama özeti genel kayıt uçlarından yazılamaz; yalnızca iş akışı
/// servisi kapıyı açarak boru hattı üzerinden yazar.
/// </summary>
internal sealed class SegmentWorkflowGate
{
    public IReadOnlyList<string>? Permissions { get; private set; }

    public bool IsOpen => Permissions is not null;

    public IDisposable Open(IReadOnlyList<string> permissions)
    {
        Permissions = permissions;
        return new Closer(this);
    }

    private sealed class Closer(SegmentWorkflowGate gate) : IDisposable
    {
        public void Dispose() => gate.Permissions = null;
    }
}

/// <summary>Liste filtresi: değeri okuyup sorguya uygular. Değer geçersizse null döner (doğrulama hatası olur).</summary>
internal delegate IQueryable<TEntity>? RecordFilter<TEntity>(IQueryable<TEntity> source, string value, MarketingStoreContext context);

/// <summary>Filtre değerlerini okuma yardımcıları.</summary>
internal static class FilterValue
{
    public static Guid? Id(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    public static Guid? User(string value, MarketingStoreContext context) =>
        string.Equals(value, "me", StringComparison.OrdinalIgnoreCase) ? context.CurrentUserId : Id(value);

    public static bool? Bool(string value) => bool.TryParse(value, out var flag) ? flag : null;

    public static DateOnly? Date(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
