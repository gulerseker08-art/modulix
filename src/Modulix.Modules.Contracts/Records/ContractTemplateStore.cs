using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Records;

/// <summary>
/// Sözleşme şablonu kayıt deposu (ADR-0022, dilim 4). Okuma sözleşme okuma izniyle, yazma <c>contracts.templates.edit</c> ile; durum, sürüm ve özet
/// sistem alanıdır (yayımlama/arşivleme <see cref="IContractTemplates"/> ile, ayrı izinle). Metin her kayıtta doğrulanır: bilinmeyen değişken,
/// kod ya da bozuk söz dizimi kaydedilemez. Arşivlenmiş şablon düzenlenmez; yayımlanmış ya da belgesi olan şablon silinmez (arşivlenir).
/// </summary>
internal sealed class ContractTemplateRecordStore(ContractsStoreContext context) : ContractsRecordStore<ContractTemplate>(context)
{
    public override string EntityKey => ContractsModule.Entities.ContractTemplate;

    protected override string ReadPermission => ContractsModule.Permissions.ContractsRead;

    protected override string WritePermission => ContractsModule.Permissions.TemplatesEdit;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<ContractTemplate>> Fields { get; } =
    [
        Field.Text<ContractTemplate>("name", ContractTemplate.NameMaxLength, t => t.Name, (t, v) => t.Name = v!, required: true),
        Field.Choice<ContractTemplate>("language", TemplateLanguages.All, t => t.Language, (t, v) => t.Language = v),
        Field.ListChoice<ContractTemplate>("contractType", ContractsModule.Lists.ContractType, t => t.ContractType, (t, v) => t.ContractType = v, required: false),
        Field.Text<ContractTemplate>("usageScope", ContractTemplate.ScopeMaxLength, t => t.UsageScope, (t, v) => t.UsageScope = v),
        Field.Text<ContractTemplate>("description", ContractTemplate.DescriptionMaxLength, t => t.Description, (t, v) => t.Description = v),
        Field.Text<ContractTemplate>("body", ContractTemplate.BodyMaxLength, t => t.Body, (t, v) => t.Body = v!, required: true),
        Field.Flag<ContractTemplate>("isExample", t => t.IsExample, (t, v) => t.IsExample = v),
        Field.Text<ContractTemplate>("bodySha256", 64, t => t.BodySha256, (t, v) => t.BodySha256 = v!).AsSystem(),
        Field.Text<ContractTemplate>("status", Contract.StatusMaxLength, t => t.Status, (t, v) => t.Status = v!).AsSystem(),
        Field.Integer<ContractTemplate>("publishedVersionNo", t => t.PublishedVersionNo, (t, v) => t.PublishedVersionNo = v).AsSystem(),
        Field.Id<ContractTemplate>("createdBy", t => t.CreatedBy, (t, v) => t.CreatedBy = v).AsSystem(),
        Field.Id<ContractTemplate>("updatedBy", t => t.UpdatedBy, (t, v) => t.UpdatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ContractTemplate>> Filters { get; } = new Dictionary<string, RecordFilter<ContractTemplate>>
    {
        ["status"] = (q, v, _) => TemplateStatus.All.Contains(v) ? q.Where(t => t.Status == v) : null,
        ["language"] = (q, v, _) => TemplateLanguages.All.Contains(v) ? q.Where(t => t.Language == v) : null,
        ["contractType"] = (q, v, _) => v.Trim() is { Length: > 0 } type ? q.Where(t => t.ContractType == null || t.ContractType == type) : null,
        ["example"] = (q, v, _) => FilterValue.Bool(v) is { } flag ? q.Where(t => t.IsExample == flag) : null,
    };

    protected override IOrderedQueryable<ContractTemplate> Order(IQueryable<ContractTemplate> source) => source.OrderBy(t => t.Name).ThenBy(t => t.Language);

    protected override IQueryable<ContractTemplate> Search(IQueryable<ContractTemplate> source, string term) =>
        source.Where(t => t.Name.Contains(term) || (t.Description != null && t.Description.Contains(term)) || (t.UsageScope != null && t.UsageScope.Contains(term)));

    protected override Task<JsonObject> PrepareNewAsync(ContractTemplate entity, JsonObject values, CancellationToken cancellationToken)
    {
        entity.CreatedBy = Context.CurrentUserId;
        return Task.FromResult(values);
    }

    protected override async Task AppliedAsync(ContractTemplate entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var workflow = Context.Gate.IsOpen;

        if (!isNew && !workflow && entity.Status == TemplateStatus.Archived)
        {
            errors.Add(new RecordFieldError("name", "Arşivlenmiş şablon düzenlenemez; önce geri alın."));
        }

        if (values.ContainsKey("body") || isNew)
        {
            foreach (var issue in ContractTemplateEngine.Validate(entity.Body))
            {
                errors.Add(new RecordFieldError("body", issue.Line > 0 ? $"Satır {issue.Line}: {issue.Message}" : issue.Message));
            }
        }

        if ((values.ContainsKey("name") || values.ContainsKey("language") || isNew)
            && await Context.Data.Set<ContractTemplate>().AsNoTracking().AnyAsync(t => t.Id != entity.Id && t.Name == entity.Name && t.Language == entity.Language, cancellationToken))
        {
            errors.Add(new RecordFieldError("name", "Bu adla ve dilde bir şablon zaten var."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        entity.BodySha256 = ContractSnapshots.Hash(entity.Body);
        entity.UpdatedBy = Context.CurrentUserId;
        if (workflow && !isNew && Context.Gate.TemplateEffect is { } effect)
        {
            await effect(Context, entity, cancellationToken);
        }
    }

    protected override async Task BeforeDeleteAsync(ContractTemplate entity, CancellationToken cancellationToken)
    {
        var data = Context.Data;
        if (entity.PublishedVersionNo is not null
            || await data.Set<ContractTemplateVersion>().AnyAsync(v => v.TemplateId == entity.Id, cancellationToken)
            || await data.Set<ContractDocument>().AnyAsync(d => d.TemplateId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Yayımlanmış ya da belgesi olan şablon silinemez; arşivleyin.");
        }
    }
}
