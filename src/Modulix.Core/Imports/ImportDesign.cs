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
/// <param name="Aliases">
/// Kayıtlı sütun eşlemesi (Dilim 4c): alan anahtarı → firmanın dosyalarında o sütun için kullandığı başka başlık adları ("Müşteri Adı" → Konu).
/// Eski tasarım satırlarında yoktur (null); JSON'da saklandığı için migration gerekmez.
/// </param>
public sealed record ImportDesignInput(
    bool Enabled, bool RunAutomation, IReadOnlyList<string> Excluded, IReadOnlyList<string> Required, IReadOnlyDictionary<string, IReadOnlyList<string>>? Aliases = null)
{
    public static ImportDesignInput Default { get; } = new(true, false, [], []);

    public IReadOnlyList<string> AliasesOf(string key) => Aliases is { } map && map.TryGetValue(key, out var names) ? names : [];
}

/// <summary>Tasarım ekranındaki sütun. <see cref="Locked"/>: kod ya da özel alan tanımı zorunlu kılıyor, kapatılamaz.</summary>
public sealed record ImportDesignField(string Key, string Label, bool Included, bool Required, bool Locked)
{
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

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
                .Select(f => design.Required.Contains(f.Key) ? f with { Required = true } : f)
                .Select(f => design.AliasesOf(f.Key) is { Count: > 0 } aliases ? f with { Aliases = aliases } : f),
        ];
    }

    /// <summary>Bir alanın en çok kaç ek başlık adı olabilir ve bir adın en çok uzunluğu.</summary>
    public const int MaxAliasesPerField = 10;

    public const int MaxAliasLength = 100;

    /// <summary>Ek adları kırpar, boşları ve tekrarları (büyük/küçük harf duyarsız) atar; boş liste veren alanı çıkarır.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? NormalizeAliases(IReadOnlyDictionary<string, IReadOnlyList<string>>? aliases)
    {
        if (aliases is null)
        {
            return null;
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (key, names) in aliases)
        {
            var cleaned = (names ?? []).Select(n => n?.Trim() ?? string.Empty).Where(n => n.Length > 0)
                .DistinctBy(ImportColumns.Name, StringComparer.Ordinal).ToList();
            if (cleaned.Count > 0)
            {
                result[key] = cleaned;
            }
        }

        return result.Count == 0 ? null : result;
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

        // Ek başlık adları: bilinen alana ait, sınır içinde, başka bir alanın adı/anahtarı değil ve iki alanda birden kullanılmıyor.
        var names = fields.SelectMany(f => new[] { (Name: ImportColumns.Name(f.Label), f.Key), (Name: ImportColumns.Name(f.Key), f.Key) })
            .Append((Name: ImportColumns.Name(ImportErrorReport.ErrorColumn), Key: string.Empty))
            .ToList();
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, aliases) in design.Aliases ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            if (fields.All(f => f.Key != key))
            {
                errors.Add(new RecordFieldError(key, "Bilinmeyen sütun."));
                continue;
            }

            if (aliases.Count > MaxAliasesPerField)
            {
                errors.Add(new RecordFieldError(key, $"Bir sütun için en çok {MaxAliasesPerField} başlık adı kaydedilebilir."));
            }

            foreach (var alias in aliases)
            {
                var name = ImportColumns.Name(alias);
                if (alias.Length > MaxAliasLength)
                {
                    errors.Add(new RecordFieldError(key, $"Başlık adı en çok {MaxAliasLength} karakter olabilir."));
                }
                else if (names.FirstOrDefault(n => n.Name == name && n.Key != key) is { Name: not null } taken)
                {
                    errors.Add(new RecordFieldError(key, taken.Key.Length == 0
                        ? $"'{alias}' hata raporunun sütun adıdır, eşlemede kullanılamaz."
                        : $"'{alias}' başka bir sütunun adı; eşlemede kullanılamaz."));
                }
                else if (!claimed.TryAdd(name, key) && claimed[name] != key)
                {
                    errors.Add(new RecordFieldError(key, $"'{alias}' birden çok sütuna eşlenmiş."));
                }
            }
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
