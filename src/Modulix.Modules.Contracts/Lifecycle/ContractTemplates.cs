using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Records;

namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>Yayımlanmış şablon sürümü (API görünümü); <see cref="Body"/> yalnızca tek sürüm okunurken gelir.</summary>
public sealed record ContractTemplateVersionView(
    Guid Id, int VersionNo, string Name, string Sha256, bool IsExample, string? Notes, Guid? PublishedBy, DateTimeOffset PublishedAt, string? Body);

/// <summary>
/// Şablon yaşam döngüsü: yayımlama, arşivleme, geri alma (ADR-0022, dilim 4). Düzenleme (<c>contracts.templates.edit</c>) ve yayımlama
/// (<c>contracts.templates.publish</c>) ayrı izinlerdir; yayımlama metni doğrular ve değişmez bir sürüm satırı üretir, belgeler yalnızca bu sürümden
/// üretilir. Tek yazma yolu iş akışı kapısıdır (kayıt boru hattı üzerinden: audit, olay, sürüm denetimi 409).
/// </summary>
public interface IContractTemplates
{
    Task<RecordResult<RecordData>> PublishAsync(Guid id, long version, string? notes, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> ArchiveAsync(Guid id, long version, string reason, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> RestoreAsync(Guid id, long version, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<ContractTemplateVersionView>>> VersionsAsync(Guid id, CancellationToken cancellationToken);

    Task<RecordResult<ContractTemplateVersionView>> VersionAsync(Guid id, int versionNo, CancellationToken cancellationToken);
}

internal sealed class ContractTemplates(
    IRecordPipeline pipeline,
    ContractWorkflowGate gate,
    ICurrentAccess access,
    ContractReader reader) : IContractTemplates
{
    private const string Key = ContractsModule.Entities.ContractTemplate;

    public async Task<RecordResult<RecordData>> PublishAsync(Guid id, long version, string? notes, CancellationToken cancellationToken)
    {
        var (template, error) = await LoadAsync(id, cancellationToken);
        if (template is null)
        {
            return error!;
        }

        if (Text(template, "status") == TemplateStatus.Archived)
        {
            return Conflict("Arşivlenmiş şablon yayımlanamaz; önce geri alın.");
        }

        var body = Text(template, "body") ?? string.Empty;
        if (ContractTemplateEngine.Validate(body) is { Count: > 0 } issues)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Şablon yayımlanamaz: metin geçersiz.",
                [.. issues.Select(i => new RecordFieldError("body", i.Line > 0 ? $"Satır {i.Line}: {i.Message}" : i.Message))]);
        }

        var publishedNo = template.Values["publishedVersionNo"]?.GetValue<int>();
        if (publishedNo is { } current && await reader.TemplateVersionAsync(id, current, cancellationToken) is { } latest && latest.Sha256 == Text(template, "bodySha256"))
        {
            return Conflict("Yayımlanmamış değişiklik yok; son yayımlanan sürümle aynı.");
        }

        var next = (publishedNo ?? 0) + 1;
        var text = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        var values = new JsonObject { ["status"] = TemplateStatus.Published, ["publishedVersionNo"] = next };
        using (gate.Open([ContractsModule.Permissions.TemplatesPublish], "Published", text, templateEffect: (ctx, t, _) =>
        {
            ContractTemplateVersions.Add(ctx, t, next, text);
            return Task.CompletedTask;
        }))
        {
            return await pipeline.UpdateAsync(new RecordCommand(Key, id, values, version) { Reason = "contract-template.Published" }, cancellationToken);
        }
    }

    public async Task<RecordResult<RecordData>> ArchiveAsync(Guid id, long version, string reason, CancellationToken cancellationToken)
    {
        var (template, error) = await LoadAsync(id, cancellationToken);
        if (template is null)
        {
            return error!;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Gerekçe zorunludur.", [new RecordFieldError("reason", "Gerekçe zorunludur.")]);
        }

        if (Text(template, "status") == TemplateStatus.Archived)
        {
            return Conflict("Şablon zaten arşivlenmiş.");
        }

        using (gate.Open([ContractsModule.Permissions.TemplatesPublish], "Archived", reason.Trim()))
        {
            return await pipeline.UpdateAsync(new RecordCommand(Key, id, new JsonObject { ["status"] = TemplateStatus.Archived }, version) { Reason = "contract-template.Archived" }, cancellationToken);
        }
    }

    public async Task<RecordResult<RecordData>> RestoreAsync(Guid id, long version, CancellationToken cancellationToken)
    {
        var (template, error) = await LoadAsync(id, cancellationToken);
        if (template is null)
        {
            return error!;
        }

        if (Text(template, "status") != TemplateStatus.Archived)
        {
            return Conflict("Yalnızca arşivlenmiş şablon geri alınır.");
        }

        // Yayımlanmış sürümü olan şablon yayımlanmış, olmayan taslak olarak döner.
        var status = template.Values["publishedVersionNo"] is null ? TemplateStatus.Draft : TemplateStatus.Published;
        using (gate.Open([ContractsModule.Permissions.TemplatesPublish], "Restored", null))
        {
            return await pipeline.UpdateAsync(new RecordCommand(Key, id, new JsonObject { ["status"] = status }, version) { Reason = "contract-template.Restored" }, cancellationToken);
        }
    }

    public async Task<RecordResult<IReadOnlyList<ContractTemplateVersionView>>> VersionsAsync(Guid id, CancellationToken cancellationToken)
    {
        var template = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!template.IsSuccess)
        {
            return RecordResult<IReadOnlyList<ContractTemplateVersionView>>.Fail(template.Error!.Code, template.Error.Message, template.Error.FieldErrors);
        }

        var versions = await reader.TemplateVersionsAsync(id, cancellationToken);
        return RecordResult<IReadOnlyList<ContractTemplateVersionView>>.Ok(
            [.. versions.Select(v => new ContractTemplateVersionView(v.Id, v.VersionNo, v.Name, v.Sha256, v.IsExample, v.Notes, v.PublishedBy, v.CreatedAt, null))]);
    }

    public async Task<RecordResult<ContractTemplateVersionView>> VersionAsync(Guid id, int versionNo, CancellationToken cancellationToken)
    {
        var template = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!template.IsSuccess)
        {
            return RecordResult<ContractTemplateVersionView>.Fail(template.Error!.Code, template.Error.Message, template.Error.FieldErrors);
        }

        return await reader.TemplateVersionAsync(id, versionNo, cancellationToken) is { } v
            ? RecordResult<ContractTemplateVersionView>.Ok(new ContractTemplateVersionView(v.Id, v.VersionNo, v.Name, v.Sha256, v.IsExample, v.Notes, v.PublishedBy, v.CreatedAt, v.Body))
            : RecordResult<ContractTemplateVersionView>.Fail(RecordErrorCode.NotFound, "Şablon sürümü bulunamadı.");
    }

    /// <summary>Şablonu okur (görünürlük boru hattında) ve yayımlama iznini denetler; sorun varsa hazır hata döner.</summary>
    private async Task<(RecordData? Template, RecordResult<RecordData>? Error)> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await pipeline.GetAsync(new RecordRef(Key, id), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return (null, loaded);
        }

        return (await access.GetPermissionsAsync(cancellationToken)).Contains(ContractsModule.Permissions.TemplatesPublish)
            ? (loaded.Value, null)
            : (null, RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{ContractsModule.Permissions.TemplatesPublish}' izni gerekir."));
    }

    private static string? Text(RecordData record, string field) =>
        record.Values.TryGetPropertyValue(field, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static RecordResult<RecordData> Conflict(string message) =>
        RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, message, [new RecordFieldError("status", message)]);
}
