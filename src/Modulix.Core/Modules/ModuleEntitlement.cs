namespace Modulix.Core.Modules;

/// <summary>Modül hakkının kaynağı (ADR-0028): hak neden var?</summary>
public enum EntitlementSource
{
    /// <summary>Platform yöneticisi elle verdi (önceden var olan haklar bu kaynakla taşınır).</summary>
    Manual = 1,

    /// <summary>Plana bağlı (Plan tabloları sonraki dilimdedir).</summary>
    Plan = 2,

    Trial = 3,

    /// <summary>Plan dışı istisna; gerekçe (<see cref="EntitlementTerms.Note"/>) zorunludur.</summary>
    Exception = 4,
}

/// <summary>
/// Hakkın koşulları. <see cref="ValidUntil"/> boşsa süresizdir. Bu dilimde bitiş tarihi yalnızca kaydedilir ve gösterilir;
/// süre dolunca hakkın otomatik kapanması sonraki dilimdedir (ADR-0028 Dilim 3).
/// </summary>
public sealed record EntitlementTerms(EntitlementSource Source = EntitlementSource.Manual, DateTimeOffset? ValidUntil = null, string? Note = null);

/// <summary>
/// Bir tenant'ın bir modülü kullanma hakkı. Platform geneli kayıttır (abonelik/plan tarafı);
/// tenant kullanıcıları değiştiremez. Kaydı olmayan modül o tenant için kapalıdır; Core her zaman açıktır.
/// </summary>
public sealed class ModuleEntitlement
{
    public const int NoteMaxLength = 500;

    private ModuleEntitlement()
    {
        // EF Core için
    }

    public ModuleEntitlement(Guid tenantId, string moduleKey, EntitlementTerms? terms = null)
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
        Enable(terms);
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string ModuleKey { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public EntitlementSource Source { get; private set; } = EntitlementSource.Manual;

    /// <summary>Hakkın bitiş tarihi; boşsa süresiz. Bu dilimde otomatik uygulanmaz (<see cref="EntitlementTerms"/>).</summary>
    public DateTimeOffset? ValidUntil { get; private set; }

    public string? Note { get; private set; }

    /// <summary>
    /// Hakkı açar. <paramref name="terms"/> verilmezse mevcut koşullar korunur (yeni kayıtta elle ve süresiz).
    /// </summary>
    public void Enable(EntitlementTerms? terms = null)
    {
        if (terms is not null)
        {
            Apply(terms);
        }

        IsEnabled = true;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Kapatma veriyi silmez; modül yeniden açılınca kayıtlar olduğu gibi görünür.</summary>
    public void Disable()
    {
        IsEnabled = false;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void Apply(EntitlementTerms terms)
    {
        if (!Enum.IsDefined(terms.Source))
        {
            throw new ArgumentException("Geçersiz hak kaynağı.", "source");
        }

        var note = string.IsNullOrWhiteSpace(terms.Note) ? null : terms.Note.Trim();
        if (note is { Length: > NoteMaxLength })
        {
            throw new ArgumentException($"Not en fazla {NoteMaxLength} karakter olabilir.", "note");
        }

        if (terms.Source == EntitlementSource.Exception && note is null)
        {
            throw new ArgumentException("İstisna için gerekçe (not) zorunludur.", "note");
        }

        if (terms.ValidUntil is { } until && until <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("Bitiş tarihi gelecekte olmalıdır.", "validUntil");
        }

        Source = terms.Source;
        ValidUntil = terms.ValidUntil;
        Note = note;
    }
}
