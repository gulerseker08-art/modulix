using System.Security.Cryptography;
using System.Text;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Communication;

/// <summary>
/// Abonelikten çıkma bağlantısının belirteci (ADR-0025, dilim 6). Her gönderim için bir satır (yüksek hacimli günlük satırı, kayıt boru hattından
/// geçmez). Belirtecin kendisi saklanmaz, yalnızca SHA-256 karması; adres normalize biçimdedir. Belirteç yalnızca o adresin pazarlama tercihini
/// değiştirebilir (çıkış ve kendi çıkışını geri alma); başka bir yetkisi yoktur.
/// </summary>
public sealed class CommunicationLinkToken : IJournalEntity, ICompanyShared
{
    public const int ReferenceMaxLength = 100;
    public const int HashLength = 64;

    private CommunicationLinkToken()
    {
        // EF Core için
    }

    public CommunicationLinkToken(string tokenHash, CommunicationChannel channel, string address, string reference, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        TokenHash = tokenHash;
        Channel = channel;
        Address = address;
        Reference = reference.Length > ReferenceMaxLength ? reference[..ReferenceMaxLength] : reference;
        CreatedAt = now;
        ExpiresAt = now.AddDays(LinkTokens.ValidDays);
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public CommunicationChannel Channel { get; private set; }

    public string Address { get; private set; } = string.Empty;

    public string Reference { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAt <= now;
}

/// <summary>Belirteç üretimi ve karması. Saf: birim testle sınanır.</summary>
public static class LinkTokens
{
    /// <summary>
    /// Bağlantı ömrü. Gönderilmiş bir iletideki çıkış bağlantısı uzun süre çalışmalıdır; süresi dolan bağlantıyla çıkış yine de kabul edilir
    /// (iletişimi azaltmak her zaman güvenlidir), yalnızca çıkışı geri alma süre ister.
    /// </summary>
    public const int ValidDays = 400;

    private const int Bytes = 32;

    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(Bytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Biçim denetimi: veritabanına gitmeden bariz geçersiz belirteçleri eler.</summary>
    public static bool LooksValid(string? token) =>
        token is { Length: >= 40 and <= 64 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
