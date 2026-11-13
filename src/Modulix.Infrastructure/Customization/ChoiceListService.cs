using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Customization;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Customization;

/// <summary>
/// Seçim listeleri (Y9b). Kayıt depolarına firmanın listesini verir (<see cref="IChoiceLists"/>): liste başına istekte
/// bir kez okunur. Firma listeyi düzenleyene kadar tabloda satırı yoktur; koddaki varsayılan geçerlidir.
/// </summary>
internal sealed class ChoiceListService(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    ModuleCatalog catalog,
    IModuleEntitlements entitlements,
    IAuditWriter audit,
    CustomizationLimits limits) : IChoiceListService, IChoiceLists
{
    private readonly Dictionary<(Guid TenantId, string ListKey), ChoiceList> cache = [];

    public async Task<ChoiceList> GetAsync(string listKey, CancellationToken cancellationToken)
    {
        var definition = catalog.FindChoiceList(listKey)?.List
            ?? throw new InvalidOperationException($"Tanımsız seçim listesi: '{listKey}'.");
        if (tenantAccessor.Current is not { } tenant)
        {
            return ChoiceListRules.Resolve(definition, null);
        }

        if (cache.TryGetValue((tenant.TenantId, listKey), out var list))
        {
            return list;
        }

        var stored = await db.ChoiceLists.AsNoTracking().SingleOrDefaultAsync(l => l.ListKey == listKey, cancellationToken);
        list = ChoiceListRules.Resolve(definition, stored?.Values);
        cache[(tenant.TenantId, listKey)] = list;
        return list;
    }

    public async Task<IReadOnlyList<ChoiceListInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var definitions = await EnabledAsync(cancellationToken);
        var keys = definitions.Select(d => d.Key).ToList();
        var stored = await db.ChoiceLists.AsNoTracking().Where(l => keys.Contains(l.ListKey)).ToListAsync(cancellationToken);
        return definitions
            .Select(d => stored.FirstOrDefault(s => s.ListKey == d.Key) is { } row
                ? new ChoiceListInfo(d, ChoiceListRules.Resolve(d, row.Values), Customized: true)
                : new ChoiceListInfo(d, ChoiceListRules.Resolve(d, null), Customized: false))
            .ToList();
    }

    public async Task<ChoiceListInfo?> SaveAsync(string listKey, IReadOnlyList<ChoiceValueInput> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        var tenant = tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan listeler yönetilemez.");
        if ((await EnabledAsync(cancellationToken)).FirstOrDefault(d => d.Key == listKey) is not { } definition)
        {
            return null;
        }

        var row = await db.ChoiceLists.SingleOrDefaultAsync(l => l.ListKey == listKey, cancellationToken);
        var current = ChoiceListRules.Resolve(definition, row?.Values);
        var updated = ChoiceListRules.Apply(definition, current, values, limits.MaxValuesPerList);
        var after = ChoiceListRules.Resolve(definition, updated);
        // İlk düzenleme firmanın listesini oluşturur: denetim kaydında tam liste görünür (ve listenin adı kayda geçer).
        var created = row is null;
        if (row is null)
        {
            row = new TenantChoiceList(listKey);
            db.ChoiceLists.Add(row);
        }

        row.Values = updated;
        var (before, now) = (created ? null : Snapshot(definition, current), Snapshot(definition, after));
        if (!JsonNode.DeepEquals(before, now))
        {
            audit.Add(new AuditEntry(
                tenant.TenantId,
                tenant.UserId,
                CoreModule.AuditedEntityKeys.ChoiceList,
                row.Id,
                created ? AuditAction.Created : AuditAction.Updated,
                RecordDiff.Compute(before, now),
                ExecutionSource.Api,
                Activity.Current?.TraceId.ToString()));
        }

        await db.SaveChangesAsync(cancellationToken);
        cache.Clear();
        return new ChoiceListInfo(definition, after, Customized: true);
    }

    // Kapalı modülün listesi görünmez ve düzenlenemez; firmanın düzenlemesi silinmez, modül açılınca geri gelir.
    private async Task<IReadOnlyList<ChoiceListDefinition>> EnabledAsync(CancellationToken cancellationToken)
    {
        var tenant = tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan listeler okunamaz.");
        var enabled = await entitlements.GetEnabledAsync(tenant.TenantId, cancellationToken);
        return catalog.ChoiceLists.Where(l => catalog.IsEnabled(l.ModuleKey, enabled)).Select(l => l.List).ToList();
    }

    // Denetim kaydında liste tek satırdır: değerler sırasıyla, pasifler işaretli.
    private static JsonObject Snapshot(ChoiceListDefinition definition, ChoiceList list) => new()
    {
        ["list"] = definition.DisplayName,
        ["values"] = new JsonArray([.. list.Values.Select(v => JsonValue.Create(v.Active ? v.Label : $"{v.Label} (pasif)"))]),
    };
}
