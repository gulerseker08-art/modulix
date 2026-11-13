using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Logistics.Domain;

namespace Modulix.Modules.Logistics.Records;

/// <summary>
/// Sefer masrafı deposu (ADR-0038 K10, dilim 8). Masraf iptal edilmemiş bir sefere yazılır; masraf türü firmanın listesindendir ve kod yalnızca
/// <b>kategoriye</b> bakar: yakıt kategorisinde litre zorunludur (kilometre isteğe bağlı), diğer kategorilerde litre ve kilometre girilmez;
/// sürücüye rücu bayrağı yalnızca ceza kategorisinde işaretlenir (bilgidir, borç kaydı üretmez). Tutar 0'dan büyüktür, KDV tutarı aşamaz,
/// gün ileri tarih olamaz. Durum ve ret gerekçesi yalnızca iş akışı komutuyla yazılır (<see cref="Lifecycle.ILogisticsWorkflow"/>); onaylı masraf
/// değişmez, reddedilen düzeltilip yeniden beklemeye alınır. Silinmez (K12).
/// </summary>
internal sealed class TripExpenseRecordStore(LogisticsStoreContext context) : LogisticsRecordStore<TripExpense>(context)
{
    public override string EntityKey => LogisticsModule.Entities.TripExpense;

    protected override string ReadPermission => LogisticsModule.Permissions.ExpensesRead;

    protected override string WritePermission => LogisticsModule.Permissions.ExpensesWrite;

    protected override IReadOnlyList<RecordField<TripExpense>> Fields { get; } =
    [
        Field.Reference<TripExpense, Trip>("tripId", "Sefer bulunamadı.", e => e.TripId, (e, v) => e.TripId = v!.Value, required: true, createOnly: true),
        Field.ListChoice<TripExpense>("expenseTypeKey", LogisticsModule.Lists.ExpenseType, e => e.ExpenseTypeKey, (e, v) => e.ExpenseTypeKey = v ?? string.Empty),
        Field.Date<TripExpense>("expenseDate", e => e.ExpenseDate == default ? null : e.ExpenseDate, (e, v) => e.ExpenseDate = v!.Value, required: true),
        Field.Decimal<TripExpense>("amount", e => e.Amount == 0 ? null : e.Amount, (e, v) => e.Amount = v ?? 0, 2, 0, LogisticsEntity.MaxAmount, required: true),
        Field.Text<TripExpense>("currency", LogisticsEntity.CurrencyLength, e => e.Currency.Length == 0 ? null : e.Currency, (e, v) => e.Currency = v ?? string.Empty, required: true, upperCase: true),
        MoneyRules.Amount<TripExpense>("vatAmount", e => e.VatAmount, (e, v) => e.VatAmount = v),
        Field.PartyRef<TripExpense>("supplierPartyId", "Tedarikçi firma bulunamadı.", e => e.SupplierPartyId, (e, v) => e.SupplierPartyId = v),
        Field.Text<TripExpense>("documentNo", TripExpense.DocumentNoMaxLength, e => e.DocumentNo, (e, v) => e.DocumentNo = v),
        Field.Decimal<TripExpense>("liters", e => e.Liters, (e, v) => e.Liters = v, 3, 0, TripExpense.MaxLiters),
        Field.Integer<TripExpense>("odometerKm", e => e.OdometerKm, (e, v) => e.OdometerKm = v, 0, TripExpense.MaxOdometerKm),
        Field.Flag<TripExpense>("recoverFromDriver", e => e.RecoverFromDriver, (e, v) => e.RecoverFromDriver = v),
        Field.Text<TripExpense>("notes", TripExpense.TextMaxLength, e => e.Notes, (e, v) => e.Notes = v),
        // Onay talebinde ve listelerde kaydı tanıtan kısa metin (tür anahtarı, gün, tutar).
        Field.Computed<TripExpense>("label", e => $"{e.ExpenseTypeKey} · {e.ExpenseDate.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture)} · {e.Amount.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)} {e.Currency}"),
        Field.Computed<TripExpense>("status", e => e.Status),
        Field.Computed<TripExpense>("statusReason", e => e.StatusReason),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<TripExpense>> Filters { get; } = new Dictionary<string, RecordFilter<TripExpense>>
    {
        ["tripId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(e => e.TripId == id) : null,
        ["status"] = (q, v) => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } keys && keys.All(TripExpense.Statuses.All.Contains)
            ? q.Where(e => keys.Contains(e.Status))
            : null,
        ["expenseTypeKey"] = (q, v) => ChoiceValueKeysValid(v) ? q.Where(e => e.ExpenseTypeKey == v) : null,
        ["supplierPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(e => e.SupplierPartyId == id) : null,
        ["dateFrom"] = (q, v) => FilterValue.Day(v) is { } day ? q.Where(e => e.ExpenseDate >= day) : null,
        ["dateTo"] = (q, v) => FilterValue.Day(v) is { } day ? q.Where(e => e.ExpenseDate <= day) : null,
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
            "amount" => By(source, e => e.Amount, desc),
            "status" => By(source, e => e.Status, desc).ThenByDescending(e => e.ExpenseDate),
            "createdAt" => By(source, e => e.CreatedAt, desc),
            _ => Order(source),
        };
    }

    protected override IQueryable<TripExpense> Search(IQueryable<TripExpense> source, string term) =>
        source.Where(e => (e.DocumentNo != null && e.DocumentNo.Contains(term)) || (e.Notes != null && e.Notes.Contains(term)));

    protected override async Task AppliedAsync(TripExpense entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (Context.Gate.IsOpen)
        {
            // Karar komutu (onayla, reddet, yeniden bekletme): yalnızca durum ve gerekçe yazılır; içerik doğrulaması kayıt değişmediği için yinelenmez.
            if (Context.Gate.Status is { } status)
            {
                entity.Status = status;
                entity.StatusReason = Context.Gate.Reason;
            }

            return;
        }

        if (!isNew && entity.Status == TripExpense.Statuses.Approved)
        {
            throw new RecordConflictException("Onaylanan masraf değiştirilemez.");
        }

        var errors = new List<RecordFieldError>();
        if (isNew && await Context.Data.Set<Trip>().AsNoTracking().Where(t => t.Id == entity.TripId).Select(t => t.Status).SingleAsync(cancellationToken) == Trip.Statuses.Cancelled)
        {
            throw new RecordConflictException("İptal edilen sefere masraf eklenemez.");
        }

        entity.DocumentNo = Trim(entity.DocumentNo);
        entity.Notes = Trim(entity.Notes);

        if (entity.Amount <= 0)
        {
            errors.Add(new RecordFieldError("amount", "Tutar 0'dan büyük olmalıdır."));
        }

        MoneyRules.Validate(entity.Amount > 0 ? entity.Amount : null, entity.Currency.Length == 0 ? null : entity.Currency, "amount", "currency", errors);
        if (entity.VatAmount is { } vat && entity.Amount > 0 && vat > entity.Amount)
        {
            errors.Add(new RecordFieldError("vatAmount", "KDV tutarı toplam tutarı aşamaz."));
        }

        if (entity.ExpenseDate > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
        {
            errors.Add(new RecordFieldError("expenseDate", "Masraf günü ileri bir tarih olamaz."));
        }

        var category = (await Context.ChoiceLists.GetAsync(LogisticsModule.Lists.ExpenseType, cancellationToken)).Find(entity.ExpenseTypeKey)?.Category;
        if (category == LogisticsModule.ExpenseCategories.Fuel)
        {
            if (entity.Liters is not > 0)
            {
                errors.Add(new RecordFieldError("liters", "Yakıt masrafında alınan litre zorunludur."));
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
                errors.Add(new RecordFieldError("odometerKm", "Kilometre sayacı yalnızca yakıt masrafında girilir."));
            }
        }

        if (entity.RecoverFromDriver && category != LogisticsModule.ExpenseCategories.Fine)
        {
            errors.Add(new RecordFieldError("recoverFromDriver", "Sürücüye rücu yalnızca ceza masrafında işaretlenir."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override Task BeforeDeleteAsync(TripExpense entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Masraf silinmez; yanlış girildiyse reddedin ya da düzeltin.");

    private static bool ChoiceValueKeysValid(string value) => Core.Contracts.Customization.ChoiceValueKeys.IsValid(value);

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
