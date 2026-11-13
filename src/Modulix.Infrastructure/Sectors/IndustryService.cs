using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Sectors;
using Modulix.Core.Platform;
using Modulix.Core.Sectors;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Sectors;

/// <summary>
/// Sektör kataloğu (ADR-0043). Tablo <c>platform</c> şemasındadır, RLS dışıdır ve tenant verisi içermez; her yazma, değiştiren
/// yönetici ile birlikte <c>PlatformAuditLog</c>'a aynı işlemde yazılır. Sektör silinmez, pasife alınır. Aynı sınıf modüllerin
/// okuma sınırını (<see cref="IIndustryCatalog"/>) da sağlar.
/// </summary>
internal sealed class IndustryService(ModulixDbContext db) : IIndustries, IIndustryCatalog
{
    public async Task<IReadOnlyList<IndustryView>> ListAsync(CancellationToken cancellationToken) =>
        await db.Industries.AsNoTracking().OrderBy(i => i.Name).Select(i => new IndustryView(i.Id, i.Code, i.Name, i.IsActive)).ToListAsync(cancellationToken);

    public async Task<IndustryView> CreateAsync(Guid actingUserId, string code, string name, CancellationToken cancellationToken)
    {
        var industry = new Industry(code, name);
        if (await db.Industries.AnyAsync(i => i.Code == code, cancellationToken))
        {
            throw new SectorCatalogConflictException($"Bu kodla bir sektör zaten var: '{code}'.");
        }

        if (await db.Industries.AnyAsync(i => i.Name == industry.Name, cancellationToken))
        {
            throw new SectorCatalogConflictException($"Bu adla bir sektör zaten var: '{industry.Name}'.");
        }

        db.Industries.Add(industry);
        Audit(actingUserId, "sector.industry.created", new JsonObject { ["code"] = industry.Code, ["name"] = industry.Name });
        await db.SaveChangesAsync(cancellationToken);
        return ToView(industry);
    }

    public async Task<IndustryView> SetActiveAsync(Guid actingUserId, string code, bool active, CancellationToken cancellationToken)
    {
        var industry = await db.Industries.SingleOrDefaultAsync(i => i.Code == code, cancellationToken)
            ?? throw new SectorCatalogNotFoundException($"Sektör bulunamadı: '{code}'.");
        if (industry.IsActive != active)
        {
            industry.SetActive(active);
            Audit(actingUserId, "sector.industry.active", new JsonObject { ["code"] = industry.Code, ["active"] = active });
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToView(industry);
    }

    async Task<IReadOnlyList<IndustryInfo>> IIndustryCatalog.ListAsync(CancellationToken cancellationToken) =>
        await db.Industries.AsNoTracking().OrderBy(i => i.Name).Select(i => new IndustryInfo(i.Code, i.Name, i.IsActive)).ToListAsync(cancellationToken);

    public async Task<Industry?> ResolveAsync(string value, CancellationToken cancellationToken)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        // Kod sabit ve benzersizdir; ad eşleşmesi yalnızca etkin kayıtlarda aranır (pasif bir sektör adla seçilemez).
        return await db.Industries.SingleOrDefaultAsync(i => i.Code == trimmed, cancellationToken)
            ?? await db.Industries.Where(i => i.IsActive && i.Name == trimmed).OrderBy(i => i.Code).FirstOrDefaultAsync(cancellationToken);
    }

    private void Audit(Guid actingUserId, string action, JsonObject details) =>
        db.PlatformAuditLog.Add(new PlatformAuditEntry(actingUserId, action, null, null, details.ToJsonString(), Activity.Current?.TraceId.ToString()));

    private static IndustryView ToView(Industry i) => new(i.Id, i.Code, i.Name, i.IsActive);
}
