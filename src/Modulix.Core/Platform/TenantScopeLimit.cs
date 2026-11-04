using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Modules;

namespace Modulix.Core.Platform;

/// <summary>
/// Platformun bir tenant için bir kayıt türünde koyduğu görünürlük tavanı (ADR-0030 K4): firmanın rolleri bu türde
/// <see cref="MaxScope"/>'tan geniş kapsam alamaz. Platform geneli kayıttır (RLS dışı); yalnızca platform yöneticisi yazar,
/// firma yöneticisi okur. Kaydı olmayan tür için sınır yoktur. Tavan tek değerdir ve <see cref="RecordScopeRules.Covers"/> ile
/// uygulanır (ekip ile departman birbirini kapsamaz).
/// </summary>
public sealed class TenantScopeLimit
{
    private TenantScopeLimit()
    {
        // EF Core için
    }

    public TenantScopeLimit(Guid tenantId, string entityKey, RecordScope maxScope, bool appliesToAdministrators = false)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId boş olamaz.", nameof(tenantId));
        }

        if (!AccessKeys.IsValidEntityKey(entityKey))
        {
            throw new ArgumentException($"Geçersiz kayıt türü anahtarı: '{entityKey}'.", nameof(entityKey));
        }

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        EntityKey = entityKey;
        Set(maxScope, appliesToAdministrators);
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string EntityKey { get; private set; } = string.Empty;

    /// <summary>İzin verilen en geniş kapsam.</summary>
    public RecordScope MaxScope { get; private set; }

    /// <summary>
    /// Tavan yönetici sistem rolünü de bağlar mı (ADR-0030 K6)? Varsayılan hayır: yönetici her zaman tüm kayıtları görür.
    /// Evet ise yönetici de tavana indirgenir; kendi kayıtlarının altına inmez.
    /// </summary>
    public bool AppliesToAdministrators { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Set(RecordScope maxScope, bool appliesToAdministrators)
    {
        if (!Enum.IsDefined(maxScope))
        {
            throw new ArgumentException("Geçersiz kayıt kapsamı.", nameof(maxScope));
        }

        MaxScope = maxScope;
        AppliesToAdministrators = appliesToAdministrators;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
