namespace Modulix.Core.Platform;

/// <summary>
/// Kullanıcı (üyelik) yönetiminin kapsamı (ADR-0026). <c>core.users.manage</c> tenant geneli bir izindir; ama yönetici olmayan
/// işlemi yapan yalnızca kendi kapsamındaki departmanların kullanıcılarını listeler, görür ve yönetir. Kapsam dışı bir üyelik,
/// başka tenant'ınki gibi "bulunamadı" sayılır; kimsin, hangi rollerin var gibi bilgi verilmez. Tüm kayıtlar kapsamındaki işlemi
/// yapan ve platform/sistem bağlamı (üyeliği olmayan) kısıtlanmaz.
/// </summary>
public interface IMembershipScope
{
    /// <summary>
    /// Verilen üyelik aktif tenant'ta mı ve işlemi yapan onu yönetebilir mi? İkisinden biri değilse <c>false</c>
    /// (çağıran "bulunamadı" döner). Aktif tenant yoksa <c>false</c>.
    /// </summary>
    Task<bool> CanManageAsync(Guid membershipId, CancellationToken cancellationToken);
}
