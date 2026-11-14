using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Audit;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Authorization;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Records;

namespace Modulix.Infrastructure.Audit;

internal sealed class AuditLog(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    ModuleCatalog catalog,
    IModuleEntitlements entitlements,
    MembershipPermissions membershipPermissions,
    IChoiceLists choiceLists,
    IAuditWriter audit) : IAuditLog
{
    public const int MaxPageSize = 100;

    /// <summary>Firmanın üyesi olmayan kişi (platform yöneticisi) denetim kaydında bu adla görünür.</summary>
    public const string PlatformAdministratorName = "Modülix platform yöneticisi";

    /// <summary>Kullanıcısız işlemler (ör. örnek veri, arka plan işi).</summary>
    public const string SystemName = "Sistem";

    public async Task<AuditLogPage> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Clamp(query.Page, 1, 100_000);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var filtered = Filter(query);
        var total = await filtered.CountAsync(cancellationToken);
        var entries = await Order(filtered, query.Sort)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);

        return new AuditLogPage(await DescribeAsync(entries, cancellationToken), page, pageSize, total);
    }

    public async Task<IReadOnlyList<AuditActor>> ListActorsAsync(CancellationToken cancellationToken)
    {
        var ids = await db.AuditLog.AsNoTracking().Select(a => a.UserId).Distinct().ToListAsync(cancellationToken);
        var users = await LoadUsersAsync(RequireTenant(), ids, cancellationToken);
        return [.. ids
            .Select(id => new AuditActor(id, users.GetValueOrDefault(id)?.Name ?? (id == Guid.Empty ? SystemName : "Bilinmeyen kişi")))
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)];
    }

    public async Task<IReadOnlyList<AuditLogItem>> ExportAsync(AuditQuery query, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filtered = Filter(query);
        var count = await filtered.CountAsync(cancellationToken);
        if (count > limit)
        {
            throw new AuditExportTooLargeException(count, limit);
        }

        var entries = await filtered.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).ToListAsync(cancellationToken);
        var items = await DescribeAsync(entries, cancellationToken);

        // Kim, hangi filtreyle, kaç kayıt dışa aktardı.
        var tenant = RequireTenant();
        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            CoreModule.AuditedEntityKeys.Audit,
            Guid.CreateVersion7(),
            AuditAction.Exported,
            RecordDiff.Compute(null, new JsonObject { ["filters"] = Describe(query), ["rows"] = items.Count }),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));
        await db.SaveChangesAsync(cancellationToken);

        return items;
    }

    public async Task<AuditLogPage> GetRecordHistoryAsync(RecordRef record, int page, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        page = Math.Clamp(page, 1, 100_000);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var entries = Filter(new AuditQuery(EntityKey: record.EntityKey, RecordId: record.Id));
        var total = await entries.CountAsync(cancellationToken);
        var rows = await entries
            .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);

        return new AuditLogPage(await DescribeAsync(rows, cancellationToken, recordReadable: true), page, pageSize, total);
    }

    // Kayıtlar tenant verisidir: sorgu filtresi ve RLS yalnızca aktif tenant'ınkileri döndürür.
    // Anahtarlar AuditQuery.SortKeys'tedir; bilinmeyen anahtar uç noktada reddedilir, burada varsayılan sıraya düşer.
    private static IOrderedQueryable<AuditLogEntry> Order(IQueryable<AuditLogEntry> entries, string? sort) => sort switch
    {
        "occurredAt" => entries.OrderBy(a => a.OccurredAt).ThenBy(a => a.Id),
        "entityKey" => entries.OrderBy(a => a.EntityKey).ThenByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id),
        "-entityKey" => entries.OrderByDescending(a => a.EntityKey).ThenByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id),
        "action" => entries.OrderBy(a => a.Action).ThenByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id),
        "-action" => entries.OrderByDescending(a => a.Action).ThenByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id),
        "source" => entries.OrderBy(a => a.Source).ThenByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id),
        "-source" => entries.OrderByDescending(a => a.Source).ThenByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id),
        _ => entries.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id),
    };

    private IQueryable<AuditLogEntry> Filter(AuditQuery query)
    {
        var entries = db.AuditLog.AsNoTracking();
        if (query.UserId is { } userId)
        {
            entries = entries.Where(a => a.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityKey))
        {
            entries = entries.Where(a => a.EntityKey == query.EntityKey);
        }

        if (query.RecordId is { } recordId)
        {
            entries = entries.Where(a => a.RecordId == recordId);
        }

        if (query.Action is { } action)
        {
            entries = entries.Where(a => a.Action == action);
        }

        if (query.Source is { } source)
        {
            entries = entries.Where(a => a.Source == source);
        }

        if (query.From is { } from)
        {
            entries = entries.Where(a => a.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            entries = entries.Where(a => a.OccurredAt < to);
        }

        return entries;
    }

    /// <param name="recordReadable">Okur kaydın kendisini görebiliyor (kayıt geçmişi): kaydın değerleri gizlenmez.</param>
    private async Task<IReadOnlyList<AuditLogItem>> DescribeAsync(
        IReadOnlyList<AuditLogEntry> entries, CancellationToken cancellationToken, bool recordReadable = false)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var tenant = RequireTenant();
        var viewer = await membershipPermissions.GetAsync(
            tenant.MembershipId, await entitlements.GetEnabledAsync(tenant.TenantId, cancellationToken), cancellationToken);
        bool Visible(AuditedEntity? entity) =>
            viewer.IsAdministrator || entity?.ReadPermission is not { } permission || viewer.Permissions.Contains(permission);

        var custom = await CustomFieldsAsync(entries, cancellationToken);
        var lists = await ChoiceListsAsync(entries, cancellationToken);
        var parsed = entries
            .Select(e => (
                Entry: e,
                Entity: WithChoiceLists(WithCustomFields(catalog.FindAuditedEntity(e.EntityKey), custom), lists),
                Changes: Parse(e.Changes)))
            .ToList();

        // Değerlerde geçen kişiler ve bağlı kayıtlar; yalnızca okurun görebildiği satırlardan.
        var userIds = new HashSet<Guid>(entries.Select(e => e.UserId));
        var records = new HashSet<(string EntityKey, Guid Id)>();
        foreach (var (entry, entity, changes) in parsed)
        {
            records.Add((entry.EntityKey, entry.RecordId));
            if (entity is null || !(recordReadable || Visible(entity)))
            {
                continue;
            }

            foreach (var (field, value) in Values(changes))
            {
                if (entity.UserFields.Contains(field) && AsGuid(value) is { } user)
                {
                    userIds.Add(user);
                }
                else if (entity.References.TryGetValue(field, out var target) && AsGuid(value) is { } id)
                {
                    records.Add((target, id));
                }
            }
        }

        var users = await LoadUsersAsync(tenant, userIds, cancellationToken);
        var titles = await LoadTitlesAsync(records, cancellationToken);

        string? Title(string entityKey, Guid id)
        {
            var entity = catalog.FindAuditedEntity(entityKey);
            return Visible(entity) ? titles.GetValueOrDefault((entityKey, id)) : null;
        }

        string? Format(AuditedEntity? entity, string field, JsonNode? value) => value switch
        {
            null => null,
            JsonArray array => string.Join(", ", array.Select(item => Format(entity, field, item)).Where(v => v is not null)),
            JsonValue v when v.GetValueKind() == JsonValueKind.True => "Evet",
            JsonValue v when v.GetValueKind() == JsonValueKind.False => "Hayır",
            JsonValue v when v.GetValueKind() == JsonValueKind.String => FormatText(entity, field, v.GetValue<string>()),
            _ => value.ToJsonString(),
        };

        string FormatText(AuditedEntity? entity, string field, string text)
        {
            if (entity is null)
            {
                return text;
            }

            if (entity.Options.TryGetValue(field, out var options) && options.TryGetValue(text, out var label))
            {
                return label;
            }

            if (entity.UserFields.Contains(field) && Guid.TryParse(text, out var userId))
            {
                return users.GetValueOrDefault(userId)?.Name ?? "Bilinmeyen kişi";
            }

            if (entity.References.TryGetValue(field, out var target) && Guid.TryParse(text, out var id))
            {
                return Title(target, id) ?? "Bağlı kayıt";
            }

            return text;
        }

        return parsed.Select(row =>
        {
            var (entry, entity, changes) = row;
            var visible = recordReadable || Visible(entity);

            // Hassas alanlar (Y8b): kaydı görebilen okur da izni yoksa değerini göremez.
            var hidden = catalog.HiddenFields(entry.EntityKey, viewer.Permissions);

            // Alanlar modülün tanım sırasıyla (ör. önce ad); tanımsız alanlar sonda.
            var order = entity?.Fields.Keys.Select((key, index) => (key, index)).ToDictionary(p => p.key, p => p.index) ?? [];
            var fields = changes.OrderBy(change => order.GetValueOrDefault(change.Key, int.MaxValue)).Select(change =>
            {
                var label = entity?.Fields.GetValueOrDefault(change.Key) ?? change.Key;
                if (!visible)
                {
                    return new AuditFieldChange(change.Key, label, null, null);
                }

                if (hidden.Contains(change.Key))
                {
                    return new AuditFieldChange(change.Key, label, null, null, Hidden: true);
                }

                var values = change.Value as JsonObject;
                return new AuditFieldChange(
                    change.Key,
                    label,
                    Format(entity, change.Key, values?["old"]),
                    Format(entity, change.Key, values?["new"]));
            }).ToList();

            var user = users.GetValueOrDefault(entry.UserId);
            return new AuditLogItem(
                entry.Id,
                entry.OccurredAt,
                entry.UserId,
                user?.Name ?? (entry.UserId == Guid.Empty ? SystemName : "Bilinmeyen kişi"),
                user?.Email,
                entry.EntityKey,
                entity?.DisplayName ?? entry.EntityKey,
                entry.RecordId,
                recordReadable ? titles.GetValueOrDefault((entry.EntityKey, entry.RecordId)) : Title(entry.EntityKey, entry.RecordId),
                entry.Action,
                entry.Source,
                !visible,
                fields,
                entry.CorrelationId,
                entry.Subscription);
        }).ToList();
    }

    // Kayıtlardaki özel alanlar (Y9a) "custom.anahtar" adıyla yazılır; adları ve seçenek adları tanımlardan gelir
    // (arşivlenmiş alanlar dahil: geçmiş değerler anlamını korur).
    private async Task<ILookup<string, Core.Customization.CustomField>> CustomFieldsAsync(
        IReadOnlyList<AuditLogEntry> entries, CancellationToken cancellationToken)
    {
        var entityKeys = entries.Select(e => e.EntityKey).Where(k => catalog.FindCustomizable(k) is not null).Distinct().ToList();
        if (entityKeys.Count == 0)
        {
            return Enumerable.Empty<Core.Customization.CustomField>().ToLookup(f => f.EntityKey);
        }

        var fields = await db.CustomFields.AsNoTracking()
            .Where(f => entityKeys.Contains(f.EntityKey))
            .OrderBy(f => f.SortOrder)
            .ToListAsync(cancellationToken);
        return fields.ToLookup(f => f.EntityKey);
    }

    private static AuditedEntity? WithCustomFields(AuditedEntity? entity, ILookup<string, Core.Customization.CustomField> custom)
    {
        if (entity is null || !custom.Contains(entity.EntityKey))
        {
            return entity;
        }

        var fields = new Dictionary<string, string>(entity.Fields);
        var options = new Dictionary<string, IReadOnlyDictionary<string, string>>(entity.Options);
        foreach (var field in custom[entity.EntityKey])
        {
            var name = RecordFields.CustomPrefix + field.Key;
            fields[name] = field.Label;
            if (field.HasOptions)
            {
                options[name] = field.Options.ToDictionary(o => o.Key, o => o.Label, StringComparer.Ordinal);
            }
        }

        return entity with { Fields = fields, Options = options };
    }

    // Liste alanlarının (Y9b) değer adları firmanın listesinden gelir: yeniden adlandırılan değer eski kayıtlarda da yeni
    // adıyla görünür; pasif değerler de listededir.
    private async Task<ILookup<string, (string Field, ChoiceList List)>> ChoiceListsAsync(
        IReadOnlyList<AuditLogEntry> entries, CancellationToken cancellationToken)
    {
        var lists = new List<(string EntityKey, string Field, ChoiceList List)>();
        foreach (var definition in entries.Select(e => e.EntityKey).Distinct().SelectMany(catalog.ChoiceListsOf))
        {
            lists.Add((definition.EntityKey, definition.Field, await choiceLists.GetAsync(definition.Key, cancellationToken)));
        }

        return lists.ToLookup(l => l.EntityKey, l => (l.Field, l.List));
    }

    private static AuditedEntity? WithChoiceLists(AuditedEntity? entity, ILookup<string, (string Field, ChoiceList List)> lists)
    {
        if (entity is null || !lists.Contains(entity.EntityKey))
        {
            return entity;
        }

        var options = new Dictionary<string, IReadOnlyDictionary<string, string>>(entity.Options);
        foreach (var (field, list) in lists[entity.EntityKey])
        {
            options[field] = list.Values.ToDictionary(v => v.Key, v => v.Label, StringComparer.Ordinal);
        }

        return entity with { Options = options };
    }

    /// <summary>Kişi adları: firmanın üyesi (veya eski üyesi) adıyla, üye olmayan platform yöneticisi sabit adla görünür.</summary>
    private async Task<Dictionary<Guid, AuditUser>> LoadUsersAsync(
        TenantContext tenant, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var accounts = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.IsPlatformAdministrator })
            .ToListAsync(cancellationToken);
        var members = (await db.Memberships.AsNoTracking()
            .Where(m => m.TenantId == tenant.TenantId && userIds.Contains(m.UserId))
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken)).ToHashSet();

        return accounts.ToDictionary(
            a => a.Id,
            a => members.Contains(a.Id) || !a.IsPlatformAdministrator
                ? new AuditUser(a.DisplayName, a.Email)
                : new AuditUser(PlatformAdministratorName, null));
    }

    /// <summary>
    /// Kayıtların adı denetim kaydından okunur: türün ad alanının bilinen son değeri (silinen kaydın da adı kalır).
    /// </summary>
    private async Task<Dictionary<(string EntityKey, Guid Id), string>> LoadTitlesAsync(
        IReadOnlyCollection<(string EntityKey, Guid Id)> records, CancellationToken cancellationToken)
    {
        var titled = records.Where(r => catalog.FindAuditedEntity(r.EntityKey)?.TitleFields.Count > 0).ToList();
        if (titled.Count == 0)
        {
            return [];
        }

        var ids = titled.Select(r => r.Id).Distinct().ToList();
        var history = await db.AuditLog.AsNoTracking()
            .Where(a => ids.Contains(a.RecordId))
            .OrderBy(a => a.OccurredAt)
            .Select(a => new { a.EntityKey, a.RecordId, a.Changes })
            .ToListAsync(cancellationToken);

        // Her ad alanının bilinen son değeri; sonra alan sırasıyla birleştirilir (ör. "Selin Aydın").
        var parts = new Dictionary<(string, Guid), Dictionary<string, string>>();
        foreach (var row in history)
        {
            var fields = catalog.FindAuditedEntity(row.EntityKey)?.TitleFields ?? [];
            var changes = Parse(row.Changes);
            foreach (var field in fields)
            {
                if (changes[field] is JsonObject change && (change["new"] ?? change["old"]) is JsonValue text
                    && text.GetValueKind() == JsonValueKind.String)
                {
                    var known = parts.TryGetValue((row.EntityKey, row.RecordId), out var existing)
                        ? existing
                        : parts[(row.EntityKey, row.RecordId)] = [];
                    known[field] = text.GetValue<string>();
                }
            }
        }

        // Seçenekli ad alanında seçeneğin adı (ör. ayar anahtarı yerine ayarın adı).
        string? Part(AuditedEntity entity, Dictionary<string, string> values, string field) =>
            values.GetValueOrDefault(field) is { } value
                && entity.Options.TryGetValue(field, out var options) && options.TryGetValue(value, out var label)
                ? label
                : values.GetValueOrDefault(field);

        return parts.ToDictionary(
            p => p.Key,
            p =>
            {
                var entity = catalog.FindAuditedEntity(p.Key.Item1)!;
                return string.Join(" ", entity.TitleFields.Select(f => Part(entity, p.Value, f)).Where(v => !string.IsNullOrWhiteSpace(v)));
            });
    }

    private static IEnumerable<(string Field, JsonNode? Value)> Values(JsonObject changes) =>
        changes.SelectMany(change => change.Value is JsonObject values
            ? new[] { (change.Key, values["old"]), (change.Key, values["new"]) }
            : []);

    private static Guid? AsGuid(JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() == JsonValueKind.String && Guid.TryParse(v.GetValue<string>(), out var id) ? id : null;

    private static JsonObject Parse(string changes) =>
        JsonNode.Parse(changes) as JsonObject ?? [];

    private static string Describe(AuditQuery query)
    {
        var parts = new List<string>();
        void Add(string name, object? value)
        {
            if (value is not null)
            {
                parts.Add(FormattableString.Invariant($"{name}={value}"));
            }
        }

        Add("kullanıcı", query.UserId);
        Add("tür", string.IsNullOrWhiteSpace(query.EntityKey) ? null : query.EntityKey);
        Add("kayıt", query.RecordId);
        Add("işlem", query.Action);
        Add("kaynak", query.Source);
        Add("başlangıç", query.From?.ToString("O", CultureInfo.InvariantCulture));
        Add("bitiş", query.To?.ToString("O", CultureInfo.InvariantCulture));
        return parts.Count == 0 ? "tümü" : string.Join("; ", parts);
    }

    private sealed record AuditUser(string Name, string? Email);

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan denetim kaydı okunamaz.");
}
