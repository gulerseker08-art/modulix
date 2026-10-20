using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// CRM kayıt depolarının ortak kalıbı; yeni bir sektör modülü de aynısını izler. Alt sınıf yalnızca
/// anahtarı, izinleri, alan listesini, sıralamayı ve aramayı verir. Kaydetmez: boru hattı kaydı,
/// audit'i ve olayı tek seferde yazar. Tenant filtresi ve RLS <see cref="IModuleData"/> üzerinden uygulanır.
/// </summary>
internal abstract class CrmRecordStore<TEntity>(IModuleData data) : IRecordStore
    where TEntity : CrmEntity, new()
{
    private Dictionary<string, RecordField<TEntity>>? fieldsByName;

    public abstract string EntityKey { get; }

    /// <summary>Lisans kontrolü modül düzeyindedir: CRM tenant için açıksa tüm CRM kayıtları kullanılabilir.</summary>
    public string FeatureKey => EntityKey;

    protected abstract string ReadPermission { get; }

    protected abstract string WritePermission { get; }

    protected abstract IReadOnlyList<RecordField<TEntity>> Fields { get; }

    protected IModuleData Data => data;

    private Dictionary<string, RecordField<TEntity>> FieldsByName =>
        fieldsByName ??= Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

    public string? PermissionFor(RecordOperation operation) =>
        operation == RecordOperation.Read ? ReadPermission : WritePermission;

    public IReadOnlyList<RecordFieldError> Validate(RecordOperation operation, JsonObject values)
    {
        var errors = new List<RecordFieldError>();

        foreach (var (name, node) in values)
        {
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
        var entity = await data.Set<TEntity>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        return entity is null ? null : ToData(entity);
    }

    public async Task<RecordPage> QueryAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        var source = data.Set<TEntity>().AsNoTracking();
        if (query.Search is { } term)
        {
            source = Search(source, term);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await Order(source)
            .ThenBy(e => e.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new RecordPage(items.Select(ToData).ToList(), query.Page, query.PageSize, total);
    }

    public async Task<RecordData> CreateAsync(JsonObject values, CancellationToken cancellationToken)
    {
        var entity = new TEntity();
        await ApplyAsync(entity, values, cancellationToken);
        data.Set<TEntity>().Add(entity);
        return ToData(entity);
    }

    public async Task<RecordData?> UpdateAsync(Guid id, JsonObject values, long? expectedVersion, CancellationToken cancellationToken)
    {
        var entity = await data.Set<TEntity>().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
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
        var entity = await data.Set<TEntity>().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        EnsureVersion(entity, expectedVersion);
        data.Set<TEntity>().Remove(entity);
        return true;
    }

    /// <summary>Liste sırası. Eşit kayıtlar kimliğe göre sıralanır; sayfalar kararlı kalır.</summary>
    protected abstract IOrderedQueryable<TEntity> Order(IQueryable<TEntity> source);

    protected abstract IQueryable<TEntity> Search(IQueryable<TEntity> source, string term);

    private RecordData ToData(TEntity entity)
    {
        var values = new JsonObject();
        foreach (var field in Fields)
        {
            values[field.Name] = field.Read(entity);
        }

        return new RecordData(EntityKey, entity.Id, entity.Version, values);
    }

    /// <summary>Yalnızca verilen alanları yazar (kısmi güncelleme). Değerler <see cref="Validate"/>'ten geçmiştir.</summary>
    private async Task ApplyAsync(TEntity entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();

        foreach (var (name, node) in values)
        {
            var field = FieldsByName[name];
            var value = field.Parse(node).Value;

            if (field.Exists is { } exists && value is Guid id && !await exists(data, id, cancellationToken))
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
