using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Credentials;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Credentials;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Records;

/// <summary>
/// Süreli belgeler (ADR-0038 K3): <c>core.credential</c>. Belgeye erişim sahip kaydın erişimidir: sahip kayıt türünü okuyabilen o türün
/// belgelerini görür, güncelleyebilen ekler, değiştirir ve siler (modül lisansı yazılabilir evredeyken). Okuyamadığı türün belgesi
/// sorgudan elenir ve tekil okumada "bulunamadı"dır. Sahip türleri modüller bildirir (<see cref="ModuleCatalog.CredentialOwners"/>);
/// türün kategorisi sahip türün kategorisiyle aynı olmalıdır. Sahip kayıt türü veri kapsamlı (sorumlu ya da şirket alanı) olamaz: o
/// durumda satır düzeyinde sahip kapsamı gerekir ve bu depo bunu uygulamaz, açılışta değil ilk kullanımda hata verir.
/// </summary>
internal sealed class CredentialRecordStore(
    ModulixDbContext db,
    ModuleCatalog catalog,
    IChoiceLists choiceLists,
    IRecordAccess access,
    IModuleLicense license,
    ITenantContextAccessor tenantAccessor,
    IServiceProvider services) : IRecordStore, IRecordWriteGuard
{
    private static readonly string[] Writable =
        ["ownerEntityKey", "ownerId", "typeKey", "documentNo", "issuedBy", "validFrom", "validTo", "status", "notes"];

    private static readonly IReadOnlyCollection<string> Sorts =
        [.. new[] { "validTo", "typeKey", "documentNo", "createdAt" }.SelectMany(k => new[] { k, "-" + k })];

    public string EntityKey => CredentialEntities.Credential;

    /// <summary>Çekirdeğin kayıt türü: her tenant için açıktır; erişimi sahip kayıt türlerinin izinleri belirler.</summary>
    public string FeatureKey => EntityKey;

    /// <summary>Sahip türlerinin izinlerinden biri yeterlidir; hangi satırların görüneceğini depo sahip türüne göre ayrıca süzer.</summary>
    public IReadOnlyList<string> AnyPermissionFor(RecordOperation operation)
    {
        var stores = Registry;
        var ownerOperation = operation == RecordOperation.Read ? RecordOperation.Read : RecordOperation.Update;
        return [.. catalog.CredentialOwners
            .Select(o => stores.Find(o.Owner.EntityKey))
            .OfType<IRecordStore>()
            .SelectMany(s => s.AnyPermissionFor(ownerOperation) ?? (s.PermissionFor(ownerOperation) is { } p ? [p] : []))
            .Distinct(StringComparer.Ordinal)];
    }

    // Kayıt deposu listesi bu depoyu da içerdiği için yapıcıda değil, kullanımda alınır.
    private IRecordStoreRegistry Registry => services.GetRequiredService<IRecordStoreRegistry>();

    public IReadOnlyList<RecordFieldError> Validate(RecordOperation operation, JsonObject values)
    {
        var errors = new List<RecordFieldError>();
        foreach (var (name, node) in values)
        {
            if (!Writable.Contains(name, StringComparer.Ordinal))
            {
                errors.Add(new RecordFieldError(name, "Bilinmeyen alan."));
            }
            else if (Parse(name, node).Error is { } error)
            {
                errors.Add(new RecordFieldError(name, error));
            }
        }

        if (operation == RecordOperation.Create)
        {
            errors.AddRange(new[] { "ownerEntityKey", "ownerId", "typeKey" }
                .Where(f => !values.ContainsKey(f) || IsEmpty(values[f]))
                .Select(f => new RecordFieldError(f, "Bu alan zorunludur.")));
        }

        return errors;
    }

    public async Task<RecordData?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var readable = await OwnerTypesAsync(RecordOperation.Read, cancellationToken);
        var entity = await db.Credentials.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == id && readable.Contains(c.OwnerEntityKey), cancellationToken);
        return entity is null ? null : ToData(entity);
    }

    public async Task<RecordPage> QueryAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        var readable = await OwnerTypesAsync(RecordOperation.Read, cancellationToken);
        var source = db.Credentials.AsNoTracking().Where(c => readable.Contains(c.OwnerEntityKey));
        string? sort = null;
        var errors = new List<RecordFieldError>();
        foreach (var (name, value) in query.Filters ?? new Dictionary<string, string>())
        {
            if (name == "sort")
            {
                if (Sorts.Contains(value, StringComparer.Ordinal))
                {
                    sort = value;
                }
                else
                {
                    errors.Add(new RecordFieldError("sort", "Geçersiz sıralama."));
                }

                continue;
            }

            if (Filter(source, name, value) is { } filtered)
            {
                source = filtered;
            }
            else
            {
                errors.Add(new RecordFieldError(name, IsFilter(name) ? "Geçersiz filtre değeri." : "Bilinmeyen filtre."));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        if (query.Search is { } term)
        {
            source = source.Where(c => (c.DocumentNo != null && c.DocumentNo.Contains(term)) || (c.IssuedBy != null && c.IssuedBy.Contains(term)));
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await Order(source, sort).ThenBy(c => c.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(cancellationToken);
        var data = items.Select(ToData).ToList();
        await AddOwnerLabelsAsync(data, cancellationToken);
        return new RecordPage(data, query.Page, query.PageSize, total);
    }

    /// <summary>
    /// Listede sahip kaydın kısa adı (<c>ownerLabel</c>, salt okunur): sahip deposu <see cref="IRecordLabelProvider"/> uygularsa (araç → plaka,
    /// sürücü → ad soyad). Yalnızca listede verilir; tekil okuma ve denetim kaydı değişmez. Satırlar zaten okunabilir sahip türleriyle süzülmüştür.
    /// </summary>
    private async Task AddOwnerLabelsAsync(List<RecordData> data, CancellationToken cancellationToken)
    {
        foreach (var group in data.GroupBy(d => d.Values["ownerEntityKey"]!.GetValue<string>()))
        {
            var ids = group.Select(d => Guid.Parse(d.Values["ownerId"]!.GetValue<string>())).Distinct().ToList();
            var labels = OwnerStore(group.Key) is IRecordLabelProvider provider
                ? await provider.LabelsAsync(ids, cancellationToken)
                : new Dictionary<Guid, string>();
            foreach (var item in group)
            {
                item.Values["ownerLabel"] = labels.GetValueOrDefault(Guid.Parse(item.Values["ownerId"]!.GetValue<string>()));
            }
        }
    }

    public async Task<RecordData> CreateAsync(JsonObject values, CancellationToken cancellationToken)
    {
        var ownerKey = (string)Parse("ownerEntityKey", values["ownerEntityKey"]).Value!;
        var ownerId = (Guid)Parse("ownerId", values["ownerId"]).Value!;
        if (catalog.FindCredentialOwner(ownerKey) is not { } owner || !await access.CanAsync(ownerKey, RecordOperation.Read, cancellationToken))
        {
            throw Invalid("ownerEntityKey", "Bu kayıt türüne süreli belge eklenemez.");
        }

        await EnsureWritableAsync(owner.ModuleKey, ownerKey, cancellationToken);
        if (OwnerStore(ownerKey) is not { } ownerStore || await ownerStore.FindAsync(ownerId, cancellationToken) is null)
        {
            // Başka tenant'ın kaydı da "bulunamadı" sayılır; varlığı belli edilmez.
            throw Invalid("ownerId", "Kayıt bulunamadı.");
        }

        var entity = new Credential { OwnerEntityKey = ownerKey, OwnerId = ownerId };
        await ApplyAsync(entity, values, owner.Owner, isNew: true, cancellationToken);
        db.Credentials.Add(entity);
        return ToData(entity);
    }

    public async Task<RecordData?> UpdateAsync(Guid id, JsonObject values, long? expectedVersion, CancellationToken cancellationToken)
    {
        if (await VisibleForWriteAsync(id, cancellationToken) is not { } found)
        {
            return null;
        }

        var (entity, owner) = found;
        EnsureVersion(entity, expectedVersion);
        var before = ToData(entity).Values;
        await ApplyAsync(entity, values, owner, isNew: false, cancellationToken);
        if (!JsonNode.DeepEquals(before, ToData(entity).Values))
        {
            entity.Touch();
        }

        return ToData(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        if (await VisibleForWriteAsync(id, cancellationToken) is not { } found)
        {
            return false;
        }

        EnsureVersion(found.Entity, expectedVersion);

        // Ekli dosya bağlı kaydı silinince erişilemez kalır (ADR-0040 K2); önce dosyalar silinir ya da belge "İptal edildi" yapılır.
        if (await db.StoredFiles.AnyAsync(f => f.EntityKey == EntityKey && f.RecordId == id, cancellationToken))
        {
            throw new RecordConflictException("Belgenin ekli dosyaları var; önce dosyaları silin ya da belgenin durumunu \"İptal edildi\" yapın.");
        }

        db.Credentials.Remove(found.Entity);
        return true;
    }

    /// <summary>Belgeye dosya eklemek ve silmek (ADR-0040) belgeyi değiştirmekle aynı kuraldır: sahip kayıtta güncelleme izni ve yazılabilir lisans.</summary>
    public async Task<string?> WriteDeniedAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return await VisibleForWriteAsync(id, cancellationToken) is null ? "Kayıt bulunamadı." : null;
        }
        catch (RecordAccessDeniedException denied)
        {
            return denied.Message;
        }
    }

    /// <summary>Okunabilir sahip türündeki belge; yazma izni ya da yazılabilir lisans yoksa yetkisiz.</summary>
    private async Task<(Credential Entity, CredentialOwner Owner)?> VisibleForWriteAsync(Guid id, CancellationToken cancellationToken)
    {
        var readable = await OwnerTypesAsync(RecordOperation.Read, cancellationToken);
        var entity = await db.Credentials.SingleOrDefaultAsync(c => c.Id == id && readable.Contains(c.OwnerEntityKey), cancellationToken);
        if (entity is null || catalog.FindCredentialOwner(entity.OwnerEntityKey) is not { } owner)
        {
            return null;
        }

        await EnsureWritableAsync(owner.ModuleKey, entity.OwnerEntityKey, cancellationToken);
        return (entity, owner.Owner);
    }

    private async Task EnsureWritableAsync(string moduleKey, string ownerKey, CancellationToken cancellationToken)
    {
        if (!await access.CanAsync(ownerKey, RecordOperation.Update, cancellationToken))
        {
            throw new RecordAccessDeniedException("Bu kaydın belgelerini değiştirmek için kayıtta güncelleme izni gerekir.");
        }

        if (tenantAccessor.Current is not { } tenant || !await license.IsWritableAsync(tenant.TenantId, moduleKey, cancellationToken))
        {
            throw new RecordAccessDeniedException("Modül lisansı salt okunur evrede; belge eklenemez, değiştirilemez ya da silinemez.");
        }
    }

    /// <summary>Kullanıcının işlem izni olduğu sahip türleri (modül tenant için açık ve izin rollerinde).</summary>
    private async Task<List<string>> OwnerTypesAsync(RecordOperation operation, CancellationToken cancellationToken)
    {
        var types = new List<string>();
        foreach (var (_, owner) in catalog.CredentialOwners)
        {
            if (OwnerStore(owner.EntityKey) is not null && await access.CanAsync(owner.EntityKey, operation, cancellationToken))
            {
                types.Add(owner.EntityKey);
            }
        }

        return types;
    }

    private IRecordStore? OwnerStore(string ownerKey)
    {
        if (Registry.Find(ownerKey) is not { } store)
        {
            return null;
        }

        if (store.UsesOwnerScope || store.CompanyField is not null)
        {
            throw new InvalidOperationException(
                $"Süreli belge sahibi '{ownerKey}' veri kapsamlı bir kayıt türü; belge deposu sahip kapsamını satır düzeyinde uygulamaz.");
        }

        return store;
    }

    private async Task ApplyAsync(Credential entity, JsonObject values, CredentialOwner owner, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        foreach (var (name, node) in values)
        {
            var value = Parse(name, node).Value;
            switch (name)
            {
                case "ownerEntityKey" or "ownerId":
                    var current = name == "ownerId" ? entity.OwnerId.ToString() : entity.OwnerEntityKey;
                    if (!isNew && value is not null && !string.Equals(value.ToString(), current, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(new RecordFieldError(name, "Oluşturulduktan sonra değiştirilemez."));
                    }

                    break;
                case "typeKey":
                    if (value is not string key)
                    {
                        errors.Add(new RecordFieldError(name, "Bu alan zorunludur."));
                        break;
                    }

                    var choice = (await choiceLists.GetAsync(CredentialEntities.TypeList, cancellationToken)).Find(key);
                    var unchanged = !isNew && choice is not null && entity.TypeKey == choice.Key;
                    if (choice is null || (!choice.Active && !unchanged))
                    {
                        errors.Add(new RecordFieldError(name, choice is null ? "Listede olmayan değer." : "Bu değer artık kullanılmıyor."));
                    }
                    else if (choice.Category != owner.Category)
                    {
                        errors.Add(new RecordFieldError(name, $"Bu belge türü {owner.DisplayName.ToLower(CultureInfo.GetCultureInfo("tr-TR"))} kaydına eklenemez."));
                    }
                    else
                    {
                        entity.TypeKey = choice.Key;
                    }

                    break;
                case "documentNo":
                    entity.DocumentNo = (string?)value;
                    break;
                case "issuedBy":
                    entity.IssuedBy = (string?)value;
                    break;
                case "notes":
                    entity.Notes = (string?)value;
                    break;
                case "validFrom":
                    entity.ValidFrom = (DateOnly?)value;
                    break;
                case "validTo":
                    entity.ValidTo = (DateOnly?)value;
                    break;
                case "status":
                    entity.Status = (string?)value ?? CredentialStatuses.Active;
                    break;
            }
        }

        if (entity.ValidFrom is { } from && entity.ValidTo is { } to && to < from)
        {
            errors.Add(new RecordFieldError("validTo", "Bitiş tarihi başlangıçtan önce olamaz."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        if (entity.DocumentNo is { } no && await db.Credentials.AnyAsync(
                c => c.Id != entity.Id && c.OwnerEntityKey == entity.OwnerEntityKey && c.OwnerId == entity.OwnerId && c.TypeKey == entity.TypeKey && c.DocumentNo == no,
                cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError("documentNo", "Bu kayıtta aynı türde ve numarada bir belge zaten var.")]);
        }
    }

    private static IOrderedQueryable<Credential> Order(IQueryable<Credential> source, string? sort)
    {
        var descending = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "typeKey" => descending ? source.OrderByDescending(c => c.TypeKey) : source.OrderBy(c => c.TypeKey),
            "documentNo" => descending ? source.OrderByDescending(c => c.DocumentNo) : source.OrderBy(c => c.DocumentNo),
            "createdAt" => descending ? source.OrderByDescending(c => c.CreatedAt) : source.OrderBy(c => c.CreatedAt),

            // Varsayılan: bitişi en yakın olan önce; süresiz belgeler sonda.
            _ => descending
                ? source.OrderBy(c => c.ValidTo == null).ThenByDescending(c => c.ValidTo)
                : source.OrderBy(c => c.ValidTo == null).ThenBy(c => c.ValidTo),
        };
    }

    private static bool IsFilter(string name) =>
        name is "ownerEntityKey" or "ownerId" or "typeKey" or "status" or "validOn" or "expiresBy" or "expiredBefore";

    /// <summary>
    /// Süzgeçler: sahip (tür, kimlik), belge türü, durum; <c>validOn</c> o gün geçerli (iptal değil, aralık içinde), <c>expiresBy</c> o güne
    /// kadar bitecek ya da bitmiş geçerli belgeler, <c>expiredBefore</c> o günden önce bitmiş geçerli belgeler. Değer geçersizse null.
    /// </summary>
    private static IQueryable<Credential>? Filter(IQueryable<Credential> source, string name, string value)
    {
        switch (name)
        {
            case "ownerEntityKey":
                return source.Where(c => c.OwnerEntityKey == value);
            case "ownerId":
                return Guid.TryParse(value, out var ownerId) ? source.Where(c => c.OwnerId == ownerId) : null;
            case "typeKey":
                return source.Where(c => c.TypeKey == value);
            case "status":
                return CredentialStatuses.All.Contains(value) ? source.Where(c => c.Status == value) : null;
        }

        if (!IsFilter(name) || Day(value) is not { } day)
        {
            return null;
        }

        var active = source.Where(c => c.Status == CredentialStatuses.Active);
        return name switch
        {
            "validOn" => active.Where(c => (c.ValidFrom == null || c.ValidFrom <= day) && (c.ValidTo == null || c.ValidTo >= day)),
            "expiresBy" => active.Where(c => c.ValidTo != null && c.ValidTo <= day),
            _ => active.Where(c => c.ValidTo != null && c.ValidTo < day),
        };
    }

    private static (object? Value, string? Error) Parse(string name, JsonNode? node)
    {
        if (IsEmpty(node))
        {
            return name is "ownerEntityKey" or "ownerId" or "typeKey" or "status" ? (null, "Bu alan zorunludur.") : (null, null);
        }

        if (node!.GetValueKind() != JsonValueKind.String)
        {
            return (null, "Metin bekleniyor.");
        }

        var text = node.GetValue<string>().Trim();
        return name switch
        {
            "ownerEntityKey" => Text(text, Credential.OwnerEntityKeyMaxLength),
            "ownerId" => Guid.TryParse(text, out var id) && id != Guid.Empty ? (id, null) : (null, "Geçersiz kimlik."),
            "typeKey" => Text(text, Credential.TypeKeyMaxLength),
            "documentNo" => Text(text, Credential.DocumentNoMaxLength),
            "issuedBy" => Text(text, Credential.IssuedByMaxLength),
            "notes" => Text(text, Credential.NotesMaxLength),
            "validFrom" or "validTo" => Day(text) is { } day ? (day, null) : (null, "Tarih yyyy-AA-gg biçiminde olmalıdır."),
            "status" => CredentialStatuses.All.Contains(text) ? (text, null) : (null, "Geçersiz durum."),
            _ => (null, "Bilinmeyen alan."),
        };
    }

    private static (object? Value, string? Error) Text(string text, int maxLength) =>
        text.Length > maxLength ? (null, $"En fazla {maxLength} karakter olabilir.") : (text, null);

    private static bool IsEmpty(JsonNode? node) =>
        node is null || (node.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace(node.GetValue<string>()));

    private static DateOnly? Day(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    private static RecordValidationException Invalid(string field, string message) => new([new RecordFieldError(field, message)]);

    private RecordData ToData(Credential c) => new(EntityKey, c.Id, c.Version, new JsonObject
    {
        ["ownerEntityKey"] = c.OwnerEntityKey,
        ["ownerId"] = c.OwnerId.ToString(),
        ["typeKey"] = c.TypeKey,
        ["documentNo"] = c.DocumentNo,
        ["issuedBy"] = c.IssuedBy,
        ["validFrom"] = c.ValidFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["validTo"] = c.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["status"] = c.Status,
        ["notes"] = c.Notes,
    });

    private static void EnsureVersion(Credential entity, long? expectedVersion)
    {
        if (expectedVersion is { } expected && expected != entity.Version)
        {
            throw new RecordConflictException(
                $"Kayıt siz düzenlerken değişti (beklenen sürüm {expected}, güncel sürüm {entity.Version}). Güncel halini alıp tekrar deneyin.");
        }
    }
}
