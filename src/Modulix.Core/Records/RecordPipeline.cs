using System.Diagnostics;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
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
    ICompanyContextAccessor companyAccessor,
    IRecordStoreRegistry stores,
    IEntitlementService entitlements,
    IPermissionService permissions,
    IDataScopeService dataScope,
    IFieldAccessService fieldAccess,
    ICustomFieldProvider customFields,
    IAuditWriter audit,
    IDomainEventOutbox outbox,
    IUnitOfWork unitOfWork,
    EventHandlingScope? handling = null,
    ISharedReferenceMerger? references = null,
    RecordCommandContext? commandContext = null,
    IFormLayoutProvider? formLayouts = null,
    FieldRules.IFieldRuleGate? fieldRules = null) : IRecordPipeline
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
            Owners = !store!.UsesOwnerScope ? null : await dataScope.GetOwnersAsync(tenant!, store.ScopeEntityKey, cancellationToken),
            // Şirket kapsamı (ADR-0008): şirket kapsamlı depoda yalnızca erişilebilir şirketlerin kayıtları.
            Companies = store.CompanyField is null ? null : CompanyScope(),
            Page = Math.Max(1, query.Page),
            PageSize = Math.Clamp(query.PageSize, 1, query.Bulk ? RecordQuery.MaxBulkPageSize : RecordQuery.MaxPageSize),
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

        // Sıralama da değeri sezdirir (sıra, tutarı ele verir); sıralama anahtarı ("-alan" ya da "alan") gizli alansa reddedilir.
        if (normalized.Filters is not null && normalized.Filters.TryGetValue("sort", out var sortKey) && hidden.Contains(sortKey.TrimStart('-')))
        {
            return Invalid<RecordPage>([new RecordFieldError("sort", "Bu alana göre sıralama yetkiniz yok.")]);
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

        // Firmanın form düzeniyle zorunlu kıldığı alanlar (ADR-0021); kodun zorunluları depoda doğrulanır.
        errors.AddRange(RecordRequiredFields.Check(await ExtraRequiredAsync(tenant!, store, cancellationToken), RecordOperation.Create, values));

        // Şirket kapsamı (ADR-0008): şirket kapsamlı kayıt oluşturulurken CompanyId verilmemişse aktif şirkete düşer; verilen
        // veya düşen değer erişilebilir olmalıdır. Değeri store doğrulamadan önce yerine koyarız.
        if (store.CompanyField is { } companyField && EnsureWritableCompany(values, companyField) is { } companyError)
        {
            errors.Add(companyError);
        }

        errors.AddRange(store.Validate(RecordOperation.Create, values));

        // Alan kuralları (ADR-0029 K7): yazılacak son hale karşı; geçersiz veride değerlendirilmez.
        if (errors.Count == 0 && fieldRules is not null)
        {
            errors.AddRange(await fieldRules.CheckAsync(store.EntityKey, null, null, values, cancellationToken));
        }

        if (errors.Count > 0)
        {
            return Invalid<RecordData>(errors);
        }

        using var reason = commandContext?.Enter(command.Reason);
        try
        {
            var created = await store.CreateAsync(values, cancellationToken);
            if (!await InScopeAsync(tenant!, store, created, cancellationToken))
            {
                return OwnerOutOfScope<RecordData>(store);
            }

            Record(tenant!, command.Source, created.EntityKey, created.Id, AuditAction.Created, DomainEventTypes.RecordCreated, before: null, after: created.Values, created.Version, command.Reason, CompanyIdOf(store, created.Values));
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

        errors.AddRange(RecordRequiredFields.Check(await ExtraRequiredAsync(tenant!, store, cancellationToken), RecordOperation.Update, values));

        // Şirket kapsamlı kaydın şirketi güncellemeyle değiştirilemez (ADR-0008): taşıma ayrı bir komuttur.
        if (store.CompanyField is { } companyField && ChangesCompany(before.Values, values, companyField))
        {
            errors.Add(new RecordFieldError(companyField, "Kaydın şirketi değiştirilemez; taşıma ayrı bir işlemdir."));
        }

        errors.AddRange(store.Validate(RecordOperation.Update, values));

        // Alan kuralları (ADR-0029 K7): kaydın mevcut değerleriyle birleşmiş son hale karşı.
        if (errors.Count == 0 && fieldRules is not null)
        {
            var after = before.Values.DeepClone().AsObject();
            foreach (var (name, node) in values)
            {
                after[name] = node?.DeepClone();
            }

            errors.AddRange(await fieldRules.CheckAsync(store.EntityKey, id, before.Values, after, cancellationToken));
        }

        if (errors.Count > 0)
        {
            return Invalid<RecordData>(errors);
        }

        using var reason = commandContext?.Enter(command.Reason);
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

            Record(tenant!, command.Source, updated.EntityKey, updated.Id, AuditAction.Updated, DomainEventTypes.RecordUpdated, before.Values, updated.Values, updated.Version, command.Reason, CompanyIdOf(store, updated.Values));
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

            Record(tenant!, source, before.EntityKey, before.Id, AuditAction.Deleted, DomainEventTypes.RecordDeleted, before.Values, after: null, before.Version + 1, reason: null, CompanyIdOf(store, before.Values));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return RecordResult<bool>.Ok(true);
        }
        catch (RecordConflictException conflict)
        {
            return RecordResult<bool>.Fail(RecordErrorCode.Conflict, conflict.Message);
        }
    }

    public async Task<RecordResult<MergePreview>> PreviewMergeAsync(string entityKey, MergeRequest request, CancellationToken cancellationToken)
    {
        var prepared = await PrepareMergeAsync<MergePreview>(entityKey, request, cancellationToken);
        if (prepared.Gate is not null)
        {
            return prepared.Gate;
        }

        try
        {
            var (tenant, store, mergeable) = (prepared.Tenant!, prepared.Store!, prepared.Mergeable!);
            var preview = await mergeable.PreviewMergeAsync(request, cancellationToken);
            if (preview is null)
            {
                return NotFound<MergePreview>();
            }

            var plan = await PlanReferencesAsync(store, mergeable, request.SourceId, cancellationToken);
            return RecordResult<MergePreview>.Ok(preview with
            {
                Impacts = [.. preview.Impacts, .. plan.Impacts],
                Blockers = [.. preview.Blockers, .. plan.Blockers],
            });
        }
        catch (RecordConflictException conflict)
        {
            return RecordResult<MergePreview>.Fail(RecordErrorCode.Conflict, conflict.Message);
        }
    }

    public async Task<RecordResult<MergeResult>> MergeAsync(
        string entityKey, MergeRequest request, CancellationToken cancellationToken, ExecutionSource source = ExecutionSource.Api)
    {
        var prepared = await PrepareMergeAsync<MergeResult>(entityKey, request, cancellationToken);
        if (prepared.Gate is not null)
        {
            return prepared.Gate;
        }

        var (tenant, store, mergeable) = (prepared.Tenant!, prepared.Store!, prepared.Mergeable!);
        try
        {
            // Çözülemeyen bağlılık varsa hiçbir şey hazırlanmaz (kısmi birleştirme olmaz).
            var plan = await PlanReferencesAsync(store, mergeable, request.SourceId, cancellationToken);
            if (plan.Blockers.Count > 0)
            {
                return Invalid<MergeResult>([.. plan.Blockers.Select(b => new RecordFieldError("merge", b))]);
            }

            // Depo kendi engellerini RecordValidationException ile bildirir (önizleme ve birleştirme aynı yoldan geçer).
            var outcome = await mergeable.MergeAsync(request, cancellationToken);
            if (outcome is null)
            {
                return NotFound<MergeResult>();
            }

            if (references is not null && mergeable.SharedEntityKey is { } sharedKey
                && await mergeable.SharedIdOfAsync(request.SourceId, cancellationToken) is { } sourceShared
                && await mergeable.SharedIdOfAsync(request.TargetId, cancellationToken) is { } targetShared)
            {
                await references.ApplyAsync(sharedKey, OwnerPrefix(store), sourceShared, targetShared, cancellationToken);
            }

            var before = outcome.TargetBefore.Values;
            var after = outcome.TargetAfter.Values.DeepClone().AsObject();
            after["mergedFrom"] = request.SourceId.ToString();
            var gone = new JsonObject { ["mergedInto"] = request.TargetId.ToString() };
            Record(tenant, source, entityKey, request.TargetId, AuditAction.Merged, DomainEventTypes.RecordUpdated,
                before, after, outcome.TargetAfter.Version, reason: null, CompanyIdOf(store, outcome.TargetAfter.Values));
            Record(tenant, source, entityKey, request.SourceId, AuditAction.Merged, DomainEventTypes.RecordMerged,
                outcome.SourceBefore.Values, gone, outcome.SourceBefore.Version + 1, reason: null, CompanyIdOf(store, outcome.SourceBefore.Values));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return RecordResult<MergeResult>.Ok(new MergeResult(outcome.TargetAfter, request.SourceId, [.. outcome.Impacts, .. plan.Impacts]));
        }
        catch (RecordValidationException invalid)
        {
            return Invalid<MergeResult>(invalid.Errors);
        }
        catch (RecordConflictException conflict)
        {
            return RecordResult<MergeResult>.Fail(RecordErrorCode.Conflict, conflict.Message);
        }
    }

    public async Task<RecordResult<MergeLink>> ResolveMergedAsync(RecordRef record, CancellationToken cancellationToken)
    {
        var (gate, tenant, store) = await AuthorizeAsync<MergeLink>(record.EntityKey, RecordOperation.Read, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        if (store is not IMergeableRecordStore mergeable || await mergeable.FindMergeLinkAsync(record.Id, cancellationToken) is not { } link)
        {
            return NotFound<MergeLink>();
        }

        // Hedefi göremeyen, eski kimliğin nereye gittiğini de öğrenmez.
        var target = await store.FindAsync(link.TargetId, cancellationToken);
        return target is not null && await InScopeAsync(tenant!, store, target, cancellationToken)
            ? RecordResult<MergeLink>.Ok(link)
            : NotFound<MergeLink>();
    }

    public async Task<RecordResult<T>> RunAtomicAsync<T>(Func<CancellationToken, Task<RecordResult<T>>> work, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var result = await work(cancellationToken);
        if (transaction is not null && result.IsSuccess)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        // Başarısız sonuçta (ya da istisnada) işlem commit edilmeden atılır: tüm yazımlar geri alınır.
        return result;
    }

    private sealed record PreparedMerge<T>(RecordResult<T>? Gate, TenantContext? Tenant, IRecordStore? Store, IMergeableRecordStore? Mergeable);

    /// <summary>
    /// Önizleme ve birleştirme ortak adımları: tenant, lisans, birleştirme izni, kaynak ve hedefin okunabilir ve kapsamda
    /// olması, gizli alan yokluğu. Kaynak daha önce birleştirilmişse (tekrarlanan istek) çakışma olarak hedefi bildirir.
    /// </summary>
    private async Task<PreparedMerge<T>> PrepareMergeAsync<T>(string entityKey, MergeRequest request, CancellationToken cancellationToken)
    {
        var (gate, tenant, store) = await AuthorizeAsync<T>(entityKey, RecordOperation.Merge, cancellationToken);
        if (gate is not null)
        {
            return new(gate, null, null, null);
        }

        if (store is not IMergeableRecordStore mergeable)
        {
            return new(Invalid<T>([new RecordFieldError("entityKey", "Bu kayıt türü birleştirilemez.")]), null, null, null);
        }

        if (request.SourceId == request.TargetId || request.SourceId == Guid.Empty || request.TargetId == Guid.Empty)
        {
            return new(Invalid<T>([new RecordFieldError("targetId", "Kayıt kendisiyle birleştirilemez.")]), null, null, null);
        }

        var target = await store.FindAsync(request.TargetId, cancellationToken);
        var source = await store.FindAsync(request.SourceId, cancellationToken);
        var targetVisible = target is not null && await InScopeAsync(tenant!, store, target, cancellationToken);
        if (source is null && targetVisible && await mergeable.FindMergeLinkAsync(request.SourceId, cancellationToken) is { } link)
        {
            // Tekrarlanan istek: kaynak zaten kaldırıldı. Sessizce başarı dönmeyiz; hedefin güncel sürümüyle yeniden denenmeli.
            return new(RecordResult<T>.Fail(RecordErrorCode.Conflict,
                $"Bu kayıt zaten birleştirilmiş (hedef: {link.TargetId}, {link.MergedAt:O})."), null, null, null);
        }

        if (source is null || !targetVisible || !await InScopeAsync(tenant!, store, source, cancellationToken))
        {
            return new(NotFound<T>(), null, null, null);
        }

        if ((await fieldAccess.GetHiddenFieldsAsync(tenant!, store.EntityKey, cancellationToken)).Count > 0)
        {
            return new(RecordResult<T>.Fail(RecordErrorCode.PermissionDenied,
                "Görme yetkiniz olmayan alanları olan kayıtları birleştiremezsiniz."), null, null, null);
        }

        return new(null, tenant, store, mergeable);
    }

    // Modülün kendi başvuruları deposunca taşınır; ortak kayda başka modüllerden gelenler burada planlanır.
    private async Task<SharedReferencePlan> PlanReferencesAsync(
        IRecordStore store, IMergeableRecordStore mergeable, Guid sourceId, CancellationToken cancellationToken)
    {
        if (references is null || mergeable.SharedEntityKey is not { } sharedKey
            || await mergeable.SharedIdOfAsync(sourceId, cancellationToken) is not { } sharedId)
        {
            return SharedReferencePlan.None;
        }

        return await references.PlanAsync(sharedKey, OwnerPrefix(store), sharedId, cancellationToken);
    }

    private static string OwnerPrefix(IRecordStore store) => store.EntityKey[..(store.EntityKey.IndexOf('.') + 1)];

    /// <summary>
    /// Adım 4, veri kapsamı: kayıt türünün sorumlu alanı varsa sorumlu kişinin kapsamında olmalıdır. Sorumlusu boş kayıt
    /// yalnızca "tüm kayıtlar" kapsamında görünür. Kapsam istek başına bir kez hesaplanır (servis saklar).
    /// </summary>
    private async Task<bool> InScopeAsync(TenantContext tenant, IRecordStore store, RecordData record, CancellationToken cancellationToken)
    {
        // Şirket kapsamı (ADR-0008): şirket kapsamlı kayıt yalnızca erişilebilir şirketlerde görünür.
        if (!InCompanyScope(store, record))
        {
            return false;
        }

        if (!store.UsesOwnerScope || await dataScope.GetOwnersAsync(tenant, store.ScopeEntityKey, cancellationToken) is not { } owners)
        {
            return true;
        }

        return await store.IsInOwnerScopeAsync(record, owners, cancellationToken);
    }

    // Özel alanı olan türde etkin alan tanımları; diğerlerinde boş (veritabanına gidilmez).
    private static readonly IReadOnlySet<string> NoRequired = new HashSet<string>();

    private async Task<IReadOnlySet<string>> ExtraRequiredAsync(TenantContext tenant, IRecordStore store, CancellationToken cancellationToken) =>
        formLayouts is null ? NoRequired : await formLayouts.GetRequiredFieldsAsync(tenant, store.EntityKey, cancellationToken);

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
    // Kapsamı üst kayıttan türeyen alt kayıt türlerinde (sorumlu alanı yok) hata genel sorumlu alan adıyla verilir.
    private static RecordResult<T> OwnerOutOfScope<T>(IRecordStore store) =>
        Invalid<T>([new RecordFieldError(store.OwnerField ?? OwnedRecordType.OwnerField, "Bu kişiyi sorumlu atayamazsınız: kayıt kapsamınızın dışında.")]);

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
        string? reason,
        Guid? companyId)
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
            // Yalnızca şirket kapsamlı kaydın işletici şirketi; ortak kayıtta boş. Abonenin ürettiği olayda tetikleyen
            // olayın şirketine düşer (neden zinciri). CRM "companyId" (müşteri firma) gibi alanlar buraya sızmaz.
            CompanyId = companyId ?? cause?.CompanyId,
            UserId = tenant.UserId,
            Source = source,
            CorrelationId = correlation,
            CausationId = cause?.Id,
            Depth = cause is null ? 0 : cause.Depth + 1,
            Reason = reason,
        });
    }

    /// <summary>
    /// Kaydın işletici şirketi bu oturumda erişilebilir mi (ADR-0008). Ortak depoda (CompanyField null) ya da süzgeç
    /// yokken (yönetici/arka plan) her zaman doğru. Değeri olmayan şirket kapsamlı kayıt kapsam dışı sayılır.
    /// </summary>
    private bool InCompanyScope(IRecordStore store, RecordData record) =>
        store.CompanyField is not { } field
        || CompanyScope() is not { } companies
        || (TryCompanyId(record.Values, field, out var id) && companies.Contains(id));

    /// <summary>
    /// Şirket okuma kapsamı (ADR-0008): erişilebilir şirketler. Null = süzgeç yok — bütün şirketlere erişen yönetici ya da
    /// şirket bağlamı olmayan arka plan/sistem (tenant geneli). Boş küme = hiçbir şirkete erişim yok.
    /// </summary>
    private IReadOnlySet<Guid>? CompanyScope()
    {
        var company = companyAccessor.Current;
        return company is null || company.IsAllCompanies ? null : company.AccessibleCompanyIds;
    }

    /// <summary>
    /// Şirket kapsamlı kayıt oluşturulurken şirketini belirler ve denetler (ADR-0008): değer verilmemişse aktif şirkete
    /// düşer; verilen veya düşen değer erişilebilir olmalıdır. Şirket bağlamı yoksa (arka plan/sistem) kapsam uygulanmaz.
    /// Hata varsa alan hatası, yoksa <c>null</c>.
    /// </summary>
    private RecordFieldError? EnsureWritableCompany(JsonObject values, string field)
    {
        if (companyAccessor.Current is not { } company)
        {
            return null;
        }

        if (!TryCompanyId(values, field, out var requested))
        {
            if (company.ActiveCompanyId is not { } active)
            {
                return new RecordFieldError(field, "Kayıt oluşturmak için erişebileceğiniz bir şirket seçili olmalı.");
            }

            values[field] = active.ToString();
            return null;
        }

        return company.CanAccess(requested) ? null : new RecordFieldError(field, "Bu şirkete erişiminiz yok.");
    }

    private static bool ChangesCompany(JsonObject before, JsonObject values, string field)
    {
        // Alan güncellemede yoksa mevcut değer korunur; değişiklik sayılmaz.
        if (!values.TryGetPropertyValue(field, out var node))
        {
            return false;
        }

        return !JsonNode.DeepEquals(node, before.TryGetPropertyValue(field, out var current) ? current : null);
    }

    private static bool TryCompanyId(JsonObject values, string field, out Guid id)
    {
        id = Guid.Empty;
        return values.TryGetPropertyValue(field, out var node)
            && node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && Guid.TryParse(text, out id);
    }

    // Olay zarfının işletici şirketi: yalnızca şirket kapsamlı depoda, CompanyField değerinden (ADR-0008). Ortak depoda null.
    private static Guid? CompanyIdOf(IRecordStore store, JsonObject values) =>
        store.CompanyField is { } field && TryCompanyId(values, field, out var id) ? id : null;

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
