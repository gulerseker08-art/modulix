using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Records;

/// <summary>
/// Ortak kayıtların depoları (ADR-0006): "core.party" ve "core.person". Okuma ve yazma izni, ortak kaydı kullanan
/// modüllerin bildirdiği izinlerden biridir (<see cref="ModuleCatalog.SharedAccessTo"/>). Ortak kayıt, modüllerin
/// bildirdiği bir alan ona başvuruyorsa silinemez (<see cref="ModuleCatalog.SharedReferencesTo"/>).
/// Modül uzantıları (ör. CRM firması) ortak alanları kendi depolarından, navigation üzerinden yazar.
/// </summary>
internal abstract class SharedRecordStore<TEntity>(ModulixDbContext db, ModuleCatalog catalog, IServiceProvider services) : IRecordStore
    where TEntity : SharedEntity, new()
{
    private Dictionary<string, SharedField<TEntity>>? fieldsByName;

    public abstract string EntityKey { get; }

    /// <summary>Çekirdeğin kayıt türü: her tenant için açıktır, erişimi izinler belirler.</summary>
    public string FeatureKey => EntityKey;

    protected abstract IReadOnlyList<SharedField<TEntity>> Fields { get; }

    /// <summary>Listede kullanılabilen filtreler; değer geçersizse null döner.</summary>
    protected abstract IReadOnlyDictionary<string, Func<IQueryable<TEntity>, string, IQueryable<TEntity>?>> Filters { get; }

    protected DbSet<TEntity> Set => db.Set<TEntity>();

    protected ModulixDbContext Db => db;

    private Dictionary<string, SharedField<TEntity>> FieldsByName =>
        fieldsByName ??= Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

    public IReadOnlyList<string> AnyPermissionFor(RecordOperation operation)
    {
        var access = catalog.SharedAccessTo(EntityKey);
        return operation == RecordOperation.Read
            ? [.. access.Select(a => a.ReadPermission).Distinct(StringComparer.Ordinal)]
            : [.. access.Select(a => a.WritePermission).OfType<string>().Distinct(StringComparer.Ordinal)];
    }

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
        var entity = await Set.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        return entity is null ? null : ToData(entity);
    }

    public async Task<RecordPage> QueryAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        IQueryable<TEntity> source = Set.AsNoTracking();
        var errors = new List<RecordFieldError>();
        foreach (var (name, value) in query.Filters ?? new Dictionary<string, string>())
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

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

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

        return new RecordPage([.. items.Select(ToData)], query.Page, query.PageSize, total);
    }

    public async Task<RecordData> CreateAsync(JsonObject values, CancellationToken cancellationToken)
    {
        var entity = new TEntity();
        await ApplyAsync(entity, values, cancellationToken);
        Set.Add(entity);
        return ToData(entity);
    }

    public async Task<RecordData?> UpdateAsync(Guid id, JsonObject values, long? expectedVersion, CancellationToken cancellationToken)
    {
        var entity = await Set.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
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
        var entity = await Set.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        EnsureVersion(entity, expectedVersion);
        if (await UsagesAsync(id, cancellationToken) is { Count: > 0 } usages)
        {
            throw new RecordConflictException(
                "Bu kayıt kullanıldığı için silinemez: " + string.Join(", ", usages.Select(u => $"{u.Count} {u.Label}")) + ".");
        }

        Set.Remove(entity);
        return true;
    }

    /// <summary>Kaydın modüllerde nerede kullanıldığı: bildirilmiş her referans alanı için kayıt sayısı (sıfırlar hariç).</summary>
    private async Task<List<(string Label, int Count)>> UsagesAsync(Guid id, CancellationToken cancellationToken)
    {
        // Kayıt deposu listesi bu depoyu da içerdiği için yapıcıda değil, burada alınır.
        var stores = services.GetRequiredService<IRecordStoreRegistry>();
        var usages = new List<(string Label, int Count)>();
        foreach (var reference in catalog.SharedReferencesTo(EntityKey))
        {
            if (stores.Find(reference.EntityKey) is not { } store)
            {
                continue;
            }

            var filters = new Dictionary<string, string> { [reference.Field] = id.ToString() };
            var page = await store.QueryAsync(new RecordQuery(reference.EntityKey, PageSize: 1, Filters: filters), cancellationToken);
            if (page.TotalCount > 0)
            {
                usages.Add((reference.Label, page.TotalCount));
            }
        }

        return usages;
    }

    protected abstract IOrderedQueryable<TEntity> Order(IQueryable<TEntity> source);

    protected abstract IQueryable<TEntity> Search(IQueryable<TEntity> source, string term);

    /// <summary>Alanlar yazıldıktan sonra veriye bağlı kurallar (ör. tekil vergi numarası); geçmezse <see cref="RecordValidationException"/>.</summary>
    protected virtual Task CheckAsync(TEntity entity, CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ApplyAsync(TEntity entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        foreach (var (name, node) in values)
        {
            var field = FieldsByName[name];
            var value = field.Parse(node).Value;
            if (field.Exists is { } exists && value is Guid id && !await exists(db, id, cancellationToken))
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

        await CheckAsync(entity, cancellationToken);
    }

    private RecordData ToData(TEntity entity)
    {
        var values = new JsonObject();
        foreach (var field in Fields)
        {
            values[field.Name] = field.Read(entity);
        }

        return new RecordData(EntityKey, entity.Id, entity.Version, values);
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

/// <summary>Taraf (firma): müşteri, tedarikçi, taşıyıcı... Vergi numarası tenant içinde tekildir.</summary>
internal sealed class PartyRecordStore(ModulixDbContext db, ModuleCatalog catalog, IServiceProvider services)
    : SharedRecordStore<Party>(db, catalog, services)
{
    public override string EntityKey => SharedEntities.Party;

    protected override IReadOnlyList<SharedField<Party>> Fields { get; } =
    [
        SharedField.Kind<Party>("kind", p => p.Kind, (p, v) => p.Kind = v),
        SharedField.Text<Party>("name", Party.NameMaxLength, p => p.Name, (p, v) => p.Name = v!, required: true),
        SharedField.Text<Party>("taxOffice", Party.TaxOfficeMaxLength, p => p.TaxOffice, (p, v) => p.TaxOffice = v),
        SharedField.Text<Party>("taxNumber", Party.TaxNumberMaxLength, p => p.TaxNumber, (p, v) => p.TaxNumber = v),
        SharedField.Text<Party>("phone", Party.PhoneMaxLength, p => p.Phone, (p, v) => p.Phone = v),
        SharedField.Email<Party>("email", Party.EmailMaxLength, p => p.Email, (p, v) => p.Email = v),
        SharedField.Text<Party>("website", Party.WebsiteMaxLength, p => p.Website, (p, v) => p.Website = v),
        SharedField.Text<Party>("city", Party.CityMaxLength, p => p.City, (p, v) => p.City = v),
        SharedField.Text<Party>("address", Party.AddressMaxLength, p => p.Address, (p, v) => p.Address = v),
    ];

    /// <summary>"taxNumber": aynı numaralı taraf (oluşturmadan önce mevcut kaydı bulmak için).</summary>
    protected override IReadOnlyDictionary<string, Func<IQueryable<Party>, string, IQueryable<Party>?>> Filters { get; } =
        new Dictionary<string, Func<IQueryable<Party>, string, IQueryable<Party>?>>
        {
            ["taxNumber"] = (q, v) => v.Trim() is { Length: > 0 and <= Party.TaxNumberMaxLength } number ? q.Where(p => p.TaxNumber == number) : null,
        };

    protected override IOrderedQueryable<Party> Order(IQueryable<Party> source) => source.OrderBy(p => p.Name);

    protected override IQueryable<Party> Search(IQueryable<Party> source, string term) =>
        source.Where(p => p.Name.Contains(term)
            || (p.TaxNumber != null && p.TaxNumber.Contains(term))
            || (p.Email != null && p.Email.Contains(term))
            || (p.Phone != null && p.Phone.Contains(term))
            || (p.City != null && p.City.Contains(term)));

    protected override async Task CheckAsync(Party entity, CancellationToken cancellationToken)
    {
        if (entity.TaxNumber is { } number
            && await Set.AnyAsync(p => p.TaxNumber == number && p.Id != entity.Id, cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError("taxNumber", "Bu vergi numarasıyla kayıtlı bir firma var.")]);
        }
    }
}

/// <summary>Kişi: müşteri yetkilisi, çalışan, sürücü...</summary>
internal sealed class PersonRecordStore(ModulixDbContext db, ModuleCatalog catalog, IServiceProvider services)
    : SharedRecordStore<Person>(db, catalog, services)
{
    public override string EntityKey => SharedEntities.Person;

    protected override IReadOnlyList<SharedField<Person>> Fields { get; } =
    [
        SharedField.Text<Person>("firstName", Person.NameMaxLength, p => p.FirstName, (p, v) => p.FirstName = v!, required: true),
        SharedField.Text<Person>("lastName", Person.NameMaxLength, p => p.LastName, (p, v) => p.LastName = v!, required: true),
        SharedField.Email<Person>("email", Person.EmailMaxLength, p => p.Email, (p, v) => p.Email = v),
        SharedField.Text<Person>("phone", Person.PhoneMaxLength, p => p.Phone, (p, v) => p.Phone = v),
        SharedField.Text<Person>("title", Person.TitleMaxLength, p => p.Title, (p, v) => p.Title = v),
        SharedField.PartyReference<Person>("partyId", p => p.PartyId, (p, v) => p.PartyId = v),
    ];

    protected override IReadOnlyDictionary<string, Func<IQueryable<Person>, string, IQueryable<Person>?>> Filters { get; } =
        new Dictionary<string, Func<IQueryable<Person>, string, IQueryable<Person>?>>
        {
            ["partyId"] = (q, v) => Guid.TryParse(v, out var id) && id != Guid.Empty ? q.Where(p => p.PartyId == id) : null,
            ["email"] = (q, v) => v.Trim() is { Length: > 0 and <= Person.EmailMaxLength } email ? q.Where(p => p.Email == email) : null,
        };

    protected override IOrderedQueryable<Person> Order(IQueryable<Person> source) =>
        source.OrderBy(p => p.LastName).ThenBy(p => p.FirstName);

    protected override IQueryable<Person> Search(IQueryable<Person> source, string term) =>
        source.Where(p => p.FirstName.Contains(term)
            || p.LastName.Contains(term)
            || (p.Email != null && p.Email.Contains(term))
            || (p.Phone != null && p.Phone.Contains(term)));
}

/// <summary>Ortak kayıt alanı: JSON'daki teknik adı, doğrulaması ve entity'ye nasıl okunup yazıldığı.</summary>
internal sealed class SharedField<TEntity>(
    string name,
    bool required,
    Func<JsonNode, (object? Value, string? Error)> parse,
    Func<TEntity, JsonNode?> read,
    Action<TEntity, object?> write)
{
    public string Name => name;

    /// <summary>Oluştururken verilmesi zorunlu; boş bırakılamaz.</summary>
    public bool Required => required;

    /// <summary>Bağlı kayıt alanıysa, aktif tenant'ta var olup olmadığını denetler.</summary>
    public Func<ModulixDbContext, Guid, CancellationToken, Task<bool>>? Exists { get; init; }

    public string? NotFoundMessage { get; init; }

    public (object? Value, string? Error) Parse(JsonNode? node)
    {
        // Boş metin de boş değer sayılır.
        if (node is null || (node.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace(node.GetValue<string>())))
        {
            return required ? (null, "Bu alan zorunludur.") : (null, null);
        }

        return parse(node);
    }

    public JsonNode? Read(TEntity entity) => read(entity);

    public void Write(TEntity entity, object? value) => write(entity, value);
}

/// <summary>Alan türleri; biçimler CRM kayıt alanlarıyla aynıdır.</summary>
internal static class SharedField
{
    public static SharedField<T> Text<T>(string name, int maxLength, Func<T, string?> get, Action<T, string?> set, bool required = false) =>
        new(name, required, node => ParseText(node, maxLength), e => get(e), (e, v) => set(e, (string?)v));

    public static SharedField<T> Email<T>(string name, int maxLength, Func<T, string?> get, Action<T, string?> set) =>
        new(name, required: false, node =>
            {
                var text = ParseText(node, maxLength);
                return text.Error is null && text.Value is string value && !IsEmail(value) ? (null, "Geçerli bir e-posta adresi girin.") : text;
            },
            e => get(e),
            (e, v) => set(e, (string?)v));

    /// <summary>Taraf türü: "Organization" (tüzel) veya "Individual" (gerçek kişi); boşsa tüzel.</summary>
    public static SharedField<T> Kind<T>(string name, Func<T, PartyKind> get, Action<T, PartyKind> set) =>
        new(name, required: false, node =>
                node.GetValueKind() == JsonValueKind.String
                && Enum.TryParse<PartyKind>(node.GetValue<string>(), ignoreCase: false, out var kind)
                && Enum.IsDefined(kind)
                && !char.IsDigit(node.GetValue<string>()[0])
                    ? (kind, null)
                    : (null, "Organization veya Individual olmalıdır."),
            e => get(e).ToString(),
            (e, v) => set(e, v is PartyKind kind ? kind : PartyKind.Organization));

    /// <summary>Aynı tenant'taki bir taraf.</summary>
    public static SharedField<T> PartyReference<T>(string name, Func<T, Guid?> get, Action<T, Guid?> set) =>
        new(name, required: false, ParseId, e => get(e)?.ToString(), (e, v) => set(e, (Guid?)v))
        {
            Exists = (db, id, ct) => db.Set<Party>().AnyAsync(p => p.Id == id, ct),
            NotFoundMessage = "Firma bulunamadı.",
        };

    private static (object? Value, string? Error) ParseId(JsonNode node) =>
        node.GetValueKind() == JsonValueKind.String && Guid.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, out var id) && id != Guid.Empty
            ? (id, null)
            : (null, "Geçerli bir kayıt kimliği olmalıdır.");

    private static (object? Value, string? Error) ParseText(JsonNode node, int maxLength)
    {
        if (node.GetValueKind() != JsonValueKind.String)
        {
            return (null, "Metin olmalıdır.");
        }

        var text = node.GetValue<string>().Trim();
        return text.Length > maxLength ? (null, $"En fazla {maxLength} karakter olabilir.") : (text, null);
    }

    private static bool IsEmail(string value)
    {
        var at = value.IndexOf('@');
        return at > 0 && at == value.LastIndexOf('@') && at < value.Length - 1 && !value.Any(char.IsWhiteSpace);
    }
}
