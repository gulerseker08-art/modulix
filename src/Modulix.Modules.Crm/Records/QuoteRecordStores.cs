using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Pricing;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Pricing;
using Modulix.Modules.Crm.Quotes;

namespace Modulix.Modules.Crm.Records;

/// <summary>Tarihçe satırı yazımı (kayıtla aynı işlemde). Tarihçe eklenir, değiştirilmez.</summary>
internal static class QuoteHistory
{
    public static void Add(CrmStoreContext context, Quote quote, string kind, string? from, string? to, string? reason) =>
        context.Data.Set<QuoteEvent>().Add(new QuoteEvent
        {
            QuoteId = quote.Id,
            Kind = kind,
            ActorId = context.CurrentUserId,
            FromStatus = from,
            ToStatus = to,
            Reason = reason,
        });
}

/// <summary>
/// Teklif başlığı kayıt deposu (ADR-0019). Alan düzenlemesi <c>crm.quotes.write</c> ile yalnızca taslakta yapılır; durum, onay,
/// gönderim ve toplam alanları iş akışı kapısı (<see cref="QuoteWorkflowGate"/>) açıkken yazılır. Toplamlar her zaman sunucuda
/// <see cref="QuoteCalculator"/> ile hesaplanır; istemci tutarı yok sayılır. Ticari alan değişikliği önceki onayı geçersiz kılar.
/// </summary>
internal sealed class QuoteRecordStore(CrmStoreContext context, QuoteWorkflowGate gate) : CrmRecordStore<Quote>(context), IRecordStore
{
    private static readonly string[] CommercialFields =
    [
        "companyId", "contactId", "currency", "priceListId", "priceIncludesTax", "documentDiscountKind", "documentDiscountValue", "validUntil",
        "paymentTerms", "deliveryTerms", "commercialTerms", "title", "description", "reportingCurrency", "exchangeRate", "exchangeRateDate",
    ];

    private bool creating;
    private Quote? copySource;
    private string? copyMode;

    public override string EntityKey => CrmModule.Entities.Quote;

    protected override string ReadPermission => CrmModule.Permissions.QuotesRead;

    protected override string WritePermission => CrmModule.Permissions.QuotesWrite;

    protected override bool GateOpen => gate.IsOpen;

    /// <summary>Kapı açıkken güncelleme eylemin izniyle yapılır (ör. onaylayan yazma iznine sahip olmak zorunda değildir).</summary>
    IReadOnlyList<string>? IRecordStore.AnyPermissionFor(RecordOperation operation) =>
        operation == RecordOperation.Update && gate.Permissions is { } permissions ? permissions : null;

    private static readonly string[] DiscountKinds = ["None", "Percent", "Amount"];

    protected override IReadOnlyList<RecordField<Quote>> Fields { get; } =
    [
        Field.Text<Quote>("quoteNumber", Quote.NumberMaxLength, q => q.QuoteNumber, (q, v) => q.QuoteNumber = v!).AsSystem(),
        Field.Id<Quote>("familyId", q => q.FamilyId == Guid.Empty ? null : q.FamilyId, (_, _) => { }).AsSystem(),
        Field.Count<Quote>("revisionNo", q => q.RevisionNo, (q, v) => q.RevisionNo = v).AsSystem(),
        Field.CreateOnlyId<Quote>("revisionOfId", _ => null),
        Field.CreateOnlyId<Quote>("duplicateOfId", _ => null),
        Field.Text<Quote>("title", Quote.TitleMaxLength, q => q.Title, (q, v) => q.Title = v!, required: true),
        Field.Text<Quote>("description", Quote.TextMaxLength, q => q.Description, (q, v) => q.Description = v),
        Field.Reference<Quote, Company>("companyId", "Firma bulunamadı.", q => q.CompanyId, (q, v) => q.CompanyId = v!.Value, required: true),
        Field.Reference<Quote, Contact>("contactId", "Kişi bulunamadı.", q => q.ContactId, (q, v) => q.ContactId = v),
        Field.Reference<Quote, Opportunity>("opportunityId", "Fırsat bulunamadı.", q => q.OpportunityId, (q, v) => q.OpportunityId = v),
        Field.Reference<Quote, PriceList>("priceListId", "Fiyat listesi bulunamadı.", q => q.PriceListId, (q, v) => q.PriceListId = v),
        Field.Owner<Quote>(OwnerField, q => q.OwnerId, (q, v) => q.OwnerId = v),
        Field.Text<Quote>("status", Quote.StatusMaxLength, q => q.Status, (q, v) => q.Status = v!).AsSystem(),
        Field.Text<Quote>("approvalStatus", Quote.StatusMaxLength, q => q.ApprovalStatus, (q, v) => q.ApprovalStatus = v!).AsSystem(),
        Field.Text<Quote>("deliveryStatus", Quote.StatusMaxLength, q => q.DeliveryStatus, (q, v) => q.DeliveryStatus = v!).AsSystem(),
        Field.Text<Quote>("statusReason", Quote.ReasonMaxLength, q => q.StatusReason, (q, v) => q.StatusReason = v).AsSystem(),
        Field.Date<Quote>("issueDate", q => q.IssueDate == default ? null : q.IssueDate, (q, v) => { if (v is { } d) { q.IssueDate = d; } }),
        Field.Time<Quote>("validUntil", q => q.ValidUntil, (q, v) => q.ValidUntil = v),
        Field.Currency<Quote>("currency", q => q.Currency, (q, v) => q.Currency = v),
        Field.OptionalCurrency<Quote>("reportingCurrency", q => q.ReportingCurrency, (q, v) => q.ReportingCurrency = v),
        Field.Decimal<Quote>("exchangeRate", q => q.ExchangeRate, (q, v) => q.ExchangeRate = v),
        Field.Date<Quote>("exchangeRateDate", q => q.ExchangeRateDate, (q, v) => q.ExchangeRateDate = v),
        Field.Flag<Quote>("priceIncludesTax", q => q.PriceIncludesTax, (q, v) => q.PriceIncludesTax = v),
        Field.Choice<Quote>("documentDiscountKind", DiscountKinds, q => q.DocumentDiscountKind, (q, v) => q.DocumentDiscountKind = v),
        Field.Money<Quote>("documentDiscountValue", q => q.DocumentDiscountValue, (q, v) => q.DocumentDiscountValue = v ?? 0m),
        Field.Text<Quote>("paymentTerms", Quote.TextMaxLength, q => q.PaymentTerms, (q, v) => q.PaymentTerms = v),
        Field.Text<Quote>("deliveryTerms", Quote.TextMaxLength, q => q.DeliveryTerms, (q, v) => q.DeliveryTerms = v),
        Field.Text<Quote>("commercialTerms", Quote.TextMaxLength, q => q.CommercialTerms, (q, v) => q.CommercialTerms = v),
        Field.Text<Quote>("internalNotes", Quote.TextMaxLength, q => q.InternalNotes, (q, v) => q.InternalNotes = v),
        Field.Money<Quote>("listTotal", q => q.ListTotal, (q, v) => q.ListTotal = v ?? 0m).AsSystem(),
        Field.Money<Quote>("discountTotal", q => q.DiscountTotal, (q, v) => q.DiscountTotal = v ?? 0m).AsSystem(),
        Field.Money<Quote>("netTotal", q => q.NetTotal, (q, v) => q.NetTotal = v ?? 0m).AsSystem(),
        Field.Money<Quote>("taxTotal", q => q.TaxTotal, (q, v) => q.TaxTotal = v ?? 0m).AsSystem(),
        Field.Money<Quote>("grossTotal", q => q.GrossTotal, (q, v) => q.GrossTotal = v ?? 0m).AsSystem(),
        Field.Money<Quote>("optionalNet", q => q.OptionalNet, (q, v) => q.OptionalNet = v ?? 0m).AsSystem(),
        Field.Text<Quote>("recurringSummary", 2000, q => q.RecurringSummary, (q, v) => q.RecurringSummary = v).AsSystem(),
        Field.Time<Quote>("calculatedAt", q => q.CalculatedAt, (q, v) => q.CalculatedAt = v).AsSystem(),
        Field.Time<Quote>("presentedAt", q => q.PresentedAt, (q, v) => q.PresentedAt = v).AsSystem(),
        Field.Time<Quote>("decidedAt", q => q.DecidedAt, (q, v) => q.DecidedAt = v).AsSystem(),
        Field.Text<Quote>("customerNameSnapshot", 300, q => q.CustomerNameSnapshot, (q, v) => q.CustomerNameSnapshot = v).AsSystem(),
        Field.CodeNameList<Quote>("serviceScopeSnapshot", Quote.ServiceScopeSnapshotMaxLength, q => q.ServiceScopeSnapshot, (q, v) => q.ServiceScopeSnapshot = v).AsSystem(),
        Field.Id<Quote>("createdBy", q => q.CreatedBy, (q, v) => q.CreatedBy = v).AsSystem(),
        Field.Id<Quote>("updatedBy", q => q.UpdatedBy, (q, v) => q.UpdatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Quote>> Filters { get; } = new Dictionary<string, RecordFilter<Quote>>
    {
        ["companyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.CompanyId == id) : null,
        ["contactId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.ContactId == id) : null,
        ["opportunityId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.OpportunityId == id) : null,
        ["familyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(x => x.FamilyId == id) : null,
        [OwnerField] = (q, v, ctx) => FilterValue.User(v, ctx) is { } owner ? q.Where(x => x.OwnerId == owner) : null,
        ["number"] = (q, v, _) => v.Trim() is { Length: > 0 } number ? q.Where(x => x.QuoteNumber == number) : null,
        ["status"] = (q, v, _) => QuoteStatus.All.Contains(v) ? q.Where(x => x.Status == v) : null,
        ["approvalStatus"] = (q, v, _) => QuoteApprovalStatus.All.Contains(v) ? q.Where(x => x.ApprovalStatus == v) : null,
        ["deliveryStatus"] = (q, v, _) => QuoteDeliveryStatus.All.Contains(v) ? q.Where(x => x.DeliveryStatus == v) : null,
        ["currency"] = (q, v, _) => v.Trim().ToUpperInvariant() is { Length: Quote.CurrencyLength } code ? q.Where(x => x.Currency == code) : null,
        ["validFrom"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(x => x.ValidUntil >= new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)) : null,
        ["validTo"] = (q, v, _) => FilterValue.Date(v) is { } d
            ? q.Where(x => x.ValidUntil < new DateTimeOffset(d.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
            : null,
        // Rapor dönemleri (UTC gün sınırları): sunum ve karar tarihi.
        ["presentedFrom"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(x => x.PresentedAt >= new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)) : null,
        ["presentedTo"] = (q, v, _) => FilterValue.Date(v) is { } d
            ? q.Where(x => x.PresentedAt < new DateTimeOffset(d.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
            : null,
        ["decidedFrom"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(x => x.DecidedAt >= new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)) : null,
        ["decidedTo"] = (q, v, _) => FilterValue.Date(v) is { } d
            ? q.Where(x => x.DecidedAt < new DateTimeOffset(d.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
            : null,
        // Aile bazlı görünüm: her ailenin yalnızca son revizyonu (revizyonlar ayrı satış gibi görünmez).
        ["latestOnly"] = (q, v, ctx) => FilterValue.Bool(v) is true
            ? q.Where(x => x.RevisionNo == ctx.Data.Set<Quote>().Where(y => y.FamilyId == x.FamilyId).Max(y => y.RevisionNo))
            : null,
        // Süresi dolmuş ama hâlâ sunulmuş durumdaki teklifler (zamanlanmış iş henüz işlemediyse bile listede doğru görünür).
        ["expiredOpen"] = (q, v, _) => FilterValue.Bool(v) is true
            ? q.Where(x => x.Status == QuoteStatus.Presented && x.ValidUntil != null && x.ValidUntil <= DateTimeOffset.UtcNow)
            : null,
    };

    protected override IOrderedQueryable<Quote> Order(IQueryable<Quote> source) =>
        source.OrderByDescending(q => q.CreatedAt).ThenByDescending(q => q.RevisionNo);

    protected override IQueryable<Quote> Search(IQueryable<Quote> source, string term) =>
        source.Where(q => q.Title.Contains(term) || q.QuoteNumber.Contains(term) || (q.Description != null && q.Description.Contains(term)));

    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(Quote entity, JsonObject values, CancellationToken cancellationToken)
    {
        creating = true;
        var errors = new List<RecordFieldError>();
        var revisionOf = FieldNamed("revisionOfId").Parse(values["revisionOfId"]).Value as Guid?;
        var duplicateOf = FieldNamed("duplicateOfId").Parse(values["duplicateOfId"]).Value as Guid?;
        if (revisionOf is not null && duplicateOf is not null)
        {
            throw new RecordValidationException([new RecordFieldError("revisionOfId", "Revizyon ve çoğaltma birlikte verilemez.")]);
        }

        if ((revisionOf ?? duplicateOf) is { } sourceId)
        {
            var name = revisionOf is not null ? "revisionOfId" : "duplicateOfId";
            if (await CanSeeAsync(sourceId, cancellationToken) is not { } source)
            {
                throw new RecordValidationException([new RecordFieldError(name, "Teklif bulunamadı.")]);
            }

            if (revisionOf is not null)
            {
                var latest = await Context.Data.Set<Quote>().Where(q => q.FamilyId == source.FamilyId).MaxAsync(q => q.RevisionNo, cancellationToken);
                if (QuoteRevisionRules.CanRevise(source.Status) is { } blocked)
                {
                    errors.Add(new RecordFieldError(name, blocked));
                }
                else if (source.RevisionNo != latest)
                {
                    errors.Add(new RecordFieldError(name, "Yalnızca ailenin son revizyonundan yeni revizyon açılır."));
                }
                else if (await Context.Data.Set<Quote>().AnyAsync(q => q.FamilyId == source.FamilyId && q.Status == QuoteStatus.Draft, cancellationToken))
                {
                    errors.Add(new RecordFieldError(name, "Bu ailede zaten açık bir taslak revizyon var."));
                }
                else
                {
                    entity.FamilyId = source.FamilyId;
                    entity.RevisionNo = latest + 1;
                    entity.QuoteNumber = source.QuoteNumber;
                }
            }

            copySource = source;
            copyMode = name;
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        return (values, false);
    }

    protected override void OnCreated(Quote entity)
    {
        if (entity.FamilyId == Guid.Empty)
        {
            entity.FamilyId = entity.Id;
            entity.RevisionNo = 1;
            entity.QuoteNumber = QuoteRevisionRules.NewNumber(entity.CreatedAt);
        }

        if (entity.IssueDate == default)
        {
            entity.IssueDate = DateOnly.FromDateTime(DateTime.UtcNow);
        }

        entity.CreatedBy = Context.CurrentUserId;
        entity.UpdatedBy = Context.CurrentUserId;
        QuoteHistory.Add(Context, entity, copyMode == "revisionOfId" ? "Revised" : "Created", null, QuoteStatus.Draft, null);
    }

    protected override async Task AppliedAsync(Quote entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var workflow = gate.IsOpen;
        var isNew = creating;
        creating = false;
        var original = isNew
            ? null
            : await Context.Data.Set<Quote>().AsNoTracking().Where(q => q.Id == entity.Id)
                .Select(q => new { q.Status, q.ApprovalStatus, q.DocumentDiscountKind, q.DocumentDiscountValue }).SingleOrDefaultAsync(cancellationToken);
        var commercialTouched = values.Any(kv => CommercialFields.Contains(kv.Key));

        // Sunulmuş ya da sonuçlanmış sürümün ticari içeriği yerinde değişmez; değişiklik için yeni revizyon açılır.
        if (!workflow && !isNew && entity.Status != QuoteStatus.Draft && values.Any(kv => !IsNonCommercial(kv.Key)))
        {
            throw new RecordValidationException([new RecordFieldError("status", "Taslak olmayan teklif değiştirilemez; yeni revizyon açın.")]);
        }

        if (!workflow)
        {
            if (values.ContainsKey("documentDiscountKind") || values.ContainsKey("documentDiscountValue"))
            {
                var wantsDiscount = entity.DocumentDiscountKind != "None" || entity.DocumentDiscountValue > 0m;
                var changed = original is null || original.DocumentDiscountKind != entity.DocumentDiscountKind || original.DocumentDiscountValue != entity.DocumentDiscountValue;
                if (wantsDiscount && changed && !await Context.Permissions.HasPermissionAsync(CrmModule.Permissions.QuotesDiscount, cancellationToken))
                {
                    errors.Add(new RecordFieldError("documentDiscountValue", "İndirim uygulama yetkiniz yok."));
                }
            }

            if (entity.DocumentDiscountKind == "None" && entity.DocumentDiscountValue != 0m)
            {
                entity.DocumentDiscountValue = 0m;
            }

            if (values.ContainsKey("companyId") || values.ContainsKey("contactId") || values.ContainsKey("opportunityId") || values.ContainsKey("priceListId") || values.ContainsKey("currency"))
            {
                await ValidateRelationsAsync(entity, errors, cancellationToken);
            }

            if ((entity.ExchangeRate is null) != (entity.ExchangeRateDate is null || entity.ReportingCurrency is null))
            {
                errors.Add(new RecordFieldError("exchangeRate", "Kur, raporlama para birimi ve kur tarihi birlikte verilmelidir."));
            }

            if (entity.ReportingCurrency is not null && entity.ReportingCurrency == entity.Currency)
            {
                errors.Add(new RecordFieldError("reportingCurrency", "Raporlama para birimi teklif para biriminden farklı olmalıdır."));
            }

            entity.UpdatedBy = Context.CurrentUserId;
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        if (workflow && !isNew && gate.EventKind is { } kind && original is not null)
        {
            QuoteHistory.Add(Context, entity, kind, original.Status, entity.Status, gate.Reason);
            if (gate.Effect is { } effect)
            {
                await effect(Context, entity, cancellationToken);
            }
        }

        // Ticari değişiklik bekleyen/verilmiş iç onayı geçersiz kılar; onay belirli içeriğe bağlıdır.
        if (!workflow && !isNew && commercialTouched && entity.ApprovalStatus is QuoteApprovalStatus.Pending or QuoteApprovalStatus.Approved)
        {
            QuoteHistory.Add(Context, entity, "ApprovalInvalidated", entity.Status, entity.Status, "Ticari içerik değişti.");
            entity.ApprovalStatus = QuoteApprovalStatus.NotRequired;
        }

        if (copySource is { } source)
        {
            var pending = await CopyLinesAsync(entity, source, duplicate: copyMode == "duplicateOfId", cancellationToken);
            copySource = null;
            await QuoteTotals.RecalculateAsync(Context.Data, entity, pending, null, "documentDiscountValue", cancellationToken);
        }
        else if (isNew || values.ContainsKey("documentDiscountKind") || values.ContainsKey("documentDiscountValue") || values.ContainsKey("priceIncludesTax"))
        {
            await QuoteTotals.RecalculateAsync(Context.Data, entity, null, null, "documentDiscountValue", cancellationToken);
        }

        // Yeni sürüm sunulunca ailenin önceki sunulmuş sürümü geçersizleşir (ADR-0019 politika kararı).
        if (workflow && !isNew && entity.Status == QuoteStatus.Presented && original?.Status == QuoteStatus.Draft)
        {
            var previous = await Context.Data.Set<Quote>()
                .Where(q => q.FamilyId == entity.FamilyId && q.Id != entity.Id && q.Status == QuoteStatus.Presented)
                .ToListAsync(cancellationToken);
            foreach (Quote old in previous)
            {
                old.Status = QuoteStatus.Superseded;
                old.StatusReason = $"Revizyon {entity.RevisionNo} sunuldu.";
                old.Touch();
                QuoteHistory.Add(Context, old, "Superseded", QuoteStatus.Presented, QuoteStatus.Superseded, old.StatusReason);
            }
        }
    }

    private static bool IsNonCommercial(string field) => field is "internalNotes" or "ownerId" or "opportunityId" or "contact" or RecordFields.Custom;

    protected override async Task BeforeDeleteAsync(Quote entity, CancellationToken cancellationToken)
    {
        if (entity.Status != QuoteStatus.Draft)
        {
            throw new RecordConflictException("Yalnızca taslak teklif silinebilir; diğerleri iptal edilir.");
        }

        var lines = await Context.Data.Set<QuoteLine>().Where(l => l.QuoteId == entity.Id).ToListAsync(cancellationToken);
        Context.Data.Set<QuoteLine>().RemoveRange(lines);
        var events = await Context.Data.Set<QuoteEvent>().Where(e => e.QuoteId == entity.Id).ToListAsync(cancellationToken);
        Context.Data.Set<QuoteEvent>().RemoveRange(events);
    }

    private async Task<Quote?> CanSeeAsync(Guid id, CancellationToken cancellationToken)
    {
        var quote = await Context.Data.Set<Quote>().SingleOrDefaultAsync(q => q.Id == id, cancellationToken);
        if (quote is null || !await Context.Access.CanAsync(CrmModule.Entities.Quote, RecordOperation.Read, cancellationToken))
        {
            return null;
        }

        var scope = await Context.Access.GetOwnersAsync(CrmModule.Entities.Quote, cancellationToken);
        return scope is null || (quote.OwnerId is { } owner && scope.Contains(owner)) ? quote : null;
    }

    private async Task ValidateRelationsAsync(Quote entity, List<RecordFieldError> errors, CancellationToken cancellationToken)
    {
        if (entity.ContactId is { } contactId)
        {
            var contact = await Context.Data.Set<Contact>().AsNoTracking().Where(c => c.Id == contactId).Select(c => new { c.CompanyId }).SingleOrDefaultAsync(cancellationToken);
            var linked = contact is not null && (contact.CompanyId == entity.CompanyId
                || await Context.Data.Set<CompanyContact>().AnyAsync(l => l.CompanyId == entity.CompanyId && l.ContactId == contactId, cancellationToken));
            if (!linked)
            {
                errors.Add(new RecordFieldError("contactId", "Seçilen kişi bu firmayla ilişkili değil."));
            }
        }

        if (entity.OpportunityId is { } opportunityId)
        {
            var opportunityCompany = await Context.Data.Set<Opportunity>().AsNoTracking()
                .Where(o => o.Id == opportunityId).Select(o => (Guid?)o.CompanyId).SingleOrDefaultAsync(cancellationToken);
            if (opportunityCompany != entity.CompanyId)
            {
                errors.Add(new RecordFieldError("opportunityId", "Fırsat bu firmaya ait değil."));
            }
        }

        if (entity.PriceListId is { } listId)
        {
            var list = await Context.Data.Set<PriceList>().AsNoTracking().Where(l => l.Id == listId).Select(l => new { l.Currency }).SingleOrDefaultAsync(cancellationToken);
            if (list is not null && !string.Equals(list.Currency, entity.Currency, StringComparison.Ordinal))
            {
                errors.Add(new RecordFieldError("priceListId", $"Fiyat listesi para birimi ({list.Currency}) teklifin para biriminden farklı."));
            }
        }
    }

    // Revizyon: kalemler aynı kararlı anahtarlarla kopyalanır (karşılaştırma için). Çoğaltma: yeni ailedir, yeni anahtarlar üretilir.
    private async Task<List<QuoteLine>> CopyLinesAsync(Quote target, Quote source, bool duplicate, CancellationToken cancellationToken)
    {
        var lines = await Context.Data.Set<QuoteLine>().AsNoTracking().Where(l => l.QuoteId == source.Id).OrderBy(l => l.Position).ToListAsync(cancellationToken);
        var keys = lines.ToDictionary(l => l.StableKey, l => duplicate ? Guid.CreateVersion7() : l.StableKey);
        var copies = new List<QuoteLine>();
        foreach (QuoteLine l in lines)
        {
            var copy = new QuoteLine
            {
                QuoteId = target.Id, StableKey = keys[l.StableKey], Position = l.Position, GroupName = l.GroupName, Kind = l.Kind, ProductId = l.ProductId,
                ProductCode = l.ProductCode, Name = l.Name, Description = l.Description, Quantity = l.Quantity, UnitId = l.UnitId, UnitName = l.UnitName,
                UnitPrice = l.UnitPrice, DiscountKind = l.DiscountKind, DiscountValue = l.DiscountValue, TaxRatePercent = l.TaxRatePercent, TaxCode = l.TaxCode,
                Role = l.Role, IsSelected = l.IsSelected, ParentStableKey = l.ParentStableKey is { } p ? keys[p] : null, RecurringPeriod = l.RecurringPeriod,
                UnitCost = l.UnitCost,
            };
            Context.Data.Set<QuoteLine>().Add(copy);
            copies.Add(copy);
        }

        if (duplicate)
        {
            // Çoğaltma kaynağın alanlarını taşır ama durumu taşımaz.
            target.Status = QuoteStatus.Draft;
            target.ApprovalStatus = QuoteApprovalStatus.NotRequired;
            target.DeliveryStatus = QuoteDeliveryStatus.NotSent;
        }

        return copies;
    }
}

/// <summary>Teklif kalemi kayıt deposu. Kalem değişikliği yalnızca taslak tekliflerde yapılır ve teklif toplamlarını sunucuda yeniden hesaplar.</summary>
internal sealed class QuoteLineRecordStore(CrmStoreContext context, IPriceCalculator calculator, QuoteWorkflowGate gate)
    : CrmRecordStore<QuoteLine>(context)
{
    private bool creating;
    private Quote? quote;

    public override string EntityKey => CrmModule.Entities.QuoteLine;

    protected override string ReadPermission => CrmModule.Permissions.QuotesRead;

    protected override string WritePermission => CrmModule.Permissions.QuotesWrite;

    protected override bool UsesScope => true;

    protected override string ScopeEntityKey => CrmModule.Entities.Quote;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override bool GateOpen => gate.IsOpen;

    protected override IReadOnlyList<RecordField<QuoteLine>> Fields { get; } =
    [
        Field.SharedId<QuoteLine>("quoteId", l => l.QuoteId),
        Field.SharedId<QuoteLine>("stableKey", l => l.StableKey),
        Field.Count<QuoteLine>("position", l => l.Position, (l, v) => l.Position = v),
        Field.Text<QuoteLine>("groupName", QuoteLine.NameMaxLength, l => l.GroupName, (l, v) => l.GroupName = v),
        Field.Choice<QuoteLine>("kind", QuoteLineKind.All, l => l.Kind, (l, v) => l.Kind = v),
        Field.Reference<QuoteLine, Product>("productId", "Ürün bulunamadı.", l => l.ProductId, (l, v) => l.ProductId = v),
        Field.Text<QuoteLine>("productCode", QuoteLine.CodeMaxLength, l => l.ProductCode, (l, v) => l.ProductCode = v),
        Field.Text<QuoteLine>("name", QuoteLine.NameMaxLength, l => l.Name, (l, v) => l.Name = v ?? string.Empty),
        Field.Text<QuoteLine>("description", QuoteLine.TextMaxLength, l => l.Description, (l, v) => l.Description = v),
        Field.Quantity<QuoteLine>("quantity", l => l.Quantity, (l, v) => l.Quantity = v ?? 0m),
        Field.Reference<QuoteLine, MeasureUnit>("unitId", "Birim bulunamadı.", l => l.UnitId, (l, v) => l.UnitId = v),
        Field.Text<QuoteLine>("unitName", 100, l => l.UnitName, (l, v) => l.UnitName = v),
        Field.Quantity<QuoteLine>("unitPrice", l => l.UnitPrice, (l, v) => l.UnitPrice = v ?? 0m),
        Field.Choice<QuoteLine>("discountKind", ["None", "Percent", "Amount"], l => l.DiscountKind, (l, v) => l.DiscountKind = v),
        Field.Money<QuoteLine>("discountValue", l => l.DiscountValue, (l, v) => l.DiscountValue = v ?? 0m),
        Field.Rate<QuoteLine>("taxRatePercent", l => l.TaxRatePercent, (l, v) => l.TaxRatePercent = v ?? 0m),
        Field.Text<QuoteLine>("taxCode", 30, l => l.TaxCode, (l, v) => l.TaxCode = v),
        Field.Choice<QuoteLine>("role", ["Standard", "Optional", "Alternative", "PackageChild"], l => l.Role, (l, v) => l.Role = v),
        Field.Flag<QuoteLine>("isSelected", l => l.IsSelected, (l, v) => l.IsSelected = v),
        Field.Id<QuoteLine>("parentStableKey", l => l.ParentStableKey, (l, v) => l.ParentStableKey = v),
        Field.OptionalChoice<QuoteLine>("recurringPeriod", CatalogRules.RecurringPeriods, l => l.RecurringPeriod, (l, v) => l.RecurringPeriod = v),
        Field.Quantity<QuoteLine>("unitCost", l => l.UnitCost, (l, v) => l.UnitCost = v),
        Field.Flag<QuoteLine>("included", l => l.Included, (l, v) => l.Included = v).AsSystem(),
        Field.Money<QuoteLine>("listAmount", l => l.ListAmount, (l, v) => l.ListAmount = v ?? 0m).AsSystem(),
        Field.Money<QuoteLine>("lineDiscount", l => l.LineDiscount, (l, v) => l.LineDiscount = v ?? 0m).AsSystem(),
        Field.Money<QuoteLine>("documentDiscountShare", l => l.DocumentDiscountShare, (l, v) => l.DocumentDiscountShare = v ?? 0m).AsSystem(),
        Field.Money<QuoteLine>("net", l => l.Net, (l, v) => l.Net = v ?? 0m).AsSystem(),
        Field.Money<QuoteLine>("tax", l => l.Tax, (l, v) => l.Tax = v ?? 0m).AsSystem(),
        Field.Money<QuoteLine>("gross", l => l.Gross, (l, v) => l.Gross = v ?? 0m).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<QuoteLine>> Filters { get; } = new Dictionary<string, RecordFilter<QuoteLine>>
    {
        ["quoteId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.QuoteId == id) : null,
        ["productId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.ProductId == id) : null,
    };

    protected override IOrderedQueryable<QuoteLine> Order(IQueryable<QuoteLine> source) =>
        source.OrderBy(l => l.QuoteId).ThenBy(l => l.Position).ThenBy(l => l.CreatedAt);

    protected override IQueryable<QuoteLine> Search(IQueryable<QuoteLine> source, string term) =>
        source.Where(l => l.Name.Contains(term) || (l.ProductCode != null && l.ProductCode.Contains(term)));

    protected override IQueryable<QuoteLine> ScopeToOwners(IQueryable<QuoteLine> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var quotes = Context.Data.Set<Quote>();
        return source.Where(l => quotes.Any(q => q.Id == l.QuoteId && ids.Contains(q.OwnerId)));
    }

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (record.Values["quoteId"]?.GetValue<string>() is not { } text || !Guid.TryParse(text, out var quoteId))
        {
            return false;
        }

        var owner = await Context.Data.Set<Quote>().AsNoTracking().Where(q => q.Id == quoteId).Select(q => q.OwnerId).SingleOrDefaultAsync(cancellationToken);
        return owner is { } ownerId && owners.Contains(ownerId);
    }

    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(QuoteLine entity, JsonObject values, CancellationToken cancellationToken)
    {
        var quoteId = FieldNamed("quoteId").Parse(values["quoteId"]).Value as Guid?;
        if (quoteId is null || await LoadQuoteAsync(quoteId.Value, cancellationToken) is not { } parent)
        {
            throw new RecordValidationException([new RecordFieldError("quoteId", "Teklif bulunamadı.")]);
        }

        creating = true;
        quote = parent;
        entity.QuoteId = parent.Id;
        entity.StableKey = Guid.CreateVersion7();
        return (values, false);
    }

    protected override async Task AppliedAsync(QuoteLine entity, JsonObject values, CancellationToken cancellationToken)
    {
        var isNew = creating;
        creating = false;
        var parent = quote?.Id == entity.QuoteId ? quote : await LoadQuoteAsync(entity.QuoteId, cancellationToken);
        quote = null;
        if (parent is null)
        {
            throw new RecordValidationException([new RecordFieldError("quoteId", "Teklif bulunamadı.")]);
        }

        if (!gate.IsOpen)
        {
            if (parent.Status != QuoteStatus.Draft)
            {
                throw new RecordValidationException([new RecordFieldError("quoteId", "Taslak olmayan teklifin kalemi değiştirilemez; yeni revizyon açın.")]);
            }

            await ApplyPricingAndGuardsAsync(entity, values, parent, isNew, cancellationToken);
        }

        if (isNew && entity.Position == 0)
        {
            var last = await Context.Data.Set<QuoteLine>().Where(l => l.QuoteId == parent.Id).Select(l => (int?)l.Position).MaxAsync(cancellationToken);
            entity.Position = (last ?? 0) + 1;
        }

        await QuoteTotals.RecalculateAsync(Context.Data, parent, isNew ? [entity] : null, null, "discountValue", cancellationToken);
        TouchParent(parent);
    }

    protected override async Task BeforeDeleteAsync(QuoteLine entity, CancellationToken cancellationToken)
    {
        var parent = await LoadQuoteAsync(entity.QuoteId, cancellationToken)
            ?? throw new RecordValidationException([new RecordFieldError("quoteId", "Teklif bulunamadı.")]);
        if (parent.Status != QuoteStatus.Draft)
        {
            throw new RecordConflictException("Taslak olmayan teklifin kalemi silinemez; yeni revizyon açın.");
        }

        // Paket ana kalemi silinince bileşenleri de gider; toplam kalan kalemlerden yeniden hesaplanır.
        var children = await Context.Data.Set<QuoteLine>().Where(l => l.QuoteId == parent.Id && l.ParentStableKey == entity.StableKey).ToListAsync(cancellationToken);
        Context.Data.Set<QuoteLine>().RemoveRange(children);
        await QuoteTotals.RecalculateAsync(Context.Data, parent, null, [entity.Id, .. children.Select(c => c.Id)], "discountValue", cancellationToken);
        TouchParent(parent);
    }

    // Kalem değişikliği teklifin ticari içeriğidir: sürüm artar, önceki iç onay geçersizleşir.
    private void TouchParent(Quote parent)
    {
        if (!gate.IsOpen)
        {
            if (parent.ApprovalStatus is QuoteApprovalStatus.Pending or QuoteApprovalStatus.Approved)
            {
                QuoteHistory.Add(Context, parent, "ApprovalInvalidated", parent.Status, parent.Status, "Kalemler değişti.");
                parent.ApprovalStatus = QuoteApprovalStatus.NotRequired;
            }

            parent.UpdatedBy = Context.CurrentUserId;
        }

        parent.Touch();
    }

    private async Task<Quote?> LoadQuoteAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        var parent = await Context.Data.Set<Quote>().SingleOrDefaultAsync(q => q.Id == quoteId, cancellationToken);
        if (parent is null || !await Context.Access.CanAsync(CrmModule.Entities.Quote, RecordOperation.Read, cancellationToken))
        {
            return null;
        }

        var scope = await Context.Access.GetOwnersAsync(CrmModule.Entities.Quote, cancellationToken);
        return scope is null || (parent.OwnerId is { } owner && scope.Contains(owner)) ? parent : null;
    }

    // Fiyat kaynağı sunucudur: ürün satırında fiyat listeden hesaplanır; listeden farklı elle fiyat, serbest kalem ve indirim değişikliği
    // ayrı izin ister (izin yoksa API üzerinden de aşılamaz). Ürün kopyası (kod, ad, vergi, birim) kalemde saklanır.
    private async Task ApplyPricingAndGuardsAsync(QuoteLine entity, JsonObject values, Quote parent, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var original = isNew
            ? null
            : await Context.Data.Set<QuoteLine>().AsNoTracking().Where(l => l.Id == entity.Id)
                .Select(l => new { l.DiscountKind, l.DiscountValue }).SingleOrDefaultAsync(cancellationToken);
        var canManual = await Context.Permissions.HasPermissionAsync(CrmModule.Permissions.QuotesManualPrice, cancellationToken);

        if (entity.DiscountKind == "None")
        {
            entity.DiscountValue = 0m;
        }

        var discountChanged = original is null
            ? entity.DiscountKind != "None"
            : original.DiscountKind != entity.DiscountKind || original.DiscountValue != entity.DiscountValue;
        if (discountChanged && !await Context.Permissions.HasPermissionAsync(CrmModule.Permissions.QuotesDiscount, cancellationToken))
        {
            errors.Add(new RecordFieldError("discountValue", "İndirim uygulama yetkiniz yok."));
        }

        if (entity.ProductId is { } productId)
        {
            var product = await Context.Data.Set<Product>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId, cancellationToken);
            if (product is null)
            {
                errors.Add(new RecordFieldError("productId", "Ürün bulunamadı."));
            }
            else if (product.Status != CatalogRules.StatusActive && (isNew || values.ContainsKey("productId")) && !await ProductUnchangedAsync(entity, product.Id, cancellationToken))
            {
                errors.Add(new RecordFieldError("productId", "Pasif ya da taslak ürün yeni teklif kalemine eklenemez."));
            }
            else
            {
                entity.ProductCode = product.Code;
                if (string.IsNullOrWhiteSpace(entity.Name))
                {
                    entity.Name = product.Name;
                }

                entity.Description ??= product.Description;
                if (entity.Kind == QuoteLineKind.Free)
                {
                    entity.Kind = product.Kind == CatalogRules.KindService ? QuoteLineKind.Service : QuoteLineKind.Product;
                }

                await PriceFromCatalogAsync(entity, product, parent, values["unitPrice"] is not null ? entity.UnitPrice : null, canManual, errors, cancellationToken);
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(entity.Name))
            {
                errors.Add(new RecordFieldError("name", "Kalem adı zorunludur."));
            }

            // Katalog dışı (serbest) kalem ve fiyatı elle girmek yetkiye bağlıdır.
            if (!canManual && (isNew || values["unitPrice"] is not null || entity.Kind == QuoteLineKind.Free))
            {
                errors.Add(new RecordFieldError("unitPrice", "Serbest kalem ekleme ve elle fiyat girme yetkiniz yok."));
            }
        }

        if (entity.Role == "PackageChild" && entity.ParentStableKey is null)
        {
            errors.Add(new RecordFieldError("parentStableKey", "Paket bileşeninin ana satırı gerekir."));
        }

        if (entity.ParentStableKey is { } parentKey
            && !await Context.Data.Set<QuoteLine>().AnyAsync(l => l.QuoteId == parent.Id && l.StableKey == parentKey, cancellationToken))
        {
            errors.Add(new RecordFieldError("parentStableKey", "Ana satır bu teklifte bulunamadı."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    private async Task<bool> ProductUnchangedAsync(QuoteLine entity, Guid productId, CancellationToken cancellationToken) =>
        await Context.Data.Set<QuoteLine>().AsNoTracking().AnyAsync(l => l.Id == entity.Id && l.ProductId == productId, cancellationToken);

    private async Task PriceFromCatalogAsync(
        QuoteLine entity, Product product, Quote parent, decimal? suppliedPrice, bool canManual, List<RecordFieldError> errors,
        CancellationToken cancellationToken)
    {
        var quantity = entity.Quantity <= 0 ? 1m : entity.Quantity;
        var result = await calculator.CalculateAsync(
            new PriceCalculationRequest(product.Id, quantity, parent.Currency, entity.UnitId, null, parent.PriceListId, parent.CompanyId), cancellationToken);

        if (result.Line is not { } line)
        {
            // Liste fiyatı yoksa yalnızca elle fiyat yetkisi olan kullanıcı kendi fiyatıyla devam edebilir.
            if (!(canManual && suppliedPrice is not null))
            {
                errors.Add(new RecordFieldError("productId", result.Message ?? "Ürün için fiyat bulunamadı."));
            }

            return;
        }

        if (line.PriceIncludesTax != parent.PriceIncludesTax)
        {
            errors.Add(new RecordFieldError("productId", "Ürün fiyatının vergi dahil/hariç biçimi teklifinkiyle uyuşmuyor."));
            return;
        }

        var catalogPrice = decimal.Round(line.ListTotal / quantity, 6, MidpointRounding.AwayFromZero);
        if (suppliedPrice is { } supplied && supplied != catalogPrice)
        {
            if (!canManual)
            {
                errors.Add(new RecordFieldError("unitPrice", "Elle birim fiyat girme yetkiniz yok; fiyat listeden gelir."));
                return;
            }

            entity.UnitPrice = supplied;
        }
        else
        {
            entity.UnitPrice = catalogPrice;
        }

        entity.TaxRatePercent = line.TaxRatePercent;
        if (entity.UnitId is { } unitId)
        {
            entity.UnitName = await Context.Data.Set<MeasureUnit>().AsNoTracking().Where(u => u.Id == unitId).Select(u => u.Name).SingleOrDefaultAsync();
        }

        if (product.ChargeType == CatalogRules.ChargeRecurring)
        {
            entity.RecurringPeriod ??= product.RecurringPeriod;
        }
    }
}
