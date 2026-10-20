using System.Diagnostics;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Records;

/// <summary>
/// Kayıt boru hattı v0. Her işlem aynı sırayla ilerler:
/// tenant → lisans → yetki → veri kapsamı → doğrulama → kayıt → audit → olay.
/// Kayıt, audit ve olay tek seferde yazılır; biri başarısız olursa hiçbiri yazılmaz.
/// </summary>
public sealed class RecordPipeline(
    ITenantContextAccessor tenantAccessor,
    IRecordStoreRegistry stores,
    IEntitlementService entitlements,
    IPermissionService permissions,
    IDataScopeService dataScope,
    IAuditWriter audit,
    IDomainEventOutbox outbox,
    IUnitOfWork unitOfWork) : IRecordPipeline
{
    public async Task<RecordResult<RecordData>> GetAsync(RecordRef record, CancellationToken cancellationToken)
    {
        var (gate, tenant, store) = await AuthorizeAsync<RecordData>(record.EntityKey, RecordOperation.Read, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var data = await store!.FindAsync(record.Id, cancellationToken);
        if (data is null || !await dataScope.CanAccessAsync(tenant!, data, RecordOperation.Read, cancellationToken))
        {
            // Kapsam dışı kayıt, var olmayan kayıtla aynı cevabı alır; varlığı belli edilmez.
            return NotFound<RecordData>();
        }

        return RecordResult<RecordData>.Ok(data);
    }

    public async Task<RecordResult<RecordPage>> QueryAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        var (gate, tenant, store) = await AuthorizeAsync<RecordPage>(query.EntityKey, RecordOperation.Read, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var normalized = query with
        {
            Page = Math.Max(1, query.Page),
            PageSize = Math.Clamp(query.PageSize, 1, RecordQuery.MaxPageSize),
        };

        var page = await store!.QueryAsync(normalized, cancellationToken);

        // v0: kapsam sonuç üzerinde uygulanır. E4'te kapsam sorguya taşınır (sayfalama doğru kalsın diye).
        var visible = new List<RecordData>(page.Items.Count);
        foreach (var item in page.Items)
        {
            if (await dataScope.CanAccessAsync(tenant!, item, RecordOperation.Read, cancellationToken))
            {
                visible.Add(item);
            }
        }

        return RecordResult<RecordPage>.Ok(page with { Items = visible });
    }

    public async Task<RecordResult<RecordData>> CreateAsync(RecordCommand command, CancellationToken cancellationToken)
    {
        var (gate, tenant, store) = await AuthorizeAsync<RecordData>(command.EntityKey, RecordOperation.Create, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var errors = store!.Validate(RecordOperation.Create, command.Values);
        if (errors.Count > 0)
        {
            return Invalid<RecordData>(errors);
        }

        try
        {
            var created = await store.CreateAsync(command.Values, cancellationToken);
            Record(tenant!, command.Source, created.EntityKey, created.Id, AuditAction.Created, DomainEventTypes.RecordCreated, before: null, after: created.Values);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return RecordResult<RecordData>.Ok(created);
        }
        catch (RecordConflictException conflict)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, conflict.Message);
        }
    }

    public async Task<RecordResult<RecordData>> UpdateAsync(RecordCommand command, CancellationToken cancellationToken)
    {
        if (command.Id is not { } id)
        {
            return Invalid<RecordData>([new RecordFieldError("id", "Güncellenecek kaydın kimliği zorunludur.")]);
        }

        var (gate, tenant, store) = await AuthorizeAsync<RecordData>(command.EntityKey, RecordOperation.Update, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var before = await store!.FindAsync(id, cancellationToken);
        if (before is null || !await dataScope.CanAccessAsync(tenant!, before, RecordOperation.Update, cancellationToken))
        {
            return NotFound<RecordData>();
        }

        var errors = store.Validate(RecordOperation.Update, command.Values);
        if (errors.Count > 0)
        {
            return Invalid<RecordData>(errors);
        }

        try
        {
            var updated = await store.UpdateAsync(id, command.Values, command.ExpectedVersion, cancellationToken);
            if (updated is null)
            {
                return NotFound<RecordData>();
            }

            Record(tenant!, command.Source, updated.EntityKey, updated.Id, AuditAction.Updated, DomainEventTypes.RecordUpdated, before.Values, updated.Values);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return RecordResult<RecordData>.Ok(updated);
        }
        catch (RecordConflictException conflict)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, conflict.Message);
        }
    }

    public async Task<RecordResult<bool>> DeleteAsync(RecordRef record, long? expectedVersion, CancellationToken cancellationToken)
    {
        var (gate, tenant, store) = await AuthorizeAsync<bool>(record.EntityKey, RecordOperation.Delete, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var before = await store!.FindAsync(record.Id, cancellationToken);
        if (before is null || !await dataScope.CanAccessAsync(tenant!, before, RecordOperation.Delete, cancellationToken))
        {
            return NotFound<bool>();
        }

        try
        {
            if (!await store.DeleteAsync(record.Id, expectedVersion, cancellationToken))
            {
                return NotFound<bool>();
            }

            Record(tenant!, ExecutionSource.Api, before.EntityKey, before.Id, AuditAction.Deleted, DomainEventTypes.RecordDeleted, before.Values, after: null);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return RecordResult<bool>.Ok(true);
        }
        catch (RecordConflictException conflict)
        {
            return RecordResult<bool>.Fail(RecordErrorCode.Conflict, conflict.Message);
        }
    }

    /// <summary>Adım 1-3: tenant, lisans, yetki. Geçemezse hazır hata sonucu döner.</summary>
    private async Task<(RecordResult<T>? Gate, TenantContext? Tenant, IRecordStore? Store)> AuthorizeAsync<T>(
        string entityKey, RecordOperation operation, CancellationToken cancellationToken)
    {
        if (tenantAccessor.Current is not { } tenant)
        {
            return (RecordResult<T>.Fail(RecordErrorCode.NoActiveTenant, "Aktif tenant yok."), null, null);
        }

        if (stores.Find(entityKey) is not { } store)
        {
            return (RecordResult<T>.Fail(RecordErrorCode.UnknownEntity, $"Bilinmeyen kayıt türü: {entityKey}"), null, null);
        }

        if (!await entitlements.CanUseAsync(tenant.TenantId, store.FeatureKey, cancellationToken))
        {
            return (RecordResult<T>.Fail(RecordErrorCode.FeatureNotLicensed, "Bu özellik tenant'ın lisansında yok."), null, null);
        }

        if (!await permissions.HasPermissionAsync(tenant, store.EntityKey, operation, cancellationToken))
        {
            return (RecordResult<T>.Fail(RecordErrorCode.PermissionDenied, "Bu işlem için yetkiniz yok."), null, null);
        }

        return (null, tenant, store);
    }

    /// <summary>Adım 7-8: audit ve olay; kayıtla birlikte yazılmak üzere hazırlanır.</summary>
    private void Record(
        TenantContext tenant,
        ExecutionSource source,
        string entityKey,
        Guid recordId,
        AuditAction action,
        string eventType,
        JsonObject? before,
        JsonObject? after)
    {
        var changes = RecordDiff.Compute(before, after);
        audit.Add(new AuditEntry(tenant.TenantId, tenant.UserId, entityKey, recordId, action, changes, source, Activity.Current?.TraceId.ToString()));
        outbox.Add(new DomainEvent(tenant.TenantId, eventType, entityKey, recordId, changes.DeepClone().AsObject()));
    }

    private static RecordResult<T> NotFound<T>() => RecordResult<T>.Fail(RecordErrorCode.NotFound, "Kayıt bulunamadı.");

    private static RecordResult<T> Invalid<T>(IReadOnlyList<RecordFieldError> errors) =>
        RecordResult<T>.Fail(RecordErrorCode.ValidationFailed, "Doğrulama hatası.", errors);
}

/// <summary>İki kayıt hali arasındaki fark: { "alan": { "old": ..., "new": ... } }. Yalnızca değişen alanlar.</summary>
public static class RecordDiff
{
    public static JsonObject Compute(JsonObject? before, JsonObject? after)
    {
        var changes = new JsonObject();
        var fields = (before?.Select(p => p.Key) ?? Enumerable.Empty<string>())
            .Union(after?.Select(p => p.Key) ?? Enumerable.Empty<string>(), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (var field in fields)
        {
            var oldValue = before is not null && before.TryGetPropertyValue(field, out var o) ? o : null;
            var newValue = after is not null && after.TryGetPropertyValue(field, out var n) ? n : null;
            if (JsonNode.DeepEquals(oldValue, newValue))
            {
                continue;
            }

            var change = new JsonObject();
            if (before is not null)
            {
                change["old"] = oldValue?.DeepClone();
            }

            if (after is not null)
            {
                change["new"] = newValue?.DeepClone();
            }

            changes[field] = change;
        }

        return changes;
    }
}
