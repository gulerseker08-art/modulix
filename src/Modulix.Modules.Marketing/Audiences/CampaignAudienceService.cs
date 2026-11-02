using System.Text.Json;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Marketing;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Marketing.Domain;
using Modulix.Modules.Marketing.Records;
using Modulix.Modules.Marketing.Segments;

namespace Modulix.Modules.Marketing.Audiences;

/// <summary>Hedef kitleye katkıda bulunan kaynak (segment ya da elle seçilenler) ve getirdiği hedef sayısı.</summary>
public sealed record AudienceSource(Guid? SegmentId, string? Name, string Mode, int Count);

public sealed record AudiencePreview(AudienceSummary Summary, IReadOnlyList<AudienceSource> Sources);

public sealed record AudienceRecipient(Guid TargetId, string? Label, Guid? PersonId, string Status, string? Code);

public sealed record AudienceRecipientsPage(IReadOnlyList<AudienceRecipient> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// Kampanya hedef kitlesi (ADR-0025, dilim 4): dahil/hariç segment ve kayıtlardan hedefleri çıkarır, kişi seçme kuralıyla alıcıları çözer, tekilleştirir
/// ve izin/engel kararıyla uygun/elenen ayırır; sonucu önizler ya da dondurur. Segment üyeliği gönderim izni vermez: uygunluk her zaman çekirdeğin
/// iletişim tercihinden gelir ve gönderimden hemen önce yeniden değerlendirilmelidir (dilim 5).
/// </summary>
public interface ICampaignAudience
{
    /// <summary>Hiçbir şey yazmaz; sayıları ve elenme nedeni dağılımını verir.</summary>
    Task<RecordResult<AudiencePreview>> PreviewAsync(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>Hesaplar ve değişmez bir anlık görüntü olarak yazar; kayıt (özet) döner. Sonradan segment ya da izin değişse o kayıt değişmez.</summary>
    Task<RecordResult<RecordData>> SnapshotAsync(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>Dondurulmuş alıcıları sayfalar (alıcı görüntüleme yetkisi gerekir). Durum: Eligible, Excluded, Unresolved.</summary>
    Task<RecordResult<AudienceRecipientsPage>> RecipientsAsync(Guid snapshotId, string? status, string? code, int page, int pageSize, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> ArchiveAsync(Guid campaignId, long? expectedVersion, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> RestoreAsync(Guid campaignId, long? expectedVersion, CancellationToken cancellationToken);
}

internal sealed class CampaignAudienceService(
    IRecordPipeline pipeline,
    ISegmentService segments,
    IEnumerable<IRecipientResolver> resolvers,
    ICommunicationPreferences preferences,
    IRecordAccess access,
    ICurrentAccess permissions,
    ITenantContextAccessor tenant,
    SegmentWorkflowGate gate,
    SnapshotPayload payload,
    SnapshotRecipients snapshotRecipients,
    TimeProvider time) : ICampaignAudience
{
    /// <summary>Bir kampanyanın hedef sayısı sınırı; üstündeki kitleler segmenti daraltmadan dondurulamaz.</summary>
    public const int MaxTargets = 20000;

    private static readonly IReadOnlyList<string> AudiencePermissions = [MarketingModule.Permissions.CampaignsAudience];

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<RecordResult<AudiencePreview>> PreviewAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var computed = await ComputeAsync(campaignId, cancellationToken);
        return computed.IsSuccess
            ? RecordResult<AudiencePreview>.Ok(new AudiencePreview(computed.Value!.Result.Summary, computed.Value.Sources))
            : RecordResult<AudiencePreview>.Fail(computed.Error!.Code, computed.Error.Message, computed.Error.FieldErrors);
    }

    public async Task<RecordResult<RecordData>> SnapshotAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var computed = await ComputeAsync(campaignId, cancellationToken);
        if (!computed.IsSuccess)
        {
            return RecordResult<RecordData>.Fail(computed.Error!.Code, computed.Error.Message, computed.Error.FieldErrors);
        }

        var (campaign, result, sources) = computed.Value!;
        if (campaign.Status == CampaignStatuses.Archived)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, "Arşivlenen kampanyanın hedef kitlesi dondurulamaz.");
        }

        if (result.Rows.Count > AudienceSnapshot.RecipientsMaxRows)
        {
            return RecordResult<RecordData>.Fail(
                RecordErrorCode.Conflict, $"Hedef kitle {result.Rows.Count:N0} satır; en fazla {AudienceSnapshot.RecipientsMaxRows:N0} dondurulabilir. Segmenti daraltın.");
        }

        var summary = result.Summary;
        payload.Recipients = JsonSerializer.Serialize(result.Rows.Select(Pack), Json);
        var values = new JsonObject
        {
            ["campaignId"] = campaignId.ToString(),
            ["target"] = campaign.Target,
            ["channel"] = campaign.Channel.ToString(),
            ["takenAt"] = time.GetUtcNow().ToString("O"),
            ["takenBy"] = tenant.Current?.UserId.ToString(),
            ["targetCount"] = summary.Targets,
            ["resolvedTargetCount"] = summary.ResolvedTargets,
            ["personCount"] = summary.Persons,
            ["uniqueAddressCount"] = summary.UniqueAddresses,
            ["eligibleCount"] = summary.Eligible,
            ["excludedCount"] = summary.Excluded,
            ["exclusions"] = JsonSerializer.Serialize(summary.Exclusions, Json),
            ["unresolved"] = JsonSerializer.Serialize(summary.Unresolved, Json),
            ["sources"] = JsonSerializer.Serialize(sources, Json),
        };

        using var open = gate.Open(AudiencePermissions);
        try
        {
            return await pipeline.CreateAsync(new RecordCommand(MarketingModule.Entities.AudienceSnapshot, null, values), cancellationToken);
        }
        finally
        {
            payload.Recipients = null;
        }
    }

    public async Task<RecordResult<AudienceRecipientsPage>> RecipientsAsync(
        Guid snapshotId, string? status, string? code, int page, int pageSize, CancellationToken cancellationToken)
    {
        // Okuma izni ve veri kapsamı kayıt boru hattında uygulanır (kampanya sorumlusuna göre); alıcıları görmek ayrı izindir.
        var snapshot = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.AudienceSnapshot, snapshotId), cancellationToken);
        if (!snapshot.IsSuccess)
        {
            return RecordResult<AudienceRecipientsPage>.Fail(snapshot.Error!.Code, snapshot.Error.Message, snapshot.Error.FieldErrors);
        }

        if (!await permissions.HasPermissionAsync(MarketingModule.Permissions.RecipientsRead, cancellationToken))
        {
            return RecordResult<AudienceRecipientsPage>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{MarketingModule.Permissions.RecipientsRead}' izni gerekir.");
        }

        var rows = Unpack(await snapshotRecipients.ReadAsync(snapshotId, cancellationToken));
        var filtered = rows.Where(r => (status is null || r.Status == status) && (code is null || r.Code == code)).ToList();
        var size = Math.Clamp(pageSize, 1, 200);
        var number = Math.Max(page, 1);
        var slice = filtered.Skip((number - 1) * size).Take(size).ToList();
        var target = Enum.Parse<SegmentTarget>(snapshot.Value!.Values["target"]!.GetValue<string>());
        var labels = await snapshotRecipients.LabelsAsync(target, [.. slice.Select(r => r.TargetId)], cancellationToken);
        return RecordResult<AudienceRecipientsPage>.Ok(new AudienceRecipientsPage(
            [.. slice.Select(r => new AudienceRecipient(r.TargetId, labels.GetValueOrDefault(r.TargetId), r.PersonId, r.Status, r.Code))], number, size, filtered.Count));
    }

    public Task<RecordResult<RecordData>> ArchiveAsync(Guid campaignId, long? expectedVersion, CancellationToken cancellationToken) =>
        ChangeStatusAsync(campaignId, expectedVersion, CampaignStatuses.Archived, CampaignStatuses.Draft, "Kampanya zaten arşivde.", cancellationToken);

    public Task<RecordResult<RecordData>> RestoreAsync(Guid campaignId, long? expectedVersion, CancellationToken cancellationToken) =>
        ChangeStatusAsync(campaignId, expectedVersion, CampaignStatuses.Draft, CampaignStatuses.Archived, "Yalnızca arşivlenen kampanya geri alınır.", cancellationToken);

    private async Task<RecordResult<RecordData>> ChangeStatusAsync(
        Guid campaignId, long? expectedVersion, string target, string requiredCurrent, string conflictMessage, CancellationToken cancellationToken)
    {
        var loaded = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.Campaign, campaignId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        if (loaded.Value!.Values["status"]?.GetValue<string>() != requiredCurrent)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, conflictMessage);
        }

        using var open = gate.Open([MarketingModule.Permissions.CampaignsWrite]);
        return await pipeline.UpdateAsync(
            new RecordCommand(MarketingModule.Entities.Campaign, campaignId, new JsonObject { ["status"] = target }, expectedVersion), cancellationToken);
    }

    private sealed record Computed(CampaignInfo Campaign, AudienceResult Result, IReadOnlyList<AudienceSource> Sources)
    {
        public void Deconstruct(out CampaignInfo campaign, out AudienceResult result, out IReadOnlyList<AudienceSource> sources)
        {
            campaign = Campaign;
            result = Result;
            sources = Sources;
        }
    }

    private sealed record CampaignInfo(SegmentTarget TargetType, string Target, string Status, CommunicationChannel Channel, RecipientRule Rule, string? Role, int? MaxPerTarget);

    private async Task<RecordResult<Computed>> ComputeAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var loaded = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.Campaign, campaignId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return RecordResult<Computed>.Fail(loaded.Error!.Code, loaded.Error.Message, loaded.Error.FieldErrors);
        }

        var v = loaded.Value!.Values;
        var campaign = new CampaignInfo(
            Enum.Parse<SegmentTarget>(v["target"]!.GetValue<string>()),
            v["target"]!.GetValue<string>(),
            v["status"]!.GetValue<string>(),
            Enum.Parse<CommunicationChannel>(v["channel"]!.GetValue<string>()),
            Enum.Parse<RecipientRule>(v["recipientRule"]!.GetValue<string>()),
            v["recipientRole"]?.GetValue<string>(),
            v["maxPerTarget"]?.GetValue<int>());

        var entries = new List<RecordData>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await pipeline.QueryAsync(
                new RecordQuery(MarketingModule.Entities.CampaignAudience, pageNumber, RecordQuery.MaxPageSize, null, new Dictionary<string, string> { ["campaignId"] = campaignId.ToString() }),
                cancellationToken);
            if (!page.IsSuccess)
            {
                return RecordResult<Computed>.Fail(page.Error!.Code, page.Error.Message, page.Error.FieldErrors);
            }

            entries.AddRange(page.Value!.Items);
            if (entries.Count >= page.Value.TotalCount || page.Value.Items.Count == 0)
            {
                break;
            }
        }

        if (!entries.Any(e => e.Values["mode"]!.GetValue<string>() == AudienceModes.Include))
        {
            return RecordResult<Computed>.Fail(RecordErrorCode.ValidationFailed, "Hedef kitle boş.", [new RecordFieldError("audience", "Hedef kitleye en az bir dahil edilen segment ya da hedef ekleyin.")]);
        }

        var includes = new HashSet<Guid>();
        var excludes = new HashSet<Guid>();
        var sources = new List<AudienceSource>();
        var manualInclude = 0;
        var manualExclude = 0;
        foreach (var entry in entries)
        {
            var include = entry.Values["mode"]!.GetValue<string>() == AudienceModes.Include;
            var set = include ? includes : excludes;
            if (entry.Values["segmentId"]?.GetValue<string>() is { } segmentText && Guid.TryParse(segmentText, out var segmentId))
            {
                var ids = await segments.MemberIdsAsync(segmentId, campaign.TargetType, MaxTargets, cancellationToken);
                if (!ids.IsSuccess)
                {
                    return RecordResult<Computed>.Fail(
                        ids.Error!.Code, $"Segment {segmentId.ToString()[^8..]}: {ids.Error.Message}", ids.Error.FieldErrors);
                }

                set.UnionWith(ids.Value!);
                var name = (await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.Segment, segmentId), cancellationToken)) is { IsSuccess: true } segment
                    ? segment.Value!.Values["name"]?.GetValue<string>()
                    : null;
                sources.Add(new AudienceSource(segmentId, name, entry.Values["mode"]!.GetValue<string>(), ids.Value!.Count));
            }
            else if (entry.Values["targetId"]?.GetValue<string>() is { } targetText && Guid.TryParse(targetText, out var targetId))
            {
                set.Add(targetId);
                if (include) { manualInclude++; } else { manualExclude++; }
            }
        }

        if (manualInclude > 0) { sources.Add(new AudienceSource(null, null, AudienceModes.Include, manualInclude)); }
        if (manualExclude > 0) { sources.Add(new AudienceSource(null, null, AudienceModes.Exclude, manualExclude)); }

        includes.ExceptWith(excludes);
        if (includes.Count > MaxTargets)
        {
            return RecordResult<Computed>.Fail(RecordErrorCode.Conflict, $"Hedef kitle {includes.Count:N0} hedef; en fazla {MaxTargets:N0} kabul edilir. Segmenti daraltın.");
        }

        var targets = includes.Order().ToList();
        var resolver = resolvers.FirstOrDefault(r => r.Supports(campaign.TargetType));
        if (resolver is null)
        {
            return RecordResult<Computed>.Fail(RecordErrorCode.Conflict, "Alıcıları çözecek bir modül yok (ör. CRM bu firma için kapalı).");
        }

        if (!await access.CanAsync(resolver.ReadEntityKey, RecordOperation.Read, cancellationToken))
        {
            return RecordResult<Computed>.Fail(RecordErrorCode.PermissionDenied, "Alıcıları çözmek için kişi kayıtlarını okuma yetkisi gerekir.");
        }

        var resolution = targets.Count == 0
            ? new RecipientResolution([], [])
            : await resolver.ResolveAsync(new RecipientRequest(campaign.TargetType, targets, campaign.Channel, campaign.Rule, campaign.Role), cancellationToken);
        var selection = AudienceRules.Select(targets, resolution, campaign.Channel, campaign.MaxPerTarget);
        var decisions = selection.Candidates.Count == 0
            ? new Dictionary<string, CommunicationDecision>()
            : (await preferences.EvaluateManyAsync(
                campaign.Channel, CommunicationPurpose.Marketing, [.. selection.Candidates.Select(c => c.Address!).Distinct(StringComparer.Ordinal)], cancellationToken))
                .ToDictionary(d => d.Key, d => d.Value);

        return RecordResult<Computed>.Ok(new Computed(campaign, AudienceRules.Finish(selection, decisions), sources));
    }

    private static object Pack(AudienceRow row) => new
    {
        t = row.TargetId,
        p = row.PersonId,
        c = row.ContactPointId,
        s = row.Status switch { AudienceStatuses.Eligible => "E", AudienceStatuses.Excluded => "X", _ => "U" },
        x = row.Code,
    };

    internal static List<AudienceRow> Unpack(string? json)
    {
        var rows = new List<AudienceRow>();
        if (string.IsNullOrEmpty(json) || JsonNode.Parse(json) is not JsonArray array)
        {
            return rows;
        }

        foreach (var node in array)
        {
            rows.Add(new AudienceRow(
                Guid.Parse(node!["t"]!.GetValue<string>()),
                node["p"] is { } p ? Guid.Parse(p.GetValue<string>()) : null,
                node["c"] is { } c ? Guid.Parse(c.GetValue<string>()) : null,
                null,
                node["s"]!.GetValue<string>() switch { "E" => AudienceStatuses.Eligible, "X" => AudienceStatuses.Excluded, _ => AudienceStatuses.Unresolved },
                node["x"]?.GetValue<string>()));
        }

        return rows;
    }
}
