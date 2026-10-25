using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Organization;

/// <summary>
/// Bir üyeliğin bir işletici şirkete görevlendirmesi (ADR-0008, adım 2): rol izni "ne yapabilir"i, şirket bağı "hangi
/// şirkette" sorusunu belirler. Yönetici (sistem rolü) bütün şirketlere erişir; bu bağ, yönetici olmayan kullanıcılar
/// içindir. Yapılandırma verisidir ve ortak sınıftadır (<see cref="ICompanyShared"/>): kendisi şirkete göre süzülmez.
/// Tenant verisidir; Row-Level Security ile korunur.
/// </summary>
public sealed class MembershipCompany : ITenantEntity, ICompanyShared
{
    private MembershipCompany()
    {
        // EF Core için
    }

    public MembershipCompany(Guid membershipId, Guid companyId)
    {
        if (membershipId == Guid.Empty)
        {
            throw new ArgumentException("MembershipId boş olamaz.", nameof(membershipId));
        }

        if (companyId == Guid.Empty)
        {
            throw new ArgumentException("CompanyId boş olamaz.", nameof(companyId));
        }

        Id = Guid.CreateVersion7();
        MembershipId = membershipId;
        CompanyId = companyId;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public Guid MembershipId { get; private set; }

    public Guid CompanyId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
