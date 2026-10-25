using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Settings;

/// <summary>
/// Firmanın logosu (Y6b). Dosya altyapısı gelene kadar veritabanında, firma başına bir tane. Yalnızca PNG ve JPEG;
/// SVG kabul edilmez (içinde betik taşıyabilir). Tür, uzantıya veya istemcinin söylediğine değil dosyanın ilk
/// baytlarına bakılarak belirlenir.
/// </summary>
public sealed class TenantLogo : ITenantEntity, ICompanyShared
{
    public const int MaxBytes = 256 * 1024;
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    private TenantLogo()
    {
        // EF Core için
    }

    public TenantLogo(byte[] data)
    {
        Id = Guid.CreateVersion7();
        Replace(data);
        Version = 1;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string ContentType { get; private set; } = Png;

    public byte[] Data { get; private set; } = [];

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public void Replace(byte[] data)
    {
        ContentType = DetectContentType(data) ?? throw new ArgumentException(InvalidMessage, nameof(data));
        Data = data;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }

    public const string InvalidMessage = "Logo PNG veya JPEG olmalı ve en fazla 256 KB olabilir.";

    /// <summary>Dosya PNG veya JPEG ve boyut sınırının içindeyse türü, değilse <c>null</c>.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > MaxBytes)
        {
            return null;
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Png;
        }

        return data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? Jpeg : null;
    }

    /// <summary>Denetim kaydında logonun özeti (ör. "PNG, 12 KB").</summary>
    public string Describe() => $"{(ContentType == Png ? "PNG" : "JPEG")}, {Math.Max(1, (Data.Length + 1023) / 1024)} KB";
}
