using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Leads;

/// <summary>
/// Lead formu yönetimi: entegrasyon sırrı üretimi/yenilemesi. Düz sır yalnızca burada, üretildiği anda bir kez döner; yalnızca SHA-256
/// özeti saklanır ve hiçbir okuma yolunda (API, audit, olay) görünmez. Sırrı yenilemek eskisini hemen geçersiz kılar. İşlem kayıt
/// boru hattından geçer (izin: <c>crm.lead-forms.manage</c>, audit: sır yenilenme zamanı).
/// </summary>
public interface ILeadFormAdmin
{
    Task<RecordResult<LeadFormSecretResult>> RotateSecretAsync(Guid formId, long version, CancellationToken cancellationToken);
}

public sealed record LeadFormSecretResult(RecordData Form, string Secret);

internal sealed class LeadFormAdmin(IRecordPipeline pipeline, LeadWorkflowGate gate, TimeProvider time) : ILeadFormAdmin
{
    public async Task<RecordResult<LeadFormSecretResult>> RotateSecretAsync(Guid formId, long version, CancellationToken cancellationToken)
    {
        var secret = LeadFormSecrets.NewSecret();
        RecordResult<RecordData> updated;
        using (gate.Open(CrmModule.Permissions.LeadFormsManage))
        {
            updated = await pipeline.UpdateAsync(new RecordCommand(CrmModule.Entities.LeadForm, formId, new JsonObject
            {
                ["secretHash"] = LeadFormSecrets.Hash(secret),
                ["secretRotatedAt"] = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            }, version), cancellationToken);
        }

        return updated.IsSuccess
            ? RecordResult<LeadFormSecretResult>.Ok(new LeadFormSecretResult(updated.Value!, secret))
            : RecordResult<LeadFormSecretResult>.Fail(updated.Error!.Code, updated.Error.Message, updated.Error.FieldErrors);
    }
}
