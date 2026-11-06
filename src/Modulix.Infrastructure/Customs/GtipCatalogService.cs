using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customs;
using Modulix.Core.Customs;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Customs;

/// <summary>
/// GTİP kataloğu (ADR-0032 K9). Tablolar <c>platform</c> şemasındadır, RLS dışıdır ve tenant verisi içermez. Okuma (<see cref="IGtipCatalog"/>)
/// sorgu tarihinde yürürlükteki en yeni sürüme bakar; yönetim (<see cref="IGtipCatalogAdmin"/>) platform yöneticisinindir ve her yazma
/// <c>PlatformAuditLog</c>'a aynı işlemde yazılır.
/// </summary>
internal sealed class GtipCatalogService(ModulixDbContext db, TimeProvider time) : IGtipCatalog, IGtipCatalogAdmin
{
    private const int InsertChunk = 2000;
    private const int MaxSearchLimit = 50;
    private const int MaxPageSize = 200;

    // ---- Okuma (modüller) ----

    public async Task<GtipCheck> CheckAsync(string code, DateOnly day, CancellationToken cancellationToken)
    {
        var (value, _) = GtipRules.Normalize(code);
        var version = await InForceAsync(day, cancellationToken);
        if (version is null)
        {
            return new GtipCheck(GtipCatalogStatus.NoCatalog, null, null);
        }

        if (value is null)
        {
            return new GtipCheck(GtipCatalogStatus.NotFound, null, version.Name);
        }

        var description = await db.GtipCatalogEntries.AsNoTracking()
            .Where(e => e.VersionId == version.Id && e.Code == value)
            .Select(e => e.Description)
            .FirstOrDefaultAsync(cancellationToken);
        if (description is not null)
        {
            return new GtipCheck(GtipCatalogStatus.Found, description, version.Name);
        }

        var hasChildren = await db.GtipCatalogEntries.AsNoTracking()
            .AnyAsync(e => e.VersionId == version.Id && e.Code.StartsWith(value), cancellationToken);
        return new GtipCheck(hasChildren ? GtipCatalogStatus.PrefixOnly : GtipCatalogStatus.NotFound, null, version.Name);
    }

    public async Task<IReadOnlyList<GtipSuggestion>> SearchAsync(string text, DateOnly day, int limit, CancellationToken cancellationToken)
    {
        var term = text?.Trim();
        if (string.IsNullOrEmpty(term))
        {
            return [];
        }

        var version = await InForceAsync(day, cancellationToken);
        if (version is null)
        {
            return [];
        }

        var entries = db.GtipCatalogEntries.AsNoTracking().Where(e => e.VersionId == version.Id);
        return await Filter(entries, term)
            .OrderBy(e => e.Code)
            .Take(Math.Clamp(limit, 1, MaxSearchLimit))
            .Select(e => new GtipSuggestion(e.Code, e.Description, e.Unit))
            .ToListAsync(cancellationToken);
    }

    // ---- Yönetim (platform) ----

    public async Task<IReadOnlyList<GtipCatalogVersionView>> ListVersionsAsync(CancellationToken cancellationToken) =>
        await db.GtipCatalogVersions.AsNoTracking()
            .OrderByDescending(v => v.EffectiveFrom)
            .Select(v => new GtipCatalogVersionView(v.Id, v.Name, v.EffectiveFrom, v.SourceFileName, v.EntryCount, v.CreatedAt))
            .ToListAsync(cancellationToken);

    public async Task<GtipCatalogVersionView> CreateVersionAsync(
        Guid actingUserId, GtipCatalogVersionInput input, IReadOnlyList<GtipCatalogEntryInput> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (GtipCatalogRules.ValidateEntries(entries) is { } entriesError)
        {
            throw new ArgumentException(entriesError, nameof(entries));
        }

        var version = new GtipCatalogVersion(input.Name, input.EffectiveFrom, input.SourceFileName, entries.Count, actingUserId, time.GetUtcNow());
        if (await db.GtipCatalogVersions.AnyAsync(v => v.EffectiveFrom == input.EffectiveFrom, cancellationToken))
        {
            throw new GtipCatalogConflictException(
                $"{input.EffectiveFrom:dd.MM.yyyy} yürürlük tarihli bir sürüm zaten var; yanlışsa önce o sürümü silin.");
        }

        var autoDetect = db.ChangeTracker.AutoDetectChangesEnabled;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            db.GtipCatalogVersions.Add(version);
            await db.SaveChangesAsync(cancellationToken);

            // Satırlar parça parça yazılır: tek SaveChanges onbinlerce varlığı bellekte tutardı.
            foreach (var chunk in entries.Chunk(InsertChunk))
            {
                db.GtipCatalogEntries.AddRange(chunk.Select(e => new GtipCatalogEntry(version.Id, e)));
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }

            Audit(actingUserId, "gtip.version.created", new JsonObject
            {
                ["name"] = version.Name,
                ["effectiveFrom"] = version.EffectiveFrom.ToString("yyyy-MM-dd"),
                ["entryCount"] = version.EntryCount,
                ["sourceFileName"] = version.SourceFileName,
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }

        return new GtipCatalogVersionView(version.Id, version.Name, version.EffectiveFrom, version.SourceFileName, version.EntryCount, version.CreatedAt);
    }

    public async Task DeleteVersionAsync(Guid actingUserId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await db.GtipCatalogVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            ?? throw new GtipCatalogNotFoundException("Katalog sürümü bulunamadı.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.GtipCatalogEntries.Where(e => e.VersionId == versionId).ExecuteDeleteAsync(cancellationToken);
        await db.GtipCatalogVersions.Where(v => v.Id == versionId).ExecuteDeleteAsync(cancellationToken);
        Audit(actingUserId, "gtip.version.deleted", new JsonObject
        {
            ["name"] = version.Name,
            ["effectiveFrom"] = version.EffectiveFrom.ToString("yyyy-MM-dd"),
            ["entryCount"] = version.EntryCount,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<GtipCatalogEntryPage> SearchEntriesAsync(Guid versionId, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (!await db.GtipCatalogVersions.AsNoTracking().AnyAsync(v => v.Id == versionId, cancellationToken))
        {
            throw new GtipCatalogNotFoundException("Katalog sürümü bulunamadı.");
        }

        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var number = Math.Max(page, 1);
        var query = db.GtipCatalogEntries.AsNoTracking().Where(e => e.VersionId == versionId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = Filter(query, search.Trim());
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(e => e.Code)
            .Skip((number - 1) * size).Take(size)
            .Select(e => new GtipCatalogEntryView(e.Code, e.Description, e.Unit))
            .ToListAsync(cancellationToken);
        return new GtipCatalogEntryPage(items, total, number, size);
    }

    // ---- Ortak ----

    /// <summary>Yalnızca rakam, nokta ve boşluktan oluşan arama kod ön eki, diğerleri tanım içinde aranır.</summary>
    private static IQueryable<GtipCatalogEntry> Filter(IQueryable<GtipCatalogEntry> entries, string term)
    {
        var digits = new string([.. term.Where(c => c is not ('.' or ' ' or ' '))]);
        return digits.Length > 0 && digits.All(char.IsAsciiDigit)
            ? entries.Where(e => e.Code.StartsWith(digits))
            : entries.Where(e => e.Description.Contains(term));
    }

    private async Task<VersionRef?> InForceAsync(DateOnly day, CancellationToken cancellationToken) =>
        await db.GtipCatalogVersions.AsNoTracking()
            .Where(v => v.EffectiveFrom <= day)
            .OrderByDescending(v => v.EffectiveFrom)
            .Select(v => new VersionRef(v.Id, v.Name))
            .FirstOrDefaultAsync(cancellationToken);

    private void Audit(Guid actingUserId, string action, JsonObject details) =>
        db.PlatformAuditLog.Add(new PlatformAuditEntry(actingUserId, action, null, null, details.ToJsonString(), Activity.Current?.TraceId.ToString()));

    private sealed record VersionRef(Guid Id, string Name);
}
