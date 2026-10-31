using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Activities;
using Modulix.Core.Contracts.Records;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Takip aktivitesi bağlantı sözleşmesinin CRM uygulaması (ADR-0022, dilim 7): aktiviteyi ve istenen kayda bağını kayıt boru hattından açar (izin, kapsam,
/// audit, olay). Bağ kurulamazsa aktivite yine kalır. Atanan kullanıcı boşsa aktivite çağıran kullanıcıya açılır.
/// </summary>
internal sealed class CrmFollowUpActivities(IRecordPipeline pipeline) : IFollowUpActivities
{
    public async Task<FollowUpResult> CreateAsync(FollowUpRequest request, CancellationToken cancellationToken)
    {
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

        var created = await pipeline.CreateAsync(new RecordCommand(CrmModule.Entities.Activity, null, values), cancellationToken);
        if (!created.IsSuccess)
        {
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
}
