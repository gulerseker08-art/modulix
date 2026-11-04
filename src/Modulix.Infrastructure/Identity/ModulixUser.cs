using Microsoft.AspNetCore.Identity;

namespace Modulix.Infrastructure.Identity;

/// <summary>
/// Platform kullanıcısı. Bir kullanıcı birden fazla tenant'a üye olabilir; tenant'a ait
/// rol ve yetkiler kullanıcıda değil üyelikte tutulur.
/// </summary>
public class ModulixUser : IdentityUser<Guid>
{
    public const int DisplayNameMaxLength = 200;

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Platform yöneticisi (Y4): firmaları ve modül haklarını yönetir; firmalara bağlı değildir. Firma verisini görmez
    /// (RLS atlanmaz). Yalnızca komut satırıyla atanır: <c>-- platform-admin e-posta</c>.
    /// </summary>
    public bool IsPlatformAdministrator { get; set; }

    /// <summary>
    /// Platform rolleri (ADR-0028 Dilim 5; bayrak birleşimi). Boşsa ve <see cref="IsPlatformAdministrator"/> açıksa kişi Owner sayılır
    /// (rol modelinden önce atanmış hesaplar); bkz. <see cref="Modulix.Core.Platform.PlatformRoles.Effective"/>.
    /// </summary>
    public Modulix.Core.Platform.PlatformRole PlatformRoles { get; set; }
}
