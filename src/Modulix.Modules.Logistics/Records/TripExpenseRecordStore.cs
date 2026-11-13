using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Logistics.Domain;

namespace Modulix.Modules.Logistics.Records;

/// <summary>
/// Sefer masrafı deposu (ADR-0038 K10, dilim 8). Masraf bir sefere bağlıdır (yalnızca oluştururken); iptal edilmiş sefere masraf açılmaz. Tür
/// firmanın listesindendir ve kural türün kategorisine bakar: yakıtta litre zorunludur (kilometre isteğe bağlı), yakıt dışında litre ve kilometre
/// girilmez; sürücüye rücu bayrağı yalnızca ceza kategorisinde işaretlenir. Tutar sıfırdan büyük, para birimi üç harflidir; KDV payı tutarı aşamaz;
/// masraf tarihi ileri bir gün olamaz. Yalnızca beklemedeki masraf düzenlenir (karar verilmiş masraf değişmez: onay tutara bağlıdır); durum
/// yalnızca iş akışı komutuyla (onay, ret) değişir. Masraf silinmez (K12): yanlış masraf reddedilir. Makbuz ve fiş kayda Core dosya deposuyla
/// eklenir (okuma = masraf okuma izni, ekleme = masraf yazma izni).
/// </summary>
internal sealed class TripExpenseRecordStore(LogisticsStoreContext context) : LogisticsRecordStore<TripExpense>(context)
{
    public override string EntityKey => LogisticsModule.Entities.TripExpense;

    protected override string ReadPermission => LogisticsModule.Permissions.ExpensesRead;

    protected override string WritePermission => LogisticsModule.Permissions.ExpensesWrite;

    protected override IReadOnlyList<RecordField<TripExpense>> Fields { get; } =
    [
        Field.Reference<TripExpense, Trip>("tripId", "Sefer bulunamadı.", e => e.TripId, (e, v) => e.TripId = v!.Value, required: true, createOnly: true),
        Field.ListChoice<TripExpense>("kindKey", LogisticsModule.Lists.ExpenseKind, e => e.KindKey, (e, v) => e.KindKey = v ?? string.Empty),
        Field.Text<TripExpense>("description", TripExpense.DescriptionMaxLength, e => e.Description, (e, v) => e.Description = v ?? string.Empty, required: true),
        Field.Date<TripExpense>("expenseDate", e => e.ExpenseDate, (e, v) => e.ExpenseDate = v!.Value, required: true),
        Field.Decimal<TripExpense>("amount", e => e.Amount == 0 ? null : e.Amount, (e, v) => e.Amount = v ?? 0, 2, 0, LogisticsEntity.MaxAmount, required: true),
        MoneyRules.Currency<TripExpense>("currency", e => e.Currency.Length == 0 ? null : e.Currency, (e, v) => e.Currency = v ?? string.Empty),
        MoneyRules.Amount<TripExpense>("vatAmount", e => e.VatAmount, (e, v) => e.VatAmount = v),
        Field.PartyRef<TripExpense>("supplierPartyId", "Tedarikçi bulunamadı.", e => e.SupplierPartyId, (e, v) => e.SupplierPartyId = v),
        Field.Decimal<TripExpense>("liters", e => e.Liters, (e, v) => e.Liters = v, 3, 0, TripExpense.MaxLiters),
        Field.Integer<TripExpense>("odometerKm", e => e.OdometerKm, (e, v) => e.OdometerKm = v, 0, TripExpense.MaxOdometerKm),
        Field.Flag<TripExpense>("chargeToDriver", e => e.ChargeToDriver, (e, v) => e.ChargeToDriver = v),
        // Litre fiyatı (KDV dahil) tutardan ve litreden türetilir; araç yakıt raporunun birim maliyeti buradan okunur.
        Field.Computed<TripExpense>("unitPrice", e => e.Liters is > 0 ? JsonValue.Create(decimal.Round(e.Amount / e.Liters.Value, 4)) : null),
        Field.Computed<TripExpense>("status", e => e.Status),
        Field.Computed<TripExpense>("statusReason", e => e.StatusReason),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<TripExpense>> Filters { get; } = new Dictionary<string, RecordFilter<TripExpense>>
    {
        ["tripId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(e => e.TripId == id) : null,
        ["status"] = (q, v) => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } keys && keys.All(TripExpense.Statuses.All.Contains)
            ? q.Where(e => keys.Contains(e.Status))
            : null,
        ["kindKey"] = (q, v) => ChoiceValueKeys.IsValid(v) ? q.Where(e => e.KindKey == v) : null,
        ["supplierPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(e => e.SupplierPartyId == id) : null,
        ["from"] = (q, v) => FilterValue.Day(v) is { } day ? q.Where(e => e.ExpenseDate >= day) : null,
        ["to"] = (q, v) => FilterValue.Day(v) is { } day ? q.Where(e => e.ExpenseDate <= day) : null,
    };

    // Varsayılan: en yeni masraf önce.
    protected override IOrderedQueryable<TripExpense> Order(IQueryable<TripExpense> source) =>
        source.OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("expenseDate", "amount", "status", "createdAt");

    protected override IOrderedQueryable<TripExpense> Order(IQueryable<TripExpense> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "expenseDate" => By(source, e => e.ExpenseDate, desc).ThenByDescending(e => e.CreatedAt),
            "amount" => By(source, e => e.Amount, desc).ThenByDescending(e => e.ExpenseDate),
            "status" => By(source, e => e.Status, desc).ThenByDescending(e => e.ExpenseDate),
            "createdAt" => By(source, e => e.CreatedAt, desc),
            _ => Order(source),
        };
    }

    protected override IQueryable<TripExpense> Search(IQueryable<TripExpense> source, string term) =>
        source.Where(e => e.Description.Contains(term));

    protected override async Task AppliedAsync(TripExpense entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (Context.Gate.IsOpen)
        {
            if (Context.Gate.Status is { } status)
            {
                entity.Status = status;
                entity.StatusReason = Context.Gate.Reason;
            }
        }
        else if (!isNew && entity.Status != TripExpense.Statuses.Pending)
        {
            throw new RecordConflictException("Karar verilmiş masraf değiştirilemez; hatalıysa yeni masraf girin.");
        }

        if (isNew)
        {
            var tripStatus = await Context.Data.Set<Trip>().AsNoTracking().Where(t => t.Id == entity.TripId).Select(t => t.Status).SingleAsync(cancellationToken);
            if (tripStatus == Trip.Statuses.Cancelled)
            {
                throw new RecordConflictException("İptal edilen sefere masraf eklenmez.");
            }
        }

        entity.Description = entity.Description.Trim();
        entity.Currency = entity.Currency.Trim();
        var errors = new List<RecordFieldError>();

        if (entity.Description.Length == 0)
        {
            errors.Add(new RecordFieldError("description", "Bu alan zorunludur."));
        }

        if (entity.Amount <= 0)
        {
            errors.Add(new RecordFieldError("amount", "Tutar sıfırdan büyük olmalıdır."));
        }

        if (entity.Currency.Length == 0)
        {
            errors.Add(new RecordFieldError("currency", "Para birimi seçilmelidir."));
        }
        else
        {
            MoneyRules.Validate(entity.Amount, entity.Currency, "amount", "currency", errors);
        }

        if (entity.VatAmount is { } vat && vat > entity.Amount)
        {
            errors.Add(new RecordFieldError("vatAmount", "KDV tutarı toplam tutarı aşamaz."));
        }

        if (entity.ExpenseDate > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))
        {
            errors.Add(new RecordFieldError("expenseDate", "Masraf tarihi ileri bir gün olamaz."));
        }

        var category = (await Context.ChoiceLists.GetAsync(LogisticsModule.Lists.ExpenseKind, cancellationToken)).Find(entity.KindKey)?.Category;
        if (category == LogisticsModule.ExpenseKindCategories.Fuel)
        {
            if (entity.Liters is not > 0)
            {
                errors.Add(new RecordFieldError("liters", "Yakıt masrafında litre zorunludur."));
            }
        }
        else
        {
            if (entity.Liters is not null)
            {
                errors.Add(new RecordFieldError("liters", "Litre yalnızca yakıt masrafında girilir."));
            }

            if (entity.OdometerKm is not null)
            {
                errors.Add(new RecordFieldError("odometerKm", "Kilometre yalnızca yakıt masrafında girilir."));
            }
        }

        if (entity.ChargeToDriver && category != LogisticsModule.ExpenseKindCategories.Fine)
        {
            errors.Add(new RecordFieldError("chargeToDriver", "Sürücüye rücu yalnızca trafik cezasında işaretlenir."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override Task BeforeDeleteAsync(TripExpense entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Masraf silinmez; hatalı masrafı reddedin.");
}
