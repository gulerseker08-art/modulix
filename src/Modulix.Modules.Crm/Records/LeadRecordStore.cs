using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Lead kayıt deposu. Alan düzenlemesi <c>crm.leads.update</c>, durum/sorumlu/dönüşüm değişiklikleri ise iş akışı kapısı
/// (<see cref="LeadWorkflowGate"/>) açıkken ilgili eylemin iziyle yapılır. Sorumlusu boş Lead'ler "atama bekleyen" havuzdadır
/// ve veri kapsamı ne olursa olsun atama iznine sahip kullanıcılara görünür. Aynı e-posta/telefon tekrar eden Lead'i engellemez
/// (aynı kişinin farklı ihtiyaçları ayrı Lead olabilir); yalnızca (kaynak, gönderim kimliği) çifti teknik tekrarı engeller.
/// </summary>
internal sealed class LeadRecordStore(CrmStoreContext context) : CrmRecordStore<Lead>(context), IRecordStore
{
    public override string EntityKey => CrmModule.Entities.Lead;

    protected override string ReadPermission => CrmModule.Permissions.LeadsRead;

    protected override string WritePermission => CrmModule.Permissions.LeadsUpdate;

    /// <summary>Oluşturma ve silme (arşiv) izinleri ayrıdır.</summary>
    string? IRecordStore.PermissionFor(RecordOperation operation) => operation switch
    {
        RecordOperation.Read => ReadPermission,
        RecordOperation.Create => CrmModule.Permissions.LeadsCreate,
        RecordOperation.Delete => CrmModule.Permissions.LeadsDelete,
        _ => WritePermission,
    };

    /// <summary>
    /// İş akışı kapısı açıkken güncelleme yalnızca kapının izniyle yapılır; böylece "ata" izni alan düzenleme, "nitelendir" izni
    /// atama sağlamaz. Kapı kapalıyken sıradan güncelleme izni geçerlidir.
    /// </summary>
    IReadOnlyList<string>? IRecordStore.AnyPermissionFor(RecordOperation operation) =>
        operation == RecordOperation.Update && Context.LeadGate.Permission is { } permission ? [permission] : null;

    protected override bool DefaultsOwnerToCurrentUser => !Context.LeadGate.IsOpen;

    protected override IReadOnlyList<RecordField<Lead>> Fields { get; } =
    [
        Field.Text<Lead>("leadNumber", Lead.NumberMaxLength, l => l.LeadNumber, (l, v) => l.LeadNumber = v!).AsSystem(),
        Field.Text<Lead>("subject", Lead.SubjectMaxLength, l => l.Subject, (l, v) => l.Subject = v!, required: true),
        Field.Text<Lead>("description", Lead.TextMaxLength, l => l.Description, (l, v) => l.Description = v),
        Field.Text<Lead>("status", 30, l => l.Status, (l, v) => l.Status = v!).AsSystem(),
        Field.Text<Lead>("statusReason", Lead.ReasonMaxLength, l => l.StatusReason, (l, v) => l.StatusReason = v).AsSystem(),
        Field.Choice<Lead>("priority", LeadPriority.All, l => l.Priority, (l, v) => l.Priority = v),
        Field.Text<Lead>("firstName", Lead.NameMaxLength, l => l.FirstName, (l, v) => l.FirstName = v),
        Field.Text<Lead>("lastName", Lead.NameMaxLength, l => l.LastName, (l, v) => l.LastName = v),
        Field.Text<Lead>("companyName", Lead.ShortMaxLength, l => l.CompanyName, (l, v) => l.CompanyName = v),
        Field.Email<Lead>("email", Lead.EmailMaxLength, l => l.Email, (l, v) => l.Email = v),
        Field.Phone<Lead>("phone", Lead.PhoneMaxLength, l => l.Phone, (l, v) => l.Phone = v),
        Field.Text<Lead>("jobTitle", Lead.NameMaxLength, l => l.JobTitle, (l, v) => l.JobTitle = v),
        Field.Url<Lead>("website", Lead.UrlMaxLength, l => l.Website, (l, v) => l.Website = v),
        Field.Country<Lead>("country", l => l.Country, (l, v) => l.Country = v),
        Field.Text<Lead>("city", Address.PartMaxLength, l => l.City, (l, v) => l.City = v),
        Field.Reference<Lead, Company>("companyId", "Firma bulunamadı.", l => l.CompanyId, (l, v) => l.CompanyId = v),
        Field.Reference<Lead, Contact>("contactId", "Kişi bulunamadı.", l => l.ContactId, (l, v) => l.ContactId = v),
        Field.Text<Lead>("productInterest", Lead.ShortMaxLength, l => l.ProductInterest, (l, v) => l.ProductInterest = v),
        Field.CodeList<Lead>("serviceScope", l => l.ServiceScope, (l, v) => l.ServiceScope = v),
        Field.Text<Lead>("needDescription", Lead.TextMaxLength, l => l.NeedDescription, (l, v) => l.NeedDescription = v),
        Field.Money<Lead>("estimatedBudget", l => l.EstimatedBudget, (l, v) => l.EstimatedBudget = v),
        Field.Currency<Lead>("currency", l => l.Currency, (l, v) => l.Currency = v),
        Field.Date<Lead>("targetPurchaseDate", l => l.TargetPurchaseDate, (l, v) => l.TargetPurchaseDate = v),
        Field.Text<Lead>("industry", Company.IndustryMaxLength, l => l.Industry, (l, v) => l.Industry = v),
        Field.ListChoice<Lead>("source", CrmModule.Lists.LeadSource, l => l.Source, (l, v) => l.Source = v, required: false),
        Field.Text<Lead>("sourceDetail", Lead.ShortMaxLength, l => l.SourceDetail, (l, v) => l.SourceDetail = v),
        Field.Text<Lead>("referrer", Lead.UrlMaxLength, l => l.Referrer, (l, v) => l.Referrer = v),
        Field.Text<Lead>("externalSource", Lead.UtmMaxLength, l => l.ExternalSource, (l, v) => l.ExternalSource = v).AsSystem(),
        Field.Text<Lead>("externalSubmissionId", Lead.ExternalIdMaxLength, l => l.ExternalSubmissionId, (l, v) => l.ExternalSubmissionId = v).AsSystem(),
        Field.Text<Lead>("utmSource", Lead.UtmMaxLength, l => l.UtmSource, (l, v) => l.UtmSource = v),
        Field.Text<Lead>("utmMedium", Lead.UtmMaxLength, l => l.UtmMedium, (l, v) => l.UtmMedium = v),
        Field.Text<Lead>("utmCampaign", Lead.UtmMaxLength, l => l.UtmCampaign, (l, v) => l.UtmCampaign = v),
        Field.Time<Lead>("firstReceivedAt", l => l.FirstReceivedAt, (l, v) => { if (v is { } time) { l.FirstReceivedAt = time; } }).AsSystem(),
        Field.Owner<Lead>(OwnerField, l => l.OwnerId, (l, v) => l.OwnerId = v).GatedUpdate(),
        Field.Time<Lead>("assignedAt", l => l.AssignedAt, (l, v) => l.AssignedAt = v).AsSystem(),
        Field.Time<Lead>("firstContactAt", l => l.FirstContactAt, (l, v) => l.FirstContactAt = v).AsSystem(),
        Field.Time<Lead>("followUpAt", l => l.FollowUpAt, (l, v) => l.FollowUpAt = v).AsSystem(),
        Field.Time<Lead>("qualifiedAt", l => l.QualifiedAt, (l, v) => l.QualifiedAt = v).AsSystem(),
        Field.Time<Lead>("disqualifiedAt", l => l.DisqualifiedAt, (l, v) => l.DisqualifiedAt = v).AsSystem(),
        Field.ListChoice<Lead>("disqualificationReason", CrmModule.Lists.LeadDisqualificationReason, l => l.DisqualificationReason,
            (l, v) => l.DisqualificationReason = v, required: false).AsSystem(),
        Field.Time<Lead>("convertedAt", l => l.ConvertedAt, (l, v) => l.ConvertedAt = v).AsSystem(),
        Field.Id<Lead>("convertedBy", l => l.ConvertedBy, (l, v) => l.ConvertedBy = v).AsSystem(),
        Field.Id<Lead>("convertedCompanyId", l => l.ConvertedCompanyId, (l, v) => l.ConvertedCompanyId = v).AsSystem(),
        Field.Id<Lead>("convertedContactId", l => l.ConvertedContactId, (l, v) => l.ConvertedContactId = v).AsSystem(),
        Field.Id<Lead>("convertedOpportunityId", l => l.ConvertedOpportunityId, (l, v) => l.ConvertedOpportunityId = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Lead>> Filters { get; } = new Dictionary<string, RecordFilter<Lead>>
    {
        [OwnerField] = (q, v, ctx) => FilterValue.User(v, ctx) is { } owner ? q.Where(l => l.OwnerId == owner) : null,
        // "ownerNot=me": başkalarının (ekibimin) Lead'leri; sorumlusu boş olanlar da dahildir.
        ["ownerNot"] = (q, v, ctx) => FilterValue.User(v, ctx) is { } owner ? q.Where(l => l.OwnerId == null || l.OwnerId != owner) : null,
        ["companyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.CompanyId == id) : null,
        ["contactId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.ContactId == id) : null,
        ["convertedCompanyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.ConvertedCompanyId == id) : null,
        ["convertedContactId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.ConvertedContactId == id) : null,
        ["convertedOpportunityId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.ConvertedOpportunityId == id) : null,
        ["status"] = (q, v, _) => LeadStatus.All.Contains(v) ? q.Where(l => l.Status == v) : null,
        ["priority"] = (q, v, _) => LeadPriority.All.Contains(v) ? q.Where(l => l.Priority == v) : null,
        ["source"] = (q, v, _) => ChoiceValueKeys.IsValid(v) ? q.Where(l => l.Source == v) : null,
        ["open"] = (q, v, _) => FilterValue.Bool(v) is { } open
            ? open ? q.Where(l => LeadStatus.Open.Contains(l.Status)) : q.Where(l => !LeadStatus.Open.Contains(l.Status))
            : null,
        // Atama bekleyenler: açık ve sorumlusu boş.
        ["unassigned"] = (q, v, _) => FilterValue.Bool(v) is { } flag
            ? flag ? q.Where(l => l.OwnerId == null && LeadStatus.Open.Contains(l.Status)) : q.Where(l => l.OwnerId != null)
            : null,
        ["qualifiedNotConverted"] = (q, v, _) => FilterValue.Bool(v) is true ? q.Where(l => l.Status == LeadStatus.Qualified) : null,
        ["receivedFrom"] = (q, v, _) => FilterValue.Time(v) is { } time ? q.Where(l => l.FirstReceivedAt >= time) : null,
        ["receivedBefore"] = (q, v, _) => FilterValue.Time(v) is { } time ? q.Where(l => l.FirstReceivedAt < time) : null,
        ["convertedFrom"] = (q, v, _) => FilterValue.Time(v) is { } time ? q.Where(l => l.ConvertedAt >= time) : null,
        ["convertedBefore"] = (q, v, _) => FilterValue.Time(v) is { } time ? q.Where(l => l.ConvertedAt < time) : null,
        // İlk temas gecikmesi: temas olmamış, açık (Yeni/İletişimde) ve bu andan önce gelmiş. Zamanı hedef süreden uç nokta/istemci hesaplar.
        ["firstContactDueBefore"] = (q, v, _) => FilterValue.Time(v) is { } time
            ? q.Where(l => l.FirstContactAt == null && (l.Status == LeadStatus.New || l.Status == LeadStatus.Contacting) && l.FirstReceivedAt < time)
            : null,
        // Takibi gecikenler: "Daha sonra takip" durumunda ve takip tarihi geçmiş.
        ["followUpDueBefore"] = (q, v, _) => FilterValue.Time(v) is { } time
            ? q.Where(l => l.Status == LeadStatus.Nurturing && l.FollowUpAt != null && l.FollowUpAt < time)
            : null,
        // Mükerrer araması: normalleştirilmiş e-posta/telefon, firma adı, alan adı. Hiçbiri tek başına kimlik kanıtı değildir.
        ["email"] = (q, v, _) => ContactNormalization.NormalizeEmail(v) is { } email ? q.Where(l => l.NormalizedEmail == email) : null,
        ["phone"] = (q, v, _) => ContactNormalization.NormalizePhone(v) is { } phone ? q.Where(l => l.NormalizedPhone == phone) : null,
        ["companyName"] = (q, v, _) => v.Trim() is { Length: > 0 } name ? q.Where(l => l.CompanyName == name) : null,
        ["websiteDomain"] = (q, v, _) => LeadNormalization.Domain(v) is { } domain ? q.Where(l => l.WebsiteDomain == domain) : null,
        // "kaynak|gönderim kimliği": teknik tekrar araması.
        ["submission"] = (q, v, _) => v.Split('|', 2) is [{ Length: > 0 } source, { Length: > 0 } id]
            ? q.Where(l => l.ExternalSource == source && l.ExternalSubmissionId == id)
            : null,
    };

    protected override IOrderedQueryable<Lead> Order(IQueryable<Lead> source) => source.OrderByDescending(l => l.FirstReceivedAt);

    protected override IQueryable<Lead> Search(IQueryable<Lead> source, string term) =>
        source.Where(l => l.Subject.Contains(term) || l.LeadNumber.Contains(term)
            || (l.FirstName != null && l.FirstName.Contains(term)) || (l.LastName != null && l.LastName.Contains(term))
            || (l.CompanyName != null && l.CompanyName.Contains(term)) || (l.Email != null && l.Email.Contains(term)));

    // ---- Doğrulama: manuel kayıtta konu ve talep sahibini tanıtan en az bir bilgi gerekir.

    private static readonly string[] Identifying =
        ["firstName", "lastName", "companyName", "email", "phone", "companyId", "contactId"];

    public override IReadOnlyList<RecordFieldError> Validate(RecordOperation operation, JsonObject values)
    {
        var errors = base.Validate(operation, values).ToList();
        if (operation == RecordOperation.Create && errors.Count == 0
            && !Identifying.Any(name => values.TryGetPropertyValue(name, out var node) && node is not null && !string.IsNullOrWhiteSpace(node.ToString())))
        {
            errors.Add(new RecordFieldError("lastName", "Talep sahibini tanıtan en az bir bilgi girin (ad, firma adı, e-posta, telefon ya da mevcut firma/kişi)."));
        }

        return errors;
    }

    // ---- Veri kapsamı: sorumlusu boş Lead'ler (atama havuzu) atama iznine sahip herkese görünür.

    protected override async Task<IQueryable<Lead>> ScopeToOwnersAsync(
        IQueryable<Lead> source, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        return await CanAssignAsync(cancellationToken)
            ? source.Where(l => l.OwnerId == null || ids.Contains(l.OwnerId))
            : source.Where(l => ids.Contains(l.OwnerId));
    }

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        await base.IsInScopeAsync(record, owners, cancellationToken)
        || (record.Values["ownerId"] is null && await CanAssignAsync(cancellationToken));

    private Task<bool> CanAssignAsync(CancellationToken cancellationToken) =>
        Context.Permissions.HasPermissionAsync(CrmModule.Permissions.LeadsAssign, cancellationToken);

    // ---- Oluşturma

    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(Lead entity, JsonObject values, CancellationToken cancellationToken)
    {
        entity.LeadNumber = LeadNormalization.NewNumber(entity.CreatedAt);
        entity.Status = LeadStatus.New;

        // Başkasına atayarak açmak atama izni ister; kendine ya da boş açmak serbesttir.
        if (!Context.LeadGate.IsOpen
            && FieldNamed(OwnerField).Parse(values[OwnerField]).Value is Guid owner
            && owner != Context.CurrentUserId
            && !await CanAssignAsync(cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError(OwnerField, "Başka birine atamak için atama izni gerekir.")]);
        }

        return (values, false);
    }

    protected override async Task AppliedAsync(Lead entity, JsonObject values, CancellationToken cancellationToken)
    {
        entity.NormalizedEmail = entity.Email is { } email ? ContactNormalization.NormalizeEmail(email) : null;
        entity.NormalizedPhone = entity.Phone is { } phone ? ContactNormalization.NormalizePhone(phone) : null;
        entity.WebsiteDomain = LeadNormalization.Domain(entity.Website);

        // Kullanıcı devrinde yeni sorumlu atanmış sayılır (devir kendi başına atama izni gerektirmez, güncelleme izni yeter).
        if (Context.CommandReason == EventReasons.Transfer && values.ContainsKey(OwnerField) && entity.OwnerId is not null)
        {
            entity.AssignedAt = DateTimeOffset.UtcNow;
        }

        await EnsureReferencesVisibleAsync(entity, values, cancellationToken);
    }

    /// <summary>Sorumluyla açılan Lead, açıldığı anda atanmış sayılır (varsayılan sorumlu atamasından sonra).</summary>
    protected override void OnCreated(Lead entity)
    {
        if (entity.OwnerId is not null && entity.AssignedAt is null)
        {
            entity.AssignedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Bağlanan mevcut firma/kişi çağıranın görebildiği kayıt olmalıdır (ADR-0010); erişilemeyen kayıt, var olmayan kayıtla aynı
    /// cevabı alır. Değişmeyen bağ yeniden gönderilirse denetlenmez. İş akışı (sistem) yazımlarında bağlar değişmez.
    /// </summary>
    private async Task EnsureReferencesVisibleAsync(Lead entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var companyScope = await Context.Access.GetOwnersAsync(CrmModule.Entities.Company, cancellationToken);
        var contactScope = await Context.Access.GetOwnersAsync(CrmModule.Entities.Contact, cancellationToken);
        var stored = await Context.Data.Set<Lead>().AsNoTracking().Where(l => l.Id == entity.Id)
            .Select(l => new { l.CompanyId, l.ContactId, l.ServiceScope }).SingleOrDefaultAsync(cancellationToken);

        if (values.ContainsKey("companyId") && entity.CompanyId is { } companyId && stored?.CompanyId != companyId)
        {
            var owner = await Context.Data.Set<Company>().AsNoTracking().Where(c => c.Id == companyId)
                .Select(c => c.OwnerId).SingleOrDefaultAsync(cancellationToken);
            var visible = await Context.Access.CanAsync(CrmModule.Entities.Company, RecordOperation.Read, cancellationToken)
                && (companyScope is null || (owner is { } ownerId && companyScope.Contains(ownerId)));
            if (!visible)
            {
                errors.Add(new RecordFieldError("companyId", "Firma bulunamadı."));
            }
        }

        if (values.ContainsKey("contactId") && entity.ContactId is { } contactId && stored?.ContactId != contactId)
        {
            var visible = await Context.Access.CanAsync(CrmModule.Entities.Contact, RecordOperation.Read, cancellationToken)
                && (contactScope is null || await Context.Data.Set<Contact>().AsNoTracking().Where(c => c.Id == contactId)
                    .Where(ContactRecordStore.VisibleTo(Context.Data, contactScope)).AnyAsync(cancellationToken));
            if (!visible)
            {
                errors.Add(new RecordFieldError("contactId", "Kişi bulunamadı."));
            }
        }

        await LookupGuard.AddContactCompanyErrorAsync(
            errors, Context, "contactId", entity.ContactId, entity.CompanyId, stored?.ContactId, stored?.CompanyId, cancellationToken);

        // Hizmet kapsamı: yeni eklenen kodlar katalogda aktif "Faaliyet" olmalı; iş akışı yazımlarında (dönüşüm vb.) denetlenmez.
        if (!Context.LeadGate.IsOpen && values.ContainsKey("serviceScope"))
        {
            await ServiceScopeGuard.AddErrorsAsync(errors, Context, "serviceScope", entity.ServiceScope, stored?.ServiceScope, cancellationToken);
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }
}
