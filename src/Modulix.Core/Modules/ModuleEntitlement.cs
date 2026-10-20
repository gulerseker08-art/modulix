namespace Modulix.Core.Modules;

/// <summary>
/// Bir tenant'ın bir modülü kullanma hakkı. Platform geneli kayıttır (abonelik/plan tarafı);
/// tenant kullanıcıları değiştiremez. Kaydı olmayan modül o tenant için kapalıdır; Core her zaman açıktır.
/// </summary>
public sealed class ModuleEntitlement
{
    private ModuleEntitlement()
    {
        // EF Core için
    }

    public ModuleEntitlement(Guid tenantId, string moduleKey)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId boş olamaz.", nameof(tenantId));
        }

        if (!AccessKeys.IsValidModuleKey(moduleKey))
        {
            throw new ArgumentException($"Geçersiz modül anahtarı: '{moduleKey}'.", nameof(moduleKey));
        }

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        ModuleKey = moduleKey;
        Enable();
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string ModuleKey { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Enable()
    {
        IsEnabled = true;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Kapatma veriyi silmez; modül yeniden açılınca kayıtlar olduğu gibi görünür.</summary>
    public void Disable()
    {
        IsEnabled = false;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
