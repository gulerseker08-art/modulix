using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Logistics.Domain;

namespace Modulix.Modules.Logistics.Records;

/// <summary>
/// Taşıyıcı anlaşmaları (ADR-0038 K4, dilim 3b). Taşıyıcı yalnızca oluştururken seçilir ve "Taşıyıcı" ilişki rolüne sahip olmalıdır.
/// Bitiş başlangıçtan önce olamaz; anlaşma numarası taşıyıcı içinde benzersizdir; aynı taşıyıcının <b>etkin</b> anlaşmalarının
/// geçerlilik aralıkları çakışamaz (süresiz anlaşma sonsuza uzanır). <c>validOn=yyyy-AA-gg</c> süzgeci o gün geçerli etkin anlaşmaları,
/// <c>expiredBefore</c> o günden önce biten anlaşmaları verir. Düzeltme için silinebilir (K12 anlaşmayı kapsamaz).
/// </summary>
internal sealed partial class CarrierTermsRecordStore(LogisticsStoreContext context) : LogisticsRecordStore<CarrierTerms>(context)
{
    public override string EntityKey => LogisticsModule.Entities.CarrierTerms;

    protected override string ReadPermission => LogisticsModule.Permissions.CarriersRead;

    protected override string WritePermission => LogisticsModule.Permissions.CarriersWrite;

    protected override IReadOnlyList<RecordField<CarrierTerms>> Fields { get; } =
    [
        Field.PartyRef<CarrierTerms>("carrierPartyId", "Taşıyıcı firma bulunamadı.", t => t.CarrierPartyId, (t, v) => t.CarrierPartyId = v!.Value, required: true, createOnly: true),
        Field.Text<CarrierTerms>("title", CarrierTerms.TitleMaxLength, t => t.Title, (t, v) => t.Title = v!, required: true),
        Field.Text<CarrierTerms>("agreementNo", CarrierTerms.AgreementNoMaxLength, t => t.AgreementNo, (t, v) => t.AgreementNo = v, upperCase: true),
        Field.Date<CarrierTerms>("validFrom", t => t.ValidFrom, (t, v) => t.ValidFrom = v!.Value, required: true),
        Field.Date<CarrierTerms>("validTo", t => t.ValidTo, (t, v) => t.ValidTo = v),
        Field.Text<CarrierTerms>("currency", 3, t => t.Currency, (t, v) => t.Currency = v!, required: true, upperCase: true),
        Field.Integer<CarrierTerms>("paymentTermDays", t => t.PaymentTermDays, (t, v) => t.PaymentTermDays = v, 0, CarrierTerms.MaxPaymentTermDays),
        Field.Text<CarrierTerms>("rateNote", CarrierTerms.TextMaxLength, t => t.RateNote, (t, v) => t.RateNote = v),
        Field.Text<CarrierTerms>("performanceNote", CarrierTerms.TextMaxLength, t => t.PerformanceNote, (t, v) => t.PerformanceNote = v),
        Field.Flag<CarrierTerms>("isActive", t => t.IsActive, (t, v) => t.IsActive = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<CarrierTerms>> Filters { get; } = new Dictionary<string, RecordFilter<CarrierTerms>>
    {
        ["carrierPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(t => t.CarrierPartyId == id) : null,
        ["isActive"] = (q, v) => bool.TryParse(v, out var flag) ? q.Where(t => t.IsActive == flag) : null,
        ["validOn"] = (q, v) => FilterValue.Day(v) is { } day
            ? q.Where(t => t.IsActive && t.ValidFrom <= day && (t.ValidTo == null || t.ValidTo >= day))
            : null,
        ["expiredBefore"] = (q, v) => FilterValue.Day(v) is { } day ? q.Where(t => t.ValidTo != null && t.ValidTo < day) : null,
    };

    private IQueryable<Party> Parties => Context.Data.Set<Party>().AsNoTracking();

    // Varsayılan: taşıyıcı adına, sonra en yeni anlaşma üstte.
    protected override IOrderedQueryable<CarrierTerms> Order(IQueryable<CarrierTerms> source) =>
        source.OrderBy(t => Parties.Where(p => p.Id == t.CarrierPartyId).Select(p => p.Name).FirstOrDefault()).ThenByDescending(t => t.ValidFrom);

    protected override IReadOnlyCollection<string>? SortKeys { get; } =
        WithDescending("carrier", "title", "validFrom", "validTo", "paymentTermDays", "isActive", "createdAt");

    protected override IOrderedQueryable<CarrierTerms> Order(IQueryable<CarrierTerms> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "carrier" => By(source, t => Parties.Where(p => p.Id == t.CarrierPartyId).Select(p => p.Name).FirstOrDefault(), desc).ThenByDescending(t => t.ValidFrom),
            "title" => By(source, t => t.Title, desc),
            "validFrom" => By(source, t => t.ValidFrom, desc),
            "validTo" => ByNullsLast(source, t => t.ValidTo, desc),
            "paymentTermDays" => ByNullsLast(source, t => t.PaymentTermDays, desc),
            "isActive" => By(source, t => t.IsActive, desc).ThenByDescending(t => t.ValidFrom),
            "createdAt" => By(source, t => t.CreatedAt, desc),
            _ => Order(source),
        };
    }

    // Taşıyıcının adıyla, anlaşma adıyla ve numarasıyla aranır.
    protected override IQueryable<CarrierTerms> Search(IQueryable<CarrierTerms> source, string term)
    {
        var carriers = Parties.Where(p => p.Name.Contains(term)).Select(p => p.Id);
        return source.Where(t => carriers.Contains(t.CarrierPartyId) || t.Title.Contains(term) || (t.AgreementNo != null && t.AgreementNo.Contains(term)));
    }

    protected override async Task AppliedAsync(CarrierTerms entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();

        if (!CurrencyPattern().IsMatch(entity.Currency))
        {
            errors.Add(new RecordFieldError("currency", "Üç harfli para birimi girin (ör. TRY)."));
        }

        if (entity.ValidTo is { } end && end < entity.ValidFrom)
        {
            errors.Add(new RecordFieldError("validTo", "Bitiş tarihi başlangıç tarihinden önce olamaz."));
        }

        if (isNew)
        {
            var roles = await Parties.Where(p => p.Id == entity.CarrierPartyId).Select(p => p.RelationshipRoles).SingleOrDefaultAsync(cancellationToken);
            if (!(roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains(LogisticsModule.RelationshipRoles.Carrier))
            {
                errors.Add(new RecordFieldError(
                    "carrierPartyId", "Seçilen firma \"Taşıyıcı\" ilişki rolüne sahip değil; önce firma kaydından rolü ekleyin."));
            }
        }

        var siblings = Context.Data.Set<CarrierTerms>().AsNoTracking().Where(t => t.CarrierPartyId == entity.CarrierPartyId && t.Id != entity.Id);
        if (entity.AgreementNo is { } number && await siblings.AnyAsync(t => t.AgreementNo == number, cancellationToken))
        {
            errors.Add(new RecordFieldError("agreementNo", "Bu taşıyıcıda aynı numaralı başka bir anlaşma var."));
        }

        // Aynı taşıyıcının etkin anlaşmaları bir günde tek olmalı: [başlangıç, bitiş] aralıkları kesişemez (boş bitiş = süresiz).
        if (entity.IsActive && errors.Count == 0)
        {
            var from = entity.ValidFrom;
            var to = entity.ValidTo;
            var overlapping = await siblings
                .Where(t => t.IsActive && (to == null || t.ValidFrom <= to) && (t.ValidTo == null || t.ValidTo >= from))
                .Select(t => t.Title)
                .FirstOrDefaultAsync(cancellationToken);
            if (overlapping is not null)
            {
                errors.Add(new RecordFieldError(
                    "validFrom", $"Bu taşıyıcının \"{overlapping}\" anlaşması aynı tarihlerde geçerli; tarihleri değiştirin ya da diğer anlaşmayı pasife alın."));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();
}
