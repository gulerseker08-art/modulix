using Microsoft.EntityFrameworkCore;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Tenancy;

namespace Modulix.Api.Platform;

/// <summary>
/// Tenant verisini dışa aktarma ve silme komutları (ADR-0020 Dilim 3). Arayüzden yapılamaz; sunucuya erişimi olan
/// işletmen çalıştırır.
///   dotnet run --project src/Modulix.Api -- tenant-export acme [--dir exports/acme]
///   dotnet run --project src/Modulix.Api -- tenant-delete acme --confirm acme
/// Silme geri alınamaz; komut önce dışa aktarma ister ve kısa adın <c>--confirm</c> ile tekrar yazılmasını şart koşar.
/// Her iki işlem uygulama günlüğüne yazılır.
/// </summary>
public static class TenantDataCommand
{
    public const string ExportName = "tenant-export";

    public const string DeleteName = "tenant-delete";

    public static bool Matches(IReadOnlyList<string> args) =>
        args.Contains(ExportName, StringComparer.OrdinalIgnoreCase) || args.Contains(DeleteName, StringComparer.OrdinalIgnoreCase);

    public static async Task<string> RunAsync(IServiceProvider services, IReadOnlyList<string> args)
    {
        var export = args.Contains(ExportName, StringComparer.OrdinalIgnoreCase);
        var name = export ? ExportName : DeleteName;
        var rest = args.SkipWhile(a => !a.Equals(name, StringComparison.OrdinalIgnoreCase)).Skip(1).ToList();
        var slug = rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.Trim().ToLowerInvariant()
            ?? throw new InvalidOperationException(export
                ? $"Kısa ad gerekli: -- {ExportName} <slug> [--dir <dizin>]"
                : $"Kısa ad gerekli: -- {DeleteName} <slug> --confirm <slug>");

        await using var scope = services.CreateAsyncScope();
        var tenantId = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().Tenants.AsNoTracking()
            .Where(t => t.Slug == slug).Select(t => (Guid?)t.Id).SingleOrDefaultAsync()
            ?? throw new InvalidOperationException($"Tenant bulunamadı: {slug}.");
        var transfer = scope.ServiceProvider.GetRequiredService<ITenantDataTransfer>();

        if (export)
        {
            var directory = Option(rest, "--dir") ?? Path.Combine("exports", $"{slug}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
            var result = await transfer.ExportAsync(tenantId, Path.GetFullPath(directory), CancellationToken.None);
            return $"{result.Slug} dışa aktarıldı: {result.Tables.Count} tablo, {result.TotalRows} satır → {result.Directory}";
        }

        if (!string.Equals(Option(rest, "--confirm"), slug, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Silme geri alınamaz. Önce dışa aktarın ({ExportName} {slug}), sonra onaylayın: -- {DeleteName} {slug} --confirm {slug}");
        }

        var deleted = await transfer.DeleteAsync(tenantId, CancellationToken.None);
        return $"{deleted.Slug} silindi: {deleted.Tables.Count} tablodan {deleted.TotalRows} satır. Kullanıcı hesapları kalır; yalnızca üyelikleri kaldırıldı.";
    }

    private static string? Option(IReadOnlyList<string> rest, string name)
    {
        var index = rest.ToList().FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < rest.Count ? rest[index + 1] : null;
    }
}
