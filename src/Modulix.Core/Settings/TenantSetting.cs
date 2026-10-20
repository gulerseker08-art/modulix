using System.Text.RegularExpressions;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Settings;

/// <summary>
/// Tenant'a özel ayar (ör. "locale", "currency"). Faz 1 Core kapsamındaki "tenant ayarları"nın
/// saklama katmanıdır ve tenant izolasyonunun ilk korunan tablosudur.
/// </summary>
public sealed partial class TenantSetting : ITenantEntity
{
    public const int KeyMaxLength = 100;
    public const int ValueMaxLength = 4000;

    private TenantSetting()
    {
        // EF Core için
    }

    public TenantSetting(string key, string value)
    {
        Id = Guid.CreateVersion7();
        Key = NormalizeKey(key);
        Value = ValidateValue(value);
        UpdatedAt = DateTimeOffset.UtcNow;
        Version = 1;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string Value { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Her değişiklikte artar; eşzamanlı güncellemeleri yakalamak için kullanılır.</summary>
    public long Version { get; private set; }

    public void SetValue(string value)
    {
        var validated = ValidateValue(value);
        if (validated == Value)
        {
            return;
        }

        Value = validated;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }

    public static string ValidateValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > ValueMaxLength)
        {
            throw new ArgumentException($"Ayar değeri en fazla {ValueMaxLength} karakter olabilir.", nameof(value));
        }

        return value;
    }

    public static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalized = key.Trim().ToLowerInvariant();
        if (normalized.Length > KeyMaxLength || !KeyPattern().IsMatch(normalized))
        {
            throw new ArgumentException(
                $"Ayar anahtarı en fazla {KeyMaxLength} karakter olmalı ve yalnızca a-z, 0-9, nokta, tire, alt çizgi içermelidir.",
                nameof(key));
        }

        return normalized;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
