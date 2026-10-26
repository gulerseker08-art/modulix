using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Organization;

/// <summary>
/// Bir üyeliğin bir işletici şirkete görevlendirmesi (ADR-0008, adım 2): rol izni "ne yapabilir"i, şirket bağı "hangi
/// şirkette" sorusunu belirler. Yönetici (sistem rolü) bütün şirketlere erişir; bu bağ, yönetici olmayan kullanıcılar
/// içindir. Yapılandırma verisidir ve ortak sınıftadır (<see cref="ICompanyShared"/>): kendisi şirkete göre süzülmez.
/// Tenant verisidir; Row-Level Security ile korunur.
///
/// Geçici vekâlet (ADR-0008, adım 5): görevin başlangıç/bitiş tarihi olabilir. İkisi de boşsa süresiz kalıcı görevdir;
/// <see cref="ValidUntil"/> doluysa geçici vekâlettir ve süresi dolunca erişim kendiliğinden kapanır (erişilebilir şirket
/// sorgusu <see cref="IsActiveAt"/> penceresini uygular). Dolum ayrı bir denetim kaydı istemez; verme/alma/tarih
/// değişikliği denetlenir, grant kaydındaki <see cref="ValidUntil"/> dolum anını saklar.
///
/// Varsayılan şube (ADR-0008, adım 5): görevin isteğe bağlı idari varsayılan şubesi (<see cref="BranchId"/>). Şube bir
/// kapsam sınırı değildir; yalnızca idaridir ve hiçbir kayıt süzgecine girmez.
/// </summary>
public sealed class MembershipCompany : ITenantEntity, ICompanyShared
{
    private MembershipCompany()
    {
        // EF Core için
    }

    public MembershipCompany(Guid membershipId, Guid companyId, DateTimeOffset? validFrom = null, DateTimeOffset? validUntil = null, Guid? branchId = null)
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
        SetValidity(validFrom, validUntil);
        SetBranch(branchId);
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public Guid MembershipId { get; private set; }

    public Guid CompanyId { get; private set; }

    /// <summary>Geçici vekâletin başlangıcı; boşsa görev baştan geçerlidir.</summary>
    public DateTimeOffset? ValidFrom { get; private set; }

    /// <summary>Geçici vekâletin bitişi (dışlayıcı); boşsa görev süresizdir. Bu ana gelince erişim kendiliğinden kapanır.</summary>
    public DateTimeOffset? ValidUntil { get; private set; }

    /// <summary>Görevin idari varsayılan şubesi (<see cref="Branch"/>); kapsam sınırı değildir. Boşsa şube seçilmemiştir.</summary>
    public Guid? BranchId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Görev verilen anda etkin mi? Erişilebilir şirket sorgusu ve aktif şirket doğrulaması bununla süzülür.</summary>
    public bool IsActiveAt(DateTimeOffset now) =>
        (ValidFrom is null || now >= ValidFrom) && (ValidUntil is null || now < ValidUntil);

    /// <summary>
    /// Geçerlilik penceresini ayarlar. Başlangıç ve bitiş birlikte verilmişse bitiş başlangıçtan sonra olmalıdır. Tek
    /// başına bitiş geçmişte olabilir: süresi dolmuş (kapanmış) bir vekâlet de geçerli bir kayıttır; "geçmişte biten bir
    /// vekâlet oluşturma" yasağı servis/uç katmanındadır, domain bu durumu temsil edebilmelidir.
    /// </summary>
    public void SetValidity(DateTimeOffset? validFrom, DateTimeOffset? validUntil)
    {
        if (validFrom is { } from && validUntil is { } until && until <= from)
        {
            throw new ArgumentException("Vekâletin bitişi başlangıcından sonra olmalıdır.", nameof(validUntil));
        }

        ValidFrom = validFrom;
        ValidUntil = validUntil;
    }

    /// <summary>Varsayılan şubeyi ayarlar (boş = şube yok). Şubenin bu şirkete ait olduğu servis katmanında doğrulanır.</summary>
    public void SetBranch(Guid? branchId) =>
        BranchId = branchId == Guid.Empty ? null : branchId;
}
