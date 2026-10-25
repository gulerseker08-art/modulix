using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// CRM kayıt depolarının ortak kalıbı; yeni bir sektör modülü de aynısını izler. Alt sınıf yalnızca
/// anahtarı, izinleri, alan listesini, filtreleri, sıralamayı ve aramayı verir. Kaydetmez: boru hattı kaydı,
/// audit'i ve olayı tek seferde yazar. Tenant filtresi ve RLS <see cref="IModuleData"/> üzerinden uygulanır.
/// </summary>
internal abstract class CrmRecordStore<TEntity>(CrmStoreContext context) : IRecordStore
    where TEntity : CrmEntity, new()
{
    /// <summary>Sorumlu alanının adı; oluştururken boş bırakılırsa isteği yapan kullanıcı atanır.</summary>
    protected const string OwnerField = OwnedRecordType.OwnerField;

    private static readonly IReadOnlyDictionary<string, RecordFilter<TEntity>> NoFilters = new Dictionary<string, RecordFilter<TEntity>>();

    private Dictionary<string, RecordField<TEntity>>? fieldsByName;

    public abstract string EntityKey { get; }

    /// <summary>Lisans kontrolü modül düzeyindedir: CRM tenant için açıksa tüm CRM kayıtları kullanılabilir.</summary>
    public string FeatureKey => EntityKey;

    protected abstract string ReadPermission { get; }

    protected abstract string WritePermission { get; }

    protected abstract IReadOnlyList<RecordField<TEntity>> Fields { get; }

    /// <summary>Listede kullanılabilen filtreler (sorgu parametresi adı → filtre).</summary>
    protected virtual IReadOnlyDictionary<string, RecordFilter<TEntity>> Filters => NoFilters;

    private IModuleData Data => context.Data;

    private Dictionary<string, RecordField<TEntity>> FieldsByName =>
        fieldsByName ??= Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

    public string? PermissionFor(RecordOperation operation) =>
        operation == RecordOperation.Read ? ReadPermission : WritePermission;

    /// <summary>Sorumlusu olan kayıtlarda veri kapsamı (Y8) sorumlu alanına göre uygulanır.</summary>
    string? IRecordStore.OwnerField => IsOwned ? OwnerField : null;

    private static bool IsOwned => typeof(IOwnedEntity).IsAssignableFrom(typeof(TEntity));

    /// <summary>Tüm CRM kayıtlarına özel alan eklenebilir (Y9a); değerler <see cref="CrmEntity.CustomFields"/>'ta saklanır.</summary>
    public bool SupportsCustomFields => true;

    public IReadOnlyList<RecordFieldError> Validate(RecordOperation operation, JsonObject values)
    {
        var errors = new List<RecordFieldError>();

        foreach (var (name, node) in values)
        {
            if (name == RecordFields.Custom)
            {
                // Boru hattı tanımlara göre doğrulamış ve birleştirmiştir.
                continue;
            }

            if (!FieldsByName.TryGetValue(name, out var field))
            {
                errors.Add(new RecordFieldError(name, "Bilinmeyen alan."));
            }
            else if (field.Parse(node).Error is { } error)
            {
                errors.Add(new RecordFieldError(name, error));
            }
        }

        if (operation == RecordOperation.Create)
        {
            errors.AddRange(Fields
                .Where(f => f.Required && !values.ContainsKey(f.Name))
                .Select(f => new RecordFieldError(f.Name, "Bu alan zorunludur.")));
        }

        return errors;
    }

    public async Task<RecordData?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await Data.Set<TEntity>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        return entity is null ? null : ToData(entity);
    }

    public async Task<RecordPage> QueryAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        var source = ApplyFilters(Data.Set<TEntity>().AsNoTracking(), query.Filters);
        if (query.Owners is { } owners && IsOwned)
        {
            source = source.Where(OwnedBy(owners));
        }
        if (query.Search is { } term)
        {
            source = Search(source, term);
        }

        foreach (var (key, value) in query.CustomFilters ?? new Dictionary<string, string>())
        {
            // Anahtar tanımdan gelir (harf, rakam, alt çizgi); yol yine de tırnaklı yazılır.
            var path = $"$.\"{key}\"";
            source = source.Where(e => JsonFunctions.Value(e.CustomFields, path) == value);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await Order(source)
            .ThenBy(e => e.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new RecordPage(items.Select(ToData).ToList(), query.Page, query.PageSize, total);
    }

    // e => owners.Contains(e.OwnerId): kapsam sorguda uygulanır, sayfalama ve toplam doğru kalır. Sorumlusu boş kayıt eşleşmez.
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
        await ApplyAsync(entity, values, cancellationToken);
        if (FieldsByName.TryGetValue(OwnerField, out var owner) && owner.Read(entity) is null)
        {
            owner.Write(entity, context.CurrentUserId);
        }

        Data.Set<TEntity>().Add(entity);
        return ToData(entity);
    }

    public async Task<RecordData?> UpdateAsync(Guid id, JsonObject values, long? expectedVersion, CancellationToken cancellationToken)
    {
        var entity = await Data.Set<TEntity>().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        EnsureVersion(entity, expectedVersion);
        var before = ToData(entity).Values;
        await ApplyAsync(entity, values, cancellationToken);

        if (!JsonNode.DeepEquals(before, ToData(entity).Values))
        {
            entity.Touch();
        }

        return ToData(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var entity = await Data.Set<TEntity>().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        EnsureVersion(entity, expectedVersion);
        Data.Set<TEntity>().Remove(entity);
        return true;
    }

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

    private RecordData ToData(TEntity entity)
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
    private async Task ApplyAsync(TEntity entity, JsonObject values, CancellationToken cancellationToken)
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
            var value = field.Parse(node).Value;

            if (field.Exists is { } exists && value is Guid id && !await exists(context, id, cancellationToken))
            {
                // Başka tenant'ın kaydı da "bulunamadı" sayılır; varlığı belli edilmez.
                errors.Add(new RecordFieldError(name, field.NotFoundMessage ?? "Kayıt bulunamadı."));
                continue;
            }

            field.Write(entity, value);
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    private static void EnsureVersion(TEntity entity, long? expectedVersion)
    {
        if (expectedVersion is { } expected && expected != entity.Version)
        {
            throw new RecordConflictException(
                $"Kayıt siz düzenlerken değişti (beklenen sürüm {expected}, güncel sürüm {entity.Version}). Güncel halini alıp tekrar deneyin.");
        }
    }
}
