using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// CRM kayıt depolarının ortak kalıbı; yeni bir sektör modülü de aynısını izler. Alt sınıf yalnızca
/// anahtarı, izinleri, alan listesini, filtreleri, sıralamayı ve aramayı verir. Kaydetmez: boru hattı kaydı,
/// audit'i ve olayı tek seferde yazar. Tenant filtresi ve RLS <see cref="IModuleData"/> üzerinden uygulanır.
/// Ortak kaydın uzantısı olan entity (ADR-0006, ör. firma → taraf) ortak kaydı <see cref="Load"/> ile yükler, yeni kayıtta
/// <see cref="PrepareNewAsync"/> ile bağlar ve <see cref="SharedOf"/> ile verir; ortak alanlar navigation üzerinden yazılır.
/// </summary>
internal abstract class CrmRecordStore<TEntity>(CrmStoreContext context) : IRecordStore
    where TEntity : CrmEntity, new()
{
    /// <summary>Sorumlu alanının adı; oluştururken boş bırakılırsa isteği yapan kullanıcı atanır.</summary>
    protected const string OwnerField = OwnedRecordType.OwnerField;

    private static readonly IReadOnlyDictionary<string, RecordFilter<TEntity>> NoFilters = new Dictionary<string, RecordFilter<TEntity>>();
    private static readonly IReadOnlyDictionary<string, AsyncRecordFilter<TEntity>> NoAsyncFilters =
        new Dictionary<string, AsyncRecordFilter<TEntity>>();

    private Dictionary<string, RecordField<TEntity>>? fieldsByName;

    public abstract string EntityKey { get; }

    /// <summary>Lisans kontrolü modül düzeyindedir: CRM tenant için açıksa tüm CRM kayıtları kullanılabilir.</summary>
    public string FeatureKey => EntityKey;

    protected abstract string ReadPermission { get; }

    protected abstract string WritePermission { get; }

    protected abstract IReadOnlyList<RecordField<TEntity>> Fields { get; }

    /// <summary>Listede kullanılabilen filtreler (sorgu parametresi adı → filtre).</summary>
    protected virtual IReadOnlyDictionary<string, RecordFilter<TEntity>> Filters => NoFilters;

    /// <summary>Veri okuyan filtreler (ör. seçim listesinin kategorisine göre); adları <see cref="Filters"/>'takilerle çakışmaz.</summary>
    protected virtual IReadOnlyDictionary<string, AsyncRecordFilter<TEntity>> AsyncFilters => NoAsyncFilters;

    private IModuleData Data => context.Data;

    /// <summary>Kayıt depolarının servisleri.</summary>
    protected CrmStoreContext Context => context;

    /// <summary>Adıyla alan (ör. bağlama kararında değeri okumak için).</summary>
    protected RecordField<TEntity> FieldNamed(string name) => FieldsByName[name];

    private Dictionary<string, RecordField<TEntity>> FieldsByName =>
        fieldsByName ??= Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

    /// <summary>Birleştirme izni (ADR-0013); yalnızca birleştirilebilen depolar verir, diğerlerinde yazma izni geçerlidir.</summary>
    protected virtual string? MergePermission => null;

    public string? PermissionFor(RecordOperation operation) => operation switch
    {
        RecordOperation.Read => ReadPermission,
        RecordOperation.Merge => MergePermission ?? WritePermission,
        _ => WritePermission,
    };

    /// <summary>Kaydın şu andaki (kaydedilmemiş değişiklikleri dahil) hali ve sürümü; birleştirme sonucu için.</summary>
    protected RecordData Snapshot(TEntity entity) => ToData(entity);

    /// <summary>Kaydın sürümü eşit değilse <see cref="RecordConflictException"/> (birleştirmede ekranın gördüğü sürüm).</summary>
    protected void EnsureRecordVersion(TEntity entity, long expected) => EnsureVersion(entity, expected);

    /// <summary>Kayıt türünün alanları (birleştirmede alan farkı için).</summary>
    protected IReadOnlyList<RecordField<TEntity>> AllFields => Fields;

    /// <summary>Sorumlusu olan kayıtlarda veri kapsamı (Y8) sorumlu alanına göre uygulanır.</summary>
    string? IRecordStore.OwnerField => IsOwned ? OwnerField : null;

    bool IRecordStore.UsesOwnerScope => UsesScope;

    /// <summary>Veri kapsamı uygulanır mı? Varsayılan: kayıt sorumlu alanı taşıyorsa; başka kayıta bağlı kapsamı olan depo true yapar.</summary>
    protected virtual bool UsesScope => IsOwned;

    Task<bool> IRecordStore.IsInOwnerScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        IsInScopeAsync(record, owners, cancellationToken);

    /// <summary>Kayıt veri kapsamında mı? Varsayılan: sorumlu alanı kapsamdaki kullanıcılardan biri.</summary>
    protected virtual Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        Task.FromResult(record.Values.TryGetPropertyValue(OwnerField, out var node)
            && node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && Guid.TryParse(text, out var owner)
            && owners.Contains(owner));

    /// <summary>Listeye veri kapsamını uygular (sorguda; sayfalama ve toplam doğru kalır).</summary>
    protected virtual IQueryable<TEntity> ScopeToOwners(IQueryable<TEntity> source, IReadOnlySet<Guid> owners) =>
        source.Where(OwnedBy(owners));

    private static bool IsOwned => typeof(IOwnedEntity).IsAssignableFrom(typeof(TEntity));

    /// <summary>Tüm CRM kayıtlarına özel alan eklenebilir (Y9a); değerler <see cref="CrmEntity.CustomFields"/>'ta saklanır.</summary>
    public bool SupportsCustomFields => true;

    public virtual IReadOnlyList<RecordFieldError> Validate(RecordOperation operation, JsonObject values)
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
            else if (!field.IsOmitted(node) && field.Parse(node).Error is { } error)
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
        var entity = await Load(Data.Set<TEntity>().AsNoTracking()).SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        return entity is null ? null : ToData(entity);
    }

    public async Task<RecordPage> QueryAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        var source = await ApplyFiltersAsync(Load(Data.Set<TEntity>().AsNoTracking()), query.Filters, cancellationToken);
        if (query.Owners is { } owners && UsesScope)
        {
            source = await ScopeToOwnersAsync(source, owners, cancellationToken);
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

    /// <summary>Asenkron kapsam uygulamasÄ±; varsayÄ±lan <see cref="ScopeToOwners"/>. Ek izne baÄlÄ± gÃ¶rÃ¼nÃ¼rlÃ¼k (Ã¶r. atanmamÄ±Å Lead) iÃ§in geÃ§ersiz kÄ±lÄ±nÄ±r.</summary>
    protected virtual Task<IQueryable<TEntity>> ScopeToOwnersAsync(IQueryable<TEntity> source, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        Task.FromResult(ScopeToOwners(source, owners));

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
        (values, var linked) = await PrepareNewAsync(entity, values, cancellationToken);
        var sharedBefore = linked ? SharedState(entity) : null;
        await ApplyAsync(entity, values, isNew: true, cancellationToken);
        if (linked && SharedChanged(entity, sharedBefore))
        {
            SharedOf(entity)!.Touch();
        }

        if (DefaultsOwnerToCurrentUser && FieldsByName.TryGetValue(OwnerField, out var owner) && owner.Read(entity) is null)
        {
            owner.Write(entity, context.CurrentUserId);
        }

        // Verilmeyen (veya boş gönderilen) zorunlu liste alanı listenin varsayılanını alır: tanımın varsayılanı etkinse o,
        // değilse ilk etkin değer (ör. ilk aşama).
        foreach (var field in Fields.Where(f => f.ChoiceListKey is not null && !f.Nullable && (!values.ContainsKey(f.Name) || f.IsOmitted(values[f.Name]))))
        {
            var list = await context.ChoiceLists.GetAsync(field.ChoiceListKey!, cancellationToken);
            field.Write(entity, list.DefaultValue?.Key);
        }

        OnCreated(entity);
        Data.Set<TEntity>().Add(entity);
        return ToData(entity);
    }

    public async Task<RecordData?> UpdateAsync(Guid id, JsonObject values, long? expectedVersion, CancellationToken cancellationToken)
    {
        var entity = await Load(Data.Set<TEntity>()).SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        EnsureVersion(entity, expectedVersion);
        var sharedBefore = SharedState(entity);
        var before = OwnValues(entity, sharedBefore);
        await ApplyAsync(entity, values, isNew: false, cancellationToken);

        // Ortak alanların değişikliği ortak kaydın, diğerleri uzantının sürümünü artırır; kaydın sürümü ikisinden oluşur.
        if (!JsonNode.DeepEquals(before, OwnValues(entity, sharedBefore)))
        {
            entity.Touch();
        }

        if (SharedChanged(entity, sharedBefore))
        {
            SharedOf(entity)!.Touch();
        }

        return ToData(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var entity = await Load(Data.Set<TEntity>()).SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        EnsureVersion(entity, expectedVersion);
        await BeforeDeleteAsync(entity, cancellationToken);
        Data.Set<TEntity>().Remove(entity);
        return true;
    }

    /// <summary>Varsayılanlar (sorumlu, liste değerleri) atandıktan sonra, kayıt eklenmeden hemen önce.</summary>
    protected virtual void OnCreated(TEntity entity)
    {
    }

    /// <summary>Sorumlusu verilmeyen yeni kayıt, kaydı oluşturana atanır. Atanmamış kayıt havuzu olan türler (Lead) kapatır.</summary>
    protected virtual bool DefaultsOwnerToCurrentUser => true;

    /// <summary>Kayıt silinmeden önce (ör. kayda ait alt kayıtları kaldırmak için).</summary>
    protected virtual Task BeforeDeleteAsync(TEntity entity, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Entity'yle birlikte yüklenecekler (ör. ortak kayıt: <c>Include(c =&gt; c.Party)</c>).</summary>
    protected virtual IQueryable<TEntity> Load(IQueryable<TEntity> source) => source;

    /// <summary>
    /// Yeni kaydı alanlar yazılmadan önce hazırlar (ör. ortak kayda bağlar). <c>Linked</c>: mevcut bir ortak kayda
    /// bağlandı; o zaman değişen ortak alanlar ortak kaydın sürümünü artırır. Veriye bağlı kural geçmezse
    /// <see cref="RecordValidationException"/>.
    /// </summary>
    protected virtual Task<(JsonObject Values, bool Linked)> PrepareNewAsync(TEntity entity, JsonObject values, CancellationToken cancellationToken) =>
        Task.FromResult((values, false));

    /// <summary>Alanlar yazıldıktan sonra (ör. ortak kaydı eşitlemek için); oluşturma ve güncellemede çağrılır.</summary>
    protected virtual Task AppliedAsync(TEntity entity, JsonObject values, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Entity'nin uzantısı olduğu ortak kayıt; yoksa null.</summary>
    protected virtual SharedEntity? SharedOf(TEntity entity) => null;

    /// <summary>Ortak kaydın alanları (değişip değişmediğini anlamak için); ortak kaydı olmayanda null.</summary>
    protected virtual JsonObject? SharedState(TEntity entity) => null;

    private bool SharedChanged(TEntity entity, JsonObject? before) =>
        before is not null && !JsonNode.DeepEquals(before, SharedState(entity));

    // Kaydın ortak kayda ait olmayan alanları.
    private JsonObject OwnValues(TEntity entity, JsonObject? shared)
    {
        var values = ToData(entity).Values;
        foreach (var (name, _) in shared ?? [])
        {
            values.Remove(name);
        }

        return values;
    }

    /// <summary>
    /// Kaydın sürümü: ortak kaydın uzantısında iki sürümden oluşur (yeni kayıtta 1), böylece başka bir modülün ortak kayıtta
    /// yaptığı değişiklik de eski sürümle gelen güncellemeyi 409 ile durdurur. Her değişiklikte artar.
    /// </summary>
    private long RecordVersion(TEntity entity) =>
        SharedOf(entity) is { } shared ? entity.Version + shared.Version - 1 : entity.Version;

    /// <summary>Liste sırası. Eşit kayıtlar kimliğe göre sıralanır; sayfalar kararlı kalır.</summary>
    protected abstract IOrderedQueryable<TEntity> Order(IQueryable<TEntity> source);

    protected abstract IQueryable<TEntity> Search(IQueryable<TEntity> source, string term);

    private async Task<IQueryable<TEntity>> ApplyFiltersAsync(
        IQueryable<TEntity> source, IReadOnlyDictionary<string, string>? filters, CancellationToken cancellationToken)
    {
        if (filters is null)
        {
            return source;
        }

        var errors = new List<RecordFieldError>();
        foreach (var (name, value) in filters)
        {
            var filtered = Filters.TryGetValue(name, out var filter)
                ? filter(source, value, context)
                : AsyncFilters.TryGetValue(name, out var asyncFilter)
                    ? await asyncFilter(source, value, context, cancellationToken)
                    : null;
            if (filter is null && !AsyncFilters.ContainsKey(name))
            {
                errors.Add(new RecordFieldError(name, "Bilinmeyen filtre."));
            }
            else if (filtered is not null)
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
        foreach (var field in Fields.Where(f => !f.WriteOnly))
        {
            values[field.Name] = field.Read(entity);
        }

        values[RecordFields.Custom] = entity.CustomFields is null ? new JsonObject() : JsonNode.Parse(entity.CustomFields);
        return new RecordData(EntityKey, entity.Id, RecordVersion(entity), values);
    }

    /// <summary>
    /// Yalnızca verilen alanları yazar (kısmi güncelleme). Değerler <see cref="Validate"/>'ten geçmiştir. Liste alanının
    /// değeri firmanın listesinde olmalıdır; pasif değer yalnızca kayıtta zaten varsa kalabilir.
    /// </summary>
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
            // Sorumlu gibi "güncellemede kapılı" alan, kullanıcı devrinde (ADR-0004, neden: transfer) de değişebilir.
            var transfer = context.CommandReason == EventReasons.Transfer;
            if ((field.SystemOnly || (field.GatedOnUpdate && !isNew && !transfer)) && !context.LeadGate.IsOpen)
            {
                continue;
            }

            var value = field.Parse(node).Value;

            if (field.CreateOnly)
            {
                // Oluştururken PrepareNewAsync kullanır; sonra değiştirilemez (boş değer verilmemiş sayılır).
                if (!isNew && value is not null && !JsonNode.DeepEquals(field.Read(entity), JsonValue.Create(value.ToString())))
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

        await AppliedAsync(entity, values, cancellationToken);
    }

    private void EnsureVersion(TEntity entity, long? expectedVersion)
    {
        if (expectedVersion is { } expected && expected != RecordVersion(entity))
        {
            throw new RecordConflictException(
                $"Kayıt siz düzenlerken değişti (beklenen sürüm {expected}, güncel sürüm {RecordVersion(entity)}). Güncel halini alıp tekrar deneyin.");
        }
    }
}
