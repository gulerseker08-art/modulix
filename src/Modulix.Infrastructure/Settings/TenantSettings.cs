using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Records;

namespace Modulix.Infrastructure.Settings;

internal sealed class TenantSettings(
    ModulixDbContext db,
    ModuleCatalog catalog,
    IModuleEntitlements entitlements,
    ITenantContextAccessor tenantAccessor,
    IRecordPipeline pipeline) : ITenantSettings
{
    public async Task<IReadOnlyList<TenantSettingValue>> GetAsync(CancellationToken cancellationToken)
    {
        var enabled = await EnabledModulesAsync(cancellationToken);
        var stored = await db.TenantSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, StringComparer.Ordinal, cancellationToken);

        return [.. catalog.Settings
            .Where(s => catalog.IsEnabled(s.ModuleKey, enabled))
            .Select(s => stored.TryGetValue(s.Definition.Key, out var row)
                ? new TenantSettingValue(s.ModuleKey, s.Definition, row.Value, row.Version)
                : new TenantSettingValue(s.ModuleKey, s.Definition, s.Definition.DefaultValue, 0))];
    }

    public async Task<RecordResult<IReadOnlyList<TenantSettingValue>>> SaveAsync(
        IReadOnlyDictionary<string, SettingWrite> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        var current = (await GetAsync(cancellationToken)).ToDictionary(s => s.Definition.Key, StringComparer.Ordinal);

        var errors = new List<RecordFieldError>();
        foreach (var (key, write) in values)
        {
            if (!current.TryGetValue(key, out var setting))
            {
                throw new ArgumentException(
                    catalog.FindSetting(key) is null ? "Tanımlı bir ayar değil." : "Bu ayarın modülü bu firmada kapalı.", key);
            }

            if (SettingRules.Validate(setting.Definition, write?.Value) is { } error)
            {
                errors.Add(new RecordFieldError(key, error));
            }
        }

        if (errors.Count > 0)
        {
            return RecordResult<IReadOnlyList<TenantSettingValue>>.Fail(RecordErrorCode.ValidationFailed, "Ayarlar kaydedilemedi.", errors);
        }

        // Okunduktan sonra başkası kaydettiyse hiçbir değer yazılmaz.
        if (values.Any(v => current[v.Key].Version != v.Value.Version))
        {
            return RecordResult<IReadOnlyList<TenantSettingValue>>.Fail(
                RecordErrorCode.Conflict, "Ayarlar siz düzenlerken değişti. Güncel hallerini alıp tekrar deneyin.");
        }

        var changed = values.Where(v => v.Value.Value != current[v.Key].Value).ToList();
        if (changed.Count == 0)
        {
            return RecordResult<IReadOnlyList<TenantSettingValue>>.Ok([.. current.Values]);
        }

        var ids = await db.TenantSettings.AsNoTracking()
            .Where(s => changed.Select(c => c.Key).Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Id, StringComparer.Ordinal, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var (key, write) in changed)
        {
            var result = ids.TryGetValue(key, out var id)
                ? await pipeline.UpdateAsync(
                    new RecordCommand(TenantSettingRecordStore.Key, id, new JsonObject { ["value"] = write.Value }, write.Version), cancellationToken)
                : await pipeline.CreateAsync(
                    new RecordCommand(TenantSettingRecordStore.Key, null, new JsonObject { ["key"] = key, ["value"] = write.Value }), cancellationToken);
            if (!result.IsSuccess)
            {
                // İşlem geri alınır; yarım kalan değişiklikler izlenmesin.
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                var error = result.Error!;
                return RecordResult<IReadOnlyList<TenantSettingValue>>.Fail(
                    error.Code,
                    error.Code == RecordErrorCode.Conflict ? "Ayarlar siz düzenlerken değişti. Güncel hallerini alıp tekrar deneyin." : error.Message,
                    error.FieldErrors?.Select(e => e with { Field = key }).ToList());
            }
        }

        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return RecordResult<IReadOnlyList<TenantSettingValue>>.Ok(await GetAsync(cancellationToken));
    }

    private async Task<IReadOnlySet<string>> EnabledModulesAsync(CancellationToken cancellationToken)
    {
        var tenant = tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan ayar okunamaz.");
        return await entitlements.GetEnabledAsync(tenant.TenantId, cancellationToken);
    }
}
