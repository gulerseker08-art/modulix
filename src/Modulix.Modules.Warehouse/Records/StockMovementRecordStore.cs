using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>
/// Stok hareket defteri (ADR-0032 K13). Hareket <b>yalnızca eklenir</b>: güncelleme ve silme reddedilir, düzeltme <c>adjustment</c> kategorisinde bir ters kayıttır.
/// Hareket, hareket kaydıyla <b>aynı işlemde</b> bakiyeyi günceller (boru hattı kaydı, denetimi ve olayı tek seferde yazar): bakiye satırının sürümü
/// eşzamanlılık belirtecidir (aynı bakiyeye eşzamanlı çıkışlardan biri 409 alır), veritabanı kısıtı eksi bakiyeyi ayrıca engeller. Hareketin zamanı, kullanıcısı,
/// üyeliği ve işlem kimliği sunucuca yazılır; istemciden gelen değerler yok sayılır. Aynı <c>idempotencyKey</c> ikinci hareket üretmez (409).
/// </summary>
internal sealed class StockMovementRecordStore(WarehouseStoreContext context) : WarehouseRecordStore<StockMovement>(context)
{
    private static readonly string[] ReversalForbidden = ["lotId", "fromLocationId", "toLocationId", "quantity"];

    public override string EntityKey => WarehouseModule.Entities.StockMovement;

    protected override string ReadPermission => WarehouseModule.Permissions.MovementsRead;

    protected override string WritePermission => WarehouseModule.Permissions.MovementsWrite;

    protected override string? ImmutableMessage =>
        "Stok hareketi değiştirilemez ya da silinemez (defter yalnızca eklenir). Düzeltmek için düzeltme türünde bir ters kayıt oluşturun.";

    protected override IReadOnlyList<RecordField<StockMovement>> Fields { get; } =
    [
        Field.ListChoice<StockMovement>("typeKey", WarehouseModule.Lists.MovementType, m => m.TypeKey, (m, v) => m.TypeKey = v ?? string.Empty),
        Field.Reference<StockMovement, Lot>("lotId", "Parti bulunamadı.", m => m.LotId == Guid.Empty ? null : m.LotId, (m, v) => m.LotId = v ?? Guid.Empty, createOnly: true),
        Field.Reference<StockMovement, Location>("fromLocationId", "Kaynak lokasyon bulunamadı.", m => m.FromLocationId, (m, v) => m.FromLocationId = v, createOnly: true),
        Field.Reference<StockMovement, Location>("toLocationId", "Hedef lokasyon bulunamadı.", m => m.ToLocationId, (m, v) => m.ToLocationId = v, createOnly: true),
        Field.Decimal<StockMovement>("quantity", m => m.Quantity == 0 ? null : m.Quantity, (m, v) => m.Quantity = v ?? 0m, scale: 3, min: 0.001m, max: 999_999_999_999m),
        Field.ListChoice<StockMovement>("reasonKey", WarehouseModule.Lists.MovementReason, m => m.ReasonKey, (m, v) => m.ReasonKey = v, required: false),
        Field.Text<StockMovement>("documentReference", StockMovement.DocumentMaxLength, m => m.DocumentReference, (m, v) => m.DocumentReference = v),
        Field.Text<StockMovement>("idempotencyKey", StockMovement.IdempotencyKeyMaxLength, m => m.IdempotencyKey, (m, v) => m.IdempotencyKey = v),
        Field.Reference<StockMovement, StockMovement>("reversesMovementId", "Geri alınacak hareket bulunamadı.", m => m.ReversesMovementId, (m, v) => m.ReversesMovementId = v, createOnly: true),
        Field.Computed<StockMovement>("occurredAt", m => m.OccurredAt == default ? null : m.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
        Field.Computed<StockMovement>("userId", m => m.UserId == Guid.Empty ? null : m.UserId.ToString()),
        Field.Computed<StockMovement>("membershipId", m => m.MembershipId == Guid.Empty ? null : m.MembershipId.ToString()),
        Field.Computed<StockMovement>("correlationId", m => m.CorrelationId == Guid.Empty ? null : m.CorrelationId.ToString()),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<StockMovement>> Filters { get; } = new Dictionary<string, RecordFilter<StockMovement>>
    {
        ["lotId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(m => m.LotId == id) : null,
        // Lokasyon süzgeci hareketin kaynağı ya da hedefi olduğu kayıtları getirir.
        ["locationId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(m => m.FromLocationId == id || m.ToLocationId == id) : null,
        ["typeKey"] = (q, v) => ChoiceValueKeys.IsValid(v) ? q.Where(m => m.TypeKey == v) : null,
        ["reversesMovementId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(m => m.ReversesMovementId == id) : null,
        ["correlationId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(m => m.CorrelationId == id) : null,
        // Zaman aralığı (yyyy-AA-gg, uçlar dahil; UTC gün sınırları).
        ["occurredFrom"] = (q, v) => ParseDay(v) is { } day ? q.Where(m => m.OccurredAt >= new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)) : null,
        ["occurredTo"] = (q, v) => ParseDay(v) is { } day ? q.Where(m => m.OccurredAt < new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)) : null,
    };

    protected override IOrderedQueryable<StockMovement> Order(IQueryable<StockMovement> source) => source.OrderByDescending(m => m.OccurredAt);

    protected override IQueryable<StockMovement> Search(IQueryable<StockMovement> source, string term) =>
        source.Where(m => (m.DocumentReference != null && m.DocumentReference.Contains(term)) || (m.IdempotencyKey != null && m.IdempotencyKey.Contains(term)));

    protected override async Task AppliedAsync(StockMovement entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        // Güncelleme zaten reddedilir (ImmutableMessage); buraya yalnızca oluşturma gelir.
        var errors = new List<RecordFieldError>();
        if (!Supplied(values, "typeKey"))
        {
            // Hareket türü sessizce listenin ilk değerine düşmez: stok etkisini belirler, açıkça seçilir.
            errors.Add(new RecordFieldError("typeKey", "Bu alan zorunludur."));
            throw new RecordValidationException(errors);
        }

        // Tekrarlanan istek (barkod/terminal): aynı anahtarla yazılmış hareket varsa ikinci hareket üretilmez.
        if (entity.IdempotencyKey is { } key)
        {
            var existing = await Context.Data.Set<StockMovement>().AsNoTracking()
                .Where(m => m.IdempotencyKey == key).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(cancellationToken);
            if (existing is { } existingId)
            {
                throw new RecordConflictException($"Bu anahtarla bir hareket zaten işlendi (hareket: {existingId}); tekrar kaydedilmedi.");
            }
        }

        var category = (await Context.ChoiceLists.GetAsync(WarehouseModule.Lists.MovementType, cancellationToken)).Find(entity.TypeKey)?.Category;
        if (category is null)
        {
            throw new RecordValidationException([new RecordFieldError("typeKey", "Hareket türünün kategorisi tanımlı değil.")]);
        }

        var tenant = Context.Tenant.Current ?? throw new InvalidOperationException("Hareket için doğrulanmış tenant bağlamı yok.");
        entity.UserId = tenant.UserId;
        entity.MembershipId = tenant.MembershipId;
        entity.CorrelationId = Guid.CreateVersion7();
        entity.OccurredAt = DateTimeOffset.UtcNow;

        if (category == MovementCategories.Adjustment)
        {
            await ResolveReversalAsync(entity, values, errors, cancellationToken);
        }
        else
        {
            await ValidateDirectAsync(entity, category, errors, cancellationToken);
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        await ApplyBalancesAsync(entity, errors, cancellationToken);
        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    /// <summary>Düzeltme: özgün hareketin partisini, miktarını ve ters yöndeki lokasyonlarını alır; kendi başına parti/lokasyon/miktar verilmez.</summary>
    private async Task ResolveReversalAsync(StockMovement entity, JsonObject values, List<RecordFieldError> errors, CancellationToken cancellationToken)
    {
        errors.AddRange(ReversalForbidden.Where(name => Supplied(values, name))
            .Select(name => new RecordFieldError(name, "Düzeltme hareketinde bu bilgi özgün hareketten gelir; verilmez.")));
        if (entity.ReversesMovementId is not { } originalId)
        {
            errors.Add(new RecordFieldError("reversesMovementId", "Düzeltme hareketi hangi hareketi geri aldığını belirtmelidir."));
            return;
        }

        if (errors.Count > 0)
        {
            return;
        }

        var original = await Context.Data.Set<StockMovement>().AsNoTracking().SingleAsync(m => m.Id == originalId, cancellationToken);
        var categories = await Context.ChoiceLists.GetAsync(WarehouseModule.Lists.MovementType, cancellationToken);
        if (categories.Find(original.TypeKey)?.Category == MovementCategories.Adjustment || original.ReversesMovementId is not null)
        {
            errors.Add(new RecordFieldError("reversesMovementId", "Bir düzeltme hareketi geri alınamaz; gerekirse yeni bir hareket kaydedin."));
        }
        else if (await Context.Data.Set<StockMovement>().AsNoTracking().AnyAsync(m => m.ReversesMovementId == originalId, cancellationToken))
        {
            errors.Add(new RecordFieldError("reversesMovementId", "Bu hareket zaten geri alınmış."));
        }

        if (errors.Count > 0)
        {
            return;
        }

        entity.LotId = original.LotId;
        entity.Quantity = original.Quantity;
        (entity.FromLocationId, entity.ToLocationId) = LedgerRules.Reverse(original.FromLocationId, original.ToLocationId);
    }

    /// <summary>Giriş, çıkış ve transfer: parti ve miktar zorunlu, lokasyon biçimi kategoriye göre, parti ve hedef lokasyon etkin olmalı.</summary>
    private async Task ValidateDirectAsync(StockMovement entity, string category, List<RecordFieldError> errors, CancellationToken cancellationToken)
    {
        if (entity.ReversesMovementId is not null)
        {
            errors.Add(new RecordFieldError("reversesMovementId", "Yalnızca düzeltme türündeki hareket başka bir hareketi geri alabilir."));
        }

        if (entity.LotId == Guid.Empty)
        {
            errors.Add(new RecordFieldError("lotId", "Bu alan zorunludur."));
        }

        if (entity.Quantity <= 0)
        {
            errors.Add(new RecordFieldError("quantity", "Miktar zorunludur ve sıfırdan büyük olmalıdır."));
        }

        if (LedgerRules.CheckShape(category, entity.FromLocationId, entity.ToLocationId) is { } shape)
        {
            errors.Add(new RecordFieldError(shape.Contains("hedef", StringComparison.Ordinal) ? "toLocationId" : "fromLocationId", shape));
        }

        if (errors.Count > 0)
        {
            return;
        }

        if (!await Context.Data.Set<Lot>().AsNoTracking().Where(l => l.Id == entity.LotId).Select(l => l.IsActive).SingleAsync(cancellationToken))
        {
            errors.Add(new RecordFieldError("lotId", "Pasif parti yeni hareket alamaz."));
        }

        // Kaynak lokasyon pasife alınmış olsa da stok oradan çıkarılabilir; hedef lokasyon etkin olmalıdır.
        if (entity.ToLocationId is { } target
            && !await Context.Data.Set<Location>().AsNoTracking().Where(l => l.Id == target).Select(l => l.IsActive).SingleAsync(cancellationToken))
        {
            errors.Add(new RecordFieldError("toLocationId", "Pasif lokasyona stok girişi yapılamaz."));
        }
    }

    /// <summary>
    /// Bakiyeye etkiyi uygular. Önce hepsi denetlenir (yetersiz stok), sonra değiştirilir; denetim başarısızsa izlenen bakiyelerde değişiklik kalmaz. Var olan satırın
    /// sürümü artar (eşzamanlılık belirteci), yeni satır eklenir.
    /// </summary>
    private async Task ApplyBalancesAsync(StockMovement entity, List<RecordFieldError> errors, CancellationToken cancellationToken)
    {
        var planned = new List<(StockBalance Balance, bool IsNew, decimal Delta)>();
        foreach (var effect in LedgerRules.Effects(entity.FromLocationId, entity.ToLocationId, entity.Quantity))
        {
            var balance = await Context.Data.Set<StockBalance>().SingleOrDefaultAsync(b => b.LotId == entity.LotId && b.LocationId == effect.LocationId, cancellationToken);
            var current = balance?.Quantity ?? 0m;
            if (current + effect.Delta < 0)
            {
                errors.Add(new RecordFieldError(
                    "quantity",
                    $"Yetersiz stok: lokasyondaki bakiye {current.ToString("0.###", CultureInfo.InvariantCulture)}, istenen {Math.Abs(effect.Delta).ToString("0.###", CultureInfo.InvariantCulture)}."));
                continue;
            }

            planned.Add((balance ?? new StockBalance { LotId = entity.LotId, LocationId = effect.LocationId }, balance is null, effect.Delta));
        }

        if (errors.Count > 0)
        {
            return;
        }

        foreach (var (balance, isNew, delta) in planned)
        {
            balance.Quantity += delta;
            if (isNew)
            {
                Context.Data.Set<StockBalance>().Add(balance);
            }
            else
            {
                balance.Touch();
            }
        }
    }

    private static bool Supplied(JsonObject values, string name) =>
        values.TryGetPropertyValue(name, out var node) && node is not null && !(node is JsonValue v && string.IsNullOrWhiteSpace(v.ToString()));

    private static DateOnly? ParseDay(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
}
