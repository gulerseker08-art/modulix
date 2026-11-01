using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Support.Domain;
using Modulix.Modules.Support.Records;

namespace Modulix.Modules.Support.Lifecycle;

// Dilim 3 hizmetleri (ADR-0024): mükerrer uyarısı, öncelik matrisi toplu kaydı ve yönlendirme kuralı denemesi.

/// <summary>Mükerrer aramasının girdisi: en az bir başvuran kimliği (müşteri, e-posta ya da telefon) gerekir.</summary>
public sealed record DuplicateProbe(Guid? PartyId, string? ReporterEmail, string? ReporterPhone, string? Subject, Guid? CategoryId, Guid? ExcludeId);

public sealed record DuplicateCandidate(Guid Id, string Number, string Subject, string Status, DateTimeOffset CreatedAt, double Score, string Reason);

/// <summary>
/// Olası mükerrer uyarısı. Yalnızca çağıranın görebildiği (izin + veri kapsamı, kayıt boru hattı) açık talepler aday olur; son 90 günün, aynı başvuranın talepleri konu benzerliği
/// (<see cref="DuplicateRules"/>) ya da aynı kategori ile değerlendirilir. Uyarıdır: kayıt açılmasını engellemez, birleştirme ayrı ve yetkilidir.
/// </summary>
public interface ITicketDuplicates
{
    Task<RecordResult<IReadOnlyList<DuplicateCandidate>>> FindAsync(DuplicateProbe probe, CancellationToken cancellationToken);
}

internal sealed class TicketDuplicates(IRecordPipeline pipeline, SupportStoreContext context) : ITicketDuplicates
{
    public async Task<RecordResult<IReadOnlyList<DuplicateCandidate>>> FindAsync(DuplicateProbe probe, CancellationToken cancellationToken)
    {
        var email = string.IsNullOrWhiteSpace(probe.ReporterEmail) ? null : probe.ReporterEmail.Trim();
        var phone = string.IsNullOrWhiteSpace(probe.ReporterPhone) ? null : probe.ReporterPhone.Trim();
        if (probe.PartyId is null && email is null && phone is null)
        {
            return RecordResult<IReadOnlyList<DuplicateCandidate>>.Fail(RecordErrorCode.ValidationFailed, "Müşteri, e-posta ya da telefon verin.", [new RecordFieldError("partyId", "Başvuran kimliği gerekir.")]);
        }

        var found = new Dictionary<Guid, (RecordData Data, string Reason)>();
        foreach (var (filter, value, reason) in new[]
        {
            ("partyId", probe.PartyId?.ToString(), "Aynı müşteri"),
            ("reporterEmail", email, "Aynı başvuran e-postası"),
            ("reporterPhone", phone, "Aynı başvuran telefonu"),
        })
        {
            if (value is null)
            {
                continue;
            }

            var page = await pipeline.QueryAsync(new RecordQuery(SupportModule.Entities.Ticket, 1, 50, null,
                new Dictionary<string, string> { [filter] = value, ["open"] = "true", ["sort"] = "-lastActivity" }), cancellationToken);
            if (!page.IsSuccess)
            {
                return RecordResult<IReadOnlyList<DuplicateCandidate>>.Fail(page.Error!.Code, page.Error.Message, page.Error.FieldErrors);
            }

            foreach (var item in page.Value!.Items)
            {
                found.TryAdd(item.Id, (item, reason));
            }
        }

        var since = context.Time.GetUtcNow().AddDays(-90);
        var list = new List<DuplicateCandidate>();
        foreach (var (id, (data, reason)) in found)
        {
            if (id == probe.ExcludeId || TicketWorkflow.Time(data, "createdAt") is not { } createdAt || createdAt < since)
            {
                continue;
            }

            var subject = TicketWorkflow.Text(data, "subject") ?? string.Empty;
            if (DuplicateRules.IsPossibleDuplicate(probe.Subject, probe.CategoryId, subject, TicketWorkflow.Id(data, "categoryId"), sameApplicant: true))
            {
                list.Add(new DuplicateCandidate(id, TicketWorkflow.Text(data, "number") ?? string.Empty, subject, TicketWorkflow.Text(data, "status") ?? string.Empty, createdAt,
                    Math.Round(SubjectSimilarity.Score(probe.Subject, subject), 2), reason));
            }
        }

        return RecordResult<IReadOnlyList<DuplicateCandidate>>.Ok([.. list.OrderByDescending(c => c.Score).ThenByDescending(c => c.CreatedAt).Take(10)]);
    }
}

public sealed record PriorityCell(string Impact, string Urgency, string Priority);

/// <summary>
/// Öncelik matrisi toplu kaydı (yönetim izni): her hücre kayıt boru hattıyla (audit ve olayla) yazılır. Hücreler birer kayıt olduğundan toplu kayıt tek işlem değildir; hata olursa o ana
/// kadar yazılanlar kalır ve sonuç hatayı söyler (yeniden kaydetmek idempotenttir). Matris yalnızca sonradan açılan ya da etki/aciliyeti düzenlenen talepleri etkiler.
/// </summary>
public interface IPriorityMatrixService
{
    Task<RecordResult<IReadOnlyList<PriorityCell>>> GetAsync(CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<PriorityCell>>> SaveAsync(IReadOnlyList<PriorityCell> cells, CancellationToken cancellationToken);
}

internal sealed class PriorityMatrixService(IRecordPipeline pipeline, ICurrentAccess access, SupportStoreContext context) : IPriorityMatrixService
{
    private const string Key = SupportModule.Entities.PriorityMatrix;

    public async Task<RecordResult<IReadOnlyList<PriorityCell>>> GetAsync(CancellationToken cancellationToken)
    {
        var page = await pipeline.QueryAsync(new RecordQuery(Key, 1, 50, null, null), cancellationToken);
        if (!page.IsSuccess)
        {
            return RecordResult<IReadOnlyList<PriorityCell>>.Fail(page.Error!.Code, page.Error.Message, page.Error.FieldErrors);
        }

        // Tenant hücresi olmayanlar yerleşik değerle tamamlanır: arayüz her zaman 9 hücrenin geçerli değerini görür.
        var custom = page.Value!.Items.ToDictionary(i => (TicketWorkflow.Text(i, "impact")!, TicketWorkflow.Text(i, "urgency")!), i => TicketWorkflow.Text(i, "priority")!);
        return RecordResult<IReadOnlyList<PriorityCell>>.Ok([.. TicketLevel.All.SelectMany(i => TicketLevel.All.Select(u => new PriorityCell(i, u, PriorityMatrixRules.Compute(custom, i, u))))]);
    }

    public async Task<RecordResult<IReadOnlyList<PriorityCell>>> SaveAsync(IReadOnlyList<PriorityCell> cells, CancellationToken cancellationToken)
    {
        if (!await access.HasPermissionAsync(SupportModule.Permissions.SettingsManage, cancellationToken))
        {
            return RecordResult<IReadOnlyList<PriorityCell>>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{SupportModule.Permissions.SettingsManage}' izni gerekir.");
        }

        var errors = PriorityMatrixRules.Validate([.. cells.Select(c => (c.Impact, c.Urgency, c.Priority))]);
        if (errors.Count > 0)
        {
            return RecordResult<IReadOnlyList<PriorityCell>>.Fail(RecordErrorCode.ValidationFailed, "Matris geçersiz.", [.. errors.Select(e => new RecordFieldError(e.Field, e.Message))]);
        }

        var existing = await pipeline.QueryAsync(new RecordQuery(Key, 1, 50, null, null), cancellationToken);
        if (!existing.IsSuccess)
        {
            return RecordResult<IReadOnlyList<PriorityCell>>.Fail(existing.Error!.Code, existing.Error.Message, existing.Error.FieldErrors);
        }

        var byCell = existing.Value!.Items.ToDictionary(i => (TicketWorkflow.Text(i, "impact")!, TicketWorkflow.Text(i, "urgency")!));
        foreach (var cell in cells)
        {
            var values = new System.Text.Json.Nodes.JsonObject { ["impact"] = cell.Impact, ["urgency"] = cell.Urgency, ["priority"] = cell.Priority };
            var result = byCell.TryGetValue((cell.Impact, cell.Urgency), out var current)
                ? (TicketWorkflow.Text(current, "priority") == cell.Priority
                    ? RecordResult<RecordData>.Ok(current)
                    : await pipeline.UpdateAsync(new RecordCommand(Key, current.Id, values, current.Version), cancellationToken))
                : await pipeline.CreateAsync(new RecordCommand(Key, null, values), cancellationToken);
            if (!result.IsSuccess)
            {
                return RecordResult<IReadOnlyList<PriorityCell>>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
            }
        }

        return await GetAsync(cancellationToken);
    }
}

public sealed record RoutingPreviewInput(string Channel, string TypeKey, Guid? CategoryId, string Priority, Guid? PartyId);

/// <summary>Yönlendirme denemesi sonucu: eşleşen kural (yoksa kategori/varsayılan kuyruk yolu) ve sonuçta çıkacak kuyruk ile sorumlu. Hiçbir şey yazmaz.</summary>
public sealed record RoutingPreviewResult(bool RuleMatched, Guid? RuleId, string? RuleName, int? RuleOrder, Guid? QueueId, Guid? OwnerId, string Source);

public interface ITicketRoutingPreview
{
    Task<RecordResult<RoutingPreviewResult>> PreviewAsync(RoutingPreviewInput input, CancellationToken cancellationToken);
}

internal sealed class TicketRoutingPreview(ICurrentAccess access, SupportStoreContext context, IChoiceLists choiceLists) : ITicketRoutingPreview
{
    public async Task<RecordResult<RoutingPreviewResult>> PreviewAsync(RoutingPreviewInput input, CancellationToken cancellationToken)
    {
        if (!await access.HasPermissionAsync(SupportModule.Permissions.SettingsManage, cancellationToken))
        {
            return RecordResult<RoutingPreviewResult>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{SupportModule.Permissions.SettingsManage}' izni gerekir.");
        }

        var errors = new List<RecordFieldError>();
        if (!TicketChannels.All.Contains(input.Channel ?? string.Empty))
        {
            errors.Add(new RecordFieldError("channel", $"Şunlardan biri olmalıdır: {string.Join(", ", TicketChannels.All)}."));
        }

        if ((await choiceLists.GetAsync(SupportModule.Lists.TicketType, cancellationToken)).Find(input.TypeKey ?? string.Empty) is null)
        {
            errors.Add(new RecordFieldError("typeKey", "Listede olmayan değer."));
        }

        if (!TicketPriority.All.Contains(input.Priority ?? string.Empty))
        {
            errors.Add(new RecordFieldError("priority", $"Şunlardan biri olmalıdır: {string.Join(", ", TicketPriority.All)}."));
        }

        if (input.CategoryId is { } categoryId && !await context.Data.Set<TicketCategory>().AsNoTracking().AnyAsync(c => c.Id == categoryId, cancellationToken))
        {
            errors.Add(new RecordFieldError("categoryId", "Kategori bulunamadı."));
        }

        if (input.PartyId is { } partyId && !await context.Data.Set<Modulix.Core.Contracts.SharedRecords.Party>().AsNoTracking().AnyAsync(p => p.Id == partyId, cancellationToken))
        {
            errors.Add(new RecordFieldError("partyId", "Müşteri bulunamadı."));
        }

        if (errors.Count > 0)
        {
            return RecordResult<RoutingPreviewResult>.Fail(RecordErrorCode.ValidationFailed, "Deneme girdileri geçersiz.", errors);
        }

        // Geçici (kaydedilmeyen) talep: gerçek yönlendirmeyle aynı kod yolu.
        var probe = new SupportTicket { Channel = input.Channel!, TypeKey = input.TypeKey!, CategoryId = input.CategoryId, Priority = input.Priority!, PartyId = input.PartyId };
        var rule = await TicketRouting.MatchRuleAsync(context, probe, cancellationToken);
        Guid? queue = null;
        Guid? owner = null;
        if (rule is not null)
        {
            if (rule.QueueId is { } q && await context.Data.Set<SupportQueue>().AsNoTracking().AnyAsync(x => x.Id == q && x.IsActive, cancellationToken))
            {
                queue = q;
            }

            if (rule.OwnerId is { } o && await context.Members.IsActiveMemberAsync(o, cancellationToken))
            {
                owner = o;
            }
        }

        var source = queue is not null ? "Yönlendirme kuralı" : "Kategori / varsayılan kuyruk";
        queue ??= await TicketRouting.ResolveQueueAsync(context.Data, input.CategoryId, cancellationToken);
        return RecordResult<RoutingPreviewResult>.Ok(new RoutingPreviewResult(rule is not null, rule?.Id, rule?.Name, rule?.Order, queue, owner, queue is null ? "Kuyruksuz (atanmamış)" : source));
    }
}
