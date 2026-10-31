using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Contracts.Domain;

namespace Modulix.Modules.Contracts.Records;

/// <summary>
/// Sözleşme kayıt depolarının ortak kalıbı (CRM'deki <c>CrmRecordStore</c> ile aynı sözleşme). Alt sınıf yalnızca anahtarı, izinleri,
/// alan listesini, filtreleri, sıralamayı ve aramayı verir. Kaydetmez: boru hattı kaydı, audit'i ve olayı tek seferde yazar.
/// Tenant filtresi ve RLS <see cref="IModuleData"/> üzerinden uygulanır.
/// </summary>
internal abstract class ContractsRecordStore<TEntity>(ContractsStoreContext context) : IRecordStore
    where TEntity : ContractsEntity, new()
{
    protected const string OwnerField = OwnedRecordType.OwnerField;

    private static readonly IReadOnlyDictionary<string, RecordFilter<TEntity>> NoFilters = new Dictionary<string, RecordFilter<TEntity>>();

    private Dictionary<string, RecordField<TEntity>>? fieldsByName;

    public abstract string EntityKey { get; }

    /// <summary>Lisans kontrolü modül düzeyindedir: modül tenant için açıksa tüm kayıtları kullanılabilir.</summary>
    public string FeatureKey => EntityKey;

    protected abstract string ReadPermission { get; }

    protected abstract string WritePermission { get; }

    protected abstract IReadOnlyList<RecordField<TEntity>> Fields { get; }

    protected virtual IReadOnlyDictionary<string, RecordFilter<TEntity>> Filters => NoFilters;

    protected ContractsStoreContext Context => context;

    private IModuleData Data => context.Data;

    protected RecordField<TEntity> FieldNamed(string name) => FieldsByName[name];

    private Dictionary<string, RecordField<TEntity>> FieldsByName => fieldsByName ??= Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

    public string? PermissionFor(RecordOperation operation) => operation == RecordOperation.Read ? ReadPermission : WritePermission;

    /// <summary>Kapı açıkken güncelleme eylemin izniyle yapılır (ör. onaylayan yazma iznine sahip olmak zorunda değildir).</summary>
    public IReadOnlyList<string>? AnyPermissionFor(RecordOperation operation) =>
        operation == RecordOperation.Update && context.Gate.Permissions is { } permissions ? permissions : null;

    string? IRecordStore.OwnerField => IsOwned ? OwnerField : null;

    bool IRecordStore.UsesOwnerScope => UsesScope;

    /// <summary>Veri kapsamı uygulanır mı? Varsayılan: kayıt sorumlu alanı taşıyorsa; üst kayda bağlı kapsamı olan depo true yapar.</summary>
    protected virtual bool UsesScope => IsOwned;

    Task<bool> IRecordStore.IsInOwnerScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        IsInScopeAsync(record, owners, cancellationToken);

    protected virtual Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        Task.FromResult(record.Values.TryGetPropertyValue(OwnerField, out var node)
            && node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && Guid.TryParse(text, out var owner)
            && owners.Contains(owner));

    /// <summary>Listeye veri kapsamını uygular (sorguda; sayfalama ve toplam doğru kalır).</summary>
    protected virtual IQueryable<TEntity> ScopeToOwners(IQueryable<TEntity> source, IReadOnlySet<Guid> owners) => source.Where(OwnedBy(owners));

    private static bool IsOwned => typeof(IOwnedEntity).IsAssignableFrom(typeof(TEntity));

    /// <summary>Tüm sözleşme kayıtlarına özel alan eklenebilir (Y9a).</summary>
    public bool SupportsCustomFields => true;

    public virtual IReadOnlyList<RecordFieldError> Validate(RecordOperation operation, JsonObject values)
    {
        var errors = new List<RecordFieldError>();
        foreach (var (name, node) in values)
        {
            if (name == RecordFields.Custom)
            {
                continue;
            }

            if (!FieldsByName.TryGetValue(name, out var field))
            {
                errors.Add(new RecordFieldError(name, "Bilinmeyen alan."));
            }
            else if (!field.IsOmitted(node) && field.Parse(node).Error is { } error)
            {
                errors.Add(new RecordFieldError(name, error));
            }
        }

        if (operation == RecordOperation.Create)
        {
            errors.AddRange(Fields.Where(f => f.Required && !values.ContainsKey(f.Name)).Select(f => new RecordFieldError(f.Name, "Bu alan zorunludur.")));
        }

        return errors;
    }

    public async Task<RecordData?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await Restrict(Data.Set<TEntity>().AsNoTracking()).SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        return entity is null ? null : ToData(entity);
    }

    public async Task<RecordPage> QueryAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        var source = ApplyFilters(Restrict(Data.Set<TEntity>().AsNoTracking()), query.Filters);
        if (query.Owners is { } owners && UsesScope)
        {
            source = ScopeToOwners(source, owners);
        }

        if (query.Search is { } term)
        {
            source = Search(source, term);
        }

        foreach (var (key, value) in query.CustomFilters ?? new Dictionary<string, string>())
        {
            var path = $"$.\"{key}\"";
            source = source.Where(e => JsonFunctions.Value(e.CustomFields, path) == value);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await Order(source).ThenBy(e => e.Id).Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(cancellationToken);
        return new RecordPage([.. items.Select(ToData)], query.Page, query.PageSize, total);
    }

    private static Expression<Func<TEntity, bool>> OwnedBy(IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var entity = Expression.Parameter(typeof(TEntity), "e");
        var owner = Expression.Property(entity, nameof(IOwnedEntity.OwnerId));
        var contains = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Guid?)], Expression.Constant(ids), owner);
        return Expression.Lambda<Func<TEntity, bool>>(contains, entity);
    }

    public async Task<RecordData> CreateAsync(JsonObject values, CancellationToken cancellationToken)
    {
        var entity = new TEntity();
        values = await PrepareNewAsync(entity, values, cancellationToken);
        await ApplyAsync(entity, values, isNew: true, cancellationToken);

        if (DefaultsOwnerToCurrentUser && FieldsByName.TryGetValue(OwnerField, out var owner) && owner.Read(entity) is null)
        {
            owner.Write(entity, context.CurrentUserId);
        }

        foreach (var field in Fields.Where(f => f.ChoiceListKey is not null && !f.Nullable && (!values.ContainsKey(f.Name) || f.IsOmitted(values[f.Name]))))
        {
            var list = await context.ChoiceLists.GetAsync(field.ChoiceListKey!, cancellationToken);
            field.Write(entity, list.DefaultValue?.Key);
        }

        await CreatedAsync(entity, cancellationToken);
        Data.Set<TEntity>().Add(entity);
        return ToData(entity);
    }

    public async Task<RecordData?> UpdateAsync(Guid id, JsonObject values, long? expectedVersion, CancellationToken cancellationToken)
    {
        var entity = await Restrict(Data.Set<TEntity>()).SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        EnsureVersion(entity, expectedVersion);
        var before = ToData(entity).Values;
        await ApplyAsync(entity, values, isNew: false, cancellationToken);
        if (!JsonNode.DeepEquals(before, ToData(entity).Values))
        {
            entity.Touch();
        }

        return ToData(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var entity = await Restrict(Data.Set<TEntity>()).SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        EnsureVersion(entity, expectedVersion);
        await BeforeDeleteAsync(entity, cancellationToken);
        Data.Set<TEntity>().Remove(entity);
        return true;
    }

    /// <summary>Varsayılanlar atandıktan sonra, kayıt eklenmeden hemen önce (ör. tarihçe satırı, toplam hesabı).</summary>
    protected virtual Task CreatedAsync(TEntity entity, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Sorumlusu verilmeyen yeni kayıt, kaydı oluşturana atanır.</summary>
    protected virtual bool DefaultsOwnerToCurrentUser => true;

    /// <summary>Kayıt silinmeden önce (alt kayıtları kaldırmak, silme kuralını denetlemek için).</summary>
    protected virtual Task BeforeDeleteAsync(TEntity entity, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Yeni kaydı alanlar yazılmadan önce hazırlar. Veriye bağlı kural geçmezse <see cref="RecordValidationException"/>.</summary>
    protected virtual Task<JsonObject> PrepareNewAsync(TEntity entity, JsonObject values, CancellationToken cancellationToken) => Task.FromResult(values);

    /// <summary>Alanlar yazıldıktan sonra (çapraz alan kuralları, hesaplar); oluşturma ve güncellemede çağrılır.</summary>
    protected virtual Task AppliedAsync(TEntity entity, JsonObject values, bool isNew, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Kapsamdan bağımsız, her okumada/yazmada uygulanan kısıt (ör. üst kaydın görünürlüğü).</summary>
    protected virtual IQueryable<TEntity> Restrict(IQueryable<TEntity> source) => source;

    /// <summary>Liste sırası. Eşit kayıtlar kimliğe göre sıralanır; sayfalar kararlı kalır.</summary>
    protected abstract IOrderedQueryable<TEntity> Order(IQueryable<TEntity> source);

    protected abstract IQueryable<TEntity> Search(IQueryable<TEntity> source, string term);

    private IQueryable<TEntity> ApplyFilters(IQueryable<TEntity> source, IReadOnlyDictionary<string, string>? filters)
    {
        if (filters is null)
        {
            return source;
        }

        var errors = new List<RecordFieldError>();
        foreach (var (name, value) in filters)
        {
            if (!Filters.TryGetValue(name, out var filter))
            {
                errors.Add(new RecordFieldError(name, "Bilinmeyen filtre."));
            }
            else if (filter(source, value, context) is { } filtered)
            {
                source = filtered;
            }
            else
            {
                errors.Add(new RecordFieldError(name, "Geçersiz filtre değeri."));
            }
        }

        return errors.Count > 0 ? throw new RecordValidationException(errors) : source;
    }

    protected RecordData ToData(TEntity entity)
    {
        var values = new JsonObject();
        foreach (var field in Fields)
        {
            values[field.Name] = field.Read(entity);
        }

        values[RecordFields.Custom] = entity.CustomFields is null ? new JsonObject() : JsonNode.Parse(entity.CustomFields);
        return new RecordData(EntityKey, entity.Id, entity.Version, values);
    }

    /// <summary>Yalnızca verilen alanları yazar (kısmi güncelleme). Değerler <see cref="Validate"/>'ten geçmiştir.</summary>
    private async Task ApplyAsync(TEntity entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        foreach (var (name, node) in values)
        {
            if (name == RecordFields.Custom)
            {
                entity.CustomFields = node is JsonObject { Count: > 0 } custom ? custom.ToJsonString() : null;
                continue;
            }

            var field = FieldsByName[name];
            if (field.IsOmitted(node))
            {
                continue;
            }

            // Durum gibi alanlar yalnızca iş akışı kapısı açıkken yazılır; genel uçlardan gelen değer yok sayılır.
            if (field.SystemOnly && !context.Gate.IsOpen)
            {
                continue;
            }

            var value = field.Parse(node).Value;
            if (field.CreateOnly && !isNew)
            {
                if (value is not null && !JsonNode.DeepEquals(field.Read(entity), JsonValue.Create(value.ToString())))
                {
                    errors.Add(new RecordFieldError(name, "Oluşturulduktan sonra değiştirilemez."));
                }

                continue;
            }

            if (field.Exists is { } exists && value is Guid id && !await exists(context, id, cancellationToken))
            {
                // Başka tenant'ın kaydı da "bulunamadı" sayılır; varlığı belli edilmez.
                errors.Add(new RecordFieldError(name, field.NotFoundMessage ?? "Kayıt bulunamadı."));
                continue;
            }

            if (field.ChoiceListKey is { } listKey && value is string key)
            {
                var choice = (await context.ChoiceLists.GetAsync(listKey, cancellationToken)).Find(key);
                var unchanged = !isNew && choice is not null && field.Read(entity)?.GetValue<string>() == choice.Key;
                if (choice is null || (!choice.Active && !unchanged))
                {
                    errors.Add(new RecordFieldError(name, choice is null ? "Listede olmayan değer." : "Bu değer artık kullanılmıyor."));
                    continue;
                }

                value = choice.Key;
            }

            field.Write(entity, value);
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        await AppliedAsync(entity, values, isNew, cancellationToken);
    }

    private static void EnsureVersion(TEntity entity, long? expectedVersion)
    {
        if (expectedVersion is { } expected && expected != entity.Version)
        {
            throw new RecordConflictException(
                $"Kayıt siz düzenlerken değişti (beklenen sürüm {expected}, güncel sürüm {entity.Version}). Güncel halini alıp tekrar deneyin.");
        }
    }

    /// <summary>Kayıt devri (ADR-0004) dışındaki güncellemelerde sorumlu değişikliği sıradan alan yazımıdır; devirde neden işaretlidir.</summary>
    protected bool IsTransfer => context.CommandReason == EventReasons.Transfer;
}
