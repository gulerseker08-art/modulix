using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Records;

/// <summary>
/// Sözleşme başlığı kayıt deposu (ADR-0022). Durum, sürüm ve toplam alanları sistem alanıdır: genel uçlardan yazılamaz, yalnızca iş
/// akışı kapısı açıkken yazılır. İçerik yalnızca taslakta ve imzaya gitmemişken düzenlenir (<see cref="ContractRules.CanEditContent"/>).
/// Müşteri ve kişi ortak kayıttır; kişi seçildiyse müşteri tarafına bağlı olmalıdır. Son bildirim tarihi sunucuda hesaplanır.
/// </summary>
internal sealed class ContractRecordStore(ContractsStoreContext context) : ContractsRecordStore<Contract>(context)
{
    /// <summary>Düzenlenmesi "içerik değişikliği" sayılan alanlar; durum kuralına tabidir.</summary>
    private static readonly string[] ContentFields =
    [
        "title", "type", "description", "partyId", "personId", "previousContractId", "startDate", "endDate", "isIndefinite", "currency", "priceIncludesTax",
        "contractValue", "paymentTerms", "renewalMode", "renewalPeriodMonths", "noticePeriodDays", "confidentiality",
    ];

    public override string EntityKey => ContractsModule.Entities.Contract;

    protected override string ReadPermission => ContractsModule.Permissions.ContractsRead;

    protected override string WritePermission => ContractsModule.Permissions.ContractsWrite;

    protected override IReadOnlyList<RecordField<Contract>> Fields { get; } =
    [
        Field.Text<Contract>("number", Contract.NumberMaxLength, c => c.Number, (c, v) => c.Number = v!).AsSystem(),
        Field.Text<Contract>("title", Contract.TitleMaxLength, c => c.Title, (c, v) => c.Title = v!, required: true),
        Field.ListChoice<Contract>("type", ContractsModule.Lists.ContractType, c => c.Type, (c, v) => c.Type = v ?? string.Empty),
        Field.Text<Contract>("description", Contract.TextMaxLength, c => c.Description, (c, v) => c.Description = v),
        Field.Shared<Contract, Party>("partyId", "Müşteri bulunamadı.", c => c.PartyId, (c, v) => c.PartyId = v!.Value, required: true),
        Field.Shared<Contract, Person>("personId", "Kişi bulunamadı.", c => c.PersonId, (c, v) => c.PersonId = v),
        Field.Owner<Contract>(OwnerField, c => c.OwnerId, (c, v) => c.OwnerId = v),
        Field.Reference<Contract, Contract>("previousContractId", "Önceki sözleşme bulunamadı.", c => c.PreviousContractId, (c, v) => c.PreviousContractId = v),
        Field.Id<Contract>("sourceQuoteId", c => c.SourceQuoteId, (c, v) => c.SourceQuoteId = v).AsSystem(),
        Field.Text<Contract>("sourceQuoteNumber", Contract.NumberMaxLength, c => c.SourceQuoteNumber, (c, v) => c.SourceQuoteNumber = v).AsSystem(),
        Field.Integer<Contract>("sourceQuoteRevisionNo", c => c.SourceQuoteRevisionNo, (c, v) => c.SourceQuoteRevisionNo = v).AsSystem(),
        Field.Text<Contract>("sourceQuoteSha256", ContractVersion.HashLength, c => c.SourceQuoteSha256, (c, v) => c.SourceQuoteSha256 = v).AsSystem(),
        Field.Id<Contract>("sourceOpportunityId", c => c.SourceOpportunityId, (c, v) => c.SourceOpportunityId = v).AsSystem(),
        Field.Date<Contract>("startDate", c => c.StartDate, (c, v) => c.StartDate = v),
        Field.Date<Contract>("endDate", c => c.EndDate, (c, v) => c.EndDate = v),
        Field.Flag<Contract>("isIndefinite", c => c.IsIndefinite, (c, v) => c.IsIndefinite = v),
        Field.Currency<Contract>("currency", c => c.Currency, (c, v) => c.Currency = v),
        Field.Flag<Contract>("priceIncludesTax", c => c.PriceIncludesTax, (c, v) => c.PriceIncludesTax = v),
        Field.Money<Contract>("contractValue", c => c.ContractValue, (c, v) => c.ContractValue = v),
        Field.Text<Contract>("paymentTerms", Contract.TextMaxLength, c => c.PaymentTerms, (c, v) => c.PaymentTerms = v),
        Field.Choice<Contract>("renewalMode", RenewalMode.All, c => c.RenewalMode, (c, v) => c.RenewalMode = v),
        Field.Integer<Contract>("renewalPeriodMonths", c => c.RenewalPeriodMonths, (c, v) => c.RenewalPeriodMonths = v, max: 120),
        Field.Integer<Contract>("noticePeriodDays", c => c.NoticePeriodDays, (c, v) => c.NoticePeriodDays = v, max: 3650),
        Field.Date<Contract>("noticeDeadline", c => c.NoticeDeadline, (c, v) => c.NoticeDeadline = v).AsSystem(),
        Field.Choice<Contract>("confidentiality", Confidentiality.All, c => c.Confidentiality, (c, v) => c.Confidentiality = v),
        Field.Flag<Contract>("hasNonStandardTerms", c => c.HasNonStandardTerms, (c, v) => c.HasNonStandardTerms = v),
        Field.Choice<Contract>("riskLevel", RiskLevel.All, c => c.RiskLevel, (c, v) => c.RiskLevel = v),
        Field.Text<Contract>("preparationStatus", Contract.StatusMaxLength, c => c.PreparationStatus, (c, v) => c.PreparationStatus = v!).AsSystem(),
        Field.Text<Contract>("approvalStatus", Contract.StatusMaxLength, c => c.ApprovalStatus, (c, v) => c.ApprovalStatus = v!).AsSystem(),
        Field.Text<Contract>("signatureStatus", Contract.StatusMaxLength, c => c.SignatureStatus, (c, v) => c.SignatureStatus = v!).AsSystem(),
        Field.Text<Contract>("effectiveStatus", Contract.StatusMaxLength, c => c.EffectiveStatus, (c, v) => c.EffectiveStatus = v!).AsSystem(),
        Field.Text<Contract>("statusReason", Contract.ReasonMaxLength, c => c.StatusReason, (c, v) => c.StatusReason = v).AsSystem(),
        Field.Integer<Contract>("currentVersionNo", c => c.CurrentVersionNo, (c, v) => c.CurrentVersionNo = v ?? 1).AsSystem(),
        Field.Integer<Contract>("effectiveVersionNo", c => c.EffectiveVersionNo, (c, v) => c.EffectiveVersionNo = v).AsSystem(),
        Field.Money<Contract>("netTotal", c => c.NetTotal, (c, v) => c.NetTotal = v ?? 0m).AsSystem(),
        Field.Money<Contract>("taxTotal", c => c.TaxTotal, (c, v) => c.TaxTotal = v ?? 0m).AsSystem(),
        Field.Money<Contract>("grossTotal", c => c.GrossTotal, (c, v) => c.GrossTotal = v ?? 0m).AsSystem(),
        Field.Text<Contract>("recurringSummary", Contract.SummaryMaxLength, c => c.RecurringSummary, (c, v) => c.RecurringSummary = v).AsSystem(),
        Field.Time<Contract>("calculatedAt", c => c.CalculatedAt, (c, v) => c.CalculatedAt = v).AsSystem(),
        Field.Id<Contract>("createdBy", c => c.CreatedBy, (c, v) => c.CreatedBy = v).AsSystem(),
        Field.Id<Contract>("updatedBy", c => c.UpdatedBy, (c, v) => c.UpdatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Contract>> Filters { get; } = new Dictionary<string, RecordFilter<Contract>>
    {
        ["partyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.PartyId == id) : null,
        ["personId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.PersonId == id) : null,
        ["previousContractId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.PreviousContractId == id) : null,
        ["sourceQuoteId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.SourceQuoteId == id) : null,
        ["sourceOpportunityId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.SourceOpportunityId == id) : null,
        [OwnerField] = (q, v, ctx) => FilterValue.User(v, ctx) is { } owner ? q.Where(c => c.OwnerId == owner) : null,
        ["number"] = (q, v, _) => v.Trim() is { Length: > 0 } number ? q.Where(c => c.Number == number) : null,
        ["type"] = (q, v, _) => v.Trim() is { Length: > 0 } type ? q.Where(c => c.Type == type) : null,
        ["preparationStatus"] = (q, v, _) => PreparationStatus.All.Contains(v) ? q.Where(c => c.PreparationStatus == v) : null,
        ["approvalStatus"] = (q, v, _) => ApprovalStatus.All.Contains(v) ? q.Where(c => c.ApprovalStatus == v) : null,
        ["signatureStatus"] = (q, v, _) => SignatureStatus.All.Contains(v) ? q.Where(c => c.SignatureStatus == v) : null,
        ["effectiveStatus"] = (q, v, _) => EffectiveStatus.All.Contains(v) ? q.Where(c => c.EffectiveStatus == v) : null,
        ["renewalMode"] = (q, v, _) => RenewalMode.All.Contains(v) ? q.Where(c => c.RenewalMode == v) : null,
        ["confidentiality"] = (q, v, _) => Confidentiality.All.Contains(v) ? q.Where(c => c.Confidentiality == v) : null,
        ["currency"] = (q, v, _) => v.Trim().ToUpperInvariant() is { Length: Contract.CurrencyLength } code ? q.Where(c => c.Currency == code) : null,
        ["indefinite"] = (q, v, _) => FilterValue.Bool(v) is { } flag ? q.Where(c => c.IsIndefinite == flag) : null,
        ["startFrom"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(c => c.StartDate >= d) : null,
        ["startTo"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(c => c.StartDate <= d) : null,
        ["endFrom"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(c => c.EndDate >= d) : null,
        ["endTo"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(c => c.EndDate <= d) : null,
        ["noticeFrom"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(c => c.NoticeDeadline >= d) : null,
        ["noticeTo"] = (q, v, _) => FilterValue.Date(v) is { } d ? q.Where(c => c.NoticeDeadline <= d) : null,
    };

    protected override IOrderedQueryable<Contract> Order(IQueryable<Contract> source) => source.OrderByDescending(c => c.CreatedAt);

    protected override IQueryable<Contract> Search(IQueryable<Contract> source, string term) =>
        source.Where(c => c.Title.Contains(term) || c.Number.Contains(term) || (c.Description != null && c.Description.Contains(term)));

    private string? before;

    /// <summary>Tarihçe için geçiş öncesi durum özeti (hazırlık/onay/imza/yürürlük).</summary>
    private static string StatusSummary(Contract c) => $"{c.PreparationStatus}/{c.ApprovalStatus}/{c.SignatureStatus}/{c.EffectiveStatus}";

    protected override void OnUpdating(Contract entity) => before = StatusSummary(entity);

    protected override Task<JsonObject> PrepareNewAsync(Contract entity, JsonObject values, CancellationToken cancellationToken)
    {
        entity.Number = ContractRules.NewNumber(DateTimeOffset.UtcNow);
        entity.CreatedBy = Context.CurrentUserId;
        return Task.FromResult(values);
    }

    protected override async Task AppliedAsync(Contract entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var workflow = Context.Gate.IsOpen;
        var contentTouched = ContentFields.Any(values.ContainsKey);

        // İçerik yalnızca taslakta ve imzaya gitmemişken değişir; iş akışı eylemleri (kapı açık) bu kurala tabi değildir.
        if (!isNew && !workflow && contentTouched && !IsTransfer)
        {
            var rule = ContractRules.CanEditContent(entity.PreparationStatus, entity.ApprovalStatus, entity.SignatureStatus, entity.EffectiveStatus);
            if (!rule.Allowed)
            {
                errors.Add(new RecordFieldError("title", rule.Message!));
            }
        }

        foreach (var (field, message) in ContractRules.ValidateDates(
                     entity.StartDate, entity.EndDate, entity.IsIndefinite, entity.RenewalMode, entity.RenewalPeriodMonths, entity.NoticePeriodDays))
        {
            errors.Add(new RecordFieldError(field, message));
        }

        if (entity.PreviousContractId == entity.Id)
        {
            errors.Add(new RecordFieldError("previousContractId", "Sözleşme kendisinin öncülü olamaz."));
        }

        // Bağımlı lookup: kişi, müşteri tarafına bağlı olmalıdır (ADR-0016). Kişi ya da müşteri değiştiyse denetlenir.
        if (entity.PersonId is { } personId && (values.ContainsKey("personId") || values.ContainsKey("partyId"))
            && !await Context.Data.Set<Person>().AsNoTracking().AnyAsync(p => p.Id == personId && p.PartyId == entity.PartyId, cancellationToken))
        {
            errors.Add(new RecordFieldError("personId", "Kişi seçilen müşteriyle ilişkili değil."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        entity.NoticeDeadline = ContractRules.NoticeDeadline(entity.EndDate, entity.NoticePeriodDays, entity.IsIndefinite);
        entity.UpdatedBy = Context.CurrentUserId;

        if (!isNew && values.ContainsKey("priceIncludesTax"))
        {
            await ContractTotals.RecalculateAsync(Context.Data, entity, null, null, cancellationToken);
        }

        if (workflow && !isNew && Context.Gate.EventKind is { } kind)
        {
            ContractHistory.Add(Context, entity, kind, Context.Gate.Reason, before, StatusSummary(entity));
            if (Context.Gate.Effect is { } effect)
            {
                await effect(Context, entity, cancellationToken);
            }
        }
    }

    protected override Task CreatedAsync(Contract entity, CancellationToken cancellationToken)
    {
        // Kapı açıkken oluşturma (ör. tekliften) kendi tarihçe türünü ve gerekçesini taşır.
        ContractHistory.Add(Context, entity, Context.Gate.EventKind ?? ContractEventKinds.Created, Context.Gate.Reason);
        return Task.CompletedTask;
    }

    protected override async Task BeforeDeleteAsync(Contract entity, CancellationToken cancellationToken)
    {
        var rule = ContractRules.CanDelete(entity.SignatureStatus, entity.EffectiveStatus);
        if (!rule.Allowed)
        {
            throw new RecordConflictException(rule.Message!);
        }

        var data = Context.Data;
        if (await data.Set<ContractCommitmentOccurrence>().AnyAsync(o => o.ContractId == entity.Id && o.Status != OccurrenceStatus.Open, cancellationToken))
        {
            throw new RecordConflictException("Gerçekleşmesi kaydedilmiş taahhüdü olan sözleşme silinemez.");
        }

        data.Set<ContractCommitmentOccurrence>().RemoveRange(await data.Set<ContractCommitmentOccurrence>().Where(o => o.ContractId == entity.Id).ToListAsync(cancellationToken));
        data.Set<ContractCommitment>().RemoveRange(await data.Set<ContractCommitment>().Where(c => c.ContractId == entity.Id).ToListAsync(cancellationToken));
        data.Set<ContractLine>().RemoveRange(await data.Set<ContractLine>().Where(l => l.ContractId == entity.Id).ToListAsync(cancellationToken));
        data.Set<ContractParty>().RemoveRange(await data.Set<ContractParty>().Where(p => p.ContractId == entity.Id).ToListAsync(cancellationToken));
        data.Set<ContractEvent>().RemoveRange(await data.Set<ContractEvent>().Where(e => e.ContractId == entity.Id).ToListAsync(cancellationToken));
        data.Set<ContractVersion>().RemoveRange(await data.Set<ContractVersion>().Where(v => v.ContractId == entity.Id).ToListAsync(cancellationToken));
    }
}

/// <summary>Tarihçe türleri. Yeni tür eklemek serbesttir; eski satırlar aynen okunur.</summary>
public static class ContractEventKinds
{
    public const string Created = "Created";
    public const string SentToReview = "SentToReview";
    public const string MarkedReady = "MarkedReady";
    public const string ReopenedDraft = "ReopenedDraft";
    public const string SubmittedForApproval = "SubmittedForApproval";
    public const string ApprovalWithdrawn = "ApprovalWithdrawn";
    public const string Approved = "Approved";
    public const string ApprovalRejected = "ApprovalRejected";
    public const string ApprovalInvalidated = "ApprovalInvalidated";
    public const string ApprovalStepApproved = "ApprovalStepApproved";
    public const string ApprovalReturned = "ApprovalReturned";
    public const string SignatureRequested = "SignatureRequested";
    public const string SignatorySigned = "SignatorySigned";
    public const string SignatureCompleted = "SignatureCompleted";
    public const string SignatureDeclined = "SignatureDeclined";
    public const string SignatureExpired = "SignatureExpired";
    public const string SignatureCancelled = "SignatureCancelled";
    public const string SignatureReminded = "SignatureReminded";
    public const string SignatureEventLogged = "SignatureEventLogged";
    public const string CommitmentDefined = "CommitmentDefined";
    public const string CommitmentCompleted = "CommitmentCompleted";
    public const string CommitmentBreached = "CommitmentBreached";
    public const string CommitmentWaived = "CommitmentWaived";
    public const string CommitmentCancelled = "CommitmentCancelled";
    public const string CommitmentEscalated = "CommitmentEscalated";
    public const string CommitmentEvaluated = "CommitmentEvaluated";
    public const string Cancelled = "Cancelled";
    public const string Revised = "Revised";
    public const string CreatedFromQuote = "CreatedFromQuote";
    public const string DocumentGenerated = "DocumentGenerated";
    public const string VersionFrozen = "VersionFrozen";
}

/// <summary>Tarihçe satırı yazımı; sözleşme güncellemesiyle aynı işlemde kaydedilir.</summary>
internal static class ContractHistory
{
    public static void Add(ContractsStoreContext context, Contract contract, string kind, string? reason, string? from = null, string? to = null) =>
        context.Data.Set<ContractEvent>().Add(new ContractEvent
        {
            ContractId = contract.Id,
            Kind = kind,
            ActorId = context.CurrentUserId,
            FromStatus = from,
            ToStatus = to,
            Reason = reason is { Length: > Contract.ReasonMaxLength } ? reason[..Contract.ReasonMaxLength] : reason,
        });
}

/// <summary>
/// Sözleşmeye bağlı alt kayıtların (taraf, kalem) ortak kuralları: görünürlük ve kapsam üst sözleşmeyi izler; üst sözleşme içerik
/// düzenlemesine kapalıysa alt kayıt da değişmez.
/// </summary>
internal static class ContractChildren
{
    public static async Task<bool> IsContractInScopeAsync(ContractsStoreContext context, Guid contractId, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        return await context.Data.Set<Contract>().AsNoTracking().AnyAsync(c => c.Id == contractId && ids.Contains(c.OwnerId), cancellationToken);
    }

    public static Guid? ContractIdOf(RecordData record) =>
        record.Values.TryGetPropertyValue("contractId", out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
        && Guid.TryParse(text, out var id)
            ? id
            : null;

    /// <summary>
    /// Üst sözleşmeyi yükler ve içerik düzenlemesine açık olduğunu doğrular; değilse doğrulama hatası. Çağıranın veri kapsamı dışındaki
    /// sözleşme (başka tenant'ınki gibi) "bulunamadı" sayılır; varlığı belli edilmez.
    /// </summary>
    public static async Task<Contract> EditableContractAsync(ContractsStoreContext context, Guid contractId, string field, CancellationToken cancellationToken)
    {
        var contract = await context.Data.Set<Contract>().SingleOrDefaultAsync(c => c.Id == contractId, cancellationToken);
        if (contract is not null && !context.Gate.IsOpen && await context.Access.GetOwnersAsync(cancellationToken) is { } owners && !owners.Contains(contract.OwnerId ?? Guid.Empty))
        {
            contract = null;
        }

        if (contract is null)
        {
            throw new RecordValidationException([new RecordFieldError(field, "Sözleşme bulunamadı.")]);
        }

        if (!context.Gate.IsOpen)
        {
            var rule = ContractRules.CanEditContent(contract.PreparationStatus, contract.ApprovalStatus, contract.SignatureStatus, contract.EffectiveStatus);
            if (!rule.Allowed)
            {
                throw new RecordValidationException([new RecordFieldError(field, rule.Message!)]);
            }
        }

        return contract;
    }
}
