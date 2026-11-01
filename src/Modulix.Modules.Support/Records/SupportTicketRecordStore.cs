using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Lifecycle;
using Modulix.Modules.Support.Sla;

namespace Modulix.Modules.Support.Records;

/// <summary>
/// Destek talebi kayıt deposu (ADR-0024). Talep genel uçtan oluşturulabilir (numara, öncelik hesabı ve yönlendirme sunucuda yapılır) ancak
/// silinemez. Durum, sorumlu, kuyruk, öncelik, çözüm ve zaman damgaları sistem alanıdır: genel uçlardan yazılamaz, yalnızca iş akışı kapısı
/// açıkken (<see cref="TicketWorkflowGate"/>) değişir. Görünürlük veri kapsamını izler; atanmamış talep (sorumlusuz) kuyrukta görünür kalır.
/// </summary>
internal sealed class SupportTicketRecordStore(SupportStoreContext context) : SupportRecordStore<SupportTicket>(context)
{
    public override string EntityKey => SupportModule.Entities.Ticket;

    protected override string ReadPermission => SupportModule.Permissions.TicketsRead;

    protected override string WritePermission => SupportModule.Permissions.TicketsWrite;

    protected override string CreatePermission => SupportModule.Permissions.TicketsCreate;

    protected override bool DefaultsOwnerToCurrentUser => false;

    protected override IReadOnlyList<RecordField<SupportTicket>> Fields { get; } =
    [
        Field.Time<SupportTicket>("createdAt", t => t.CreatedAt, (_, _) => { }).AsSystem(),
        Field.Text<SupportTicket>("number", SupportTicket.NumberMaxLength, t => t.Number, (t, v) => t.Number = v!).AsSystem(),
        Field.Text<SupportTicket>("subject", SupportTicket.SubjectMaxLength, t => t.Subject, (t, v) => t.Subject = v!, required: true),
        Field.Text<SupportTicket>("description", SupportTicket.TextMaxLength, t => t.Description, (t, v) => t.Description = v),
        Field.ListChoice<SupportTicket>("typeKey", SupportModule.Lists.TicketType, t => t.TypeKey, (t, v) => t.TypeKey = v ?? string.Empty),
        Field.Choice<SupportTicket>("channel", TicketChannels.All, t => t.Channel, (t, v) => t.Channel = v),
        Field.Shared<SupportTicket, Party>("partyId", "Müşteri bulunamadı.", t => t.PartyId, (t, v) => t.PartyId = v),
        Field.Shared<SupportTicket, Person>("personId", "Kişi bulunamadı.", t => t.PersonId, (t, v) => t.PersonId = v),
        Field.Text<SupportTicket>("customerName", SupportTicket.NameMaxLength, t => t.CustomerName, (t, v) => t.CustomerName = v).AsSystem(),
        Field.Text<SupportTicket>("reporterName", SupportTicket.NameMaxLength, t => t.ReporterName, (t, v) => t.ReporterName = v),
        Field.Text<SupportTicket>("reporterEmail", SupportTicket.EmailMaxLength, t => t.ReporterEmail, (t, v) => t.ReporterEmail = v),
        Field.Text<SupportTicket>("reporterPhone", SupportTicket.PhoneMaxLength, t => t.ReporterPhone, (t, v) => t.ReporterPhone = v),
        Field.Reference<SupportTicket, TicketCategory>("categoryId", "Kategori bulunamadı.", t => t.CategoryId, (t, v) => t.CategoryId = v),
        Field.Choice<SupportTicket>("impact", TicketLevel.All, t => t.Impact, (t, v) => t.Impact = v),
        Field.Choice<SupportTicket>("urgency", TicketLevel.All, t => t.Urgency, (t, v) => t.Urgency = v),
        Field.Text<SupportTicket>("priority", SupportTicket.StatusMaxLength, t => t.Priority, (t, v) => t.Priority = v!).AsSystem(),
        Field.Flag<SupportTicket>("priorityOverridden", t => t.PriorityOverridden, (t, v) => t.PriorityOverridden = v).AsSystem(),
        Field.Text<SupportTicket>("priorityReason", SupportTicket.ReasonMaxLength, t => t.PriorityReason, (t, v) => t.PriorityReason = v).AsSystem(),
        Field.Text<SupportTicket>("status", SupportTicket.StatusMaxLength, t => t.Status, (t, v) => t.Status = v!).AsSystem(),
        Field.Text<SupportTicket>("statusReason", SupportTicket.ReasonMaxLength, t => t.StatusReason, (t, v) => t.StatusReason = v).AsSystem(),
        Field.Id<SupportTicket>("queueId", t => t.QueueId, (t, v) => t.QueueId = v).AsSystem(),
        Field.Owner<SupportTicket>(OwnerField, t => t.OwnerId, (t, v) => t.OwnerId = v).AsSystem(),
        Field.Reference<SupportTicket, SupportTicket>("parentTicketId", "İlişkili talep bulunamadı.", t => t.ParentTicketId, (t, v) => t.ParentTicketId = v, createOnly: true),
        Field.Text<SupportTicket>("clientKey", SupportTicket.ClientKeyMaxLength, t => t.ClientKey, (t, v) => t.ClientKey = v).AsSystem(),
        Field.Id<SupportTicket>("mergedIntoId", t => t.MergedIntoId, (t, v) => t.MergedIntoId = v).AsSystem(),
        Field.Date<SupportTicket>("eventDate", t => t.EventDate, (t, v) => t.EventDate = v),
        Field.Text<SupportTicket>("expectedOutcome", SupportTicket.TextMaxLength, t => t.ExpectedOutcome, (t, v) => t.ExpectedOutcome = v),
        Field.ListChoice<SupportTicket>("resolutionCode", SupportModule.Lists.ResolutionCode, t => t.ResolutionCode, (t, v) => t.ResolutionCode = v, required: false).AsSystem(),
        Field.Text<SupportTicket>("resolutionNote", SupportTicket.TextMaxLength, t => t.ResolutionNote, (t, v) => t.ResolutionNote = v).AsSystem(),
        Field.Time<SupportTicket>("resolvedAt", t => t.ResolvedAt, (t, v) => t.ResolvedAt = v).AsSystem(),
        Field.Id<SupportTicket>("resolvedBy", t => t.ResolvedBy, (t, v) => t.ResolvedBy = v).AsSystem(),
        Field.Text<SupportTicket>("rootCause", SupportTicket.TextMaxLength, t => t.RootCause, (t, v) => t.RootCause = v).AsSystem(),
        Field.Text<SupportTicket>("correctiveAction", SupportTicket.TextMaxLength, t => t.CorrectiveAction, (t, v) => t.CorrectiveAction = v).AsSystem(),
        Field.Time<SupportTicket>("closedAt", t => t.ClosedAt, (t, v) => t.ClosedAt = v).AsSystem(),
        Field.Integer<SupportTicket>("reopenCount", t => t.ReopenCount, (t, v) => t.ReopenCount = v ?? 0).AsSystem(),
        Field.Time<SupportTicket>("firstResponseAt", t => t.FirstResponseAt, (t, v) => t.FirstResponseAt = v).AsSystem(),
        Field.Time<SupportTicket>("firstResponseDueAt", t => t.FirstResponseDueAt, (t, v) => t.FirstResponseDueAt = v).AsSystem(),
        Field.Time<SupportTicket>("resolutionDueAt", t => t.ResolutionDueAt, (t, v) => t.ResolutionDueAt = v).AsSystem(),
        Field.Time<SupportTicket>("lastActivityAt", t => t.LastActivityAt, (t, v) => t.LastActivityAt = v ?? DateTimeOffset.UtcNow).AsSystem(),
        Field.Id<SupportTicket>("createdBy", t => t.CreatedBy, (t, v) => t.CreatedBy = v).AsSystem(),
        Field.Id<SupportTicket>("updatedBy", t => t.UpdatedBy, (t, v) => t.UpdatedBy = v).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<SupportTicket>> Filters { get; } = new Dictionary<string, RecordFilter<SupportTicket>>
    {
        ["partyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(t => t.PartyId == id) : null,
        ["personId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(t => t.PersonId == id) : null,
        ["categoryId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(t => t.CategoryId == id) : null,
        ["queueId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(t => t.QueueId == id) : null,
        ["parentTicketId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(t => t.ParentTicketId == id) : null,
        // Bağlı kayıt: "anahtar:kimlik" (ör. contracts.contract:{guid}); karşı modülün sayfalarındaki "ilişkili talepler" listesi için.
        ["linked"] = (q, v, _) => v.LastIndexOf(':') is > 0 and var colon && FilterValue.Id(v[(colon + 1)..]) is { } linkedId && v[..colon] is var linkedKey
            ? q.Where(t => context.Data.Set<TicketLink>().Any(l => l.TicketId == t.Id && l.EntityKey == linkedKey && l.RecordId == linkedId))
            : null,
        ["mergedIntoId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(t => t.MergedIntoId == id) : null,
        // Başvuran e-postası/telefonu (tanımsız başvuran için mükerrer denetimi); tam eşleşme.
        ["reporterEmail"] = (q, v, _) => v.Trim() is { Length: > 0 } email ? q.Where(t => t.ReporterEmail == email) : null,
        ["reporterPhone"] = (q, v, _) => v.Trim() is { Length: > 0 } phone ? q.Where(t => t.ReporterPhone == phone) : null,
        [OwnerField] = (q, v, ctx) => FilterValue.User(v, ctx) is { } owner ? q.Where(t => t.OwnerId == owner) : null,
        ["number"] = (q, v, _) => v.Trim() is { Length: > 0 } number ? q.Where(t => t.Number == number) : null,
        // Virgülle ayrılmış birden çok durum kabul edilir (ör. status=New,Triage).
        ["status"] = (q, v, _) => Split(v, TicketStatus.All) is { } statuses ? q.Where(t => statuses.Contains(t.Status)) : null,
        ["priority"] = (q, v, _) => Split(v, TicketPriority.All) is { } priorities ? q.Where(t => priorities.Contains(t.Priority)) : null,
        ["channel"] = (q, v, _) => TicketChannels.All.Contains(v) ? q.Where(t => t.Channel == v) : null,
        ["typeKey"] = (q, v, _) => ChoiceValueKeyFilter(v) is { } key ? q.Where(t => t.TypeKey == key) : null,
        // Açık talepler: iptal edilmemiş ve kapanmamış (çözüldü, müşteri teyidini beklerken açık sayılır).
        ["open"] = (q, v, _) => FilterValue.Bool(v) is { } flag
            ? q.Where(t => (t.Status != TicketStatus.Closed && t.Status != TicketStatus.Cancelled && t.Status != TicketStatus.Merged) == flag)
            : null,
        // Atanmamış: çalışılan durumda ve sorumlusuz (kuyrukta bekleyen).
        ["unassigned"] = (q, v, _) => FilterValue.Bool(v) is { } flag
            ? q.Where(t => (t.OwnerId == null
                && (t.Status == TicketStatus.New || t.Status == TicketStatus.Triage || t.Status == TicketStatus.Assigned || t.Status == TicketStatus.InProgress
                    || t.Status == TicketStatus.WaitingCustomer || t.Status == TicketStatus.WaitingInternal || t.Status == TicketStatus.Reopened)) == flag)
            : null,
        // SLA ihlali: açık (çalışan/duraklatılmış) SLA örneği hedef anını aşmış (işaretlenmiş ya da hedef anı geçmiş). Gösterge ve liste bu süzgeci paylaşır.
        ["slaBreached"] = (q, v, ctx) => FilterValue.Bool(v) is { } flag
            ? q.Where(t => ctx.Data.Set<SlaInstance>().Any(i => i.TicketId == t.Id && (i.Status == SlaStatus.Running || i.Status == SlaStatus.Paused)
                && (i.BreachedAt != null || (i.Status == SlaStatus.Running && i.DueAt != null && i.DueAt <= DateTimeOffset.UtcNow))) == flag)
            : null,
        // SLA riski: çalışan, henüz ihlal olmamış örneğin ilk uyarı eşiği geçmiş.
        ["slaAtRisk"] = (q, v, ctx) => FilterValue.Bool(v) is { } flag
            ? q.Where(t => ctx.Data.Set<SlaInstance>().Any(i => i.TicketId == t.Id && i.Status == SlaStatus.Running && i.BreachedAt == null
                && (i.DueAt == null || i.DueAt > DateTimeOffset.UtcNow) && i.WarningAt != null && i.WarningAt <= DateTimeOffset.UtcNow) == flag)
            : null,
        // Yanıtsız: çalışılan durumda ve henüz ilk insan yanıtı verilmemiş.
        ["awaitingFirstResponse"] = (q, v, _) => FilterValue.Bool(v) is { } flag
            ? q.Where(t => (t.FirstResponseAt == null
                && t.Status != TicketStatus.Closed && t.Status != TicketStatus.Cancelled && t.Status != TicketStatus.Merged && t.Status != TicketStatus.Resolved) == flag)
            : null,
    };

    private static string? ChoiceValueKeyFilter(string value) => Modulix.Core.Contracts.Customization.ChoiceValueKeys.IsValid(value.Trim()) ? value.Trim() : null;

    private static List<string>? Split(string value, IReadOnlyCollection<string> allowed) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } parts && parts.All(allowed.Contains)
            ? [.. parts]
            : null;

    protected override IOrderedQueryable<SupportTicket> Order(IQueryable<SupportTicket> source) => source.OrderByDescending(t => t.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys => TicketSort.All;

    protected override IOrderedQueryable<SupportTicket> Order(IQueryable<SupportTicket> source, string? sort) => sort switch
    {
        "number" => source.OrderBy(t => t.Number),
        "-number" => source.OrderByDescending(t => t.Number),
        "subject" => source.OrderBy(t => t.Subject),
        "priority" => source.OrderBy(t => t.Priority == TicketPriority.Critical ? 0 : t.Priority == TicketPriority.High ? 1 : t.Priority == TicketPriority.Normal ? 2 : 3).ThenBy(t => t.CreatedAt),
        "createdAt" => source.OrderBy(t => t.CreatedAt),
        "-lastActivity" => source.OrderByDescending(t => t.LastActivityAt),
        "resolutionDue" => source.OrderBy(t => t.ResolutionDueAt == null).ThenBy(t => t.ResolutionDueAt),
        "firstResponseDue" => source.OrderBy(t => t.FirstResponseDueAt == null).ThenBy(t => t.FirstResponseDueAt),
        _ => Order(source),
    };

    protected override IQueryable<SupportTicket> Search(IQueryable<SupportTicket> source, string term) =>
        source.Where(t => t.Subject.Contains(term) || t.Number.Contains(term)
            || (t.ReporterName != null && t.ReporterName.Contains(term)) || (t.ReporterEmail != null && t.ReporterEmail.Contains(term)));

    /// <summary>Atanmamış talep (sorumlusuz) kuyrukta herkes tarafından görünür; sahipli talep sahibin veri kapsamına göre görünür.</summary>
    protected override IQueryable<SupportTicket> ScopeToOwners(IQueryable<SupportTicket> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        return source.Where(t => t.OwnerId == null || ids.Contains(t.OwnerId));
    }

    protected override Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (!record.Values.TryGetPropertyValue(OwnerField, out var node) || node is not JsonValue value || !value.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var owner))
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(owners.Contains(owner));
    }

    private string? beforeStatus;
    private Guid? beforeOwner;
    private Guid? beforeQueue;
    private Guid? beforeParty;
    private Guid? beforePerson;
    private Guid? beforeCategory;
    private string? beforePriority;
    private DateTimeOffset? beforeFirstResponse;

    protected override void OnUpdating(SupportTicket entity)
    {
        beforeStatus = entity.Status;
        beforeOwner = entity.OwnerId;
        beforeQueue = entity.QueueId;
        beforeParty = entity.PartyId;
        beforePerson = entity.PersonId;
        beforeCategory = entity.CategoryId;
        beforePriority = entity.Priority;
        beforeFirstResponse = entity.FirstResponseAt;
    }

    protected override async Task<JsonObject> PrepareNewAsync(SupportTicket entity, JsonObject values, CancellationToken cancellationToken)
    {
        if (Context.Gate.IsOpen)
        {
            throw new RecordValidationException([new RecordFieldError("subject", "Talep yalnızca talep oluşturma işlemiyle açılır.")]);
        }

        var permissions = await Context.Permissions.GetPermissionsAsync(cancellationToken);
        var errors = new List<RecordFieldError>();

        // Sorumlu ve kuyruk oluştururken yalnızca atama yetkisiyle verilebilir; yetkisiz isteğin değeri sessizce yok sayılmaz.
        Guid? ownerId = null;
        Guid? queueId = null;
        if (values.TryGetPropertyValue(OwnerField, out var ownerNode) && ownerNode is JsonValue ownerValue && ownerValue.TryGetValue<string>(out var ownerText) && !string.IsNullOrWhiteSpace(ownerText))
        {
            if (!permissions.Contains(SupportModule.Permissions.TicketsAssign))
            {
                errors.Add(new RecordFieldError(OwnerField, "Talebi oluştururken sorumlu atamak için atama yetkisi gerekir."));
            }
            else if (FilterValue.Id(ownerText) is not { } parsedOwner || !await Context.Members.IsActiveMemberAsync(parsedOwner, cancellationToken))
            {
                errors.Add(new RecordFieldError(OwnerField, "Kullanıcı bu kuruluşun üyesi değil."));
            }
            else
            {
                ownerId = parsedOwner;
            }
        }

        if (values.TryGetPropertyValue("queueId", out var queueNode) && queueNode is JsonValue queueValue && queueValue.TryGetValue<string>(out var queueText) && !string.IsNullOrWhiteSpace(queueText))
        {
            if (!permissions.Contains(SupportModule.Permissions.TicketsAssign))
            {
                errors.Add(new RecordFieldError("queueId", "Talebi oluştururken kuyruk seçmek için atama yetkisi gerekir."));
            }
            else if (FilterValue.Id(queueText) is not { } parsedQueue || !await Context.Data.Set<SupportQueue>().AnyAsync(q => q.Id == parsedQueue && q.IsActive, cancellationToken))
            {
                errors.Add(new RecordFieldError("queueId", "Kuyruk bulunamadı ya da pasif."));
            }
            else
            {
                queueId = parsedQueue;
            }
        }

        // Tekrar koruması: aynı istemci anahtarıyla gelen ikinci istek yeni talep açmaz.
        if (values.TryGetPropertyValue("clientKey", out var keyNode) && keyNode is JsonValue keyValue && keyValue.TryGetValue<string>(out var key) && !string.IsNullOrWhiteSpace(key))
        {
            key = key.Trim();
            if (key.Length > SupportTicket.ClientKeyMaxLength)
            {
                errors.Add(new RecordFieldError("clientKey", $"En fazla {SupportTicket.ClientKeyMaxLength} karakter olabilir."));
            }
            else if (await Context.Data.Set<SupportTicket>().AsNoTracking().Where(t => t.ClientKey == key).Select(t => new { t.Id, t.Number }).FirstOrDefaultAsync(cancellationToken) is { } existing)
            {
                throw new RecordConflictException($"Bu istek daha önce işlendi: talep {existing.Number} ({existing.Id}). Yeni talep açılmadı.");
            }
            else
            {
                entity.ClientKey = key;
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        entity.Number = TicketNumbers.New(DateTimeOffset.UtcNow);
        entity.CreatedBy = Context.CurrentUserId;
        entity.OwnerId = ownerId;
        entity.QueueId = queueId;
        return values;
    }

    protected override async Task AppliedAsync(SupportTicket entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var workflow = Context.Gate.IsOpen;

        if (!isNew && !workflow && !IsTransfer && entity.Status is TicketStatus.Resolved or TicketStatus.Closed or TicketStatus.Cancelled or TicketStatus.Merged)
        {
            errors.Add(new RecordFieldError("subject", "Çözülmüş, kapanmış ya da iptal edilmiş talep düzenlenemez; gerekirse önce yeniden açın."));
        }

        // Müşteri adı kopyası: oluştururken ve müşteri/kişi bağlantısı değişirken ortak kayıttan yenilenir.
        if (isNew || entity.PartyId != beforeParty || entity.PersonId != beforePerson)
        {
            entity.CustomerName = entity.PartyId is { } customerId
                ? await Context.Data.Set<Party>().AsNoTracking().Where(p => p.Id == customerId).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken)
                : entity.PersonId is { } contactId
                    ? await Context.Data.Set<Person>().AsNoTracking().Where(p => p.Id == contactId).Select(p => p.FirstName + " " + p.LastName).FirstOrDefaultAsync(cancellationToken)
                    : null;
        }

        // Müşteri tanımlı olmalı ya da başvuran adı verilmelidir (gereksiz müşteri kaydı açılmaz, ancak anonim talep de olmaz).
        if (entity.PartyId is null && entity.PersonId is null && string.IsNullOrWhiteSpace(entity.ReporterName))
        {
            errors.Add(new RecordFieldError("reporterName", "Müşteri seçin ya da başvuranın adını girin."));
        }

        // Bağımlı lookup: kişi, müşteri tarafına bağlı olmalıdır (ADR-0016).
        if (entity.PersonId is { } personId && entity.PartyId is { } partyId && (values.ContainsKey("personId") || values.ContainsKey("partyId"))
            && !await Context.Data.Set<Person>().AsNoTracking().AnyAsync(p => p.Id == personId && p.PartyId == partyId, cancellationToken))
        {
            errors.Add(new RecordFieldError("personId", "Kişi seçilen müşteriyle ilişkili değil."));
        }

        if (entity.CategoryId is { } categoryId && values.ContainsKey("categoryId") && (isNew || categoryId != beforeCategory)
            && !await Context.Data.Set<TicketCategory>().AsNoTracking().AnyAsync(c => c.Id == categoryId && c.IsActive, cancellationToken))
        {
            errors.Add(new RecordFieldError("categoryId", "Kategori pasif."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        // Öncelik etki × aciliyetten hesaplanır; yetkili ve gerekçeli elle değiştirilmişse (PriorityOverridden) hesap onu ezmez.
        if (!entity.PriorityOverridden && !(workflow && values.ContainsKey("priority")))
        {
            entity.Priority = await PriorityMatrices.ComputeAsync(Context, entity.Impact, entity.Urgency, cancellationToken);
        }

        // Kategoriye (ve üst kategoriye) bağlı zorunlu alanlar: yalnızca kullanıcı düzenlemesinde ve oluşturmada; iş akışı eylemleri alan kuralına takılmaz.
        if (!workflow && !IsTransfer)
        {
            var missing = await MissingRequiredFieldsAsync(entity, cancellationToken);
            if (missing.Count > 0)
            {
                throw new RecordValidationException([.. missing.Select(f => new RecordFieldError(f, "Bu kategori için bu alan zorunludur."))]);
            }
        }

        entity.UpdatedBy = Context.CurrentUserId;
        if (!isNew)
        {
            if (workflow && Context.Gate.EventKind is { } kind)
            {
                TicketHistory.Add(Context, entity, kind, Context.Gate.Reason, beforeStatus, beforeOwner, beforeQueue);
                if (Context.Gate.Effect is { } effect)
                {
                    await effect(Context, entity, cancellationToken);
                }
            }
            else if (entity.PartyId != beforeParty || entity.PersonId != beforePerson)
            {
                // Tanımsız başvuranı ortak firmaya/kişiye bağlamak (ya da değiştirmek) tarihçeye düşer.
                TicketHistory.Add(Context, entity, TicketEventKinds.CustomerLinked, null, beforeStatus, beforeOwner, beforeQueue);
            }

            // SLA örnekleri talebin durumu, önceliği ve ilk yanıtıyla aynı işlemde eşitlenir.
            if (entity.Status != beforeStatus || entity.Priority != beforePriority || entity.FirstResponseAt != beforeFirstResponse)
            {
                await SlaEngine.OnUpdatedAsync(Context, entity, beforeStatus, beforePriority, beforeFirstResponse, cancellationToken);
            }
        }
    }

    protected override async Task CreatedAsync(SupportTicket entity, CancellationToken cancellationToken)
    {
        entity.LastActivityAt = DateTimeOffset.UtcNow;
        entity.Priority = await PriorityMatrices.ComputeAsync(Context, entity.Impact, entity.Urgency, cancellationToken);

        // Yönlendirme sırası: (1) yetkiyle verilmiş açık kuyruk/sorumlu, (2) öncelik sıralı yönlendirme kuralı (ilk eşleşme; yalnızca açılışta bir kez), (3) kategori ve üst kategori
        // varsayılan kuyruğu, (4) tenant varsayılan kuyruğu, (5) kuyruksuz (atanmamış).
        TicketRoutingRule? rule = null;
        if (entity.QueueId is null && entity.OwnerId is null)
        {
            rule = await TicketRouting.MatchRuleAsync(Context, entity, cancellationToken);
            if (rule is not null)
            {
                if (rule.QueueId is { } ruleQueue && await Context.Data.Set<SupportQueue>().AsNoTracking().AnyAsync(q => q.Id == ruleQueue && q.IsActive, cancellationToken))
                {
                    entity.QueueId = ruleQueue;
                }

                // Pasif ya da üyeliği kapanmış sorumlu atanmaz; kural kuyruk eylemiyle sürer.
                if (rule.OwnerId is { } ruleOwner && await Context.Members.IsActiveMemberAsync(ruleOwner, cancellationToken))
                {
                    entity.OwnerId = ruleOwner;
                }
            }
        }

        entity.QueueId ??= await TicketRouting.ResolveQueueAsync(Context.Data, entity.CategoryId, cancellationToken);
        entity.Status = entity.OwnerId is null ? TicketStatus.New : TicketStatus.Assigned;
        TicketHistory.Add(Context, entity, TicketEventKinds.Created, null, null, null, null);
        if (rule is not null)
        {
            TicketHistory.Add(Context, entity, TicketEventKinds.RoutedByRule, $"Yönlendirme kuralı: {rule.Name}", null, null, null);
        }

        await SlaEngine.OnCreatedAsync(Context, entity, cancellationToken);
    }

    /// <summary>Talebin kategorisinin (ve üst kategorisinin) zorunlu alanlarından boş olanlar.</summary>
    private async Task<IReadOnlyList<string>> MissingRequiredFieldsAsync(SupportTicket ticket, CancellationToken cancellationToken)
    {
        if (ticket.CategoryId is not { } categoryId)
        {
            return [];
        }

        var chain = await Context.Data.Set<TicketCategory>().AsNoTracking().Where(c => c.Id == categoryId)
            .Select(c => new { c.RequiredFields, Parent = c.ParentId }).FirstOrDefaultAsync(cancellationToken);
        if (chain is null)
        {
            return [];
        }

        var required = CategoryFieldRules.Parse(chain.RequiredFields).ToList();
        if (chain.Parent is { } parentId)
        {
            required.AddRange(CategoryFieldRules.Parse(await Context.Data.Set<TicketCategory>().AsNoTracking().Where(c => c.Id == parentId).Select(c => c.RequiredFields).FirstOrDefaultAsync(cancellationToken)));
        }

        return CategoryFieldRules.Missing(required, field => field switch
        {
            "description" => !string.IsNullOrWhiteSpace(ticket.Description),
            "personId" => ticket.PersonId is not null,
            "reporterEmail" => !string.IsNullOrWhiteSpace(ticket.ReporterEmail),
            "reporterPhone" => !string.IsNullOrWhiteSpace(ticket.ReporterPhone),
            "eventDate" => ticket.EventDate is not null,
            "expectedOutcome" => !string.IsNullOrWhiteSpace(ticket.ExpectedOutcome),
            _ => true,
        });
    }

    protected override Task BeforeDeleteAsync(SupportTicket entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Talep silinemez; geçmişi korunur. Vazgeçildiyse talebi iptal edin.");
}

/// <summary>Liste sıralama anahtarları.</summary>
public static class TicketSort
{
    public static readonly IReadOnlyList<string> All = ["number", "-number", "subject", "priority", "createdAt", "-lastActivity", "resolutionDue", "firstResponseDue"];
}

/// <summary>Tarihçe satırı yazımı; talep güncellemesiyle aynı işlemde kaydedilir. Mesaj gövdesi tarihçeye girmez.</summary>
internal static class TicketHistory
{
    public static void Add(
        SupportStoreContext context, SupportTicket ticket, string kind, string? reason, string? fromStatus, Guid? fromOwner, Guid? fromQueue) =>
        context.Data.Set<TicketEvent>().Add(new TicketEvent
        {
            TicketId = ticket.Id,
            Kind = kind,
            ActorId = context.CurrentUserId,
            FromStatus = fromStatus,
            ToStatus = ticket.Status,
            FromOwnerId = fromOwner,
            ToOwnerId = ticket.OwnerId,
            FromQueueId = fromQueue,
            ToQueueId = ticket.QueueId,
            Reason = reason is { Length: > SupportTicket.ReasonMaxLength } ? reason[..SupportTicket.ReasonMaxLength] : reason,
        });
}

/// <summary>
/// Yönlendirme (dilim 1): kategori varsayılan kuyruğu → üst kategori varsayılan kuyruğu → tenant varsayılan kuyruğu → kuyruksuz (atanmamış).
/// Sıra sabit olduğu için sonuç belirlidir; pasif kuyruk atlanır. Öncelik sıralı kural motoru dilim 2'dedir.
/// </summary>
internal static class TicketRouting
{
    /// <summary>Talebe uyan ilk yönlendirme kuralı (<see cref="RoutingRuleSelector"/>); yoksa null.</summary>
    public static async Task<TicketRoutingRule?> MatchRuleAsync(SupportStoreContext context, SupportTicket ticket, CancellationToken cancellationToken)
    {
        var rules = await context.Data.Set<TicketRoutingRule>().AsNoTracking().Where(r => r.IsActive).ToListAsync(cancellationToken);
        if (rules.Count == 0)
        {
            return null;
        }

        var chain = await CategoryChainAsync(context.Data, ticket.CategoryId, cancellationToken);
        var selected = RoutingRuleSelector.Select(
            rules.Select(r => new RoutingRule(r.Id, r.Name, r.Order, r.IsActive, r.Channel, r.TypeKey, r.CategoryId, r.Priority, r.PartyId, r.QueueId, r.OwnerId)),
            new RoutingFacts(ticket.Channel, ticket.TypeKey, chain, ticket.Priority, ticket.PartyId));
        return selected is null ? null : rules.First(r => r.Id == selected.Id);
    }

    public static async Task<IReadOnlyList<Guid>> CategoryChainAsync(Modulix.Core.Contracts.Persistence.IModuleData data, Guid? categoryId, CancellationToken cancellationToken)
    {
        var chain = new List<Guid>();
        if (categoryId is { } id)
        {
            chain.Add(id);
            if (await data.Set<TicketCategory>().AsNoTracking().Where(c => c.Id == id).Select(c => c.ParentId).FirstOrDefaultAsync(cancellationToken) is { } parent)
            {
                chain.Add(parent);
            }
        }

        return chain;
    }

    public static async Task<Guid?> ResolveQueueAsync(Modulix.Core.Contracts.Persistence.IModuleData data, Guid? categoryId, CancellationToken cancellationToken)
    {
        if (categoryId is { } id)
        {
            var category = await data.Set<TicketCategory>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            for (var depth = 0; category is not null && depth < 3; depth++)
            {
                if (category.DefaultQueueId is { } queueId && await data.Set<SupportQueue>().AsNoTracking().AnyAsync(q => q.Id == queueId && q.IsActive, cancellationToken))
                {
                    return queueId;
                }

                category = category.ParentId is { } parent
                    ? await data.Set<TicketCategory>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == parent, cancellationToken)
                    : null;
            }
        }

        return await data.Set<SupportQueue>().AsNoTracking().Where(q => q.IsDefault && q.IsActive).Select(q => (Guid?)q.Id).FirstOrDefaultAsync(cancellationToken);
    }
}
