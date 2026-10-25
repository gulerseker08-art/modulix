namespace Modulix.Core.Contracts.Tenancy;

/// <summary>
/// Sunucuda doğrulanmış aktif şirket bağlamı (ADR-0008, adım 2). Aktif tenant bağlamının (<see cref="TenantContext"/>)
/// üstüne biner: hangi işletici şirketlere erişildiği ve o anki aktif şirket. İstemcinin gönderdiği bir başlık veya
/// parametreden değil, oturumdan belirlenir ve her istekte erişilebilir şirketlere karşı yeniden doğrulanır.
/// </summary>
/// <param name="ActiveCompanyId">
/// Oturumun varsayılan çalışma kapsamı; yalnızca <see cref="AccessibleCompanyIds"/> içinden bir değer olabilir. Kişinin
/// hiç şirket erişimi yoksa <c>null</c>: yalnızca ortak (<see cref="ICompanyShared"/>) veriyi görür.
/// </param>
/// <param name="AccessibleCompanyIds">Kişinin erişebildiği şirketler. Yönetici için tenant'ın bütün şirketleridir.</param>
/// <param name="IsAllCompanies">Kişi tenant yöneticisidir (sistem rolü) ve bütün şirketlere erişir.</param>
public sealed record CompanyContext(Guid? ActiveCompanyId, IReadOnlySet<Guid> AccessibleCompanyIds, bool IsAllCompanies)
{
    /// <summary>Verilen şirket bu oturumda erişilebilir mi? Aktif şirketi değiştirmek ve kayıt yazmak bununla denetlenir.</summary>
    public bool CanAccess(Guid companyId) => AccessibleCompanyIds.Contains(companyId);
}

/// <summary>İsteğin aktif şirket bağlamı. Aktif tenant yoksa <c>null</c>.</summary>
public interface ICompanyContextAccessor
{
    CompanyContext? Current { get; }
}
