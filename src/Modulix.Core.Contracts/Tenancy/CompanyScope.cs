namespace Modulix.Core.Contracts.Tenancy;

/// <summary>
/// Tenant'ın bütün işletici şirketlerinin gördüğü ortak kayıt (ADR-0008): taraf, kişi, rol, ayar gibi ana veri.
/// Her tenant tablosu <see cref="ICompanyShared"/> ya da <see cref="ICompanyScoped"/> uygular; sınıfsız tablo testi kırar.
/// </summary>
public interface ICompanyShared;

/// <summary>
/// Bir işletici şirkete ait işlem veya finans kaydı (ADR-0008): fatura, cari hareket, kasa gibi. Yalnızca erişimi olan
/// şirketlerde görünür.
/// </summary>
public interface ICompanyScoped
{
    Guid CompanyId { get; }
}
