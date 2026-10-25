using System.Diagnostics;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;

namespace Modulix.Core.Records;

/// <summary>
/// Kayıt boru hattı v0. Her işlem aynı sırayla ilerler:
/// tenant → lisans → yetki → veri kapsamı → doğrulama → kayıt → audit → olay.
/// Kayıt, audit ve olay tek seferde yazılır; biri başarısız olursa hiçbiri yazılmaz.
/// Hassas alanlar (Y8b) yetki adımında belirlenir: okumada çıkarılır, yazmada reddedilir, süzmede kullanılamaz.
/// Özel alanlar (Y9a) doğrulama adımında tanımlarına göre denetlenir ve kaydın mevcut özel alanlarıyla birleştirilir.
/// Olay zarfı (ADR-0007) burada dolar: sıra numarası kaydın yeni sürümü, kişi ve kaynak denetim kaydıyla aynı; abonenin
/// yaptığı değişiklik tetikleyen olaya bağlanır (<see cref="EventHandlingScope"/>).
/// </summary>
public sealed class RecordPipeline(
    ITenantContextAccessor tenantAccessor,
    IRecordStoreRegistry stores,
    IEntitlementService entitlements,
    IPermissionService permissions,
    IDataScopeService dataScope,
    IFieldAccessService fieldAccess,
    ICustomFieldProvider customFields,
    IAuditWriter audit,
    IDomainEventOutbox outbox,
    IUnitOfWork unitOfWork,
    EventHandlingScope? handling = null) : IRecordPipeline
{
    public async Task<RecordResult<RecordData>> GetAsync(RecordRef record, CancellationToken cancellationToken)
    {
        var (gate, tenant, store) = await AuthorizeAsync<RecordData>(record.EntityKey, RecordOperation.Read, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var hidden = await fieldAccess.GetHiddenFieldsAsync(tenant!, store!.EntityKey, cancellationToken);
        var custom = await CustomFieldsAsync(tenant!, store, cancellationToken);
        var data = await store.FindAsync(record.Id, cancellationToken);
        if (data is null || !await InScopeAsync(tenant!, store!, data, cancellationToken))
        {
            // Kapsam dışı kayıt, var olmayan kayıtla aynı cevabı alır; varlığı belli edilmez.
            return NotFound<RecordData>();
        }

        return RecordResult<RecordData>.Ok(Present(data, hidden, custom));
    }

    public async Task<RecordResult<RecordPage>> QueryAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        var (gate, tenant, store) = await AuthorizeAsync<RecordPage>(query.EntityKey, RecordOperation.Read, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var search = query.Search?.Trim();
        var normalized = query with
        {
            // Kapsam sorguda uygulanır; sayfalama ve toplam sayı doğru kalır.
            Owners = store!.OwnerField is null ? null : await dataScope.GetOwnersAsync(tenant!, cancellationToken),
            Page = Math.Max(1, query.Page),
            PageSize = Math.Clamp(query.PageSize, 1, RecordQuery.MaxPageSize),
            Search = string.IsNullOrEmpty(search) ? null : search[..Math.Min(search.Length, RecordQuery.MaxSearchLength)],
        };

        if (normalized.Filters is { Count: > RecordQuery.MaxFilters })
        {
            return Invalid<RecordPage>([new RecordFieldError("filters", $"En fazla {RecordQuery.MaxFilters} filtre verilebilir.")]);
        }

        // Hassas alana göre süzmek değerini sezdirir (ör. "tutar > 1 milyon"); göremeyen için bilinmeyen filtre gibidir.
        var hidden = await fieldAccess.GetHiddenFieldsAsync(tenant!, store.EntityKey, cancellationToken);
        if (normalized.Filters?.Keys.FirstOrDefault(hidden.Contains) is { } hiddenFilter)
        {
            return Invalid<RecordPage>([new RecordFieldError(hiddenFilter, "Bu alana göre süzme yetkiniz yok.")]);
        }

        var custom = await CustomFieldsAsync(tenant!, store, cancellationToken);
        if (store.SupportsCustomFields)
        {
            var (filters, customFilters, filterErrors) = RecordCustomFields.SplitFilters(custom, normalized.Filters);
            if (filterErrors.Count > 0)
            {
                return Invalid<RecordPage>(filterErrors);
            }

            normalized = normalized with { Filters = filters, CustomFilters = customFilters };
        }

        RecordPage page;
        try
        {
            page = await store.QueryAsync(normalized, cancellationToken);
        }
        catch (RecordValidationException invalid)
        {
            return Invalid<RecordPage>(invalid.Errors);
        }

        return RecordResult<RecordPage>.Ok(page with { Items = [.. page.Items.Select(i => Present(i, hidden, custom))] });
    }

    public async Task<RecordResult<RecordData>> CreateAsync(RecordCommand command, CancellationToken cancellationToken)
    {
        var (gate, tenant, store) = await AuthorizeAsync<RecordData>(command.EntityKey, RecordOperation.Create, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var hidden = await fieldAccess.GetHiddenFieldsAsync(tenant!, store!.EntityKey, cancellationToken);
        if (WithoutHidden(command.Values, hidden) is not { } values)
        {
            return HiddenFieldDenied<RecordData>();
        }

        var custom = await CustomFieldsAsync(tenant!, store, cancellationToken);
        var errors = new List<RecordFieldError>();
        if (store.SupportsCustomFields)
        {
            (values, errors) = RecordCustomFields.Prepare(custom, RecordOperation.Create, values, before: null);
        }

        errors.AddRange(store.Validate(RecordOperation.Create, values));
        if (errors.Count > 0)
        {
            return Invalid<RecordData>(errors);
        }

        try
        {
            var created = await store.CreateAsync(values, cancellationToken);
            if (!await InScopeAsync(tenant!, store, created, cancellationToken))
            {
                return OwnerOutOfScope<RecordData>(store);
            }

            Record(tenant!, command.Source, created.EntityKey, created.Id, AuditAction.Created, DomainEventTypes.RecordCreated, before: null, after: created.Values, created.Version, command.Reason);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return RecordResult<RecordData>.Ok(Present(created, hidden, custom));
        }
        catch (RecordValidationException invalid)
        {
            return Invalid<RecordData>(invalid.Errors);
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
        if (before is null || !await InScopeAsync(tenant!, store, before, cancellationToken))
        {
            return NotFound<RecordData>();
        }

        // Gizli alan güncellemeye girmez: kayıttaki değeri korunur (ekran onu boş göndermiş olabilir).
        var hidden = await fieldAccess.GetHiddenFieldsAsync(tenant!, store.EntityKey, cancellationToken);
        if (WithoutHidden(command.Values, hidden) is not { } values)
        {
            return HiddenFieldDenied<RecordData>();
        }

        var custom = await CustomFieldsAsync(tenant!, store, cancellationToken);
        var errors = new List<RecordFieldError>();
        if (store.SupportsCustomFields)
        {
            (values, errors) = RecordCustomFields.Prepare(custom, RecordOperation.Update, values, before.Values);
        }

        errors.AddRange(store.Validate(RecordOperation.Update, values));
        if (errors.Count > 0)
        {
            return Invalid<RecordData>(errors);
        }

        try
        {
            var updated = await store.UpdateAsync(id, values, command.ExpectedVersion, cancellationToken);
            if (updated is null)
            {
                return NotFound<RecordData>();
            }

            if (!await InScopeAsync(tenant!, store, updated, cancellationToken))
            {
                return OwnerOutOfScope<RecordData>(store);
            }

            Record(tenant!, command.Source, updated.EntityKey, updated.Id, AuditAction.Updated, DomainEventTypes.RecordUpdated, before.Values, updated.Values, updated.Version, command.Reason);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return RecordResult<RecordData>.Ok(Present(updated, hidden, custom));
        }
        catch (RecordValidationException invalid)
        {
            return Invalid<RecordData>(invalid.Errors);
        }
        catch (RecordConflictException conflict)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, conflict.Message);
        }
    }

    public async Task<RecordResult<bool>> DeleteAsync(
        RecordRef record, long? expectedVersion, CancellationToken cancellationToken, ExecutionSource source = ExecutionSource.Api)
    {
        var (gate, tenant, store) = await AuthorizeAsync<bool>(record.EntityKey, RecordOperation.Delete, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var before = await store!.FindAsync(record.Id, cancellationToken);
        if (before is null || !await InScopeAsync(tenant!, store, before, cancellationToken))
        {
            return NotFound<bool>();
        }

        try
        {
            if (!await store.DeleteAsync(record.Id, expectedVersion, cancellationToken))
            {
                return NotFound<bool>();
            }

            Record(tenant!, source, before.EntityKey, before.Id, AuditAction.Deleted, DomainEventTypes.RecordDeleted, before.Values, after: null, before.Version + 1, reason: null);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return RecordResult<bool>.Ok(true);
        }
        catch (RecordConflictException conflict)
        {
            return RecordResult<bool>.Fail(RecordErrorCode.Conflict, conflict.Message);
        }
    }

    /// <summary>
    /// Adım 4, veri kapsamı: kayıt türünün sorumlu alanı varsa sorumlu kişinin kapsamında olmalıdır. Sorumlusu boş kayıt
    /// yalnızca "tüm kayıtlar" kapsamında görünür. Kapsam istek başına bir kez hesaplanır (servis saklar).
    /// </summary>
    private async Task<bool> InScopeAsync(TenantContext tenant, IRecordStore store, RecordData record, CancellationToken cancellationToken)
    {
        if (store.OwnerField is not { } field || await dataScope.GetOwnersAsync(tenant, cancellationToken) is not { } owners)
        {
            return true;
        }

        return record.Values.TryGetPropertyValue(field, out var node)
            && node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && Guid.TryParse(text, out var owner)
            && owners.Contains(owner);
    }

    // Özel alanı olan türde etkin alan tanımları; diğerlerinde boş (veritabanına gidilmez).
    private async Task<IReadOnlyList<Customization.CustomFieldInfo>> CustomFieldsAsync(
        TenantContext tenant, IRecordStore store, CancellationToken cancellationToken) =>
        store.SupportsCustomFields ? await customFields.GetFieldsAsync(tenant, store.EntityKey, cancellationToken) : [];

    private static RecordData Present(RecordData data, IReadOnlySet<string> hidden, IReadOnlyList<Customization.CustomFieldInfo> custom) =>
        Strip(data.Values.ContainsKey(RecordFields.Custom) ? RecordCustomFields.Present(data, custom) : data, hidden);

    // Kayıt, gizli alanları çıkarılmış haliyle verilir (kopya; depo verisi değişmez).
    private static RecordData Strip(RecordData data, IReadOnlySet<string> hidden)
    {
        if (hidden.Count == 0)
        {
            return data;
        }

        var values = data.Values.DeepClone().AsObject();
        foreach (var field in hidden)
        {
            values.Remove(field);
        }

        return data with { Values = values };
    }

    // Gizli alanlar çıkarılmış değerler; gizli alana boş olmayan değer verilmişse null (yazma reddedilir).
    private static JsonObject? WithoutHidden(JsonObject values, IReadOnlySet<string> hidden)
    {
        if (hidden.Count == 0 || !values.Any(p => hidden.Contains(p.Key)))
        {
            return values;
        }

        var kept = new JsonObject();
        foreach (var (name, node) in values)
        {
            if (!hidden.Contains(name))
            {
                kept[name] = node?.DeepClone();
            }
            else if (node is not null)
            {
                return null;
            }
        }

        return kept;
    }

    private static RecordResult<T> HiddenFieldDenied<T>() =>
        RecordResult<T>.Fail(RecordErrorCode.PermissionDenied, "Görme yetkiniz olmayan bir alanı değiştiremezsiniz.");

    // Oluşturulan ya da güncellenen kaydın sorumlusu kapsam dışında: kayıt yazılmaz.
    private static RecordResult<T> OwnerOutOfScope<T>(IRecordStore store) =>
        Invalid<T>([new RecordFieldError(store.OwnerField!, "Bu kişiyi sorumlu atayamazsınız: kayıt kapsamınızın dışında.")]);

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

    /// <summary>
    /// Adım 7-8: audit ve olay; kayıtla birlikte yazılmak üzere hazırlanır. Abonenin değişikliğinde kaynak arka plan
    /// işidir, correlation tetikleyen olaydan gelir.
    /// </summary>
    private void Record(
        TenantContext tenant,
        ExecutionSource source,
        string entityKey,
        Guid recordId,
        AuditAction action,
        string eventType,
        JsonObject? before,
        JsonObject? after,
        long sequence,
        string? reason)
    {
        var cause = handling?.Event;
        if (cause is not null)
        {
            source = ExecutionSource.BackgroundJob;
        }

        var correlation = cause?.CorrelationId ?? Activity.Current?.TraceId.ToString();
        var changes = RecordDiff.Compute(RecordCustomFields.Flatten(before), RecordCustomFields.Flatten(after));
        audit.Add(new AuditEntry(tenant.TenantId, tenant.UserId, entityKey, recordId, action, changes, source, correlation)
        {
            Subscription = handling?.Subscription,
        });
        outbox.Add(new DomainEvent(tenant.TenantId, eventType, entityKey, recordId, changes.DeepClone().AsObject())
        {
            Sequence = sequence,
            CompanyId = storeCompanyId(after ?? before) ?? cause?.CompanyId,
            UserId = tenant.UserId,
            Source = source,
            CorrelationId = correlation,
            CausationId = cause?.Id,
            Depth = cause is null ? 0 : cause.Depth + 1,
            Reason = reason,
        });
    }

    private static Guid? storeCompanyId(JsonObject? values) =>
        values is not null && values.TryGetPropertyValue("companyId", out var node)
        && node is JsonValue value && value.TryGetValue<string>(out var text)
        && Guid.TryParse(text, out var id) ? id : null;

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
