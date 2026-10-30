using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Persistence;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Teklif onay vekâleti kayıt deposu (ADR-0019, dilim 10). Okuma ve yazma <c>crm.quote-rules.manage</c> iznindedir (vekâleti yönetici tanımlar);
/// onay anında vekâlet iç okuyucuyla denetlenir. Tüm tenant'a uygulanır.
/// </summary>
internal sealed class QuoteApprovalDelegationRecordStore(CrmStoreContext context) : CrmRecordStore<QuoteApprovalDelegation>(context)
{
    public override string EntityKey => CrmModule.Entities.QuoteApprovalDelegation;

    protected override string ReadPermission => CrmModule.Permissions.QuoteRulesManage;

    protected override string WritePermission => CrmModule.Permissions.QuoteRulesManage;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<QuoteApprovalDelegation>> Fields { get; } =
    [
        Field.Owner<QuoteApprovalDelegation>("fromUserId", d => d.FromUserId == Guid.Empty ? null : d.FromUserId, (d, v) => d.FromUserId = v ?? Guid.Empty),
        Field.Owner<QuoteApprovalDelegation>("toUserId", d => d.ToUserId == Guid.Empty ? null : d.ToUserId, (d, v) => d.ToUserId = v ?? Guid.Empty),
        Field.Time<QuoteApprovalDelegation>("startsAt", d => d.StartsAt == default ? null : d.StartsAt, (d, v) => { if (v is { } t) { d.StartsAt = t; } }),
        Field.Time<QuoteApprovalDelegation>("endsAt", d => d.EndsAt, (d, v) => d.EndsAt = v),
        Field.Flag<QuoteApprovalDelegation>("isActive", d => d.IsActive, (d, v) => d.IsActive = v),
        Field.Text<QuoteApprovalDelegation>("note", QuoteApprovalDelegation.NoteMaxLength, d => d.Note, (d, v) => d.Note = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<QuoteApprovalDelegation>> Filters { get; } =
        new Dictionary<string, RecordFilter<QuoteApprovalDelegation>>
        {
            ["active"] = (q, v, _) => FilterValue.Bool(v) is { } active ? q.Where(d => d.IsActive == active) : null,
            ["fromUserId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(d => d.FromUserId == id) : null,
            ["toUserId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(d => d.ToUserId == id) : null,
        };

    protected override IOrderedQueryable<QuoteApprovalDelegation> Order(IQueryable<QuoteApprovalDelegation> source) =>
        source.OrderByDescending(d => d.IsActive).ThenBy(d => d.StartsAt);

    protected override IQueryable<QuoteApprovalDelegation> Search(IQueryable<QuoteApprovalDelegation> source, string term) =>
        source.Where(d => d.Note != null && d.Note.Contains(term));

    protected override Task AppliedAsync(QuoteApprovalDelegation entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (entity.FromUserId == Guid.Empty)
        {
            errors.Add(new RecordFieldError("fromUserId", "Vekâlet veren zorunludur."));
        }

        if (entity.ToUserId == Guid.Empty)
        {
            errors.Add(new RecordFieldError("toUserId", "Vekil zorunludur."));
        }

        if (entity.FromUserId != Guid.Empty && entity.FromUserId == entity.ToUserId)
        {
            errors.Add(new RecordFieldError("toUserId", "Kişi kendine vekâlet veremez."));
        }

        if (entity.StartsAt == default)
        {
            entity.StartsAt = DateTimeOffset.UtcNow;
        }

        if (entity.EndsAt is { } end && end <= entity.StartsAt)
        {
            errors.Add(new RecordFieldError("endsAt", "Bitiş, başlangıçtan sonra olmalıdır."));
        }

        return errors.Count > 0 ? Task.FromException(new RecordValidationException(errors)) : Task.CompletedTask;
    }
}

internal sealed class QuoteApprovalDelegationConfiguration : IEntityTypeConfiguration<QuoteApprovalDelegation>
{
    public void Configure(EntityTypeBuilder<QuoteApprovalDelegation> builder)
    {
        CrmTables.ConfigureCommon(builder, "QuoteApprovalDelegations");
        builder.Property(d => d.Note).HasMaxLength(QuoteApprovalDelegation.NoteMaxLength);
        builder.HasIndex(d => new { d.TenantId, d.FromUserId, d.IsActive });
        builder.HasIndex(d => new { d.TenantId, d.ToUserId, d.IsActive });
    }
}
