using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Api.Auth;

/// <summary>
/// Data Protection anahtar halkası: oturum çerezi, davet ve şifre sıfırlama belirteçleri bu anahtarlarla korunur.
/// Anahtarlar <c>platform.DataProtectionKeys</c> tablosunda saklanır; böylece uygulama yeniden başlayınca oturumlar ve
/// e-postadaki bağlantılar geçerli kalır ve birden fazla örnek aynı anahtarları kullanır.
/// <c>DataProtection:CertificatePath</c> (PFX) verilirse anahtarlar veritabanına bu sertifikayla şifrelenerek yazılır;
/// şifresi <c>DataProtection:CertificatePassword</c> (ortam değişkeni ya da user-secrets, repoya yazılmaz).
/// </summary>
public static class DataProtectionSetup
{
    public const string SectionName = "DataProtection";

    /// <summary>Uygulama adı: farklı örneklerin aynı anahtar halkasını paylaşması için sabittir; değiştirilirse bütün oturumlar düşer.</summary>
    public const string ApplicationName = "Modulix";

    public static IServiceCollection AddModulixDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var builder = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToDbContext<ModulixDbContext>();

        if (Certificate(configuration) is { } certificate)
        {
            builder.ProtectKeysWithCertificate(certificate);
        }

        return services;
    }

    /// <summary>Sertifika yapılandırılmamışsa üretimde uyarı yazar: anahtarlar veritabanında şifresiz durur.</summary>
    public static void WarnIfKeysUnprotected(WebApplication app)
    {
        if (!app.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(app.Configuration[$"{SectionName}:CertificatePath"]))
        {
            app.Logger.LogWarning(
                "Data Protection anahtarları veritabanında şifresiz saklanıyor. Üretimde {Section}:CertificatePath ile bir sertifika verin.",
                SectionName);
        }
    }

    private static X509Certificate2? Certificate(IConfiguration configuration)
    {
        var path = configuration[$"{SectionName}:CertificatePath"];
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{SectionName}:CertificatePath bulunamadı: {path}");
        }

        return X509CertificateLoader.LoadPkcs12FromFile(path, configuration[$"{SectionName}:CertificatePassword"]);
    }
}
