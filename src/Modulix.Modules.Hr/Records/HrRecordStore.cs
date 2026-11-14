using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// İK kayıt depolarının ortak kalıbı (diğer modüllerdeki kayıt deposu ile aynı sözleşme). Alt sınıf yalnızca anahtarı, izinleri,
/// alan listesini, filtreleri, sıralamayı ve aramayı verir. Kaydetmez: boru hattı kaydı, audit'i ve olayı tek seferde yazar.
/// Tenant filtresi ve RLS <see cref="IModuleData"/> üzerinden uygulanır.
/// </summary>
internal abstract class HrRecordStore<TEntity>(HrStoreContext context) : IRecordStore, IRecordWriteGuard
    where TEntity : HrEntity, new()
{
    private static readonly IReadOnlyDictionary<string, RecordFilter<TEntity>> NoFilters = new Dictionary<string, RecordFilter<TEntity>>();

    private Dictionary<string, RecordField<TEntity>>? fieldsByName;

    public abstract string EntityKey { get; }

    /// <summary>Anonim oluşturmayı kabul eder mi (<see cref="RecordCommand.Anonymous"/>; yalnızca tam anonim anket cevabı).</summary>
    public virtual bool AllowsAnonymousWrites => false;

    /// <summary>
    /// Kayıt düzeyinde yan yazma kuralı (ADR-0040 K2, ADR-0042 bölüm 25): kayda dosya eklemek ve silmek, tür izninden sonra kaydın kendi kuralına da
    /// uymalıdır. Kural <see cref="FileWriteDeniedAsync"/>'tedir; kayıt bulunamazsa karar okuma denetimine kalır.
    /// </summary>
    public async Task<string?> WriteDeniedAsync(Guid id, CancellationToken cancellationToken) =>
        await Data.Set<TEntity>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken) is { } entity
            ? await FileWriteDeniedAsync(entity, cancellationToken)
            : null;

    /// <summary>
    /// Bu kayda dosya eklenemez/silinemezse nedeni. Varsayılan: kural yok (tür güncelleme izni yeter). Kaydı yalnızca belirli durumda ya da belirli
    /// kişi değiştirebiliyorsa (ör. masraf satırı: taslak beyan, sahibi ya da İK) aynı kural burada da verilir; yoksa dosya kaydın kuralını atlatır.
    /// </summary>
    protected virtual Task<string?> FileWriteDeniedAsync(TEntity entity, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary>Lisans kontrolü modül düzeyindedir: modül tenant için açıksa tüm kayıtları kullanılabilir.</summary>
    public string FeatureKey => EntityKey;

    protected abstract string ReadPermission { get; }

    protected abstract string WritePermission { get; }

    protected abstract IReadOnlyList<RecordField<TEntity>> Fields { get; }

    protected virtual IReadOnlyDictionary<string, RecordFilter<TEntity>> Filters => NoFilters;

    protected HrStoreContext Context => context;

    /// <summary>
    /// Doluysa kayıt oluşturulduktan sonra <b>değiştirilemez ve silinemez</b>: güncelleme ve silme bu iletiyle çakışma (409) döner. Yalnızca eklenen kayıtlar
    /// (stok hareket defteri, ADR-0032 K13) ve türetilmiş kayıtlar (bakiye) kullanır.
    /// </summary>
    protected virtual string? ImmutableMessage => null;

    /// <summary>Doluysa bu kayıt türü genel uçtan oluşturulamaz (yalnızca okunur; örn. bakiye defterden türetilir).</summary>
    protected virtual string? CreateBlockedMessage => null;

    private IModuleData Data => context.Data;

    private Dictionary<string, RecordField<TEntity>> FieldsByName => fieldsByName ??= Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

    /// <summary>
    /// Okuma kayıt türünün okuma iznidir. Yazmada iş akışı kapısı açıksa eylemin izni (ör. izin onayı), değilse oluşturma ya da yazma izni.
    /// </summary>
    public string? PermissionFor(RecordOperation operation) => operation switch
    {
        RecordOperation.Read => ReadPermission,
        _ when context.Gate.Permission is { } action => action,
        RecordOperation.Create => CreatePermission,
        _ => WritePermission,
    };

    /// <summary>Kayıt oluşturma izni; varsayılan yazma iznidir (ör. izin talebi açmak düzenlemeden ayrı izindir).</summary>
    protected virtual string CreatePermission => WritePermission;

    /// <summary>
    /// Veri kapsamı (Y8) alanı: değeri kullanıcı kimliği olan sorumlu alanı. Doluysa boru hattı rolün kapsamına göre süzer ("kendi": yalnızca
    /// kendi kaydı); depo <see cref="ScopeToOwners"/> ile listeye uygular. Boşsa kayıt türü kapsamdan etkilenmez.
    /// </summary>
    protected virtual string? OwnerFieldName => null;

    string? IRecordStore.OwnerField => OwnerFieldName;

    Task<bool> IRecordStore.IsInOwnerScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        Task.FromResult(IsInScope(record, owners));

    /// <summary>Kayıt kapsamda mı? Varsayılan: sorumlu alanı kapsamdaki kullanıcılardan biri. Liste süzgeci (<see cref="ScopeToOwners"/>) aynı kuralı uygulamalıdır.</summary>
    protected virtual bool IsInScope(RecordData record, IReadOnlySet<Guid> owners) =>
        OwnerFieldName is { } field && UserIn(record, field, owners);

    /// <summary>Listeye veri kapsamını uygular (sorguda; sayfalama ve toplam doğru kalır).</summary>
    protected virtual IQueryable<TEntity> ScopeToOwners(IQueryable<TEntity> source, IReadOnlySet<Guid> owners) => source;

    /// <summary>Kayıttaki kullanıcı alanının değeri verilen kümede mi?</summary>
    protected static bool UserIn(RecordData record, string field, IReadOnlySet<Guid> users) =>
        record.Values.TryGetPropertyValue(field, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
        && Guid.TryParse(text, out var user) && users.Contains(user);

    /// <summary>Doluysa genel uçtan güncelleme bu iletiyle çakışma (409) döner (ör. durumu komutla değişen kayıt; kapı açıkken null verilir).</summary>
    protected virtual string? UpdateBlockedMessage => null;

    /// <summary>Tüm İK kayıtlarına özel alan eklenebilir (Y9a).</summary>
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
        var visible = await RestrictAsync(Data.Set<TEntity>().AsNoTracking(), cancellationToken);
        var entity = await visible.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        await PrepareAsync([entity], cancellationToken);
        return ToData(entity);
    }

    /// <summary>
    /// Kullanıcıya göre okuma süzgeci (rol kapsamından bağımsız iş kuralı; ör. duyurunun hedef kitlesi ve yayın tarihi). Tekil okumada ve listede
    /// uygulanır; görünmeyen kayıt "bulunamadı"dır. Varsayılan: süzgeç yok.
    /// </summary>
    protected virtual Task<IQueryable<TEntity>> RestrictAsync(IQueryable<TEntity> source, CancellationToken cancellationToken) => Task.FromResult(source);

    public async Task<RecordPage> QueryAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        var (queryFilters, sort) = SplitSort(query.Filters);
        var source = ApplyFilters(await RestrictAsync(Data.Set<TEntity>().AsNoTracking(), cancellationToken), queryFilters);
        if (query.Owners is { } owners && OwnerFieldName is not null)
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
        var items = await Order(source, sort).ThenBy(e => e.Id).Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(cancellationToken);
        await PrepareAsync(items, cancellationToken);
        return new RecordPage([.. items.Select(ToData)], query.Page, query.PageSize, total);
    }

    public async Task<RecordData> CreateAsync(JsonObject values, CancellationToken cancellationToken)
    {
        if (CreateBlockedMessage is { } blocked)
        {
            throw new RecordConflictException(blocked);
        }

        var entity = new TEntity();

        // Verilmeyen zorunlu liste alanı, listenin varsayılan (yoksa ilk etkin) değerini alır; kategoriye bağlı kurallar bunu görür.
        foreach (var field in Fields.Where(f => f.ChoiceListKey is not null && !f.Nullable && (!values.ContainsKey(f.Name) || f.IsOmitted(values[f.Name]))))
        {
            var list = await context.ChoiceLists.GetAsync(field.ChoiceListKey!, cancellationToken);
            field.Write(entity, list.DefaultValue?.Key);
        }

        await ApplyAsync(entity, values, isNew: true, cancellationToken);
        Data.Set<TEntity>().Add(entity);
        await PrepareAsync([entity], cancellationToken);
        return ToData(entity);
    }

    public async Task<RecordData?> UpdateAsync(Guid id, JsonObject values, long? expectedVersion, CancellationToken cancellationToken)
    {
        if ((ImmutableMessage ?? UpdateBlockedMessage) is { } immutable)
        {
            throw new RecordConflictException(immutable);
        }

        var entity = await Data.Set<TEntity>().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        EnsureVersion(entity, expectedVersion);
        await PrepareAsync([entity], cancellationToken);
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
        if (ImmutableMessage is { } immutable)
        {
            throw new RecordConflictException(immutable);
        }

        var entity = await Data.Set<TEntity>().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        EnsureVersion(entity, expectedVersion);
        await BeforeDeleteAsync(entity, cancellationToken);
        Data.Set<TEntity>().Remove(entity);
        return true;
    }

    /// <summary>
    /// Kayıtlar veriye dönüştürülmeden önce bir kez çağrılır: hesaplanan alanların ihtiyaç duyduğu ilişkili veri (ör. çalışanın kişi kaydındaki
    /// adı) burada toplu yüklenir; kayıt başına sorgu atılmaz.
    /// </summary>
    protected virtual Task PrepareAsync(IReadOnlyCollection<TEntity> entities, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Kayıt silinmeden önce (bağlı kayıtları denetlemek ya da kaldırmak için).</summary>
    protected virtual Task BeforeDeleteAsync(TEntity entity, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Alanlar yazıldıktan sonra (çapraz alan kuralları, benzersizlik); oluşturma ve güncellemede çağrılır.</summary>
    protected virtual Task AppliedAsync(TEntity entity, JsonObject values, bool isNew, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Liste sırası. Eşit kayıtlar kimliğe göre sıralanır; sayfalar kararlı kalır.</summary>
    protected abstract IOrderedQueryable<TEntity> Order(IQueryable<TEntity> source);

    /// <summary>Liste süzgeçleriyle gelen <c>sort</c> anahtarını kabul eden depolar anahtar listesini verir (ör. <c>code</c>, <c>-code</c>); diğerleri için "sort" bilinmeyen süzgeçtir.</summary>
    protected virtual IReadOnlyCollection<string>? SortKeys => null;

    /// <summary>İstenen sıralama (yalnızca <see cref="SortKeys"/> içindeki anahtarlar gelir); varsayılan sıra için null.</summary>
    protected virtual IOrderedQueryable<TEntity> Order(IQueryable<TEntity> source, string? sort) => Order(source);

    /// <summary>Her anahtarın artan ve azalan (başına <c>-</c>) halini verir.</summary>
    protected static IReadOnlyCollection<string> WithDescending(params string[] keys) => [.. keys, .. keys.Select(k => "-" + k)];

    /// <summary>Anahtara göre artan ya da azalan sıralar.</summary>
    protected static IOrderedQueryable<TEntity> By<TKey>(IQueryable<TEntity> source, Expression<Func<TEntity, TKey>> key, bool descending) =>
        descending ? source.OrderByDescending(key) : source.OrderBy(key);

    /// <summary>Anahtara göre sıralar; boş değerler yön ne olursa olsun sonda kalır.</summary>
    protected static IOrderedQueryable<TEntity> ByNullsLast<TKey>(IQueryable<TEntity> source, Expression<Func<TEntity, TKey>> key, bool descending)
    {
        var isNull = Expression.Lambda<Func<TEntity, bool>>(Expression.Equal(key.Body, Expression.Constant(null, key.Body.Type)), key.Parameters);
        var nullsLast = source.OrderBy(isNull);
        return descending ? nullsLast.ThenByDescending(key) : nullsLast.ThenBy(key);
    }

    private (IReadOnlyDictionary<string, string>? Filters, string? Sort) SplitSort(IReadOnlyDictionary<string, string>? filters)
    {
        if (filters is null || SortKeys is not { } keys || !filters.TryGetValue("sort", out var sort))
        {
            return (filters, null);
        }

        if (!keys.Contains(sort, StringComparer.Ordinal))
        {
            throw new RecordValidationException([new RecordFieldError("sort", "Geçersiz sıralama.")]);
        }

        return (filters.Where(f => f.Key != "sort").ToDictionary(f => f.Key, f => f.Value), sort);
    }

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
            else if (filter(source, value) is { } filtered)
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
            if (field.IsOmitted(node) || field.SystemOnly)
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
}
