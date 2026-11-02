using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Marketing;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Marketing.Domain;
using Modulix.Modules.Marketing.Records;

namespace Modulix.Modules.Marketing.Segments;

/// <summary>
/// Segment iş akışı (ADR-0025): önizleme, yayım, yenileme, arşiv, kopya ve üye listesi. Yazma işlemleri kayıt boru hattından geçer
/// (izin, veri kapsamı, denetim kaydı, olay); durum ve sürüm alanları yalnızca bu servisin açtığı kapıyla yazılır.
/// </summary>
public interface ISegmentService
{
    /// <summary>Kayıt yazmaz. Koşul alan yetkisi ve veri kapsamıyla sayılır; satırlar alıcı görüntüleme yetkisine bağlıdır.</summary>
    Task<RecordResult<SegmentEvaluation>> PreviewAsync(string target, string? condition, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Segmentin güncel üyeliği: dinamikte yayımlanmış sürüm ∪ dahil ∖ hariç, statikte dahil ∖ hariç.</summary>
    Task<RecordResult<SegmentEvaluation>> MembersAsync(Guid segmentId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Taslak koşulu doğrular, çalıştırır ve değişmez bir sürüm olarak yayımlar (statik segmentte etkinleştirir).</summary>
    Task<RecordResult<RecordData>> PublishAsync(Guid segmentId, long? expectedVersion, CancellationToken cancellationToken);

    /// <summary>Yayımlanmış koşulu yeniden sayar ve hesaplama özetini günceller.</summary>
    Task<RecordResult<RecordData>> RefreshAsync(Guid segmentId, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> ArchiveAsync(Guid segmentId, long? expectedVersion, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> RestoreAsync(Guid segmentId, long? expectedVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Etkin segmentin güncel üyelerinin kimlikleri (hedef kitle için); segment etkin değilse ya da hedef türü uyuşmuyorsa hata. Çağıranın veri kapsamı
    /// ve alan yetkisi uygulanır. Sonuç <paramref name="limit"/>'ten büyükse "çok büyük" hatası.
    /// </summary>
    Task<RecordResult<IReadOnlyList<Guid>>> MemberIdsAsync(Guid segmentId, SegmentTarget expectedTarget, int limit, CancellationToken cancellationToken);

    /// <summary>Yeni taslak segment açar: tür, hedef, amaç ve koşul kopyalanır; üyeler ve sürüm geçmişi kopyalanmaz.</summary>
    Task<RecordResult<RecordData>> CopyAsync(Guid segmentId, string code, string name, CancellationToken cancellationToken);
}

internal sealed class SegmentService(
    IRecordPipeline pipeline, ISegmentEngine engine, SegmentWorkflowGate gate, TimeProvider time) : ISegmentService
{
    private static readonly IReadOnlyList<string> PublishPermissions = [MarketingModule.Permissions.SegmentsPublish];

    public async Task<RecordResult<SegmentEvaluation>> PreviewAsync(string target, string? condition, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<SegmentTarget>(target, ignoreCase: true, out var parsedTarget) || !Enum.IsDefined(parsedTarget) || char.IsDigit(target[0]))
        {
            return RecordResult<SegmentEvaluation>.Fail(RecordErrorCode.ValidationFailed, "Doğrulama hatası.", [new RecordFieldError("target", "Geçersiz hedef türü.")]);
        }

        var (parsed, errors) = SegmentConditions.Parse(condition);
        if (parsed is null)
        {
            return Invalid<SegmentEvaluation>(errors);
        }

        return await EvaluateAsync(new SegmentEvaluationRequest(parsedTarget, parsed, null, false, page, pageSize, true), cancellationToken);
    }

    public async Task<RecordResult<SegmentEvaluation>> MembersAsync(Guid segmentId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(segmentId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return RecordResult<SegmentEvaluation>.Fail(loaded.Error!.Code, loaded.Error.Message, loaded.Error.FieldErrors);
        }

        var segment = SegmentState.From(loaded.Value!);
        var condition = await PublishedConditionAsync(segment, cancellationToken);
        if (condition.Error is { } error)
        {
            return RecordResult<SegmentEvaluation>.Fail(error.Code, error.Message, error.FieldErrors);
        }

        return await EvaluateAsync(
            new SegmentEvaluationRequest(segment.Target, condition.Value, segmentId, true, page, pageSize, true), cancellationToken);
    }

    public async Task<RecordResult<RecordData>> PublishAsync(Guid segmentId, long? expectedVersion, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(segmentId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var segment = SegmentState.From(loaded.Value!);
        if (segment.Status == SegmentStatuses.Archived)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, "Arşivlenen segment yayımlanamaz; önce geri alın.");
        }

        SegmentCondition? condition = null;
        string? definition = null;
        if (segment.Kind == SegmentKinds.Dynamic)
        {
            var (parsed, errors) = SegmentConditions.Parse(segment.Draft);
            if (parsed is null)
            {
                return Invalid<RecordData>(errors.Count == 0 ? [new ConditionError("", "Yayımlamak için taslak koşul yazılmalıdır.")] : errors);
            }

            condition = parsed;
            definition = SegmentConditions.Serialize(parsed);
            var published = await PublishedDefinitionAsync(segmentId, cancellationToken);
            if (segment.Status == SegmentStatuses.Active && published == definition)
            {
                return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, "Taslakta yayımlanmamış değişiklik yok.");
            }
        }
        else if (segment.Status == SegmentStatuses.Active)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, "Segment zaten etkin.");
        }

        // Yayımlamadan önce koşul bir kez çalıştırılır: alan yetkisi, süre ve sonuç doğrulanır; sayı özet olarak saklanır.
        var evaluation = await EvaluateAsync(new SegmentEvaluationRequest(segment.Target, condition, segmentId, true, 1, 1, false), cancellationToken);
        if (!evaluation.IsSuccess)
        {
            return RecordResult<RecordData>.Fail(evaluation.Error!.Code, evaluation.Error.Message, evaluation.Error.FieldErrors);
        }

        using var open = gate.Open(PublishPermissions);
        return await pipeline.RunAtomicAsync(async ct =>
        {
            var versionNo = segment.PublishedVersion;
            if (definition is not null)
            {
                versionNo++;
                var version = await pipeline.CreateAsync(
                    new RecordCommand(MarketingModule.Entities.SegmentVersion, null, new JsonObject
                    {
                        ["segmentId"] = segmentId.ToString(),
                        ["versionNo"] = versionNo,
                        ["definition"] = definition,
                        ["publishedAt"] = time.GetUtcNow().ToString("O"),
                    }),
                    ct);
                if (!version.IsSuccess)
                {
                    return RecordResult<RecordData>.Fail(version.Error!.Code, version.Error.Message, version.Error.FieldErrors);
                }
            }

            return await WriteStateAsync(segmentId, expectedVersion, SegmentStatuses.Active, versionNo, evaluation.Value!, null, ct);
        }, cancellationToken);
    }

    public async Task<RecordResult<RecordData>> RefreshAsync(Guid segmentId, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(segmentId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var segment = SegmentState.From(loaded.Value!);
        if (segment.Status != SegmentStatuses.Active)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, "Yalnızca etkin segment yenilenir.");
        }

        var condition = await PublishedConditionAsync(segment, cancellationToken);
        if (condition.Error is { } error)
        {
            return RecordResult<RecordData>.Fail(error.Code, error.Message, error.FieldErrors);
        }

        var evaluation = await EvaluateAsync(new SegmentEvaluationRequest(segment.Target, condition.Value, segmentId, true, 1, 1, false), cancellationToken);
        using var open = gate.Open(PublishPermissions);
        if (!evaluation.IsSuccess)
        {
            // Hata da hesaplama özetine yazılır (son hesaplama, süre, hata); önceki sayı korunur.
            var message = evaluation.Error!.Message.Length > Segment.ErrorMaxLength ? evaluation.Error.Message[..Segment.ErrorMaxLength] : evaluation.Error.Message;
            var failed = await pipeline.UpdateAsync(
                new RecordCommand(MarketingModule.Entities.Segment, segmentId, new JsonObject { ["lastError"] = message, ["lastComputedAt"] = time.GetUtcNow().ToString("O") }),
                cancellationToken);
            return failed.IsSuccess
                ? RecordResult<RecordData>.Fail(evaluation.Error.Code, evaluation.Error.Message, evaluation.Error.FieldErrors)
                : failed;
        }

        return await WriteStateAsync(segmentId, null, null, null, evaluation.Value!, null, cancellationToken);
    }

    public async Task<RecordResult<IReadOnlyList<Guid>>> MemberIdsAsync(Guid segmentId, SegmentTarget expectedTarget, int limit, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(segmentId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return RecordResult<IReadOnlyList<Guid>>.Fail(loaded.Error!.Code, loaded.Error.Message, loaded.Error.FieldErrors);
        }

        var segment = SegmentState.From(loaded.Value!);
        if (segment.Status != SegmentStatuses.Active)
        {
            return RecordResult<IReadOnlyList<Guid>>.Fail(RecordErrorCode.Conflict, "Segment etkin değil; yalnızca yayımlanmış (etkin) segment hedef kitlede kullanılır.");
        }

        if (segment.Target != expectedTarget)
        {
            return RecordResult<IReadOnlyList<Guid>>.Fail(RecordErrorCode.Conflict, "Segmentin hedef türü kampanyanınkiyle aynı değil.");
        }

        var condition = await PublishedConditionAsync(segment, cancellationToken);
        if (condition.Error is { } error)
        {
            return RecordResult<IReadOnlyList<Guid>>.Fail(error.Code, error.Message, error.FieldErrors);
        }

        try
        {
            return RecordResult<IReadOnlyList<Guid>>.Ok(
                await engine.ResolveIdsAsync(new SegmentEvaluationRequest(segment.Target, condition.Value, segmentId, true, 1, 1, false), limit, cancellationToken));
        }
        catch (SegmentConditionException ex)
        {
            return RecordResult<IReadOnlyList<Guid>>.Fail(
                RecordErrorCode.ValidationFailed, "Koşul geçersiz.", [.. ex.Errors.Select(e => new RecordFieldError("condition", e.Message))]);
        }
        catch (SegmentTimeoutException ex)
        {
            return RecordResult<IReadOnlyList<Guid>>.Fail(RecordErrorCode.Conflict, ex.Message);
        }
        catch (SegmentTooLargeException ex)
        {
            return RecordResult<IReadOnlyList<Guid>>.Fail(RecordErrorCode.Conflict, ex.Message);
        }
    }

    public Task<RecordResult<RecordData>> ArchiveAsync(Guid segmentId, long? expectedVersion, CancellationToken cancellationToken) =>
        ChangeStatusAsync(segmentId, expectedVersion, s => s.Status == SegmentStatuses.Archived ? null : SegmentStatuses.Archived, "Segment zaten arşivde.", cancellationToken);

    public Task<RecordResult<RecordData>> RestoreAsync(Guid segmentId, long? expectedVersion, CancellationToken cancellationToken) =>
        ChangeStatusAsync(
            segmentId,
            expectedVersion,
            s => s.Status != SegmentStatuses.Archived ? null : s.PublishedVersion > 0 ? SegmentStatuses.Active : SegmentStatuses.Draft,
            "Yalnızca arşivlenen segment geri alınır.",
            cancellationToken);

    public async Task<RecordResult<RecordData>> CopyAsync(Guid segmentId, string code, string name, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(segmentId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var segment = SegmentState.From(loaded.Value!);
        var draft = segment.Draft ?? (segment.Kind == SegmentKinds.Dynamic ? await PublishedDefinitionAsync(segmentId, cancellationToken) : null);
        var values = new JsonObject
        {
            ["code"] = code,
            ["name"] = name,
            ["target"] = segment.Target.ToString(),
            ["kind"] = segment.Kind,
            ["purpose"] = loaded.Value!.Values["purpose"]?.DeepClone(),
            ["description"] = loaded.Value.Values["description"]?.DeepClone(),
        };
        if (draft is not null)
        {
            values["draft"] = draft;
        }

        return await pipeline.CreateAsync(new RecordCommand(MarketingModule.Entities.Segment, null, values), cancellationToken);
    }

    private async Task<RecordResult<RecordData>> ChangeStatusAsync(
        Guid segmentId, long? expectedVersion, Func<SegmentState, string?> next, string noChangeMessage, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(segmentId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var target = next(SegmentState.From(loaded.Value!));
        if (target is null)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, noChangeMessage);
        }

        using var open = gate.Open(PublishPermissions);
        return await pipeline.UpdateAsync(
            new RecordCommand(MarketingModule.Entities.Segment, segmentId, new JsonObject { ["status"] = target }, expectedVersion), cancellationToken);
    }

    /// <summary>Durum, yayın sürümü ve hesaplama özetini tek güncellemede yazar (kapı açık olmalıdır).</summary>
    private Task<RecordResult<RecordData>> WriteStateAsync(
        Guid segmentId, long? expectedVersion, string? status, int? publishedVersion, SegmentEvaluation evaluation, string? error, CancellationToken cancellationToken)
    {
        var values = new JsonObject
        {
            ["lastComputedAt"] = time.GetUtcNow().ToString("O"),
            ["lastMemberCount"] = evaluation.Count,
            ["lastComputeMs"] = evaluation.DurationMs,
            ["lastError"] = error,
        };
        if (status is not null)
        {
            values["status"] = status;
        }

        if (publishedVersion is { } version)
        {
            values["publishedVersion"] = version;
        }

        return pipeline.UpdateAsync(new RecordCommand(MarketingModule.Entities.Segment, segmentId, values, expectedVersion), cancellationToken);
    }

    private async Task<RecordResult<SegmentEvaluation>> EvaluateAsync(SegmentEvaluationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return RecordResult<SegmentEvaluation>.Ok(await engine.EvaluateAsync(request, cancellationToken));
        }
        catch (SegmentConditionException ex)
        {
            return Invalid<SegmentEvaluation>(ex.Errors);
        }
        catch (SegmentTimeoutException ex)
        {
            return RecordResult<SegmentEvaluation>.Fail(RecordErrorCode.Conflict, ex.Message);
        }
    }

    private Task<RecordResult<RecordData>> LoadAsync(Guid segmentId, CancellationToken cancellationToken) =>
        pipeline.GetAsync(new RecordRef(MarketingModule.Entities.Segment, segmentId), cancellationToken);

    /// <summary>Dinamik segmentte yayımlanmış sürümün koşulu; statikte ve hiç yayımlanmamışta koşul yoktur (null).</summary>
    private async Task<(SegmentCondition? Value, RecordError? Error)> PublishedConditionAsync(SegmentState segment, CancellationToken cancellationToken)
    {
        if (segment.Kind != SegmentKinds.Dynamic || segment.PublishedVersion == 0)
        {
            return (null, null);
        }

        var definition = await PublishedDefinitionAsync(segment.Id, cancellationToken);
        var (condition, errors) = SegmentConditions.Parse(definition);
        return condition is null
            ? (null, new RecordError(RecordErrorCode.ValidationFailed, "Yayımlanmış koşul okunamadı.", [.. errors.Select(e => new RecordFieldError(e.Path, e.Message))]))
            : (condition, null);
    }

    private async Task<string?> PublishedDefinitionAsync(Guid segmentId, CancellationToken cancellationToken)
    {
        var page = await pipeline.QueryAsync(
            new RecordQuery(MarketingModule.Entities.SegmentVersion, 1, 1, null, new Dictionary<string, string> { ["segmentId"] = segmentId.ToString() }),
            cancellationToken);
        return page.IsSuccess ? page.Value!.Items.FirstOrDefault()?.Values["definition"]?.GetValue<string>() : null;
    }

    private static RecordResult<T> Invalid<T>(IReadOnlyList<ConditionError> errors) =>
        RecordResult<T>.Fail(
            RecordErrorCode.ValidationFailed,
            "Koşul geçersiz.",
            [.. errors.Select(e => new RecordFieldError(e.Path.Length == 0 ? "condition" : "condition." + e.Path, e.Message))]);

    /// <summary>Segment kaydının iş akışının ihtiyaç duyduğu alanları.</summary>
    private sealed record SegmentState(Guid Id, SegmentTarget Target, string Kind, string Status, string? Draft, int PublishedVersion)
    {
        public static SegmentState From(RecordData data)
        {
            var values = data.Values;
            return new SegmentState(
                data.Id,
                Enum.Parse<SegmentTarget>(values["target"]!.GetValue<string>()),
                values["kind"]!.GetValue<string>(),
                values["status"]!.GetValue<string>(),
                values["draft"]?.GetValue<string>(),
                values["publishedVersion"]?.GetValue<int>() ?? 0);
        }
    }
}
