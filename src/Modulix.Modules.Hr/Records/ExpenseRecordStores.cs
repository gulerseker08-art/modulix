using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// Masraf beyanları (ADR-0042 dilim 3). Beyan açılırken: çalışan ayrılmamış olmalı; "masraf yönetimi" izni olmayan kullanıcı yalnızca kendi kartına
/// beyan açar; para birimi üç harfli koddur. Başlık, açıklama ve para birimi yalnızca taslakta ve beyanın sahibi ya da İK tarafından değiştirilir
/// (para birimi satır varken değişmez). Toplam ve satır sayısı satır deposunca yazılır; durum ve karar alanları yalnızca <see cref="ExpenseWorkflow"/>
/// komutlarıyla. Beyan silinmez, iptal edilir. Görünürlük: rolün kapsamındaki sahipler ve onaylayanı kendisi olan beyanlar.
/// </summary>
internal sealed partial class ExpenseClaimRecordStore(HrStoreContext context) : HrRecordStore<ExpenseClaim>(context)
{
    private readonly Dictionary<Guid, string> names = [];

    public override string EntityKey => HrModule.Entities.ExpenseClaim;

    protected override string ReadPermission => HrModule.Permissions.ExpensesRead;

    protected override string WritePermission => HrModule.Permissions.ExpensesRequest;

    protected override string? OwnerFieldName => "employeeUserId";

    protected override bool IsInScope(RecordData record, IReadOnlySet<Guid> owners) =>
        base.IsInScope(record, owners) || (Context.CurrentUserId is { } me && UserIn(record, "approverUserId", new HashSet<Guid> { me }));

    protected override IQueryable<ExpenseClaim> ScopeToOwners(IQueryable<ExpenseClaim> source, IReadOnlySet<Guid> owners)
    {
        Guid[] ids = [.. owners];
        var me = Context.CurrentUserId ?? Guid.Empty;
        return source.Where(c => (c.OwnerId != null && ids.Contains(c.OwnerId.Value)) || c.ApproverUserId == me);
    }

    protected override IReadOnlyList<RecordField<ExpenseClaim>> Fields => fields ??=
    [
        Field.Reference<ExpenseClaim, Employee>("employeeId", "Çalışan bulunamadı.", c => c.EmployeeId, (c, v) => c.EmployeeId = v!.Value, required: true, createOnly: true),
        Field.Text<ExpenseClaim>("title", ExpenseClaim.TitleMaxLength, c => c.Title, (c, v) => c.Title = v!, required: true),
        Field.Text<ExpenseClaim>("currency", 3, c => c.Currency, (c, v) => c.Currency = v ?? "TRY", upperCase: true),
        Field.Text<ExpenseClaim>("description", ExpenseClaim.TextMaxLength, c => c.Description, (c, v) => c.Description = v),

        // Sunucunun yazdığı alanlar: genel uçtan gelen değer yok sayılır.
        Field.Computed<ExpenseClaim>("totalAmount", c => JsonValue.Create(c.TotalAmount)),
        Field.Computed<ExpenseClaim>("lineCount", c => JsonValue.Create(c.LineCount)),
        Field.Computed<ExpenseClaim>("status", c => c.Status),
        Field.Computed<ExpenseClaim>("employeeUserId", c => c.OwnerId?.ToString()),
        Field.Computed<ExpenseClaim>("approverUserId", c => c.ApproverUserId?.ToString()),
        Field.Computed<ExpenseClaim>("submittedAt", c => c.SubmittedAt?.ToString("O")),
        Field.Computed<ExpenseClaim>("decidedByUserId", c => c.DecidedByUserId?.ToString()),
        Field.Computed<ExpenseClaim>("decidedAt", c => c.DecidedAt?.ToString("O")),
        Field.Computed<ExpenseClaim>("decisionReason", c => c.DecisionReason),
        Field.Computed<ExpenseClaim>("paidByUserId", c => c.PaidByUserId?.ToString()),
        Field.Computed<ExpenseClaim>("paidAt", c => c.PaidAt?.ToString("O")),
        Field.Computed<ExpenseClaim>("paymentReference", c => c.PaymentReference),
        Field.Computed<ExpenseClaim>("employeeName", c => names.GetValueOrDefault(c.EmployeeId)),
    ];

    private IReadOnlyList<RecordField<ExpenseClaim>>? fields;

    protected override IReadOnlyDictionary<string, RecordFilter<ExpenseClaim>> Filters { get; } = new Dictionary<string, RecordFilter<ExpenseClaim>>
    {
        ["status"] = (q, v) => v.Split(',') is var statuses && statuses.All(ExpenseClaim.Statuses.All.Contains) ? q.Where(c => statuses.Contains(c.Status)) : null,
        ["employeeId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(c => c.EmployeeId == id) : null,
        ["approverUserId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(c => c.ApproverUserId == id) : null,
        ["employeeUserId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(c => c.OwnerId == id) : null,
        ["currency"] = (q, v) => v.Length == 3 ? q.Where(c => c.Currency == v.ToUpperInvariant()) : null,
    };

    private IQueryable<Person> People => Context.Data.Set<Person>().AsNoTracking();

    private IQueryable<Employee> Employees => Context.Data.Set<Employee>().AsNoTracking();

    protected override IOrderedQueryable<ExpenseClaim> Order(IQueryable<ExpenseClaim> source) => source.OrderByDescending(c => c.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("createdAt", "submittedAt", "employee", "title", "totalAmount", "status");

    protected override IOrderedQueryable<ExpenseClaim> Order(IQueryable<ExpenseClaim> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "createdAt" => By(source, c => c.CreatedAt, desc),
            "submittedAt" => ByNullsLast(source, c => c.SubmittedAt, desc).ThenByDescending(c => c.CreatedAt),
            "employee" => By(source, c => Employees.Where(e => e.Id == c.EmployeeId)
                .Join(People, e => e.PersonId, p => p.Id, (e, p) => p.FirstName + " " + p.LastName).FirstOrDefault(), desc).ThenByDescending(c => c.CreatedAt),
            "title" => By(source, c => c.Title, desc).ThenByDescending(c => c.CreatedAt),
            "totalAmount" => By(source, c => c.TotalAmount, desc).ThenByDescending(c => c.CreatedAt),
            "status" => By(source, c => c.Status, desc).ThenByDescending(c => c.CreatedAt),
            _ => Order(source),
        };
    }

    protected override IQueryable<ExpenseClaim> Search(IQueryable<ExpenseClaim> source, string term)
    {
        var people = People.Where(p => p.FirstName.Contains(term) || p.LastName.Contains(term) || (p.FirstName + " " + p.LastName).Contains(term)).Select(p => p.Id);
        var employees = Employees.Where(e => people.Contains(e.PersonId)).Select(e => e.Id);
        return source.Where(c => employees.Contains(c.EmployeeId) || c.Title.Contains(term));
    }

    protected override Task PrepareAsync(IReadOnlyCollection<ExpenseClaim> entities, CancellationToken cancellationToken) =>
        EmployeeNames.LoadAsync(Context, entities.Select(c => c.EmployeeId), names, cancellationToken);

    protected override Task BeforeDeleteAsync(ExpenseClaim entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Masraf beyanları silinmez; beyanı iptal edin.");

    protected override async Task AppliedAsync(ExpenseClaim entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (!isNew && Context.Gate.IsOpen)
        {
            ApplyDecision(entity);
            return;
        }

        var errors = new List<RecordFieldError>();
        if (!CurrencyPattern().IsMatch(entity.Currency))
        {
            errors.Add(new RecordFieldError("currency", "Para birimi üç harfli kod olmalıdır (ör. TRY, EUR)."));
        }

        if (isNew)
        {
            var employee = await Employees.Where(e => e.Id == entity.EmployeeId).Select(e => new { e.UserId, e.Status }).SingleAsync(cancellationToken);
            if (employee.Status == Employee.Statuses.Terminated)
            {
                errors.Add(new RecordFieldError("employeeId", "Ayrılmış çalışana masraf beyanı açılamaz."));
            }

            if (employee.UserId != Context.CurrentUserId && !await Context.Access.HasPermissionAsync(HrModule.Permissions.ExpensesManage, cancellationToken))
            {
                errors.Add(new RecordFieldError("employeeId", "Yalnızca kendiniz için masraf beyanı açabilirsiniz."));
            }

            entity.OwnerId = employee.UserId;
        }
        else
        {
            if (entity.Status != ExpenseClaim.Statuses.Draft)
            {
                throw new RecordConflictException("Yalnızca taslak beyan düzenlenebilir; onaylayan beyanı düzeltme için iade edebilir.");
            }

            if (await ExpenseAccess.CannotEditAsync(Context, entity.OwnerId, cancellationToken) is { } denied)
            {
                throw new RecordConflictException(denied);
            }

            if (values.ContainsKey("currency") && entity.LineCount > 0
                && await Context.Data.Set<ExpenseClaim>().AsNoTracking().AnyAsync(c => c.Id == entity.Id && c.Currency != entity.Currency, cancellationToken))
            {
                errors.Add(new RecordFieldError("currency", "Satırı olan beyanın para birimi değiştirilemez."));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    /// <summary>Komutla gelen durum değişikliği (kapı açık): gönderim, karar ve ödeme bilgileri yazılır.</summary>
    private void ApplyDecision(ExpenseClaim entity)
    {
        if (Context.Gate.Status is not { } status)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        switch (status)
        {
            case ExpenseClaim.Statuses.Submitted:
                entity.SubmittedAt = now;
                entity.DecisionReason = null;
                entity.ApproverUserId = Context.Gate.ApproverUserId;
                break;
            case ExpenseClaim.Statuses.Paid:
                entity.PaidAt = now;
                entity.PaidByUserId = Context.CurrentUserId;
                entity.PaymentReference = Context.Gate.Reason;
                break;
            default:
                entity.DecidedAt = now;
                entity.DecidedByUserId = Context.CurrentUserId;
                entity.DecisionReason = Context.Gate.Reason;
                break;
        }

        entity.Status = status;
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();
}

/// <summary>Masraf kayıtlarında düzenleme yetkisi: beyanın sahibi ya da "masraf yönetimi" izni.</summary>
internal static class ExpenseAccess
{
    public static async Task<string?> CannotEditAsync(HrStoreContext context, Guid? owner, CancellationToken cancellationToken) =>
        owner is not null && owner == context.CurrentUserId || await context.Access.HasPermissionAsync(HrModule.Permissions.ExpensesManage, cancellationToken)
            ? null
            : "Yalnızca kendi masraf beyanınızı düzenleyebilirsiniz.";
}

/// <summary>
/// Masraf satırları (ADR-0042 dilim 3). Beyan yalnızca oluştururken seçilir; satır yalnızca beyan taslakken ve beyanın sahibi ya da İK tarafından
/// eklenir, değiştirilir ve silinir. Tutar 0'dan büyük, KDV tutardan büyük olamaz; masraf tarihi ileri olamaz. Her değişiklikte beyanın toplamı ve satır
/// sayısı aynı işlemde yeniden hesaplanır. Fiş/fatura satıra dosya olarak eklenir. Görünürlük beyanınkiyle aynıdır.
/// </summary>
internal sealed class ExpenseLineRecordStore(HrStoreContext context) : HrRecordStore<ExpenseLine>(context)
{
    private readonly Dictionary<Guid, Guid?> approvers = [];

    public override string EntityKey => HrModule.Entities.ExpenseLine;

    protected override string ReadPermission => HrModule.Permissions.ExpensesRead;

    protected override string WritePermission => HrModule.Permissions.ExpensesRequest;

    protected override string? OwnerFieldName => "employeeUserId";

    protected override bool IsInScope(RecordData record, IReadOnlySet<Guid> owners) =>
        base.IsInScope(record, owners) || (Context.CurrentUserId is { } me && UserIn(record, "approverUserId", new HashSet<Guid> { me }));

    protected override IQueryable<ExpenseLine> ScopeToOwners(IQueryable<ExpenseLine> source, IReadOnlySet<Guid> owners)
    {
        Guid[] ids = [.. owners];
        var me = Context.CurrentUserId ?? Guid.Empty;
        var approved = Context.Data.Set<ExpenseClaim>().AsNoTracking().Where(c => c.ApproverUserId == me).Select(c => c.Id);
        return source.Where(l => (l.OwnerId != null && ids.Contains(l.OwnerId.Value)) || approved.Contains(l.ClaimId));
    }

    protected override IReadOnlyList<RecordField<ExpenseLine>> Fields => fields ??=
    [
        Field.Reference<ExpenseLine, ExpenseClaim>("claimId", "Masraf beyanı bulunamadı.", l => l.ClaimId, (l, v) => l.ClaimId = v!.Value, required: true, createOnly: true),
        Field.Date<ExpenseLine>("expenseDate", l => l.ExpenseDate, (l, v) => l.ExpenseDate = v!.Value, required: true),
        Field.ListChoice<ExpenseLine>("expenseTypeKey", HrModule.Lists.ExpenseType, l => l.ExpenseTypeKey, (l, v) => l.ExpenseTypeKey = v ?? string.Empty),
        Field.Text<ExpenseLine>("description", ExpenseLine.DescriptionMaxLength, l => l.Description, (l, v) => l.Description = v),
        Field.Decimal<ExpenseLine>("amount", l => l.Amount, (l, v) => l.Amount = v ?? 0m, 2, 0.01m, ExpenseLine.MaxAmount, required: true),
        Field.Decimal<ExpenseLine>("vatAmount", l => l.VatAmount, (l, v) => l.VatAmount = v, 2, 0m, ExpenseLine.MaxAmount),
        Field.Computed<ExpenseLine>("employeeUserId", l => l.OwnerId?.ToString()),
        Field.Computed<ExpenseLine>("approverUserId", l => approvers.GetValueOrDefault(l.ClaimId)?.ToString()),
    ];

    private IReadOnlyList<RecordField<ExpenseLine>>? fields;

    protected override IReadOnlyDictionary<string, RecordFilter<ExpenseLine>> Filters { get; } = new Dictionary<string, RecordFilter<ExpenseLine>>
    {
        ["claimId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(l => l.ClaimId == id) : null,
        ["expenseTypeKey"] = (q, v) => v.Length is > 0 and <= 100 ? q.Where(l => l.ExpenseTypeKey == v) : null,
    };

    protected override IOrderedQueryable<ExpenseLine> Order(IQueryable<ExpenseLine> source) => source.OrderBy(l => l.ExpenseDate).ThenBy(l => l.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("expenseDate", "expenseTypeKey", "amount");

    protected override IOrderedQueryable<ExpenseLine> Order(IQueryable<ExpenseLine> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "expenseDate" => By(source, l => l.ExpenseDate, desc).ThenBy(l => l.CreatedAt),
            "expenseTypeKey" => By(source, l => l.ExpenseTypeKey, desc).ThenBy(l => l.ExpenseDate),
            "amount" => By(source, l => l.Amount, desc).ThenBy(l => l.ExpenseDate),
            _ => Order(source),
        };
    }

    protected override IQueryable<ExpenseLine> Search(IQueryable<ExpenseLine> source, string term) =>
        source.Where(l => l.Description != null && l.Description.Contains(term));

    protected override async Task PrepareAsync(IReadOnlyCollection<ExpenseLine> entities, CancellationToken cancellationToken)
    {
        Guid[] missing = [.. entities.Select(l => l.ClaimId).Where(id => id != Guid.Empty && !approvers.ContainsKey(id)).Distinct()];
        if (missing.Length == 0)
        {
            return;
        }

        foreach (var claim in await Context.Data.Set<ExpenseClaim>().AsNoTracking().Where(c => missing.Contains(c.Id))
                     .Select(c => new { c.Id, c.ApproverUserId }).ToListAsync(cancellationToken))
        {
            approvers[claim.Id] = claim.ApproverUserId;
        }
    }

    protected override async Task AppliedAsync(ExpenseLine entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var claim = await EditableClaimAsync(entity.ClaimId, cancellationToken);
        var errors = new List<RecordFieldError>();
        if (entity.ExpenseDate > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))
        {
            errors.Add(new RecordFieldError("expenseDate", "Masraf tarihi ileri bir gün olamaz."));
        }

        if (entity.VatAmount is { } vat && vat > entity.Amount)
        {
            errors.Add(new RecordFieldError("vatAmount", "KDV tutarı, KDV dahil tutardan büyük olamaz."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        if (isNew)
        {
            entity.OwnerId = claim.OwnerId;
        }

        await RecalculateAsync(claim, entity.Id, entity.Amount, cancellationToken);
    }

    protected override async Task BeforeDeleteAsync(ExpenseLine entity, CancellationToken cancellationToken)
    {
        var claim = await EditableClaimAsync(entity.ClaimId, cancellationToken);
        await RecalculateAsync(claim, entity.Id, amount: null, cancellationToken);
    }

    /// <summary>Beyan (izlenen) taslak olmalı ve kullanıcı beyanın sahibi ya da İK olmalı; değilse 409.</summary>
    private async Task<ExpenseClaim> EditableClaimAsync(Guid claimId, CancellationToken cancellationToken)
    {
        var claim = await Context.Data.Set<ExpenseClaim>().SingleAsync(c => c.Id == claimId, cancellationToken);
        if (claim.Status != ExpenseClaim.Statuses.Draft)
        {
            throw new RecordConflictException("Satırlar yalnızca taslak beyanda eklenir, değiştirilir ya da silinir.");
        }

        return await ExpenseAccess.CannotEditAsync(Context, claim.OwnerId, cancellationToken) is { } denied
            ? throw new RecordConflictException(denied)
            : claim;
    }

    /// <summary>Beyanın toplamı: diğer satırlar + bu satırın yeni tutarı (silmede yok).</summary>
    private async Task RecalculateAsync(ExpenseClaim claim, Guid lineId, decimal? amount, CancellationToken cancellationToken)
    {
        var others = await Context.Data.Set<ExpenseLine>().AsNoTracking().Where(l => l.ClaimId == claim.Id && l.Id != lineId)
            .GroupBy(_ => 1).Select(g => new { Sum = g.Sum(l => l.Amount), Count = g.Count() }).SingleOrDefaultAsync(cancellationToken);
        claim.SetTotals((others?.Sum ?? 0m) + (amount ?? 0m), (others?.Count ?? 0) + (amount is null ? 0 : 1));
    }
}
