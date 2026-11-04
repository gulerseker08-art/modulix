using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;

namespace Modulix.Core.Authorization;

/// <summary>
/// Rolün belirli bir kayıt türündeki kapsamı (ADR-0030 K2): rolün varsayılan kapsamının (<see cref="Role.Scope"/>) yerine geçer.
/// Etkin kapsam = tür için istisna varsa o, yoksa rolün varsayılanı. Tenant verisidir; Row-Level Security ile korunur ve
/// rolle aynı tenant'tadır (bileşik yabancı anahtar). Sistem rolünde (yönetici) istisna tutulmaz.
/// </summary>
public sealed class RoleScopeOverride : ITenantEntity, ICompanyShared
{
    private RoleScopeOverride()
    {
        // EF Core için
    }

    public RoleScopeOverride(Guid roleId, string entityKey, RecordScope scope)
    {
        if (roleId == Guid.Empty)
        {
            throw new ArgumentException("RoleId boş olamaz.", nameof(roleId));
        }

        if (!AccessKeys.IsValidEntityKey(entityKey))
        {
            throw new ArgumentException($"Geçersiz kayıt türü anahtarı: '{entityKey}'.", nameof(entityKey));
        }

        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentException("Geçersiz kayıt kapsamı.", nameof(scope));
        }

        Id = Guid.CreateVersion7();
        RoleId = roleId;
        EntityKey = entityKey;
        Scope = scope;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public Guid RoleId { get; private set; }

    /// <summary>Kapsamı değişen kayıt türü (ör. "crm.opportunity"); sahip alanı olan bir tür olmalıdır.</summary>
    public string EntityKey { get; private set; } = string.Empty;

    public RecordScope Scope { get; private set; }

    public void SetScope(RecordScope scope)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentException("Geçersiz kayıt kapsamı.", nameof(scope));
        }

        Scope = scope;
    }
}
