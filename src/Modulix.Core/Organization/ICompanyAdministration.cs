using Modulix.Core.Platform;

namespace Modulix.Core.Organization;

/// <summary>
/// İstek kurallara uygun ama tenant'ın durumuyla çelişiyor: aynı adda şirket açmak gibi. API 409 döner.
/// </summary>
public sealed class CompanyConflictException(string message) : Exception(message);

/// <summary>
/// Aktif tenant'ın işletici şirketleri ve kullanıcıların şirket görevleri (ADR-0008, adım 2). Yapılandırma verisidir;
/// kayıt boru hattından değil, bu servisten yazılır ve değişiklik aynı işlemde denetim kaydına geçer. Aktif tenant yoksa
/// yazma işlemleri <see cref="InvalidOperationException"/> fırlatır. Aynı ad için <see cref="CompanyConflictException"/>,
/// geçersiz ad veya tenant'ta bulunmayan şirket için <see cref="ArgumentException"/> fırlatır.
/// </summary>
public interface ICompanyAdministration
{
    /// <summary>Aktif tenant'ın şirketleri, ada göre sıralı.</summary>
    Task<IReadOnlyList<CompanySummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Yeni işletici şirket açar (varsayılan değildir); adı tenant içinde tekil olmalıdır.</summary>
    Task<CompanySummary> CreateAsync(string name, CancellationToken cancellationToken);

    /// <summary>Şirketi yeniden adlandırır. Şirket bulunamazsa <c>null</c>.</summary>
    Task<CompanySummary?> RenameAsync(Guid companyId, string name, CancellationToken cancellationToken);

    /// <summary>Üyeliğin görevlendirildiği şirketlerin kimlikleri. Üyelik bu tenant'ta değilse <c>null</c>.</summary>
    Task<IReadOnlyList<Guid>?> GetMembershipCompaniesAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>
    /// Üyeliğin şirket görevlerini verilen kümeye eşitler. Yönetici (sistem rolü) zaten bütün şirketlere eriştiği için
    /// görevleri kayıt bütünlüğü içindir. Üyelik bu tenant'ta değilse <c>false</c>, geçersiz şirket için <see cref="ArgumentException"/>.
    /// </summary>
    Task<bool> SetMembershipCompaniesAsync(Guid membershipId, IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken);
}
