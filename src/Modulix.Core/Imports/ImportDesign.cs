using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Imports;

/// <summary>
/// Firmanın bir kayıt türü için içe aktarma tasarımı (ADR-0033 bölüm 3). Satır yoksa tanım geçerlidir: tür açıktır, bütün sütunlar şablondadır ve
/// otomasyon çalışmaz. Tenant verisidir; Row-Level Security ile korunur.
/// </summary>
public sealed class TenantImportDesign : ITenantEntity, ICompanyShared
{
    public const int EntityKeyMaxLength = 100;

    private TenantImportDesign()
    {
        // EF Core için
    }

    public TenantImportDesign(string entityKey)
    {
        Id = Guid.CreateVersion7();
        EntityKey = entityKey;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public string EntityKey { get; private set; } = string.Empty;

    /// <summary>Tasarım (<see cref="ImportDesignInput"/> JSON'u).</summary>
    public string DesignJson { get; private set; } = string.Empty;

    /// <summary>Her değişiklikte artar; eşzamanlı düzenleme denetimi içindir.</summary>
    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public void Set(string designJson, Guid? userId)
    {
        DesignJson = designJson;
        UpdatedBy = userId;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}

/// <param name="Enabled">Tür içe aktarılabilir mi?</param>
/// <param name="RunAutomation">
/// İçe aktarılan kayıtlar kuralları ve atama e-postalarını tetikler mi? Varsayılan kapalıdır: geçmiş veri taşınırken yüzlerce kural çalışması ve
/// e-posta üretilmesin. Veriyi tutarlı tutan abonelikler bundan etkilenmez.
/// </param>
/// <param name="Excluded">Şablondan çıkarılan sütunlar; dosyada varsa tanınmayan sütun gibi yok sayılır.</param>
/// <param name="Required">Firmanın zorunlu kıldığı sütunlar.</param>
public sealed record ImportDesignInput(bool Enabled, bool RunAutomation, IReadOnlyList<string> Excluded, IReadOnlyList<string> Required)
{
    public static ImportDesignInput Default { get; } = new(true, false, [], []);
}

/// <summary>Tasarım ekranındaki sütun. <see cref="Locked"/>: kod ya da özel alan tanımı zorunlu kılıyor, kapatılamaz.</summary>
public sealed record ImportDesignField(string Key, string Label, bool Included, bool Required, bool Locked);

public sealed record ImportDesignInfo(
    string EntityKey, string DisplayName, bool Enabled, bool RunAutomation, IReadOnlyList<ImportDesignField> Fields, long Version);

/// <summary>Tasarım kuralları (saf mantık).</summary>
public static class ImportDesignRules
{
    public static IReadOnlyList<ImportField> Apply(IReadOnlyList<ImportField> fields, ImportDesignInput design)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(design);
        return
        [
            .. fields
                .Where(f => f.Required || !design.Excluded.Contains(f.Key))
                .Select(f => design.Required.Contains(f.Key) ? f with { Required = true } : f),
        ];
    }

    public static IReadOnlyList<RecordFieldError> Validate(IReadOnlyList<ImportField> fields, ImportDesignInput design)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(design);
        var errors = new List<RecordFieldError>();
        foreach (var key in design.Excluded.Concat(design.Required).Where(k => fields.All(f => f.Key != k)).Distinct())
        {
            errors.Add(new RecordFieldError(key, "Bilinmeyen sütun."));
        }

        foreach (var field in fields.Where(f => f.Required && design.Excluded.Contains(f.Key)))
        {
            errors.Add(new RecordFieldError(field.Key, $"{field.Label} zorunlu sütundur, çıkarılamaz."));
        }

        foreach (var field in fields.Where(f => !f.Required && design.Excluded.Contains(f.Key) && design.Required.Contains(f.Key)))
        {
            errors.Add(new RecordFieldError(field.Key, $"{field.Label} hem çıkarılmış hem zorunlu olamaz."));
        }

        return errors;
    }
}

/// <summary>Firmanın tasarımını okur (aktif tenant); satır yoksa <see cref="ImportDesignInput.Default"/>.</summary>
public interface IImportDesignStore
{
    Task<ImportDesignInput> GetAsync(string entityKey, CancellationToken cancellationToken);
}

/// <summary>Tasarım ekranının servisi: yalnızca modülü açık türler listelenir.</summary>
public interface IImportDesignService
{
    Task<IReadOnlyList<ImportDesignInfo>> ListAsync(CancellationToken cancellationToken);

    Task<RecordResult<ImportDesignInfo>> SaveAsync(string entityKey, ImportDesignInput input, long version, CancellationToken cancellationToken);
}
