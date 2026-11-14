using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Opportunities;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Fırsat kayıt deposu (ADR-0017). Alan düzenlemesi <c>crm.opportunities.write</c> ile yapılır; durum, kapanış ve sistem alanları
/// yalnızca iş akışı kapısı (<see cref="OpportunityWorkflowGate"/>) açıkken yazılır, aşama ise oluştururken serbest, sonradan yalnızca
/// kapıyla değişir. Aşama–durum tutarlılığı, aşamaya özel zorunlu alanlar, olasılık türetmesi ve tarihçe burada, kaydın kendi
/// işleminde uygulanır: böylece aşama değişikliği hangi yoldan gelirse gelsin (API, Kanban, iş akışı) aynı kurallara tabidir.
/// </summary>
internal sealed class OpportunityRecordStore(CrmStoreContext context) : CrmRecordStore<Opportunity>(context), IRecordStore
{
    private OpportunityPolicy? policy;
    private ChoiceList? stages;

    public override string EntityKey => CrmModule.Entities.Opportunity;

    protected override string ReadPermission => CrmModule.Permissions.OpportunitiesRead;

    protected override string WritePermission => CrmModule.Permissions.OpportunitiesWrite;

    /// <summary>Kapı açıkken güncelleme yalnızca eylemin izinlerinden biriyle yapılır (aşama için aşama izni ya da yazma izni).</summary>
    IReadOnlyList<string>? IRecordStore.AnyPermissionFor(RecordOperation operation) =>
        operation == RecordOperation.Update && Context.OpportunityGate.Permissions is { } permissions ? permissions : null;

    // Kaynak Lead dönüşümü de sistem alanlarını (kaynak Lead, aşama) yazar; iki kapıdan biri açıksa yazılır.
    protected override bool GateOpen => Context.LeadGate.IsOpen || Context.OpportunityGate.IsOpen;

    protected override IReadOnlyList<RecordField<Opportunity>> Fields { get; } =
    [
        Field.Text<Opportunity>("opportunityNumber", Opportunity.NumberMaxLength, o => o.OpportunityNumber, (o, v) => o.OpportunityNumber = v!).AsSystem(),
        Field.Text<Opportunity>("title", Opportunity.TitleMaxLength, o => o.Title, (o, v) => o.Title = v!, required: true),
        Field.Text<Opportunity>("description", Opportunity.TextMaxLength, o => o.Description, (o, v) => o.Description = v),
        Field.Reference<Opportunity, Company>("companyId", "Firma bulunamadı.", o => o.CompanyId, (o, v) => o.CompanyId = v!.Value, required: true),
        Field.Reference<Opportunity, Contact>("contactId", "Kişi bulunamadı.", o => o.ContactId, (o, v) => o.ContactId = v),
        // Kaynak Lead yalnızca dönüşüm işleminde (iş akışı kapısı açıkken) yazılır; genel uçlardan gelen değer yok sayılır.
        Field.Id<Opportunity>("sourceLeadId", o => o.SourceLeadId, (o, v) => o.SourceLeadId = v).AsSystem(),
        // Kampanya bağı (ADR-0025): Pazarlama modülünün kaydı; dönüşümde müşteri adayından geçer, elle seçilirse görünürlüğü denetlenir (LookupGuard).
        Field.Id<Opportunity>("campaignId", o => o.CampaignId, (o, v) => o.CampaignId = v),
        // Aşama oluştururken serbesttir; sonra yalnızca aşama değiştirme eylemiyle (kapı) değişir.
        Field.ListChoice<Opportunity>("stage", CrmModule.Lists.OpportunityStage, o => o.Stage, (o, v) => o.Stage = v!).GatedUpdate(),
        Field.Text<Opportunity>("status", Opportunity.StatusMaxLength, o => o.Status, (o, v) => o.Status = v!).AsSystem(),
        Field.Text<Opportunity>("statusReason", Opportunity.LostReasonMaxLength, o => o.StatusReason, (o, v) => o.StatusReason = v).AsSystem(),
        Field.Choice<Opportunity>("priority", LeadPriority.All, o => o.Priority, (o, v) => o.Priority = v),
        Field.ListChoice<Opportunity>("type", CrmModule.Lists.OpportunityType, o => o.Type, (o, v) => o.Type = v, required: false),
        Field.ListChoice<Opportunity>("source", CrmModule.Lists.LeadSource, o => o.Source, (o, v) => o.Source = v, required: false),
        Field.Money<Opportunity>("amount", o => o.Amount, (o, v) => o.Amount = v),
        Field.Currency<Opportunity>("currency", o => o.Currency, (o, v) => o.Currency = v),
        Field.Money<Opportunity>("customerBudget", o => o.CustomerBudget, (o, v) => o.CustomerBudget = v),
        Field.Choice<Opportunity>("budgetStatus", OpportunityBudgetStatus.All, o => o.BudgetStatus, (o, v) => o.BudgetStatus = v),
        Field.Date<Opportunity>("expectedCloseDate", o => o.ExpectedCloseDate, (o, v) => o.ExpectedCloseDate = v),
        Field.Percent<Opportunity>("probability", o => o.Probability, (o, v) => o.Probability = v),
        Field.Flag<Opportunity>("probabilityManual", o => o.ProbabilityManual, (o, v) => o.ProbabilityManual = v).AsSystem(),
        Field.Text<Opportunity>("needDescription", Opportunity.TextMaxLength, o => o.NeedDescription, (o, v) => o.NeedDescription = v),
        Field.Text<Opportunity>("currentProblem", Opportunity.TextMaxLength, o => o.CurrentProblem, (o, v) => o.CurrentProblem = v),
        Field.Text<Opportunity>("expectedBenefit", Opportunity.TextMaxLength, o => o.ExpectedBenefit, (o, v) => o.ExpectedBenefit = v),
        Field.Text<Opportunity>("scope", Opportunity.TextMaxLength, o => o.Scope, (o, v) => o.Scope = v),
        Field.CodeList<Opportunity>("serviceScope", o => o.ServiceScope, (o, v) => o.ServiceScope = v),
        Field.Text<Opportunity>("successCriteria", Opportunity.TextMaxLength, o => o.SuccessCriteria, (o, v) => o.SuccessCriteria = v),
        Field.Text<Opportunity>("risks", Opportunity.TextMaxLength, o => o.Risks, (o, v) => o.Risks = v),
        Field.Text<Opportunity>("decisionProcess", Opportunity.TextMaxLength, o => o.DecisionProcess, (o, v) => o.DecisionProcess = v),
        Field.Date<Opportunity>("targetDecisionDate", o => o.TargetDecisionDate, (o, v) => o.TargetDecisionDate = v),
        Field.Text<Opportunity>("nextAction", Opportunity.ShortMaxLength, o => o.NextAction, (o, v) => o.NextAction = v),
        Field.Date<Opportunity>("nextActionDate", o => o.NextActionDate, (o, v) => o.NextActionDate = v),
        Field.Time<Opportunity>("lastInteractionAt", o => o.LastInteractionAt, (o, v) => o.LastInteractionAt = v).AsSystem(),
        Field.Time<Opportunity>("stageEnteredAt", o => o.StageEnteredAt, (o, v) => { if (v is { } time) { o.StageEnteredAt = time; } }).AsSystem(),
        // Kapanış bilgileri (yalnızca iş akışı).
        Field.Date<Opportunity>("actualCloseDate", o => o.ActualCloseDate, (o, v) => o.ActualCloseDate = v).AsSystem(),
        Field.Id<Opportunity>("primaryQuoteId", o => o.PrimaryQuoteId, (o, v) => o.PrimaryQuoteId = v).AsSystem(),
        Field.Money<Opportunity>("wonAmount", o => o.WonAmount, (o, v) => o.WonAmount = v).AsSystem(),
        Field.Text<Opportunity>("wonCurrency", Opportunity.CurrencyLength, o => o.WonCurrency, (o, v) => o.WonCurrency = v).AsSystem(),
        Field.Text<Opportunity>("winReason", Opportunity.LostReasonMaxLength, o => o.WinReason, (o, v) => o.WinReason = v).AsSystem(),
        Field.Text<Opportunity>("handoverNote", Opportunity.TextMaxLength, o => o.HandoverNote, (o, v) => o.HandoverNote = v).AsSystem(),
        Field.ListChoice<Opportunity>("lossReason", CrmModule.Lists.OpportunityLossReason, o => o.LossReason, (o, v) => o.LossReason = v, required: false).AsSystem(),
        Field.Text<Opportunity>("lostReason", Opportunity.LostReasonMaxLength, o => o.LostReason, (o, v) => o.LostReason = v).AsSystem(),
        Field.Id<Opportunity>("winningCompetitorId", o => o.WinningCompetitorId, (o, v) => o.WinningCompetitorId = v).AsSystem(),
        Field.Date<Opportunity>("recontactDate", o => o.RecontactDate, (o, v) => o.RecontactDate = v).AsSystem(),
        Field.Date<Opportunity>("holdReviewDate", o => o.HoldReviewDate, (o, v) => o.HoldReviewDate = v).AsSystem(),
        Field.Count<Opportunity>("closureCycle", o => o.ClosureCycle, (o, v) => o.ClosureCycle = v).AsSystem(),
        Field.Count<Opportunity>("closeDateChangeCount", o => o.CloseDateChangeCount, (o, v) => o.CloseDateChangeCount = v).AsSystem(),
        Field.Owner<Opportunity>(OwnerField, o => o.OwnerId, (o, v) => o.OwnerId = v),
        Field.Text<Opportunity>("notes", Company.NotesMaxLength, o => o.Notes, (o, v) => o.Notes = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Opportunity>> Filters { get; } = new Dictionary<string, RecordFilter<Opportunity>>
    {
        // Hizmet kapsamı (ADR-0029 Dilim F): tam kod eşleşir.
        ["serviceScope"] = (q, v, _) => CodeListFilter.Code(v) is { } code ? q.Where(o => ("," + o.ServiceScope + ",").Contains("," + code + ",")) : null,
        ["companyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(o => o.CompanyId == id) : null,
        ["contactId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(o => o.ContactId == id) : null,
        ["sourceLeadId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(o => o.SourceLeadId == id) : null,
        ["campaignId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(o => o.CampaignId == id) : null,
        [OwnerField] = (q, v, ctx) => FilterValue.User(v, ctx) is { } owner ? q.Where(o => o.OwnerId == owner) : null,
        ["number"] = (q, v, _) => v.Trim() is { Length: > 0 } number ? q.Where(o => o.OpportunityNumber == number) : null,
        ["status"] = (q, v, _) => OpportunityStatus.All.Contains(v) ? q.Where(o => o.Status == v) : null,
        ["priority"] = (q, v, _) => LeadPriority.All.Contains(v) ? q.Where(o => o.Priority == v) : null,
        ["type"] = (q, v, _) => ChoiceValueKeys.IsValid(v) ? q.Where(o => o.Type == v) : null,
        ["source"] = (q, v, _) => ChoiceValueKeys.IsValid(v) ? q.Where(o => o.Source == v) : null,
        ["lossReason"] = (q, v, _) => ChoiceValueKeys.IsValid(v) ? q.Where(o => o.LossReason == v) : null,
        ["currency"] = (q, v, _) => v.Trim().ToUpperInvariant() is { Length: Opportunity.CurrencyLength } code ? q.Where(o => o.Currency == code) : null,
        // Açık (Open) ve beklemedeki (OnHold) fırsatlar: devir ve "yürüyen iş" görünümleri bunu kullanır.
        ["live"] = (q, v, _) => FilterValue.Bool(v) is { } live
            ? live ? q.Where(o => o.Status == OpportunityStatus.Open || o.Status == OpportunityStatus.OnHold)
                : q.Where(o => o.Status != OpportunityStatus.Open && o.Status != OpportunityStatus.OnHold)
            : null,
        ["closeFrom"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(o => o.ExpectedCloseDate >= d) : null,
        ["closeTo"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(o => o.ExpectedCloseDate <= d) : null,
        ["actualCloseFrom"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(o => o.ActualCloseDate >= d) : null,
        ["actualCloseTo"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(o => o.ActualCloseDate <= d) : null,
        // Gecikmiş: açık ve tahmini kapanışı bu günden önce.
        ["closeOverdueBefore"] = (q, v, _) => FilterValue.Date(v) is { } d
            ? q.Where(o => o.Status == OpportunityStatus.Open && o.ExpectedCloseDate != null && o.ExpectedCloseDate < d)
            : null,
        // Sonraki aksiyonu olmayan açık fırsatlar.
        ["noNextAction"] = (q, v, _) => FilterValue.Bool(v) is true
            ? q.Where(o => o.Status == OpportunityStatus.Open && o.NextActionDate == null)
            : null,
        // Hareketsiz: son müşteri etkileşimi (yoksa oluşturulma) verilen andan önce olan açık fırsatlar.
        ["inactiveBefore"] = (q, v, _) => FilterValue.Time(v) is { } time
            ? q.Where(o => o.Status == OpportunityStatus.Open && (o.LastInteractionAt ?? o.CreatedAt) < time)
            : null,
        ["holdReviewDueBefore"] = (q, v, _) => FilterValue.Date(v) is { } d
            ? q.Where(o => o.Status == OpportunityStatus.OnHold && o.HoldReviewDate != null && o.HoldReviewDate <= d)
            : null,
        ["closeDatePostponed"] = (q, v, _) => FilterValue.Bool(v) is true ? q.Where(o => o.CloseDateChangeCount > 0) : null,
    };

    protected override IReadOnlyDictionary<string, AsyncRecordFilter<Opportunity>> AsyncFilters { get; } =
        new Dictionary<string, AsyncRecordFilter<Opportunity>>
        {
            ["stage"] = async (q, v, ctx, ct) =>
                await FilterValue.ChoiceAsync(v, CrmModule.Lists.OpportunityStage, ctx, ct) is { } stage ? q.Where(o => o.Stage == stage) : null,
            // "stageCategory=won|lost|open": aşamanın kategorisine göre (firmanın eklediği aşamalar dahil).
            ["stageCategory"] = async (q, v, ctx, ct) =>
            {
                var category = v.Trim().ToLowerInvariant();
                if (category is not (CrmModule.StageCategories.Open or CrmModule.StageCategories.Won or CrmModule.StageCategories.Lost))
                {
                    return null;
                }

                var keys = (await ctx.ChoiceLists.GetAsync(CrmModule.Lists.OpportunityStage, ct)).KeysIn(category).ToList();
                return q.Where(o => keys.Contains(o.Stage));
            },
            // "open=true": durumu Açık olan fırsatlar (beklemede, kazanılmış, kaybedilmiş ve iptal edilmiş olanlar hariç).
            ["open"] = (q, v, _, _) => Task.FromResult(FilterValue.Bool(v) is { } open
                ? open ? q.Where(o => o.Status == OpportunityStatus.Open) : q.Where(o => o.Status != OpportunityStatus.Open)
                : null),
        };

    protected override IOrderedQueryable<Opportunity> Order(IQueryable<Opportunity> source) =>
        source.OrderByDescending(o => o.CreatedAt);

    // Liste başlıklarına göre sıralama (sort=title, sort=-closeDate ...); boş değerler sonda, eşitlikte en yeni önce. Tutar ve olasılık hassas alandır
    // (sıralama değeri sezdirir) ve firma/sorumlu adı ilişki ya da üyelik tablosunda olduğundan bunlara göre sıralama yoktur.
    protected override IReadOnlyCollection<string>? SortKeys { get; } =
    [
        "title", "-title", "number", "-number", "stage", "-stage", "status", "-status", "priority", "-priority", "type", "-type", "source", "-source",
        "closeDate", "-closeDate", "nextActionDate", "-nextActionDate", "created", "-created",
    ];

    protected override IOrderedQueryable<Opportunity> Order(IQueryable<Opportunity> source, string? sort) => sort switch
    {
        "title" => source.OrderBy(o => o.Title).ThenByDescending(o => o.CreatedAt),
        "-title" => source.OrderByDescending(o => o.Title).ThenByDescending(o => o.CreatedAt),
        "number" => source.OrderBy(o => o.OpportunityNumber),
        "-number" => source.OrderByDescending(o => o.OpportunityNumber),
        "stage" => source.OrderBy(o => o.Stage).ThenByDescending(o => o.CreatedAt),
        "-stage" => source.OrderByDescending(o => o.Stage).ThenByDescending(o => o.CreatedAt),
        "status" => source.OrderBy(o => o.Status).ThenByDescending(o => o.CreatedAt),
        "-status" => source.OrderByDescending(o => o.Status).ThenByDescending(o => o.CreatedAt),
        "priority" => source.OrderBy(o => o.Priority).ThenByDescending(o => o.CreatedAt),
        "-priority" => source.OrderByDescending(o => o.Priority).ThenByDescending(o => o.CreatedAt),
        "type" => source.OrderBy(o => o.Type == null).ThenBy(o => o.Type).ThenByDescending(o => o.CreatedAt),
        "-type" => source.OrderBy(o => o.Type == null).ThenByDescending(o => o.Type).ThenByDescending(o => o.CreatedAt),
        "source" => source.OrderBy(o => o.Source == null).ThenBy(o => o.Source).ThenByDescending(o => o.CreatedAt),
        "-source" => source.OrderBy(o => o.Source == null).ThenByDescending(o => o.Source).ThenByDescending(o => o.CreatedAt),
        "closeDate" => source.OrderBy(o => o.ExpectedCloseDate == null).ThenBy(o => o.ExpectedCloseDate).ThenByDescending(o => o.CreatedAt),
        "-closeDate" => source.OrderBy(o => o.ExpectedCloseDate == null).ThenByDescending(o => o.ExpectedCloseDate).ThenByDescending(o => o.CreatedAt),
        "nextActionDate" => source.OrderBy(o => o.NextActionDate == null).ThenBy(o => o.NextActionDate).ThenByDescending(o => o.CreatedAt),
        "-nextActionDate" => source.OrderBy(o => o.NextActionDate == null).ThenByDescending(o => o.NextActionDate).ThenByDescending(o => o.CreatedAt),
        "created" => source.OrderBy(o => o.CreatedAt),
        _ => Order(source),
    };

    protected override IQueryable<Opportunity> Search(IQueryable<Opportunity> source, string term) =>
        source.Where(o => o.Title.Contains(term) || o.OpportunityNumber.Contains(term));

    // ---- Oluşturma

    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(
        Opportunity entity, JsonObject values, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        entity.OpportunityNumber = OpportunityRules.NewNumber(entity.CreatedAt);
        entity.Status = OpportunityStatus.Open;
        entity.StageEnteredAt = entity.CreatedAt;
        return (values, false);
    }

    /// <summary>
    /// Varsayılanlar atandıktan sonra: aşama açık bir aşama olmalı (kazanıldı/kaybedildi aşamasında fırsat açılmaz), olasılık verilmediyse
    /// aşamanın varsayılanı atanır ve ilk tarihçe satırı yazılır.
    /// </summary>
    protected override void OnCreated(Opportunity entity)
    {
        if (CategoryOf(entity.Stage) != CrmModule.StageCategories.Open)
        {
            throw new RecordValidationException([new RecordFieldError("stage", "Yeni fırsat açık bir aşamada başlamalıdır (kazanıldı/kaybedildi aşaması seçilemez).")]);
        }

        // Aşama koşulları oluşturmada uygulanmaz (kullanıcı kayıt açarken engellenmez); aşamaya GEÇERKEN uygulanır.
        if (entity.Probability is null && policy!.ProbabilityOf(entity.Stage) is { } percent)
        {
            entity.Probability = percent;
        }

        AddHistory(entity, OpportunityHistoryKinds.Created, null, null, null, null, null);
    }

    // ---- Güncelleme: tutarlılık, türetmeler ve tarihçe

    protected override async Task AppliedAsync(Opportunity entity, JsonObject values, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        var stored = await Context.Data.Set<Opportunity>().AsNoTracking().Where(o => o.Id == entity.Id)
            .Select(o => new Stored(o.CompanyId, o.ContactId, o.Stage, o.Status, o.ExpectedCloseDate, o.Probability, o.ServiceScope, o.CampaignId)).SingleOrDefaultAsync(cancellationToken);

        // Lookup alanlarının sunucu doğrulaması (ADR-0016): bağlanan firma/kişi çağıranın görebildiği kayıt olmalı ve kişi seçili
        // firmayla ilişkili olmalıdır. İş akışı yazımlarında ve değişmeyen bağlarda denetlenmez.
        if (!GateOpen)
        {
            var errors = new List<RecordFieldError>();
            await LookupGuard.AddCompanyErrorAsync(errors, Context, "companyId", entity.CompanyId, stored?.CompanyId, cancellationToken);
            await LookupGuard.AddContactErrorAsync(errors, Context, "contactId", entity.ContactId, stored?.ContactId, cancellationToken);
            await LookupGuard.AddContactCompanyErrorAsync(
                errors, Context, "contactId", entity.ContactId, entity.CompanyId, stored?.ContactId, stored?.CompanyId, cancellationToken);
            await ServiceScopeGuard.AddErrorsAsync(errors, Context, "serviceScope", entity.ServiceScope, stored?.ServiceScope, cancellationToken);
            await LookupGuard.AddCampaignErrorAsync(errors, Context, "campaignId", entity.CampaignId, stored?.CampaignId, cancellationToken);
            if (errors.Count > 0)
            {
                throw new RecordValidationException(errors);
            }
        }

        if (stored is null)
        {
            return; // yeni kayıt: varsayılanlar atandıktan sonra OnCreated tamamlar
        }

        var stageChanged = !string.Equals(stored.Stage, entity.Stage, StringComparison.Ordinal);
        var statusChanged = stored.Status != entity.Status;

        if ((stageChanged || statusChanged) && !GateOpen)
        {
            throw new RecordValidationException([new RecordFieldError("stage", "Aşama ve durum yalnızca aşama değiştirme / kapatma eylemleriyle değişir.")]);
        }

        // Aşama–durum tutarlılığı her yazımda denetlenir: çelişkili birleşim hiçbir yoldan kaydedilemez.
        var category = CategoryOf(entity.Stage);
        if (category is null || !OpportunityRules.IsConsistent(entity.Status, category))
        {
            throw new RecordValidationException([new RecordFieldError("stage", "Aşama ile durum çelişiyor.")]);
        }

        if (stageChanged)
        {
            entity.StageEnteredAt = DateTimeOffset.UtcNow;
            if (entity.Status == OpportunityStatus.Open)
            {
                RequireStageFields(entity);
            }
        }

        if (statusChanged || stageChanged)
        {
            if (entity.Status == OpportunityStatus.Won && entity.WonAmount is null)
            {
                entity.WonAmount = entity.Amount;
                entity.WonCurrency ??= entity.Currency;
            }

            if (entity.Status == OpportunityStatus.Won && entity.WonAmount is null)
            {
                throw new RecordValidationException([new RecordFieldError("wonAmount", "Kazanılan tutar girilmeli (fırsat tutarı da bilinmiyor).")]);
            }

            if (entity.Status == OpportunityStatus.Lost && string.IsNullOrWhiteSpace(entity.LossReason))
            {
                throw new RecordValidationException([new RecordFieldError("lossReason", "Kaybetme nedeni zorunludur.")]);
            }

            var wasClosed = OpportunityStatus.IsClosed(stored.Status);
            if (wasClosed && entity.Status == OpportunityStatus.Open)
            {
                // Yeniden açma: yeni kapanış döngüsü başlar, eski kapanışın bilgileri kayıttan kalkar (tarihçede korunur).
                entity.ClosureCycle++;
                entity.ActualCloseDate = null;
                entity.WonAmount = null;
                entity.WonCurrency = null;
                entity.WinReason = null;
                entity.HandoverNote = null;
                entity.LossReason = null;
                entity.LostReason = null;
                entity.WinningCompetitorId = null;
                entity.RecontactDate = null;
            }

            if (OpportunityStatus.IsClosed(entity.Status))
            {
                entity.ProbabilityManual = false;
                entity.HoldReviewDate = null;
            }

            // Kapanıştan gelen 0/100 olasılık yeniden açılan fırsata taşınmaz: elle girilmediyse aşamanın varsayılanı (yoksa bilinmiyor).
            var current = wasClosed && !entity.ProbabilityManual ? null : entity.Probability;
            entity.Probability = OpportunityRules.ProbabilityFor(entity.Status, entity.ProbabilityManual, current, policy!.ProbabilityOf(entity.Stage));
        }
        else if (!GateOpen && values.ContainsKey("probability"))
        {
            // Yetkili kullanıcının elle girdiği olasılık aşama varsayılanını ezer; temizlenirse varsayılana dönülebilir.
            entity.ProbabilityManual = entity.Probability is not null && (entity.Probability != stored.Probability || entity.ProbabilityManual);
        }

        if (stored.ExpectedCloseDate != entity.ExpectedCloseDate)
        {
            if (stored.ExpectedCloseDate is not null)
            {
                entity.CloseDateChangeCount++;
            }

            AddHistory(entity, OpportunityHistoryKinds.CloseDate, null, null, stored.ExpectedCloseDate, entity.ExpectedCloseDate, null);
        }

        if (statusChanged || stageChanged)
        {
            var details = statusChanged && OpportunityStatus.IsClosed(entity.Status) ? CloseDetails(entity) : null;
            AddHistory(entity, statusChanged ? OpportunityHistoryKinds.Status : OpportunityHistoryKinds.Stage,
                stored.Stage, stored.Status, null, null, details);
        }
    }

    protected override async Task BeforeDeleteAsync(Opportunity entity, CancellationToken cancellationToken)
    {
        Context.Data.Set<OpportunityHistory>().RemoveRange(await Context.Data.Set<OpportunityHistory>().Where(h => h.OpportunityId == entity.Id).ToListAsync(cancellationToken));
        Context.Data.Set<OpportunityContact>().RemoveRange(await Context.Data.Set<OpportunityContact>().Where(c => c.OpportunityId == entity.Id).ToListAsync(cancellationToken));
        Context.Data.Set<OpportunityCompetitor>().RemoveRange(await Context.Data.Set<OpportunityCompetitor>().Where(c => c.OpportunityId == entity.Id).ToListAsync(cancellationToken));
    }

    // ---- Yardımcılar

    private sealed record Stored(
        Guid CompanyId, Guid? ContactId, string Stage, string Status, DateOnly? ExpectedCloseDate, int? Probability, string? ServiceScope, Guid? CampaignId);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        policy ??= await Context.GetPolicyAsync(cancellationToken);
        stages ??= await Context.ChoiceLists.GetAsync(CrmModule.Lists.OpportunityStage, cancellationToken);
    }

    private string? CategoryOf(string stage) => stages?.Find(stage)?.Category;

    /// <summary>Aşamanın koşul alanları dolu mu? Eksikler alan bazında bildirilir.</summary>
    private void RequireStageFields(Opportunity entity)
    {
        var errors = new List<RecordFieldError>();
        var stageLabel = stages?.Find(entity.Stage)?.Label ?? entity.Stage;
        foreach (var name in policy!.RequirementsOf(entity.Stage))
        {
            var field = Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (field is not null && IsBlank(field.Read(entity)))
            {
                errors.Add(new RecordFieldError(field.Name, $"\"{stageLabel}\" aşamasına geçmek için bu alan dolu olmalı."));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    private static bool IsBlank(JsonNode? node) =>
        node is null || (node is JsonValue value && value.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetValue<string>()))
        || (node is JsonArray array && array.Count == 0);

    private void AddHistory(
        Opportunity entity, string kind, string? fromStage, string? fromStatus, DateOnly? fromClose, DateOnly? toClose, string? details)
    {
        var created = kind == OpportunityHistoryKinds.Created;
        Context.Data.Set<OpportunityHistory>().Add(new OpportunityHistory
        {
            OpportunityId = entity.Id,
            Kind = kind,
            At = DateTimeOffset.UtcNow,
            ChangedBy = Context.CurrentUserId,
            Cycle = entity.ClosureCycle,
            FromStage = fromStage,
            ToStage = kind == OpportunityHistoryKinds.CloseDate ? null : entity.Stage,
            FromStatus = fromStatus,
            ToStatus = kind == OpportunityHistoryKinds.CloseDate ? null : entity.Status,
            FromCloseDate = fromClose,
            ToCloseDate = toClose,
            Reason = created ? null : Context.OpportunityGate.Reason,
            Details = details,
        });
    }

    // Kapanış anındaki bilgiler (yeniden açılsa bile tarihçede kalır). Tutarlar hassastır: tarihçe uç noktası izni olmayana vermez.
    private static string CloseDetails(Opportunity entity) => new JsonObject
    {
        ["amount"] = entity.Amount,
        ["currency"] = entity.Currency,
        ["wonAmount"] = entity.WonAmount,
        ["wonCurrency"] = entity.WonCurrency,
        ["actualCloseDate"] = entity.ActualCloseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["lossReason"] = entity.LossReason,
        ["winningCompetitorId"] = entity.WinningCompetitorId?.ToString(),
    }.ToJsonString();
}
