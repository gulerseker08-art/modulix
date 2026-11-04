using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Activities;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Aktivite alt kayıtlarının (ilişki, katılımcı) ortak kalıbı. Okuma ve yazma izni aktiviteninkiyle aynıdır; veri kapsamı ve özel
/// aktivite gizliliği ÜST AKTİVİTE üzerinden uygulanır: ayrıntıyı göremeyen kullanıcı bir aktivitenin ilişkisini ya da katılımcısını
/// da göremez. Yazmak için aktivitenin çağıranın kapsamında ve ayrıntısının görünür olması gerekir.
/// </summary>
internal abstract class ActivityChildStore<TChild>(CrmStoreContext context) : CrmRecordStore<TChild>(context)
    where TChild : CrmEntity, IActivityChild, new()
{
    protected override string ReadPermission => CrmModule.Permissions.ActivitiesRead;

    protected override string WritePermission => CrmModule.Permissions.ActivitiesWrite;

    protected override bool UsesScope => true;

    protected override string ScopeEntityKey => CrmModule.Entities.Activity;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override async Task<IQueryable<TChild>> RestrictAsync(IQueryable<TChild> source, CancellationToken cancellationToken)
    {
        var visible = (await ActivityVisibility.DetailVisibleAsync(Context.Data.Set<Activity>().AsNoTracking(), Context, cancellationToken)).Select(a => a.Id);
        return source.Where(c => visible.Contains(c.ActivityId));
    }

    protected override IQueryable<TChild> ScopeToOwners(IQueryable<TChild> source, IReadOnlySet<Guid> owners)
    {
        var inScope = Context.Data.Set<Activity>().AsNoTracking().Where(ActivityVisibility.InScope(Context, owners)).Select(a => a.Id);
        return source.Where(c => inScope.Contains(c.ActivityId));
    }

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        Guid.TryParse(record.Values["activityId"]?.GetValue<string>(), out var activityId)
        && await Context.Data.Set<Activity>().AsNoTracking().Where(a => a.Id == activityId)
            .Where(ActivityVisibility.InScope(Context, owners)).AnyAsync(cancellationToken);

    /// <summary>Çağıranın yazabileceği (kapsamında ve ayrıntısı görünür) aktivite mi? Görünmeyen aktivite var olmayan gibi yanıtlanır.</summary>
    protected async Task EnsureActivityEditableAsync(Guid activityId, CancellationToken cancellationToken)
    {
        var query = await ActivityVisibility.DetailVisibleAsync(
            Context.Data.Set<Activity>().AsNoTracking().Where(a => a.Id == activityId), Context, cancellationToken);
        if (await Context.Access.GetOwnersAsync(CrmModule.Entities.Activity, cancellationToken) is { } owners)
        {
            query = query.Where(ActivityVisibility.InScope(Context, owners));
        }

        if (!await query.AnyAsync(cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError("activityId", "Aktivite bulunamadı.")]);
        }
    }

    /// <summary>Alt kayıt eklemek aktivitenin sürümünü artırır: ekran, ilişkisi değişen aktiviteyi eski sürümle ezmesin.</summary>
    protected override IOrderedQueryable<TChild> Order(IQueryable<TChild> source) => source.OrderBy(c => c.CreatedAt);

    protected static Guid? ActivityIdOf(JsonObject values) =>
        values["activityId"] is JsonValue node && node.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : null;
}

/// <summary>
/// Aktivite ilişkisi (<c>crm.activity-link</c>): aktiviteyi bir iş kaydına (firma, kişi, fırsat, Lead ya da herhangi bir modülün
/// kaydına) bağlar. Hedefin var olduğu ve çağıranın onu görebildiği <see cref="IActivityLinkTarget"/> ile doğrulanır; hedef bağı,
/// kayıt türü ve kimliği oluşturulduktan sonra değişmez (yanlış bağ silinip yenisi eklenir). Aktivite başına en fazla bir birincil
/// ilişki vardır; aynı kayıt iki kez bağlanamaz.
/// </summary>
internal sealed class ActivityLinkRecordStore(CrmStoreContext context) : ActivityChildStore<ActivityLink>(context)
{
    public override string EntityKey => CrmModule.Entities.ActivityLink;

    protected override IReadOnlyList<RecordField<ActivityLink>> Fields { get; } =
    [
        Field.CreateOnlyId<ActivityLink>("activityId", l => l.ActivityId),
        Field.Text<ActivityLink>("entityKey", ActivityLink.EntityKeyMaxLength, l => l.EntityKey, (l, v) => l.EntityKey = v!, required: true),
        Field.Id<ActivityLink>("recordId", l => l.RecordId, (l, v) => l.RecordId = v ?? Guid.Empty),
        Field.Flag<ActivityLink>("isPrimary", l => l.IsPrimary, (l, v) => l.IsPrimary = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ActivityLink>> Filters { get; } = new Dictionary<string, RecordFilter<ActivityLink>>
    {
        ["activityId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.ActivityId == id) : null,
        ["entityKey"] = (q, v, _) => v.Length is > 0 and <= ActivityLink.EntityKeyMaxLength ? q.Where(l => l.EntityKey == v) : null,
        ["recordId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.RecordId == id) : null,
    };

    protected override IQueryable<ActivityLink> Search(IQueryable<ActivityLink> source, string term) => source.Where(l => l.EntityKey.Contains(term));

    public override IReadOnlyList<RecordFieldError> Validate(RecordOperation operation, JsonObject values)
    {
        var errors = base.Validate(operation, values).ToList();
        if (operation == RecordOperation.Create && errors.Count == 0
            && (values["recordId"] is null || ActivityIdOf(values) is null))
        {
            errors.Add(new RecordFieldError("recordId", "Aktivite ve ilişkilendirilecek kayıt zorunludur."));
        }

        return errors;
    }

    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(ActivityLink entity, JsonObject values, CancellationToken cancellationToken)
    {
        var activityId = ActivityIdOf(values) ?? throw new RecordValidationException([new RecordFieldError("activityId", "Aktivite zorunludur.")]);
        await EnsureActivityEditableAsync(activityId, cancellationToken);
        entity.ActivityId = activityId;
        return (values, false);
    }

    protected override async Task AppliedAsync(ActivityLink entity, JsonObject values, CancellationToken cancellationToken)
    {
        var stored = await Context.Data.Set<ActivityLink>().AsNoTracking().Where(l => l.Id == entity.Id)
            .Select(l => new { l.EntityKey, l.RecordId, l.IsPrimary }).SingleOrDefaultAsync(cancellationToken);
        var errors = new List<RecordFieldError>();

        if (stored is not null)
        {
            await EnsureActivityEditableAsync(entity.ActivityId, cancellationToken);
            if (stored.EntityKey != entity.EntityKey || stored.RecordId != entity.RecordId)
            {
                errors.Add(new RecordFieldError("recordId", "Bağlı kayıt değiştirilemez; ilişkiyi silip yenisini ekleyin."));
            }
        }
        else
        {
            if (Context.LinkTargets.FirstOrDefault(t => t.EntityKey == entity.EntityKey) is not { } target)
            {
                errors.Add(new RecordFieldError("entityKey", "Bu kayıt türü aktiviteye bağlanamaz."));
            }
            else if (!await target.IsVisibleAsync(entity.RecordId, cancellationToken))
            {
                // Başka tenant'ın ya da erişilemeyen kayıt, var olmayan kayıtla aynı cevabı alır.
                errors.Add(new RecordFieldError("recordId", "Kayıt bulunamadı."));
            }

            if (await Context.Data.Set<ActivityLink>().AnyAsync(
                    l => l.ActivityId == entity.ActivityId && l.EntityKey == entity.EntityKey && l.RecordId == entity.RecordId, cancellationToken))
            {
                errors.Add(new RecordFieldError("recordId", "Bu kayıt aktiviteye zaten bağlı."));
            }
        }

        if (entity.IsPrimary && (stored is null || !stored.IsPrimary)
            && await Context.Data.Set<ActivityLink>().AnyAsync(l => l.ActivityId == entity.ActivityId && l.IsPrimary && l.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("isPrimary", "Aktivitenin zaten birincil bir ilişkisi var; önce onu birincil olmaktan çıkarın."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override async Task BeforeDeleteAsync(ActivityLink entity, CancellationToken cancellationToken) =>
        await EnsureActivityEditableAsync(entity.ActivityId, cancellationToken);
}

/// <summary>
/// Aktivite katılımcısı (<c>crm.activity-participant</c>): dahili kullanıcı, harici CRM kişisi ya da serbest e-posta; tam biri dolu.
/// Rol (zorunlu/isteğe bağlı/izleyici) sorumluluk değildir. Harici katılımcı eklemek o kişiye hiçbir CRM kaydı erişimi vermez.
/// Dahili katılımcı yalnızca bu aktiviteyi görür (veri kapsamı genişlemesi), bağlı kayıtları kendi yetkisiyle görür.
/// </summary>
internal sealed class ActivityParticipantRecordStore(CrmStoreContext context) : ActivityChildStore<ActivityParticipant>(context)
{
    public override string EntityKey => CrmModule.Entities.ActivityParticipant;

    protected override IReadOnlyList<RecordField<ActivityParticipant>> Fields { get; } =
    [
        Field.CreateOnlyId<ActivityParticipant>("activityId", p => p.ActivityId),
        Field.Owner<ActivityParticipant>("userId", p => p.UserId, (p, v) => p.UserId = v),
        Field.Reference<ActivityParticipant, Contact>("contactId", "Kişi bulunamadı.", p => p.ContactId, (p, v) => p.ContactId = v),
        Field.Email<ActivityParticipant>("email", ActivityParticipant.EmailMaxLength, p => p.Email, (p, v) => p.Email = v),
        Field.Text<ActivityParticipant>("displayName", ActivityParticipant.NameMaxLength, p => p.DisplayName, (p, v) => p.DisplayName = v),
        Field.Choice<ActivityParticipant>("role", ParticipantRole.All, p => p.Role, (p, v) => p.Role = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ActivityParticipant>> Filters { get; } =
        new Dictionary<string, RecordFilter<ActivityParticipant>>
        {
            ["activityId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(p => p.ActivityId == id) : null,
            ["userId"] = (q, v, ctx) => FilterValue.User(v, ctx) is { } user ? q.Where(p => p.UserId == user) : null,
        };

    protected override IQueryable<ActivityParticipant> Search(IQueryable<ActivityParticipant> source, string term) =>
        source.Where(p => (p.DisplayName != null && p.DisplayName.Contains(term)) || (p.Email != null && p.Email.Contains(term)));

    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(
        ActivityParticipant entity, JsonObject values, CancellationToken cancellationToken)
    {
        var activityId = ActivityIdOf(values) ?? throw new RecordValidationException([new RecordFieldError("activityId", "Aktivite zorunludur.")]);
        await EnsureActivityEditableAsync(activityId, cancellationToken);
        entity.ActivityId = activityId;
        return (values, false);
    }

    protected override async Task AppliedAsync(ActivityParticipant entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var kinds = (entity.UserId is null ? 0 : 1) + (entity.ContactId is null ? 0 : 1) + (string.IsNullOrWhiteSpace(entity.Email) ? 0 : 1);
        if (kinds != 1)
        {
            errors.Add(new RecordFieldError("userId", "Katılımcı olarak tam olarak biri verilmelidir: kullanıcı, kişi ya da e-posta."));
        }

        entity.NormalizedEmail = ContactNormalization.NormalizeEmail(entity.Email ?? string.Empty);
        if (entity.Email is null)
        {
            entity.NormalizedEmail = null;
        }

        var stored = await Context.Data.Set<ActivityParticipant>().AsNoTracking().Where(p => p.Id == entity.Id)
            .Select(p => new { p.UserId, p.ContactId, p.NormalizedEmail }).SingleOrDefaultAsync(cancellationToken);
        if (stored is not null)
        {
            await EnsureActivityEditableAsync(entity.ActivityId, cancellationToken);
            if (stored.UserId != entity.UserId || stored.ContactId != entity.ContactId || stored.NormalizedEmail != entity.NormalizedEmail)
            {
                errors.Add(new RecordFieldError("userId", "Katılımcı değiştirilemez; silip yenisini ekleyin."));
            }
        }
        else
        {
            // Harici kişi çağıranın görebildiği bir CRM kişisi olmalıdır; erişilemeyen kişi "bulunamadı" der.
            if (entity.ContactId is { } contactId
                && !(Context.LinkTargets.FirstOrDefault(t => t.EntityKey == CrmModule.Entities.Contact) is { } target
                    && await target.IsVisibleAsync(contactId, cancellationToken)))
            {
                errors.Add(new RecordFieldError("contactId", "Kişi bulunamadı."));
            }

            var existing = Context.Data.Set<ActivityParticipant>().Where(p => p.ActivityId == entity.ActivityId);
            if (await existing.AnyAsync(p => (entity.UserId != null && p.UserId == entity.UserId)
                    || (entity.ContactId != null && p.ContactId == entity.ContactId)
                    || (entity.NormalizedEmail != null && p.NormalizedEmail == entity.NormalizedEmail), cancellationToken))
            {
                errors.Add(new RecordFieldError("userId", "Bu katılımcı zaten ekli."));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override async Task BeforeDeleteAsync(ActivityParticipant entity, CancellationToken cancellationToken) =>
        await EnsureActivityEditableAsync(entity.ActivityId, cancellationToken);
}
