using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Activities;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Marketing;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Marketing.Domain;
using Modulix.Modules.Marketing.Records;

namespace Modulix.Modules.Marketing.Responses;

/// <summary>Yanıt kaydı isteği: yanıt, gerçekten gönderilmiş bir iletiye (teslimat satırı) bağlanır. Adres alınmaz.</summary>
public sealed record RegisterResponseRequest(Guid DeliveryId, string Kind, string? Note, DateTimeOffset? ReceivedAt);

public interface ICampaignResponses
{
    /// <summary>
    /// Yanıt kaydeder. Teslimat satırı çağıranın görebildiği bir kampanyaya ait ve sağlayıcıya verilmiş (kabul edildi ya da sonucu belirsiz) olmalıdır; kampanya, çalıştırma ve hedef
    /// kimlikleri satırdan kopyalanır.
    /// </summary>
    Task<RecordResult<RecordData>> RegisterAsync(RegisterResponseRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Yanıt eylemi (<see cref="ResponseActions"/>). Dış etki (lead, aktivite, çıkış kaydı) ile durum değişikliği tek işlemde yapılır; aynı yanıt için eşzamanlı eylemler
    /// kilitle sıralanır, bu yüzden ikinci lead açılmaz. Lead ve aktivite çağıranın CRM yetkisiyle açılır.
    /// </summary>
    Task<RecordResult<RecordData>> ActAsync(Guid responseId, string action, string? reason, long? expectedVersion, CancellationToken cancellationToken);
}

internal sealed class CampaignResponses(
    IRecordPipeline pipeline,
    IEnumerable<IAddressLookup> lookups,
    IEnumerable<ICampaignLeads> leads,
    IFollowUpActivities followUps,
    IOptOutRecorder optOuts,
    ITransactionLock locks,
    ICurrentAccess permissions,
    ITenantContextAccessor tenant,
    ITenantClock clock,
    DeliveryJournal deliveries,
    SnapshotRecipients labels,
    SegmentWorkflowGate gate,
    TimeProvider time) : ICampaignResponses
{
    private static readonly string[] WritePermissions = [MarketingModule.Permissions.ResponsesWrite];

    public async Task<RecordResult<RecordData>> RegisterAsync(RegisterResponseRequest request, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(MarketingModule.Permissions.ResponsesWrite, cancellationToken))
        {
            return Fail<RecordData>(RecordErrorCode.PermissionDenied, $"Bu işlem için '{MarketingModule.Permissions.ResponsesWrite}' izni gerekir.");
        }

        var errors = new List<RecordFieldError>();
        if (!ResponseKinds.All.Contains(request.Kind))
        {
            errors.Add(new RecordFieldError("kind", "Yanıt türü geçersiz."));
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > CampaignResponse.NoteMaxLength)
        {
            errors.Add(new RecordFieldError("note", $"Not en fazla {CampaignResponse.NoteMaxLength} karakter olabilir."));
        }

        var now = time.GetUtcNow();
        if (request.ReceivedAt is { } received && received > now.AddMinutes(5))
        {
            errors.Add(new RecordFieldError("receivedAt", "Yanıt zamanı gelecekte olamaz."));
        }

        if (errors.Count > 0)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Yanıt kaydı geçersiz.", errors);
        }

        // Teslimat satırı ancak çağıranın görebildiği bir çalıştırma ve kampanya üzerinden okunur: erişemediği satır "bulunamadı" gibi yanıtlanır.
        var delivery = await deliveries.FindAsync(request.DeliveryId, cancellationToken);
        var run = delivery is null ? null : await ReadAsync(MarketingModule.Entities.CampaignRun, delivery.RunId, cancellationToken);
        var campaign = run is null ? null : await ReadAsync(MarketingModule.Entities.Campaign, Guid.Parse(run.Values["campaignId"]!.GetValue<string>()), cancellationToken);
        if (delivery is null || run is null || campaign is null)
        {
            return Fail<RecordData>(RecordErrorCode.NotFound, "İleti bulunamadı.");
        }

        if (delivery.Status is not (DeliveryStatuses.Accepted or DeliveryStatuses.Unknown))
        {
            return Fail<RecordData>(RecordErrorCode.ValidationFailed, "Yanıt yalnızca gönderilmiş bir iletiye kaydedilir.", ("deliveryId", "Bu ileti sağlayıcıya verilmemiş."));
        }

        var values = new JsonObject
        {
            ["campaignId"] = campaign.Id.ToString(),
            ["runId"] = run.Id.ToString(),
            ["deliveryId"] = delivery.Id.ToString(),
            ["targetId"] = delivery.TargetId.ToString(),
            ["personId"] = delivery.PersonId?.ToString(),
            ["kind"] = request.Kind,
            ["note"] = note,
            ["receivedAt"] = (request.ReceivedAt ?? now).ToString("O", CultureInfo.InvariantCulture),
            ["status"] = ResponseStatuses.New,
            ["ownerId"] = tenant.Current?.UserId.ToString(),
        };
        using var open = gate.Open(WritePermissions);
        return await pipeline.CreateAsync(new RecordCommand(MarketingModule.Entities.CampaignResponse, null, values), cancellationToken);
    }

    public async Task<RecordResult<RecordData>> ActAsync(Guid responseId, string action, string? reason, long? expectedVersion, CancellationToken cancellationToken)
    {
        if (!ResponseActions.All.Contains(action))
        {
            return Fail<RecordData>(RecordErrorCode.ValidationFailed, "Bilinmeyen eylem.", ("action", "Geçersiz eylem."));
        }

        if (!await permissions.HasPermissionAsync(MarketingModule.Permissions.ResponsesWrite, cancellationToken))
        {
            return Fail<RecordData>(RecordErrorCode.PermissionDenied, $"Bu işlem için '{MarketingModule.Permissions.ResponsesWrite}' izni gerekir.");
        }

        var text = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (ResponseTransitions.NeedsReason(action) && text is null)
        {
            return Fail<RecordData>(RecordErrorCode.ValidationFailed, "Gerekçe zorunludur.", ("reason", "Bu işlem için gerekçe zorunludur."));
        }

        if (text?.Length > CampaignResponse.ReasonMaxLength)
        {
            return Fail<RecordData>(RecordErrorCode.ValidationFailed, "Gerekçe çok uzun.", ("reason", $"Gerekçe en fazla {CampaignResponse.ReasonMaxLength} karakter olabilir."));
        }

        // Okuma çağıranın izni ve veri kapsamıyla yapılır; görmediği yanıt "bulunamadı"dır.
        if (await ReadAsync(MarketingModule.Entities.CampaignResponse, responseId, cancellationToken) is null)
        {
            return Fail<RecordData>(RecordErrorCode.NotFound, "Yanıt bulunamadı.");
        }

        return await pipeline.RunAtomicAsync(async ct =>
        {
            await locks.AcquireAsync($"marketing-response:{tenant.Current!.TenantId:N}:{responseId:N}", ct);

            // Kilit alındıktan sonra yeniden okunur: eşzamanlı eylem durumu değiştirmiş olabilir.
            var loaded = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.CampaignResponse, responseId), ct);
            if (!loaded.IsSuccess)
            {
                return loaded;
            }

            var response = ResponseState.From(loaded.Value!);
            if (!ResponseTransitions.Allowed(action, response.Status, response.Kind))
            {
                return Fail<RecordData>(RecordErrorCode.Conflict, NotAllowedMessage(action, response));
            }

            var now = time.GetUtcNow();
            var userId = tenant.Current?.UserId;
            var values = new JsonObject { ["status"] = ResponseTransitions.Next(action, response.Status), ["updatedBy"] = userId?.ToString() };

            switch (action)
            {
                case ResponseActions.Take:
                    values["ownerId"] = userId?.ToString();
                    break;

                case ResponseActions.Close:
                    values["closedReason"] = text;
                    break;

                case ResponseActions.Reopen:
                    values["closedReason"] = null;
                    break;

                case ResponseActions.ConvertToLead:
                {
                    if (response.LeadId is not null)
                    {
                        return Fail<RecordData>(RecordErrorCode.Conflict, "Bu yanıt zaten bir lead'e bağlı.");
                    }

                    var lead = await ConvertAsync(response, ct);
                    if (lead.Failure is { } failure)
                    {
                        return failure;
                    }

                    values["leadId"] = lead.LeadId!.Value.ToString();
                    break;
                }

                case ResponseActions.CreateActivity:
                {
                    if (response.ActivityId is not null)
                    {
                        return Fail<RecordData>(RecordErrorCode.Conflict, "Bu yanıt için zaten bir aktivite açıldı.");
                    }

                    var activity = await ActivityAsync(response, ct);
                    if (activity.Failure is { } failure)
                    {
                        return failure;
                    }

                    values["activityId"] = activity.ActivityId!.Value.ToString();
                    break;
                }

                case ResponseActions.ApplyOptOut:
                {
                    var applied = await OptOutAsync(response, ct);
                    if (applied is { } failure)
                    {
                        return failure;
                    }

                    values["optOutAppliedAt"] = now.ToString("O", CultureInfo.InvariantCulture);
                    values["closedReason"] = "Çıkış uygulandı";
                    break;
                }
            }

            using var open = gate.Open(WritePermissions);
            return await pipeline.UpdateAsync(new RecordCommand(MarketingModule.Entities.CampaignResponse, responseId, values, expectedVersion), ct);
        }, cancellationToken);
    }

    // ---- Dış etkiler ----

    private async Task<(Guid? LeadId, RecordResult<RecordData>? Failure)> ConvertAsync(ResponseState response, CancellationToken cancellationToken)
    {
        var context = await ContextAsync(response, cancellationToken);
        if (context.Failure is { } contextFailure)
        {
            return (null, contextFailure);
        }

        var (campaign, delivery) = (context.Campaign!, context.Delivery!);

        // Kampanya zaten bir lead'e gönderildiyse yanıt o lead'e bağlanır; yeni lead açılmaz. Lead çağıranın boru hattı yetkisiyle okunabilmelidir.
        if (campaign.Target == SegmentTarget.Lead)
        {
            return await ReadAsync(MarketingModule.ExternalEntities.Lead, delivery.TargetId, cancellationToken) is null
                ? (null, Fail<RecordData>(RecordErrorCode.NotFound, "Lead bulunamadı ya da görme yetkiniz yok."))
                : (delivery.TargetId, null);
        }

        if (leads.FirstOrDefault() is not { } creator)
        {
            return (null, Fail<RecordData>(RecordErrorCode.Conflict, "Lead kullanılamıyor: bu firmada lead modülü kurulu değil."));
        }

        var contact = await ContactAsync(campaign, delivery, cancellationToken);
        var (first, last) = PersonNames.Split(contact.PersonName);
        var (source, medium, name) = UtmRules.Resolve(campaign.UtmSource, campaign.UtmMedium, campaign.UtmCampaign, campaign.Channel.ToString(), campaign.Code);
        var email = campaign.Channel == CommunicationChannel.Email ? contact.Address : null;
        var phone = campaign.Channel == CommunicationChannel.Email ? null : contact.Address;
        var result = await creator.CreateAsync(new CampaignLeadRequest(
            $"Kampanya yanıtı: {campaign.Name}",
            response.Note,
            first,
            last,
            contact.CompanyName,
            email,
            phone,
            response.OwnerId,
            $"Kampanya: {campaign.Name}",
            source,
            medium,
            name), cancellationToken);
        if (result.IsCreated)
        {
            return (result.LeadId, null);
        }

        return (null, Fail<RecordData>(
            result.Forbidden ? RecordErrorCode.PermissionDenied : RecordErrorCode.Conflict,
            result.Unavailable ? "Lead kullanılamıyor: bu firmada lead modülü kapalı." : result.Error ?? "Lead açılamadı."));
    }

    private async Task<(Guid? ActivityId, RecordResult<RecordData>? Failure)> ActivityAsync(ResponseState response, CancellationToken cancellationToken)
    {
        var context = await ContextAsync(response, cancellationToken);
        if (context.Failure is { } contextFailure)
        {
            return (null, contextFailure);
        }

        var due = (await clock.GetAsync(cancellationToken)).Today.AddDays(1);
        var (linkKey, linkId) = response.LeadId is { } leadId ? (MarketingModule.ExternalEntities.Lead, leadId) : (MarketingModule.Entities.CampaignResponse, response.Id);
        var result = await followUps.CreateAsync(new FollowUpRequest(
            $"Kampanya yanıtı: {context.Campaign!.Name}",
            response.Note,
            due,
            response.OwnerId,
            linkKey,
            linkId), cancellationToken);
        return result.IsCreated
            ? (result.ActivityId, null)
            : (null, Fail<RecordData>(RecordErrorCode.Conflict, result.Unavailable ? "Aktivite kullanılamıyor: bu firmada aktivite modülü kapalı." : result.Error ?? "Aktivite açılamadı."));
    }

    private async Task<RecordResult<RecordData>?> OptOutAsync(ResponseState response, CancellationToken cancellationToken)
    {
        var context = await ContextAsync(response, cancellationToken);
        if (context.Failure is { } contextFailure)
        {
            return contextFailure;
        }

        // Adres, gönderilen iletinin iletişim noktasından çözülür (başka bir adres değil). Çözülemiyorsa çıkış uygulanmış gibi gösterilmez.
        var contact = await ContactAsync(context.Campaign!, context.Delivery!, cancellationToken);
        if (contact.Address is null)
        {
            return Fail<RecordData>(RecordErrorCode.Conflict, "Adres artık çözülemiyor (iletişim noktası pasif ya da silinmiş); çıkışı iletişim tercihleri ekranından elle kaydedin.");
        }

        await optOuts.RecordAsync(context.Campaign!.Channel, contact.Address, "Kampanya yanıtı: iletişim istemiyorum", cancellationToken);
        return null;
    }

    // ---- Yardımcılar ----

    private sealed record CampaignState(Guid Id, string Code, string Name, SegmentTarget Target, CommunicationChannel Channel, string? UtmSource, string? UtmMedium, string? UtmCampaign);

    private sealed record ResponseContext(CampaignState? Campaign, CampaignDelivery? Delivery, RecordResult<RecordData>? Failure);

    private async Task<ResponseContext> ContextAsync(ResponseState response, CancellationToken cancellationToken)
    {
        var data = await ReadAsync(MarketingModule.Entities.Campaign, response.CampaignId, cancellationToken);
        var delivery = await deliveries.FindAsync(response.DeliveryId, cancellationToken);
        if (data is null || delivery is null)
        {
            return new ResponseContext(null, null, Fail<RecordData>(RecordErrorCode.NotFound, "Kampanya ya da ileti bulunamadı."));
        }

        var v = data.Values;
        return new ResponseContext(
            new CampaignState(
                data.Id, v["code"]!.GetValue<string>(), v["name"]!.GetValue<string>(), Enum.Parse<SegmentTarget>(v["target"]!.GetValue<string>()),
                Enum.Parse<CommunicationChannel>(v["channel"]!.GetValue<string>()), Text(v["utmSource"]), Text(v["utmMedium"]), Text(v["utmCampaign"])),
            delivery,
            null);
    }

    private sealed record ContactInfo(string? Address, string? PersonName, string? CompanyName);

    /// <summary>Gönderilen iletinin adresi ve kişi/firma adları. Adlar yalnızca çağıranın ortak kaydı okuma yetkisi varsa dolar; adres iletişim noktası artık yoksa null.</summary>
    private async Task<ContactInfo> ContactAsync(CampaignState campaign, CampaignDelivery delivery, CancellationToken cancellationToken)
    {
        string? address = null;
        if (lookups.FirstOrDefault(l => l.Supports(campaign.Target)) is { } lookup)
        {
            address = (await lookup.LookupAsync(campaign.Target, campaign.Channel, [new AddressLookup(delivery.TargetId, delivery.ContactPointId)], cancellationToken))
                .FirstOrDefault()?.Address;
        }

        string? company = null;
        string? person = null;
        if (campaign.Target == SegmentTarget.Company)
        {
            company = (await labels.LabelsAsync(SegmentTarget.Company, [delivery.TargetId], cancellationToken)).GetValueOrDefault(delivery.TargetId);
            if (delivery.PersonId is { } personId)
            {
                person = (await labels.LabelsAsync(SegmentTarget.Person, [personId], cancellationToken)).GetValueOrDefault(personId);
            }
        }
        else if (campaign.Target == SegmentTarget.Person)
        {
            person = (await labels.LabelsAsync(SegmentTarget.Person, [delivery.TargetId], cancellationToken)).GetValueOrDefault(delivery.TargetId);
        }

        return new ContactInfo(address, person, company);
    }

    private async Task<RecordData?> ReadAsync(string entityKey, Guid id, CancellationToken cancellationToken)
    {
        var result = await pipeline.GetAsync(new RecordRef(entityKey, id), cancellationToken);
        return result.IsSuccess ? result.Value : null;
    }

    private static string? Text(JsonNode? node) => node is null ? null : node.GetValue<string>();

    private static string NotAllowedMessage(string action, ResponseState response) => action switch
    {
        ResponseActions.ConvertToLead when !ResponseTransitions.CanBecomeLead(response.Kind) => "Bu yanıt türü lead'e dönüştürülemez (yalnızca ilgi, soru ve görüşme talebi).",
        ResponseActions.ApplyOptOut when response.Kind != ResponseKinds.OptOut => "Çıkış yalnızca 'iletişim istemiyorum' türündeki yanıttan uygulanır.",
        _ => $"'{StatusText(response.Status)}' durumundaki yanıtta bu işlem yapılamaz.",
    };

    private static string StatusText(string status) => status switch
    {
        ResponseStatuses.New => "yeni",
        ResponseStatuses.InProgress => "işlemde",
        ResponseStatuses.Converted => "lead'e dönüştürüldü",
        _ => "kapalı",
    };

    private static RecordResult<T> Fail<T>(RecordErrorCode code, string message, params (string Field, string Message)[] fields) =>
        RecordResult<T>.Fail(code, message, fields.Length == 0 ? null : [.. fields.Select(f => new RecordFieldError(f.Field, f.Message))]);

    private sealed record ResponseState(Guid Id, Guid CampaignId, Guid DeliveryId, string Kind, string Status, string? Note, Guid? OwnerId, Guid? LeadId, Guid? ActivityId)
    {
        public static ResponseState From(RecordData data)
        {
            var v = data.Values;
            return new ResponseState(
                data.Id,
                Guid.Parse(v["campaignId"]!.GetValue<string>()),
                Guid.Parse(v["deliveryId"]!.GetValue<string>()),
                v["kind"]!.GetValue<string>(),
                v["status"]!.GetValue<string>(),
                Text(v["note"]),
                GuidOrNull(v["ownerId"]),
                GuidOrNull(v["leadId"]),
                GuidOrNull(v["activityId"]));
        }

        private static Guid? GuidOrNull(JsonNode? node) => node is null ? null : Guid.Parse(node.GetValue<string>());
    }
}
