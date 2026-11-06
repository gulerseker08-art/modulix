using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Activities;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Takip aktivitesi bağlantı sözleşmesinin CRM uygulaması (ADR-0022, dilim 7): aktiviteyi ve istenen kayda bağını kayıt boru hattından açar (izin, kapsam,
/// audit, olay). Bağ kurulamazsa aktivite yine kalır. Atanan kullanıcı boşsa aktivite çağıran kullanıcıya açılır.
/// <para>
/// <b>Idempotency</b> (ADR-0027, Dilim 3): <see cref="FollowUpRequest.IdempotencyKey"/> verilirse aktivite <c>(externalSource = "automation", externalId = anahtar)</c>
/// ile açılır; bu çift veritabanında tekildir (filtreli benzersiz indeks), böylece en az bir kez teslim eden çağıran ikinci aktivite açamaz: anahtar zaten varsa
/// mevcut aktivite döner, eşzamanlı iki çağrıdan kaybeden de aynı yolla mevcut olanı bulur. Anahtar yalnızca bu yoldan, iş akışı kapısı iç çağrı için açılarak yazılır.
/// </para>
/// </summary>
internal sealed class CrmFollowUpActivities(IRecordPipeline pipeline, IModuleData data, ActivityWorkflowGate gate) : IFollowUpActivities
{
    /// <summary>İdempotent aktivitelerin <c>externalSource</c> değeri.</summary>
    public const string AutomationSource = "automation";

    public async Task<FollowUpResult> CreateAsync(FollowUpRequest request, CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey;
        if (key is not null)
        {
            if (key.Length > Activity.ExternalIdMaxLength)
            {
                return new FollowUpResult(null, "Idempotency anahtarı çok uzun.");
            }

            if (await ExistingAsync(key, cancellationToken) is { } existing)
            {
                return new FollowUpResult(existing, null, AlreadyExisted: true);
            }
        }

        var values = new JsonObject
        {
            ["type"] = "FollowUp",
            ["subject"] = request.Subject.Length > 200 ? request.Subject[..200] : request.Subject,
            ["dueAt"] = new DateTimeOffset(request.DueDate.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            values["notes"] = request.Description.Length > 2000 ? request.Description[..2000] : request.Description;
        }

        if (request.AssigneeId is { } assignee)
        {
            values["ownerId"] = assignee.ToString();
        }

        if (key is not null)
        {
            values["externalSource"] = AutomationSource;
            values["externalId"] = key;
        }

        RecordResult<RecordData> created;
        using (key is null ? null : gate.Open(CrmModule.Permissions.ActivitiesWrite))
        {
            created = await pipeline.CreateAsync(new RecordCommand(CrmModule.Entities.Activity, null, values), cancellationToken);
        }

        if (!created.IsSuccess)
        {
            // Eşzamanlı ikinci çağrı benzersiz indekse takılmış olabilir: kazananın aktivitesi varsa onu döndür.
            if (key is not null && await ExistingAsync(key, cancellationToken) is { } raced)
            {
                return new FollowUpResult(raced, null, AlreadyExisted: true);
            }

            return new FollowUpResult(null, created.Error!.Message, created.Error.Code is RecordErrorCode.FeatureNotLicensed or RecordErrorCode.NoActiveTenant);
        }

        await pipeline.CreateAsync(new RecordCommand(CrmModule.Entities.ActivityLink, null, new JsonObject
        {
            ["activityId"] = created.Value!.Id.ToString(),
            ["entityKey"] = request.LinkedEntityKey,
            ["recordId"] = request.LinkedRecordId.ToString(),
            ["isPrimary"] = false,
        }), cancellationToken);
        return new FollowUpResult(created.Value.Id, null);
    }

    private async Task<Guid?> ExistingAsync(string key, CancellationToken cancellationToken) =>
        await data.Set<Activity>().AsNoTracking()
            .Where(a => a.ExternalSource == AutomationSource && a.ExternalId == key)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>"crm.follow-up" kimliğinin izin çapası (başka modüllerin zamanlanmış işleri adına aktivite açar); olay işlemez.</summary>
internal sealed class CrmFollowUpAnchor : Modulix.Core.Contracts.Events.IEventSubscriber
{
    public Task HandleAsync(Modulix.Core.Contracts.Events.EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}
