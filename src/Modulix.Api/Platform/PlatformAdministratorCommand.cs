using Microsoft.AspNetCore.Identity;
using Modulix.Api.Users;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Users;

namespace Modulix.Api.Platform;

/// <summary>
/// Platform yöneticisi atama komutu (Y4). Arayüzden atanamaz; sunucuya erişimi olan kişi atar.
///   dotnet run --project src/Modulix.Api -- platform-admin kisi@ornek.com
///   dotnet run --project src/Modulix.Api -- platform-admin kisi@ornek.com --remove
/// Hesap yoksa şifresiz bir hesap oluşturulur ve şifre belirleme bağlantısı konsola yazılır (App:PublicUrl gerekir;
/// geliştirmede https://localhost:7026). Atama ve kaldırma uygulama günlüğüne de yazılır.
/// </summary>
public static class PlatformAdministratorCommand
{
    public const string Name = "platform-admin";

    public static async Task<string> RunAsync(IServiceProvider services, IReadOnlyList<string> args)
    {
        var rest = args.SkipWhile(a => !a.Equals(Name, StringComparison.OrdinalIgnoreCase)).Skip(1).ToList();
        var email = rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.Trim()
            ?? throw new InvalidOperationException($"E-posta gerekli: -- {Name} kisi@ornek.com [--remove]");
        var remove = rest.Contains("--remove", StringComparer.OrdinalIgnoreCase);

        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ModulixUser>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PlatformAdministratorCommand));

        var user = await users.FindByEmailAsync(email);
        if (remove)
        {
            if (user is null || !user.IsPlatformAdministrator)
            {
                return $"{email} platform yöneticisi değil; değişiklik yapılmadı.";
            }

            user.IsPlatformAdministrator = false;
            user.PlatformRoles = Modulix.Core.Platform.PlatformRole.None;
            ThrowIfFailed(await users.UpdateAsync(user));
            logger.LogWarning("Platform yöneticiliği kaldırıldı: {UserId} ({Email})", user.Id, user.Email);
            return $"{user.Email} artık platform yöneticisi değil (bir sonraki isteğinde geçerli olur).";
        }

        string? setPasswordLink = null;
        if (user is null)
        {
            user = new ModulixUser { UserName = email, Email = email, DisplayName = email.Split('@')[0] };
            ThrowIfFailed(await users.CreateAsync(user));
            var publicUrl = scope.ServiceProvider.GetRequiredService<IConfiguration>()[PublicUrl.ConfigurationKey];
            if (string.IsNullOrWhiteSpace(publicUrl))
            {
                publicUrl = scope.ServiceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment()
                    ? "https://localhost:7026"
                    : throw new InvalidOperationException($"{PublicUrl.ConfigurationKey} ayarı gerekli: şifre belirleme bağlantısı bu adresle oluşturulur.");
            }

            setPasswordLink = AccountTokens.PasswordResetLink(publicUrl, user.Id, await users.GeneratePasswordResetTokenAsync(user));
        }
        else if (user.IsPlatformAdministrator && user.PlatformRoles == Modulix.Core.Platform.PlatformRole.None)
        {
            return $"{user.Email} zaten platform yöneticisi.";
        }

        user.IsPlatformAdministrator = true;
        user.PlatformRoles = Modulix.Core.Platform.PlatformRole.None; // rol kaydı boş = Owner (ADR-0028 Dilim 5)
        ThrowIfFailed(await users.UpdateAsync(user));
        logger.LogWarning("Platform yöneticisi atandı: {UserId} ({Email})", user.Id, user.Email);

        return setPasswordLink is null
            ? $"{user.Email} platform yöneticisi oldu."
            : $"{user.Email} için hesap oluşturuldu ve platform yöneticisi oldu. Şifre belirleme bağlantısı (24 saat geçerli):{Environment.NewLine}{setPasswordLink}";
    }

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }
}
