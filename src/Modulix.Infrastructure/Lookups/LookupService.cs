using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Lookups;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Lookups;

/// <summary>
/// Lookup ayarları ve arama (ADR-0016). Ayar satırı yoksa kodda tanımlı varsayılan geçerlidir. Arama ve kimlikten çözümleme
/// kayıt boru hattından geçer; böylece izin, veri kapsamı, hassas alan ve tenant kuralları hedef kaydınkiyle birebir aynıdır.
/// Önbellek yoktur: ayar ve yetki değişikliği bir sonraki istekte geçerlidir.
/// </summary>
internal sealed class LookupService(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    ModuleCatalog catalog,
    IModuleEntitlements entitlements,
    IAuditWriter audit,
    IRecordPipeline pipeline,
    IRecordAccess access,
    ITenantMembers members,
    IDataScopeService scope) : ILookupSettings, ILookupSearch
{
    // Üye adı araması: tr-TR kültüründe büyük/küçük harf ve aksan duyarsız ("gul" → "Gül", "ISIK" → "Işık").
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public async Task<IReadOnlyList<LookupInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var enabled = await EnabledAsync(cancellationToken);
        var stored = await db.LookupSettings.AsNoTracking().ToListAsync(cancellationToken);
        return catalog.Lookups
            .Where(l => catalog.IsEnabled(l.ModuleKey, enabled))
            .Select(l => Info(l.ModuleKey, l.Lookup, stored.FirstOrDefault(s => s.LookupKey == l.Lookup.Key)))
            .ToList();
    }

    public async Task<LookupInfo?> FindAsync(string key, CancellationToken cancellationToken)
    {
        if (catalog.FindLookup(key) is not { } found || !await IsEnabledAsync(found.ModuleKey, cancellationToken))
        {
            return null;
        }

        var row = await db.LookupSettings.AsNoTracking().SingleOrDefaultAsync(s => s.LookupKey == key, cancellationToken);
        return Info(found.ModuleKey, found.Lookup, row);
    }

    public async Task<RecordResult<LookupInfo>> SaveAsync(string key, bool? searchable, long version, CancellationToken cancellationToken)
    {
        var tenant = tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan lookup ayarı yazılamaz.");
        if (catalog.FindLookup(key) is not { } found || !await IsEnabledAsync(found.ModuleKey, cancellationToken))
        {
            return RecordResult<LookupInfo>.Fail(RecordErrorCode.NotFound, "Lookup bulunamadı.");
        }

        var row = await db.LookupSettings.SingleOrDefaultAsync(s => s.LookupKey == key, cancellationToken);
        if ((row?.Version ?? 0) != version)
        {
            return Conflict(row?.Version ?? 0, version);
        }

        var before = Info(found.ModuleKey, found.Lookup, row);
        var created = row is null;
        if ((created && searchable is null) || (!created && row!.Searchable == searchable))
        {
            return RecordResult<LookupInfo>.Ok(before);
        }

        if (row is null)
        {
            row = new TenantLookupSetting(key);
            db.LookupSettings.Add(row);
        }

        row.Set(searchable, tenant.UserId);
        var after = Info(found.ModuleKey, found.Lookup, row);
        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            CoreModule.AuditedEntityKeys.LookupSetting,
            row.Id,
            created ? AuditAction.Created : AuditAction.Updated,
            RecordDiff.Compute(created ? null : Snapshot(before), Snapshot(after)),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Eşzamanlı yazım: aynı sürümden başlayan ikinci kayıt (eşzamanlılık belirteci ya da tekil anahtar) çakışmadır.
            return Conflict(version + 1, version);
        }

        return RecordResult<LookupInfo>.Ok(after);
    }

    public async Task<RecordResult<LookupPage>> SearchAsync(LookupSearch request, CancellationToken cancellationToken)
    {
        if (await FindAsync(request.Key, cancellationToken) is not { } info)
        {
            return RecordResult<LookupPage>.Fail(RecordErrorCode.NotFound, "Lookup bulunamadı.");
        }

        var definition = info.Definition;
        var text = string.IsNullOrWhiteSpace(request.Text)
            ? null
            : string.Join(' ', request.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text is not null)
        {
            // Kapalı aramayı yalnızca arayüzde gizlemek yetmez: metin araması burada reddedilir.
            if (!info.Searchable)
            {
                return SearchError("Bu alanda metin araması kapalı; listeden seçin.");
            }

            if (text.Length > RecordLookupLimits.MaxSearchLength)
            {
                return SearchError($"En fazla {RecordLookupLimits.MaxSearchLength} karakter aranabilir.");
            }

            if (text.Length < definition.MinSearchLength)
            {
                return SearchError($"En az {definition.MinSearchLength} karakter girin.");
            }
        }

        var page = Math.Max(1, request.Page);
        var size = request.PageSize <= 0 ? definition.PageSize : Math.Min(request.PageSize, RecordLookupLimits.MaxPageSize);
        return definition.IsMember
            ? await SearchMembersAsync(info, text, page, size, cancellationToken)
            : await SearchRecordsAsync(info, text, page, size, request.ParentId, cancellationToken);
    }

    public async Task<RecordResult<IReadOnlyList<LookupItem>>> ResolveAsync(
        string key, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (await FindAsync(key, cancellationToken) is not { } info)
        {
            return RecordResult<IReadOnlyList<LookupItem>>.Fail(RecordErrorCode.NotFound, "Lookup bulunamadı.");
        }

        if (ids.Count > RecordLookupLimits.MaxResolveIds)
        {
            return RecordResult<IReadOnlyList<LookupItem>>.Fail(RecordErrorCode.ValidationFailed,
                $"En fazla {RecordLookupLimits.MaxResolveIds} kimlik çözümlenebilir.");
        }

        var items = new List<LookupItem>();
        var all = info.Definition.IsMember ? await members.ListActiveAsync(cancellationToken) : null;
        foreach (var id in ids.Distinct())
        {
            if (all is not null)
            {
                items.Add(all.FirstOrDefault(m => m.UserId == id) is { } member
                    ? new LookupItem(id, member.DisplayName, null, false)
                    : Unavailable(id));
                continue;
            }

            // Silinmiş, erişimi kalkmış ve hiç olmamış kayıt aynı cevabı alır: ad ve varlık sızmaz.
            var result = await pipeline.GetAsync(new RecordRef(info.Definition.TargetEntityKey!, id), cancellationToken);
            items.Add(result.IsSuccess ? ToItem(info.Definition, result.Value!) : Unavailable(id));
        }

        return RecordResult<IReadOnlyList<LookupItem>>.Ok(items);
    }

    private async Task<RecordResult<LookupPage>> SearchRecordsAsync(
        LookupInfo info, string? text, int page, int size, Guid? parentId, CancellationToken cancellationToken)
    {
        var definition = info.Definition;
        var filters = new Dictionary<string, string>();
        if (definition.DependsOnFilter is { } filter && parentId is { } parent)
        {
            filters[filter] = parent.ToString();
        }

        var result = await pipeline.QueryAsync(
            new RecordQuery(definition.TargetEntityKey!, page, size, text, filters), cancellationToken);
        if (!result.IsSuccess)
        {
            return RecordResult<LookupPage>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
        }

        var canView = await access.CanAsync(definition.TargetEntityKey!, RecordOperation.Read, cancellationToken);
        return RecordResult<LookupPage>.Ok(new LookupPage(
            [.. result.Value!.Items.Select(i => ToItem(definition, i))],
            result.Value.Page, result.Value.PageSize, result.Value.TotalCount, info.Searchable, canView));
    }

    // Üyeler kayıt değildir: seçilebilenler kişinin kayıt kapsamındaki etkin üyelerdir (sorumlu atama kuralı, Y8).
    private async Task<RecordResult<LookupPage>> SearchMembersAsync(
        LookupInfo info, string? text, int page, int size, CancellationToken cancellationToken)
    {
        var owners = await scope.GetOwnersAsync(tenantAccessor.Current!, info.Definition.SourceEntityKey, cancellationToken);
        var matches = (await members.ListActiveAsync(cancellationToken))
            .Where(m => owners is null || owners.Contains(m.UserId))
            .Where(m => text is null || TurkishCulture.CompareInfo.IndexOf(m.DisplayName, text, Loose) >= 0)
            .OrderBy(m => m.DisplayName, StringComparer.Create(TurkishCulture, ignoreCase: true))
            .ThenBy(m => m.UserId)
            .ToList();
        return RecordResult<LookupPage>.Ok(new LookupPage(
            [.. matches.Skip((page - 1) * size).Take(size).Select(m => new LookupItem(m.UserId, m.DisplayName, null, false))],
            page, size, matches.Count, info.Searchable, CanView: false));
    }

    private static RecordResult<LookupPage> SearchError(string message) =>
        RecordResult<LookupPage>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError("search", message)]);

    private static LookupItem Unavailable(Guid id) => new(id, null, null, Unavailable: true);

    // Etiket ve ikincil bilgi yalnızca boru hattının verdiği (hassas alanları çıkarılmış) değerlerden kurulur.
    private static LookupItem ToItem(LookupDefinition definition, RecordData data)
    {
        string? Text(string field) =>
            data.Values[field] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : null;

        var label = string.Join(' ', definition.LabelFields.Select(Text).Where(t => t is not null));
        var secondary = string.Join(" · ", definition.SecondaryFields.Select(Text).Where(t => t is not null));
        return new LookupItem(data.Id, label.Length == 0 ? "(adsız)" : label, secondary.Length == 0 ? null : secondary, false);
    }

    private static LookupInfo Info(string moduleKey, LookupDefinition definition, TenantLookupSetting? row) =>
        new(moduleKey, definition, row?.Searchable ?? definition.DefaultSearchable, row?.Searchable is not null, row?.Version ?? 0);

    private static JsonObject Snapshot(LookupInfo info) => new()
    {
        ["lookup"] = $"{info.Definition.SourceEntityKey} · {info.Definition.Title}",
        ["searchable"] = info.Searchable ? "Açık" : "Kapalı",
        ["source"] = info.Customized ? "Firma ayarı" : "Varsayılan",
    };

    private static RecordResult<LookupInfo> Conflict(long current, long expected) => RecordResult<LookupInfo>.Fail(
        RecordErrorCode.Conflict,
        $"Ayar siz düzenlerken değişti (beklenen sürüm {expected}, güncel sürüm {current}). Güncel halini alıp tekrar deneyin.");

    private async Task<bool> IsEnabledAsync(string moduleKey, CancellationToken cancellationToken) =>
        catalog.IsEnabled(moduleKey, await EnabledAsync(cancellationToken));

    private async Task<IReadOnlySet<string>> EnabledAsync(CancellationToken cancellationToken)
    {
        var tenant = tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan lookup okunamaz.");
        return await entitlements.GetEnabledAsync(tenant.TenantId, cancellationToken);
    }
}
