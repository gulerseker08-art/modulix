using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// Demirbaşlar (ADR-0042 dilim 4). Envanter numarası büyük harfe çevrilir ve tenant içinde tektir; para birimi tutarla birlikte girilir. Durum elle
/// yalnızca depoda / bakımda / hurda / kayıp arasında değişir; "zimmetli" durumu ve zimmetli çalışan yalnızca zimmet kaydından gelir, zimmetteki
/// demirbaşın durumu iade ya da kayıp komutuyla değişir. Hiç zimmetlenmemiş demirbaş silinebilir; zimmet geçmişi olan silinmez (hurdaya ayrılır).
/// Veri kapsamı alanı şu anki zimmetli kullanıcıdır: "kendi" kapsamındaki çalışan üzerindeki demirbaşları görür.
/// </summary>
internal sealed partial class AssetRecordStore(HrStoreContext context) : HrRecordStore<Asset>(context), IRecordLabelProvider
{
    private readonly Dictionary<Guid, string> names = [];

    public override string EntityKey => HrModule.Entities.Asset;

    protected override string ReadPermission => HrModule.Permissions.AssetsRead;

    protected override string WritePermission => HrModule.Permissions.AssetsManage;

    protected override string? OwnerFieldName => "currentUserId";

    protected override IQueryable<Asset> ScopeToOwners(IQueryable<Asset> source, IReadOnlySet<Guid> owners)
    {
        Guid[] ids = [.. owners];
        return source.Where(a => a.CurrentUserId != null && ids.Contains(a.CurrentUserId.Value));
    }

    /// <summary>Demirbaşı başka listelerde envanter numarası ve adıyla tanıtır.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> LabelsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        Guid[] wanted = [.. ids];
        return await Context.Data.Set<Asset>().AsNoTracking().Where(a => wanted.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.AssetTag + " · " + a.Name, cancellationToken);
    }

    protected override IReadOnlyList<RecordField<Asset>> Fields => fields ??=
    [
        Field.Text<Asset>("assetTag", Asset.TagMaxLength, a => a.AssetTag, (a, v) => a.AssetTag = v!, required: true, upperCase: true),
        Field.Text<Asset>("name", Asset.NameMaxLength, a => a.Name, (a, v) => a.Name = v!, required: true),
        Field.ListChoice<Asset>("categoryKey", HrModule.Lists.AssetCategory, a => a.CategoryKey, (a, v) => a.CategoryKey = v ?? string.Empty),
        Field.Text<Asset>("serialNumber", Asset.SerialMaxLength, a => a.SerialNumber, (a, v) => a.SerialNumber = v),
        Field.Text<Asset>("brand", Asset.BrandMaxLength, a => a.Brand, (a, v) => a.Brand = v),
        Field.Text<Asset>("model", Asset.BrandMaxLength, a => a.Model, (a, v) => a.Model = v),
        Field.Date<Asset>("purchaseDate", a => a.PurchaseDate, (a, v) => a.PurchaseDate = v),
        Field.Decimal<Asset>("purchaseCost", a => a.PurchaseCost, (a, v) => a.PurchaseCost = v, 2, 0m, 100_000_000m),
        Field.Text<Asset>("currency", 3, a => a.Currency, (a, v) => a.Currency = v, upperCase: true),
        Field.Choice<Asset>("status", Asset.Statuses.All, a => a.Status, (a, v) => a.Status = v),
        Field.Text<Asset>("notes", Asset.NotesMaxLength, a => a.Notes, (a, v) => a.Notes = v),

        // Zimmet kaydından türetilir; genel uçtan gelen değer yok sayılır.
        Field.Computed<Asset>("currentAssignmentId", a => a.CurrentAssignmentId?.ToString()),
        Field.Computed<Asset>("currentEmployeeId", a => a.CurrentEmployeeId?.ToString()),
        Field.Computed<Asset>("currentUserId", a => a.CurrentUserId?.ToString()),
        Field.Computed<Asset>("currentEmployeeName", a => a.CurrentEmployeeId is { } id ? names.GetValueOrDefault(id) : null),
    ];

    private IReadOnlyList<RecordField<Asset>>? fields;

    protected override IReadOnlyDictionary<string, RecordFilter<Asset>> Filters { get; } = new Dictionary<string, RecordFilter<Asset>>
    {
        ["status"] = (q, v) => v.Split(',') is var statuses && statuses.All(Asset.Statuses.All.Contains) ? q.Where(a => statuses.Contains(a.Status)) : null,
        ["categoryKey"] = (q, v) => v.Length is > 0 and <= 100 ? q.Where(a => a.CategoryKey == v) : null,
        ["currentEmployeeId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(a => a.CurrentEmployeeId == id) : null,
    };

    protected override IOrderedQueryable<Asset> Order(IQueryable<Asset> source) => source.OrderBy(a => a.AssetTag);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("assetTag", "name", "categoryKey", "status", "purchaseDate", "createdAt");

    protected override IOrderedQueryable<Asset> Order(IQueryable<Asset> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "assetTag" => By(source, a => a.AssetTag, desc),
            "name" => By(source, a => a.Name, desc).ThenBy(a => a.AssetTag),
            "categoryKey" => By(source, a => a.CategoryKey, desc).ThenBy(a => a.AssetTag),
            "status" => By(source, a => a.Status, desc).ThenBy(a => a.AssetTag),
            "purchaseDate" => ByNullsLast(source, a => a.PurchaseDate, desc).ThenBy(a => a.AssetTag),
            "createdAt" => By(source, a => a.CreatedAt, desc),
            _ => Order(source),
        };
    }

    protected override IQueryable<Asset> Search(IQueryable<Asset> source, string term)
    {
        var upper = term.Trim().ToUpperInvariant();
        return source.Where(a => a.AssetTag.Contains(upper) || a.Name.Contains(term) || (a.SerialNumber != null && a.SerialNumber.Contains(term))
            || (a.Brand != null && a.Brand.Contains(term)) || (a.Model != null && a.Model.Contains(term)));
    }

    protected override Task PrepareAsync(IReadOnlyCollection<Asset> entities, CancellationToken cancellationToken) =>
        EmployeeNames.LoadAsync(Context, entities.Where(a => a.CurrentEmployeeId is not null).Select(a => a.CurrentEmployeeId!.Value), names, cancellationToken);

    protected override async Task AppliedAsync(Asset entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (await Context.Data.Set<Asset>().AsNoTracking().AnyAsync(a => a.AssetTag == entity.AssetTag && a.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("assetTag", "Bu envanter numarası başka bir demirbaşta kayıtlı."));
        }

        if (entity.CurrentAssignmentId is null && entity.Status == Asset.Statuses.Assigned)
        {
            errors.Add(new RecordFieldError("status", "\"Zimmetli\" durumu yalnızca zimmetle gelir; demirbaşı çalışana zimmetleyin."));
        }
        else if (entity.CurrentAssignmentId is not null && entity.Status != Asset.Statuses.Assigned)
        {
            errors.Add(new RecordFieldError("status", "Zimmetteki demirbaşın durumu iade ya da kayıp kaydıyla değişir."));
        }

        if (entity.Currency is { } currency && !CurrencyPattern().IsMatch(currency))
        {
            errors.Add(new RecordFieldError("currency", "Para birimi üç harfli kod olmalıdır (ör. TRY)."));
        }
        else if (entity.PurchaseCost is not null && entity.Currency is null)
        {
            errors.Add(new RecordFieldError("currency", "Alış tutarıyla birlikte para birimi girilmelidir."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override async Task BeforeDeleteAsync(Asset entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<AssetAssignment>().AsNoTracking().AnyAsync(a => a.AssetId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Zimmet geçmişi olan demirbaş silinmez; durumunu \"Hurda\" yapın.");
        }
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();
}

/// <summary>
/// Zimmetler (ADR-0042 dilim 4). Zimmet açılırken: demirbaş depoda olmalı (bakımda, hurda, kayıp ya da başka çalışanda olan zimmetlenmez); çalışan
/// ayrılmamış olmalı; teslim tarihi ileri olamaz; beklenen iade tarihi teslimden önce olamaz. Demirbaş aynı işlemde "zimmetli" olur. Zimmet
/// genel uçtan değiştirilmez ve silinmez; teslim alındı onayı, iade ve kayıp <see cref="AssetWorkflow"/> komutlarıyla. Kayıt sahibi çalışanın
/// kullanıcısıdır ("kendi" kapsamındaki çalışan yalnızca kendi zimmetlerini görür).
/// </summary>
internal sealed class AssetAssignmentRecordStore(HrStoreContext context) : HrRecordStore<AssetAssignment>(context)
{
    private readonly Dictionary<Guid, string> names = [];
    private readonly Dictionary<Guid, (string Tag, string Name)> assets = [];

    public override string EntityKey => HrModule.Entities.AssetAssignment;

    protected override string ReadPermission => HrModule.Permissions.AssetsRead;

    protected override string WritePermission => HrModule.Permissions.AssetsManage;

    protected override string? OwnerFieldName => "employeeUserId";

    protected override IQueryable<AssetAssignment> ScopeToOwners(IQueryable<AssetAssignment> source, IReadOnlySet<Guid> owners)
    {
        Guid[] ids = [.. owners];
        return source.Where(a => a.OwnerId != null && ids.Contains(a.OwnerId.Value));
    }

    protected override string? UpdateBlockedMessage =>
        Context.Gate.IsOpen ? null : "Zimmet değiştirilemez; iade edip yeniden zimmetleyin.";

    protected override IReadOnlyList<RecordField<AssetAssignment>> Fields => fields ??=
    [
        Field.Reference<AssetAssignment, Asset>("assetId", "Demirbaş bulunamadı.", a => a.AssetId, (a, v) => a.AssetId = v!.Value, required: true, createOnly: true),
        Field.Reference<AssetAssignment, Employee>("employeeId", "Çalışan bulunamadı.", a => a.EmployeeId, (a, v) => a.EmployeeId = v!.Value, required: true, createOnly: true),
        Field.Date<AssetAssignment>("assignedOn", a => a.AssignedOn, (a, v) => a.AssignedOn = v!.Value, required: true),
        Field.Date<AssetAssignment>("expectedReturnOn", a => a.ExpectedReturnOn, (a, v) => a.ExpectedReturnOn = v),
        Field.Text<AssetAssignment>("conditionOut", AssetAssignment.TextMaxLength, a => a.ConditionOut, (a, v) => a.ConditionOut = v),

        Field.Computed<AssetAssignment>("status", a => a.Status),
        Field.Computed<AssetAssignment>("employeeUserId", a => a.OwnerId?.ToString()),
        Field.Computed<AssetAssignment>("assignedByUserId", a => a.AssignedByUserId?.ToString()),
        Field.Computed<AssetAssignment>("acknowledgedAt", a => a.AcknowledgedAt?.ToString("O")),
        Field.Computed<AssetAssignment>("returnedOn", a => a.ReturnedOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
        Field.Computed<AssetAssignment>("conditionIn", a => a.ConditionIn),
        Field.Computed<AssetAssignment>("closedByUserId", a => a.ClosedByUserId?.ToString()),
        Field.Computed<AssetAssignment>("employeeName", a => names.GetValueOrDefault(a.EmployeeId)),
        Field.Computed<AssetAssignment>("assetTag", a => assets.TryGetValue(a.AssetId, out var asset) ? asset.Tag : null),
        Field.Computed<AssetAssignment>("assetName", a => assets.TryGetValue(a.AssetId, out var asset) ? asset.Name : null),
    ];

    private IReadOnlyList<RecordField<AssetAssignment>>? fields;

    protected override IReadOnlyDictionary<string, RecordFilter<AssetAssignment>> Filters { get; } = new Dictionary<string, RecordFilter<AssetAssignment>>
    {
        ["status"] = (q, v) => v.Split(',') is var statuses && statuses.All(AssetAssignment.Statuses.All.Contains) ? q.Where(a => statuses.Contains(a.Status)) : null,
        ["assetId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(a => a.AssetId == id) : null,
        ["employeeId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(a => a.EmployeeId == id) : null,
        ["employeeUserId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(a => a.OwnerId == id) : null,
        ["acknowledged"] = (q, v) => v switch { "true" => q.Where(a => a.AcknowledgedAt != null), "false" => q.Where(a => a.AcknowledgedAt == null), _ => null },
    };

    protected override IOrderedQueryable<AssetAssignment> Order(IQueryable<AssetAssignment> source) =>
        source.OrderByDescending(a => a.AssignedOn).ThenByDescending(a => a.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("assignedOn", "status", "createdAt");

    protected override IOrderedQueryable<AssetAssignment> Order(IQueryable<AssetAssignment> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "assignedOn" => By(source, a => a.AssignedOn, desc).ThenBy(a => a.CreatedAt),
            "status" => By(source, a => a.Status, desc).ThenByDescending(a => a.AssignedOn),
            "createdAt" => By(source, a => a.CreatedAt, desc),
            _ => Order(source),
        };
    }

    protected override IQueryable<AssetAssignment> Search(IQueryable<AssetAssignment> source, string term)
    {
        var upper = term.Trim().ToUpperInvariant();
        var matching = Context.Data.Set<Asset>().AsNoTracking().Where(a => a.AssetTag.Contains(upper) || a.Name.Contains(term)).Select(a => a.Id);
        return source.Where(a => matching.Contains(a.AssetId));
    }

    protected override async Task PrepareAsync(IReadOnlyCollection<AssetAssignment> entities, CancellationToken cancellationToken)
    {
        await EmployeeNames.LoadAsync(Context, entities.Select(a => a.EmployeeId), names, cancellationToken);
        Guid[] missing = [.. entities.Select(a => a.AssetId).Where(id => id != Guid.Empty && !assets.ContainsKey(id)).Distinct()];
        if (missing.Length > 0)
        {
            foreach (var asset in await Context.Data.Set<Asset>().AsNoTracking().Where(a => missing.Contains(a.Id))
                         .Select(a => new { a.Id, a.AssetTag, a.Name }).ToListAsync(cancellationToken))
            {
                assets[asset.Id] = (asset.AssetTag, asset.Name);
            }
        }
    }

    protected override Task BeforeDeleteAsync(AssetAssignment entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Zimmet kayıtları silinmez; demirbaşı iade alın.");

    protected override async Task AppliedAsync(AssetAssignment entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (!isNew)
        {
            await ApplyCommandAsync(entity, cancellationToken);
            return;
        }

        var errors = new List<RecordFieldError>();
        var asset = await Context.Data.Set<Asset>().SingleAsync(a => a.Id == entity.AssetId, cancellationToken);
        if (asset.Status != Asset.Statuses.InStock)
        {
            errors.Add(new RecordFieldError("assetId", asset.Status == Asset.Statuses.Assigned
                ? "Demirbaş başka bir çalışana zimmetli; önce iade alın."
                : "Yalnızca depodaki demirbaş zimmetlenir (bakımda, hurda ya da kayıp değil)."));
        }

        var employee = await Context.Data.Set<Employee>().AsNoTracking().Where(e => e.Id == entity.EmployeeId)
            .Select(e => new { e.UserId, e.Status }).SingleAsync(cancellationToken);
        if (employee.Status == Employee.Statuses.Terminated)
        {
            errors.Add(new RecordFieldError("employeeId", "Ayrılmış çalışana zimmet verilmez."));
        }

        if (entity.AssignedOn > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))
        {
            errors.Add(new RecordFieldError("assignedOn", "Teslim tarihi ileri bir gün olamaz."));
        }

        if (entity.ExpectedReturnOn is { } expected && expected < entity.AssignedOn)
        {
            errors.Add(new RecordFieldError("expectedReturnOn", "Beklenen iade tarihi teslimden önce olamaz."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        entity.OwnerId = employee.UserId;
        entity.AssignedByUserId = Context.CurrentUserId;
        asset.AssignTo(entity.Id, entity.EmployeeId, employee.UserId);
    }

    /// <summary>
    /// Komutla gelen değişiklik (kapı açık): teslim alındı onayı (durum değişmez) ya da iade/kayıp (zimmet kapanır, demirbaş aynı işlemde depoya ya da
    /// kayba döner).
    /// </summary>
    private async Task ApplyCommandAsync(AssetAssignment entity, CancellationToken cancellationToken)
    {
        if (Context.Gate.Status is not { } status)
        {
            return;
        }

        if (status == AssetAssignment.Statuses.Active)
        {
            entity.AcknowledgedAt = DateTimeOffset.UtcNow;
            return;
        }

        entity.Status = status;
        entity.ReturnedOn = DateOnly.FromDateTime(DateTime.UtcNow);
        entity.ConditionIn = Context.Gate.Reason;
        entity.ClosedByUserId = Context.CurrentUserId;

        var asset = await Context.Data.Set<Asset>().SingleAsync(a => a.Id == entity.AssetId, cancellationToken);
        asset.Release(status == AssetAssignment.Statuses.Lost ? Asset.Statuses.Lost : Asset.Statuses.InStock);
    }
}
