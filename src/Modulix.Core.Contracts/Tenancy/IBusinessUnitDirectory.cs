namespace Modulix.Core.Contracts.Tenancy;

/// <summary>
/// Departman ağacının (ADR-0026) bir düğümü. <see cref="PathNames"/> kökten kendine adlardır (ör. "Merkez › Finans"); <see cref="PathIds"/> kökten kendine
/// kimliklerdir ("ve altı" karşılaştırması için).
/// </summary>
public sealed record BusinessUnitInfo(Guid Id, Guid? ParentId, string Name, string PathNames, IReadOnlyList<Guid> PathIds, int Depth, bool IsActive)
{
    /// <summary>Bu departman <paramref name="ancestorId"/>'nin kendisi ya da altında mı?</summary>
    public bool IsWithin(Guid ancestorId) => PathIds.Contains(ancestorId);
}

/// <summary>
/// Aktif tenant'ın departman ağacı ve üyelerin departmanı, modüllere salt okunur (ADR-0042 bölüm 22). Departman ortak organizasyon verisidir ve
/// Core'a aittir: modüller departmanı buradan okur, kendi departman listesi tutmaz. Ağaç ve üyelik değişiklikleri yalnızca Core'un kullanıcı yönetimi
/// akışlarıyla yapılır (departman değişikliği kayıt görünürlüğünü etkiler). Departman adları erişim yetkisi değildir; ağacın tamamı döner.
/// </summary>
public interface IBusinessUnitDirectory
{
    /// <summary>Ağacın tamamı (kapalılar dahil), seviye ve ada göre sıralı. Aktif tenant yoksa boş.</summary>
    Task<IReadOnlyList<BusinessUnitInfo>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Verilen kullanıcıların bu tenant'taki departmanı (kullanıcı → departman). Üyeliği olmayan kullanıcı sonuçta yoktur.</summary>
    Task<IReadOnlyDictionary<Guid, Guid>> UnitsOfUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}
