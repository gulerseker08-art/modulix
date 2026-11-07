using System.Text.Json;
using Modulix.Core.Platform;

namespace Modulix.Api.Platform;

/// <summary>
/// On-prem imzalı lisans komutları (ADR-0028 Dilim 7b). Arayüzden yapılamaz; sunucuya erişimi olan işletmen çalıştırır.
///   dotnet run --project src/Modulix.Api -- license-apply lisans.json
///   dotnet run --project src/Modulix.Api -- license-keygen --dir anahtar
///   dotnet run --project src/Modulix.Api -- license-sign icerik.json --key anahtar/license-private.pem [--out lisans.json]
/// <c>license-apply</c> müşteri ortamında çalışır; yalnızca genel anahtar (<c>License:PublicKey</c>) gerekir. <c>license-keygen</c> ve
/// <c>license-sign</c> lisansı düzenleyen tarafındadır: özel anahtar müşteri ortamına, depoya ya da imaja <b>konmaz</b>.
/// </summary>
public static class LicenseCommand
{
    public const string ApplyName = "license-apply";

    public const string KeygenName = "license-keygen";

    public const string SignName = "license-sign";

    public const string PublicKeySetting = "License:PublicKey";

    private static readonly string[] Names = [ApplyName, KeygenName, SignName];

    public static bool Matches(IReadOnlyList<string> args) => Names.Any(n => args.Contains(n, StringComparer.OrdinalIgnoreCase));

    public static async Task<string> RunAsync(IServiceProvider services, IReadOnlyList<string> args)
    {
        var name = Names.First(n => args.Contains(n, StringComparer.OrdinalIgnoreCase));
        var rest = args.SkipWhile(a => !a.Equals(name, StringComparison.OrdinalIgnoreCase)).Skip(1).ToList();
        return name switch
        {
            ApplyName => await ApplyAsync(services, rest),
            KeygenName => Keygen(rest),
            _ => Sign(rest),
        };
    }

    private static async Task<string> ApplyAsync(IServiceProvider services, IReadOnlyList<string> rest)
    {
        var file = rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Lisans dosyası gerekli: -- {ApplyName} <dosya>");
        if (!File.Exists(file))
        {
            throw new InvalidOperationException($"Lisans dosyası bulunamadı: {file}");
        }

        await using var scope = services.CreateAsyncScope();
        var publicKey = scope.ServiceProvider.GetRequiredService<IConfiguration>()[PublicKeySetting] ?? string.Empty;
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LicenseCommand));
        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<ILicenseApplier>()
                .ApplyAsync(await File.ReadAllTextAsync(file), publicKey, CancellationToken.None);
            logger.LogWarning("Lisans uygulandı: {LicenseId} ({Tenant}), bitiş {ValidUntil:O}", result.LicenseId, result.TenantSlug, result.ValidUntil);
            return $"Lisans uygulandı: {result.LicenseId} → {result.TenantSlug}, {string.Join(", ", result.Modules)}; bitiş {result.ValidUntil:yyyy-MM-dd}.";
        }
        catch (LicenseException ex)
        {
            logger.LogError("Lisans reddedildi: {Reason}", ex.Message);
            throw new InvalidOperationException(ex.Message);
        }
    }

    private static string Keygen(IReadOnlyList<string> rest)
    {
        var directory = Option(rest, "--dir") ?? throw new InvalidOperationException($"Dizin gerekli: -- {KeygenName} --dir <dizin>");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "license-private.pem");
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"Özel anahtar zaten var, üzerine yazılmaz: {path}");
        }

        var (privatePem, publicKey) = LicenseFile.GenerateKeyPair();
        File.WriteAllText(path, privatePem);
        return $"Özel anahtar yazıldı (gizli tutun, depoya koymayın): {path}\nGenel anahtar ({PublicKeySetting} olarak müşteri ortamına verilir):\n{publicKey}";
    }

    private static string Sign(IReadOnlyList<string> rest)
    {
        var input = rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"İçerik dosyası gerekli: -- {SignName} <icerik.json> --key <özel anahtar> [--out <dosya>]");
        var keyPath = Option(rest, "--key") ?? throw new InvalidOperationException("Özel anahtar gerekli: --key <license-private.pem>");
        var output = Option(rest, "--out") ?? Path.ChangeExtension(input, ".license.json");

        LicensePayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<LicensePayload>(File.ReadAllText(input), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("İçerik boş.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"İçerik okunamadı: {ex.Message}");
        }

        try
        {
            File.WriteAllText(output, LicenseFile.Sign(payload, File.ReadAllText(keyPath)));
        }
        catch (LicenseException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }

        return $"Lisans imzalandı: {output}";
    }

    private static string? Option(IReadOnlyList<string> rest, string name)
    {
        var index = rest.ToList().FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < rest.Count ? rest[index + 1] : null;
    }
}
