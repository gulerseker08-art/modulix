using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Records;

namespace Modulix.Modules.Crm.Leads;

/// <summary>
/// Lead durum ve sahiplik eylemleri. Tek yazma yolu: eylem iznine bağlı iş akışı kapısı açılır ve kayıt boru hattı üzerinden
/// güncelleme yapılır; böylece kapsam, hassas alan, audit (eski/yeni değer, gerekçe) ve olay (neden: <c>lead.*</c>) otomatik
/// işlenir. Genel kayıt uçları durumu, sorumluyu ya da dönüşüm alanlarını yazamaz. Her eylem kaydın sürümünü ister (eşzamanlılık).
/// <para>
/// Bu arayüz yapay zekâ araçlarının (SearchLeads, SuggestLeadNextAction vb.) da kullanacağı uygulama servisidir; araçlar
/// kullanıcının izinleriyle bu yöntemleri çağırır. AI'ın kendiliğinden eleme/dönüştürme yapmasına izin verilmez (bkz. README).
/// </para>
/// </summary>
public interface ILeadWorkflow
{
    /// <summary>Sorumluyu değiştirir (<c>crm.leads.assign</c>). Eski/yeni sorumlu audit'e yazılır; atama e-postası genel abonelikten gider.</summary>
    Task<RecordResult<RecordData>> AssignAsync(Guid leadId, Guid ownerId, long version, CancellationToken cancellationToken);

    /// <summary>Yeni ya da takipteki Lead'i "İletişim kuruluyor"a alır. İlk temas tarihini DOLDURMAZ (yalnızca gerçekleşen temas doldurur).</summary>
    Task<RecordResult<RecordData>> StartContactingAsync(Guid leadId, long version, CancellationToken cancellationToken);

    /// <summary>Asgari koşulları (ve varsa sektör koşullarını) doğrulayıp Lead'i nitelikli yapar (<c>crm.leads.qualify</c>).</summary>
    Task<RecordResult<RecordData>> QualifyAsync(Guid leadId, long version, string? note, CancellationToken cancellationToken);

    /// <summary>Lead'i neden zorunlu olarak eler (<c>crm.leads.disqualify</c>). Neden, firmanın eleme nedeni listesindendir.</summary>
    Task<RecordResult<RecordData>> DisqualifyAsync(Guid leadId, long version, string reason, string? note, CancellationToken cancellationToken);

    /// <summary>Lead'i takip tarihiyle "Daha sonra takip"e alır (<c>crm.leads.qualify</c>). Tarih yoksa açık takip aktivitesinin tarihi kullanılır.</summary>
    Task<RecordResult<RecordData>> NurtureAsync(Guid leadId, long version, DateTimeOffset? followUpAt, string? note, CancellationToken cancellationToken);

    /// <summary>Elenmiş Lead'i gerekçeyle yeniden açar (<c>crm.leads.disqualify</c>). Dönüşmüş Lead yeniden açılamaz.</summary>
    Task<RecordResult<RecordData>> ReopenAsync(Guid leadId, long version, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Gerçekleşen bir temasın zamanını işler (olay aboneliği çağırır, <c>crm.leads.update</c> altında): ilk temas yalnızca daha
    /// erken bir temasla değişir; Lead yeniyse İletişim kuruluyor'a geçer. Aynı temasın tekrarı değişiklik üretmez (idempotent).
    /// </summary>
    Task<RecordResult<RecordData>> RecordContactAsync(Guid leadId, DateTimeOffset contactAt, CancellationToken cancellationToken);
}

/// <summary>Lead olay nedenleri (<see cref="RecordCommand.Reason"/>): genel kayıt olaylarının (<c>RecordUpdated</c>) yanında iş anlamını taşır.</summary>
public static class LeadEventReasons
{
    public const string Assigned = "lead.assigned";
    public const string Contacting = "lead.contacting";
    public const string Qualified = "lead.qualified";
    public const string Disqualified = "lead.disqualified";
    public const string Nurturing = "lead.nurturing";
    public const string Reopened = "lead.reopened";
    public const string FirstContact = "lead.first-contact";
    public const string Converted = "lead.converted";
}

internal sealed class LeadWorkflow(
    IRecordPipeline pipeline,
    LeadWorkflowGate gate,
    IEnumerable<ILeadQualificationRule> rules,
    TimeProvider time) : ILeadWorkflow
{
    private const string Key = CrmModule.Entities.Lead;

    public async Task<RecordResult<RecordData>> AssignAsync(Guid leadId, Guid ownerId, long version, CancellationToken cancellationToken)
    {
        if (await LoadAsync(leadId, cancellationToken) is not { Lead: { } lead, Error: null })
        {
            return await FailAsync(leadId, cancellationToken);
        }

        if (LeadRules.CanAssign(Status(lead)) is { } blocked)
        {
            return Invalid(blocked, "status");
        }

        // Aynı kişiye yeniden atama değişiklik üretmez: tekrar denemelerde bildirim/audit çoğalmaz.
        if (lead.Values["ownerId"]?.GetValue<string>() == ownerId.ToString())
        {
            return RecordResult<RecordData>.Ok(lead);
        }

        return await UpdateAsync(lead, version, CrmModule.Permissions.LeadsAssign, LeadEventReasons.Assigned, new JsonObject
        {
            ["ownerId"] = ownerId.ToString(),
            ["assignedAt"] = Now(),
        }, cancellationToken);
    }

    public async Task<RecordResult<RecordData>> StartContactingAsync(Guid leadId, long version, CancellationToken cancellationToken)
    {
        if (await LoadAsync(leadId, cancellationToken) is not { Lead: { } lead, Error: null })
        {
            return await FailAsync(leadId, cancellationToken);
        }

        return LeadRules.CanStartContacting(Status(lead)) is { } blocked
            ? Invalid(blocked, "status")
            : await UpdateAsync(lead, version, CrmModule.Permissions.LeadsQualify, LeadEventReasons.Contacting, new JsonObject
            {
                ["status"] = LeadStatus.Contacting,
                ["statusReason"] = null,
                ["followUpAt"] = null,
            }, cancellationToken);
    }

    public async Task<RecordResult<RecordData>> QualifyAsync(Guid leadId, long version, string? note, CancellationToken cancellationToken)
    {
        if (await LoadAsync(leadId, cancellationToken) is not { Lead: { } lead, Error: null })
        {
            return await FailAsync(leadId, cancellationToken);
        }

        if (LeadRules.CanQualify(Status(lead)) is { } blocked)
        {
            return Invalid(blocked, "status");
        }

        var missing = rules.SelectMany(r => r.Check(Snapshot(lead))).Distinct().ToList();
        if (missing.Count > 0)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Müşteri adayı nitelendirilemedi: eksik bilgiler var.",
                [.. missing.Select(m => new RecordFieldError("qualification", m))]);
        }

        return await UpdateAsync(lead, version, CrmModule.Permissions.LeadsQualify, LeadEventReasons.Qualified, new JsonObject
        {
            ["status"] = LeadStatus.Qualified,
            ["statusReason"] = Trim(note),
            ["qualifiedAt"] = Now(),
            ["followUpAt"] = null,
        }, cancellationToken);
    }

    public async Task<RecordResult<RecordData>> DisqualifyAsync(
        Guid leadId, long version, string reason, string? note, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Invalid("Eleme nedeni zorunludur.", "disqualificationReason");
        }

        if (await LoadAsync(leadId, cancellationToken) is not { Lead: { } lead, Error: null })
        {
            return await FailAsync(leadId, cancellationToken);
        }

        return LeadRules.CanDisqualify(Status(lead)) is { } blocked
            ? Invalid(blocked, "status")
            : await UpdateAsync(lead, version, CrmModule.Permissions.LeadsDisqualify, LeadEventReasons.Disqualified, new JsonObject
            {
                ["status"] = LeadStatus.Disqualified,
                ["disqualificationReason"] = reason.Trim(),
                ["statusReason"] = Trim(note),
                ["disqualifiedAt"] = Now(),
                ["followUpAt"] = null,
            }, cancellationToken);
    }

    public async Task<RecordResult<RecordData>> NurtureAsync(
        Guid leadId, long version, DateTimeOffset? followUpAt, string? note, CancellationToken cancellationToken)
    {
        if (await LoadAsync(leadId, cancellationToken) is not { Lead: { } lead, Error: null })
        {
            return await FailAsync(leadId, cancellationToken);
        }

        if (LeadRules.CanNurture(Status(lead)) is { } blocked)
        {
            return Invalid(blocked, "status");
        }

        // Takip tarihi ya da açık bir takip aktivitesi şarttır; ikincisi varsa tarih aktivitenin vadesinden alınır.
        var follow = followUpAt ?? await EarliestOpenActivityDueAsync(leadId, cancellationToken);
        if (follow is null)
        {
            return Invalid("Takip tarihi girin ya da müşteri adayına vadeli bir takip aktivitesi ekleyin.", "followUpAt");
        }

        if (followUpAt is not null && followUpAt <= time.GetUtcNow())
        {
            return Invalid("Takip tarihi gelecekte olmalıdır.", "followUpAt");
        }

        return await UpdateAsync(lead, version, CrmModule.Permissions.LeadsQualify, LeadEventReasons.Nurturing, new JsonObject
        {
            ["status"] = LeadStatus.Nurturing,
            ["statusReason"] = Trim(note),
            ["followUpAt"] = follow.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        }, cancellationToken);
    }

    public async Task<RecordResult<RecordData>> ReopenAsync(Guid leadId, long version, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Invalid("Yeniden açma gerekçesi zorunludur.", "statusReason");
        }

        if (await LoadAsync(leadId, cancellationToken) is not { Lead: { } lead, Error: null })
        {
            return await FailAsync(leadId, cancellationToken);
        }

        if (LeadRules.CanReopen(Status(lead)) is { } blocked)
        {
            return Invalid(blocked, "status");
        }

        var firstContact = lead.Values["firstContactAt"]?.GetValue<string>();
        return await UpdateAsync(lead, version, CrmModule.Permissions.LeadsDisqualify, LeadEventReasons.Reopened, new JsonObject
        {
            ["status"] = LeadRules.ReopenedStatus(firstContact is null ? null : DateTimeOffset.Parse(firstContact, CultureInfo.InvariantCulture)),
            ["statusReason"] = reason.Trim(),
            ["disqualifiedAt"] = null,
            ["disqualificationReason"] = null,
        }, cancellationToken);
    }

    public async Task<RecordResult<RecordData>> RecordContactAsync(Guid leadId, DateTimeOffset contactAt, CancellationToken cancellationToken)
    {
        if (await LoadAsync(leadId, cancellationToken) is not { Lead: { } lead, Error: null })
        {
            return await FailAsync(leadId, cancellationToken);
        }

        var status = Status(lead);
        var current = lead.Values["firstContactAt"]?.GetValue<string>() is { } text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture)
            : (DateTimeOffset?)null;
        var values = new JsonObject();
        if (current is null || contactAt < current)
        {
            values["firstContactAt"] = contactAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        if (status == LeadStatus.New)
        {
            values["status"] = LeadStatus.Contacting;
        }

        // Değişiklik yoksa yazılmaz: olay tekrar teslim edilse bile ikinci audit/olay oluşmaz.
        return values.Count == 0
            ? RecordResult<RecordData>.Ok(lead)
            : await UpdateAsync(lead, lead.Version, CrmModule.Permissions.LeadsUpdate, LeadEventReasons.FirstContact, values, cancellationToken);
    }

    // ---- yardımcılar

    private async Task<(RecordData? Lead, RecordError? Error)> LoadAsync(Guid leadId, CancellationToken cancellationToken)
    {
        var result = await pipeline.GetAsync(new RecordRef(Key, leadId), cancellationToken);
        return (result.Value, result.Error);
    }

    private async Task<RecordResult<RecordData>> FailAsync(Guid leadId, CancellationToken cancellationToken)
    {
        var result = await pipeline.GetAsync(new RecordRef(Key, leadId), cancellationToken);
        return result.IsSuccess
            ? RecordResult<RecordData>.Ok(result.Value!)
            : RecordResult<RecordData>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
    }

    private async Task<RecordResult<RecordData>> UpdateAsync(
        RecordData lead, long version, string permission, string reason, JsonObject values, CancellationToken cancellationToken)
    {
        using var _ = gate.Open(permission);
        return await pipeline.UpdateAsync(new RecordCommand(Key, lead.Id, values, version) { Reason = reason }, cancellationToken);
    }

    private async Task<DateTimeOffset?> EarliestOpenActivityDueAsync(Guid leadId, CancellationToken cancellationToken)
    {
        var open = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.Activity, 1, RecordQuery.MaxPageSize, null,
            new Dictionary<string, string> { ["leadId"] = leadId.ToString(), ["open"] = "true" }), cancellationToken);
        return open.IsSuccess
            ? open.Value!.Items
                .Select(a => a.Values["dueAt"]?.GetValue<string>())
                .Where(d => d is not null)
                .Select(d => DateTimeOffset.Parse(d!, CultureInfo.InvariantCulture))
                .Where(d => d > time.GetUtcNow())
                .Order()
                .Select(d => (DateTimeOffset?)d)
                .FirstOrDefault()
            : null;
    }

    private string Now() => time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static string Status(RecordData lead) => lead.Values["status"]?.GetValue<string>() ?? LeadStatus.New;

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static RecordResult<RecordData> Invalid(string message, string field) =>
        RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError(field, message)]);

    internal static LeadSnapshot Snapshot(RecordData lead)
    {
        string? Text(string name) => lead.Values[name]?.GetValue<string>();
        Guid? Id(string name) => Guid.TryParse(Text(name), out var id) ? id : null;
        return new LeadSnapshot(
            Text("subject") ?? string.Empty, Text("description"), Text("needDescription"), Text("productInterest"),
            Text("firstName"), Text("lastName"), Text("companyName"), Text("email"), Text("phone"),
            Id("companyId"), Id("contactId"),
            lead.Values["estimatedBudget"] is JsonValue budget && budget.TryGetValue<decimal>(out var amount) ? amount : null,
            Text("targetPurchaseDate") is { } date ? DateOnly.Parse(date, CultureInfo.InvariantCulture) : null,
            Text("industry"));
    }
}
