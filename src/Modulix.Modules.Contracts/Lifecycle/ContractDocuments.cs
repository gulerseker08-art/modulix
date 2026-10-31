using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Records;

namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>PDF çizicinin girdisi: yalnızca render edilmiş bloklar ve üst/alt bilgi. Canlı veri, müşteri kartı ya da sözleşme buraya girmez.</summary>
public sealed record ContractPdfModel(
    string Title,
    string ContractNumber,
    int ContractVersionNo,
    string TemplateName,
    int TemplateVersionNo,
    string? ProviderName,
    string? ProviderDetails,
    string? FooterNote,
    bool IsExample,
    bool IsFinal,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<DocBlock> Blocks);

/// <summary>
/// PDF çizici (host uygular: Türkçe karakter, uzun tablolar, sayfa sonu, sayfa numarası). Modül çizim kütüphanesini bilmez; çıktı baytlarının
/// SHA-256 özeti belgeyle saklanır.
/// </summary>
public interface IContractPdfRenderer
{
    byte[] Render(ContractPdfModel model);
}

public sealed record ContractDocumentInfo(
    Guid Id, int ContractVersionNo, bool IsFinal, Guid TemplateId, int TemplateVersionNo, string TemplateName, bool IsExample, string FileName, int SizeBytes,
    string Sha256, Guid? CreatedBy, DateTimeOffset CreatedAt);

/// <param name="IsDraftTemplate">Önizleme yayımlanmamış çalışma kopyasıyla yapıldı (belge üretilmez).</param>
/// <param name="IsFinal">Sözleşme sürümü dondurulmuş: belge kesin içeriktir; aksi halde taslak kopya.</param>
public sealed record ContractPreview(
    string TemplateName, int TemplateVersionNo, bool IsExample, bool IsDraftTemplate, bool IsFinal, IReadOnlyList<DocBlock> Blocks, IReadOnlyList<string> Missing);

public sealed record ContractDocumentFile(ContractDocumentInfo Info, byte[] Content);

/// <summary>
/// Sözleşme belgesi önizleme ve üretimi (ADR-0022, dilim 4). Belge yalnızca yayımlanmış şablon sürümünden üretilir; hangi sözleşme sürümü, şablon
/// sürümü ve veri anlık görüntüsüyle üretildiği ile dosyanın SHA-256 özeti saklanır; satır değişmez. Şablon sonradan değişse de belge değişmez.
/// Eksik değişken ya da veri varsa belge üretilmez ve eksikler açıkça söylenir. Tutar değişkeni kullanan şablon finansal alan izni ister.
/// </summary>
public interface IContractDocuments
{
    /// <summary>Önizleme: <paramref name="useDraft"/> doğruysa çalışma kopyası (şablon düzenleme izni ister), aksi halde son yayımlanan sürüm.</summary>
    Task<RecordResult<ContractPreview>> PreviewAsync(Guid contractId, Guid templateId, bool useDraft, CancellationToken cancellationToken);

    Task<RecordResult<ContractDocumentInfo>> GenerateAsync(Guid contractId, Guid templateId, CancellationToken cancellationToken);

    Task<RecordResult<IReadOnlyList<ContractDocumentInfo>>> ListAsync(Guid contractId, CancellationToken cancellationToken);

    /// <summary>Belge dosyası: indirme izni (<c>contracts.documents.download</c>) ve sözleşmeye erişim ister.</summary>
    Task<RecordResult<ContractDocumentFile>> GetFileAsync(Guid contractId, Guid documentId, CancellationToken cancellationToken);
}

internal sealed class ContractDocuments(
    IRecordPipeline pipeline,
    ContractWorkflowGate gate,
    ICurrentAccess access,
    ContractReader reader,
    ContractsStoreContext storeContext,
    TimeProvider time,
    IContractPolicyProvider? policyProvider = null,
    IContractPdfRenderer? renderer = null) : IContractDocuments
{
    private sealed record Prepared(
        RecordData ContractRecord, Contract Contract, ContractSnapshot Snapshot, bool IsFinal, TemplateSource Template, ContractDocumentData Data, TemplateRender Render, ContractPolicySettings Policy);

    private sealed record TemplateSource(Guid Id, string Name, int VersionNo, string Body, bool IsExample, bool IsDraft);

    public async Task<RecordResult<ContractPreview>> PreviewAsync(Guid contractId, Guid templateId, bool useDraft, CancellationToken cancellationToken)
    {
        var (prepared, error) = await PrepareAsync<ContractPreview>(contractId, templateId, useDraft, cancellationToken);
        return prepared is null
            ? error!
            : RecordResult<ContractPreview>.Ok(new ContractPreview(
                prepared.Template.Name, prepared.Template.VersionNo, prepared.Template.IsExample, prepared.Template.IsDraft, prepared.IsFinal, prepared.Render.Blocks, prepared.Render.Missing));
    }

    public async Task<RecordResult<ContractDocumentInfo>> GenerateAsync(Guid contractId, Guid templateId, CancellationToken cancellationToken)
    {
        // Sözleşme görünürlüğü önce (kapsam dışı/başka tenant 404), sonra izin.
        var visible = (await pipeline.GetAsync(new RecordRef(ContractsModule.Entities.Contract, contractId), cancellationToken)).IsSuccess;
        if (!visible)
        {
            return RecordResult<ContractDocumentInfo>.Fail(RecordErrorCode.NotFound, "Sözleşme bulunamadı.");
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(ContractsModule.Permissions.DocumentsGenerate))
        {
            return RecordResult<ContractDocumentInfo>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{ContractsModule.Permissions.DocumentsGenerate}' izni gerekir.");
        }

        if (renderer is null)
        {
            return RecordResult<ContractDocumentInfo>.Fail(RecordErrorCode.Conflict, "PDF üretici yapılandırılmamış; belge üretilemez.");
        }

        var (prepared, error) = await PrepareAsync<ContractDocumentInfo>(contractId, templateId, useDraft: false, cancellationToken);
        if (prepared is null)
        {
            return error!;
        }

        if (prepared.Render.Missing.Count > 0)
        {
            var message = "Belge üretilemedi; eksik bilgiler: " + string.Join(", ", prepared.Render.Missing) + ".";
            return RecordResult<ContractDocumentInfo>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError("templateId", message)]);
        }

        var now = time.GetUtcNow();
        var c = prepared.Contract;
        var pdf = renderer.Render(new ContractPdfModel(
            prepared.Snapshot.Title, c.Number, prepared.Snapshot.VersionNo, prepared.Template.Name, prepared.Template.VersionNo, prepared.Policy.ProviderName,
            prepared.Policy.ProviderDetails, prepared.Policy.FooterNote, prepared.Template.IsExample, prepared.IsFinal, now, prepared.Render.Blocks));
        var sha = Convert.ToHexString(SHA256.HashData(pdf));
        var fileName = $"{c.Number}-v{prepared.Snapshot.VersionNo}-{ContractTemplateEngine.Slug(prepared.Template.Name)}.pdf";
        var document = new ContractDocument
        {
            ContractId = c.Id,
            ContractVersionNo = prepared.Snapshot.VersionNo,
            IsFinal = prepared.IsFinal,
            TemplateId = prepared.Template.Id,
            TemplateVersionNo = prepared.Template.VersionNo,
            TemplateName = prepared.Template.Name,
            IsExample = prepared.Template.IsExample,
            FileName = fileName,
            SizeBytes = pdf.Length,
            Sha256 = sha,
            Snapshot = ContractDocumentDataBuilder.SnapshotJson(prepared.Data, prepared.Snapshot.VersionNo, prepared.Template.VersionNo, prepared.Template.Name, now),
            Content = pdf,
            CreatedBy = storeContext.CurrentUserId,
        };

        var reason = $"{prepared.Template.Name} V{prepared.Template.VersionNo} · belge özeti {sha[..12]}{(prepared.IsFinal ? " · kesin" : " · taslak kopya")}";
        RecordResult<RecordData> result;
        using (gate.Open([ContractsModule.Permissions.DocumentsGenerate], ContractEventKinds.DocumentGenerated, reason, effect: (ctx, _, _) =>
        {
            ContractDocumentWriter.Add(ctx, document);
            return Task.CompletedTask;
        }))
        {
            // Belge, sözleşme geçişiyle (tarihçe satırı + audit) aynı işlemde yazılır; eşzamanlı düzenlemede sürüm çakışması (409) hiçbir belge bırakmaz.
            result = await pipeline.UpdateAsync(
                new RecordCommand(ContractsModule.Entities.Contract, contractId, new JsonObject(), prepared.ContractRecord.Version) { Reason = "contract.DocumentGenerated" }, cancellationToken);
        }

        return result.IsSuccess
            ? RecordResult<ContractDocumentInfo>.Ok(Info(document))
            : RecordResult<ContractDocumentInfo>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
    }

    public async Task<RecordResult<IReadOnlyList<ContractDocumentInfo>>> ListAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await pipeline.GetAsync(new RecordRef(ContractsModule.Entities.Contract, contractId), cancellationToken);
        if (!contract.IsSuccess)
        {
            return RecordResult<IReadOnlyList<ContractDocumentInfo>>.Fail(contract.Error!.Code, contract.Error.Message, contract.Error.FieldErrors);
        }

        return RecordResult<IReadOnlyList<ContractDocumentInfo>>.Ok([.. (await reader.DocumentsAsync(contractId, cancellationToken)).Select(Info)]);
    }

    public async Task<RecordResult<ContractDocumentFile>> GetFileAsync(Guid contractId, Guid documentId, CancellationToken cancellationToken)
    {
        var contract = await pipeline.GetAsync(new RecordRef(ContractsModule.Entities.Contract, contractId), cancellationToken);
        if (!contract.IsSuccess)
        {
            return RecordResult<ContractDocumentFile>.Fail(contract.Error!.Code, contract.Error.Message, contract.Error.FieldErrors);
        }

        if (!(await access.GetPermissionsAsync(cancellationToken)).Contains(ContractsModule.Permissions.DocumentsDownload))
        {
            return RecordResult<ContractDocumentFile>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{ContractsModule.Permissions.DocumentsDownload}' izni gerekir.");
        }

        if (await reader.DocumentMetaAsync(contractId, documentId, cancellationToken) is not { } meta
            || await reader.DocumentContentAsync(contractId, documentId, cancellationToken) is not { } content)
        {
            return RecordResult<ContractDocumentFile>.Fail(RecordErrorCode.NotFound, "Belge bulunamadı.");
        }

        return RecordResult<ContractDocumentFile>.Ok(new ContractDocumentFile(Info(meta), content));
    }

    /// <summary>Önizleme ve üretimin ortak hazırlığı: erişim, şablon kaynağı, uyum, izin, veri ve render.</summary>
    private async Task<(Prepared? Prepared, RecordResult<T>? Error)> PrepareAsync<T>(Guid contractId, Guid templateId, bool useDraft, CancellationToken cancellationToken)
    {
        RecordResult<T> Fail(RecordErrorCode code, string message, string field = "templateId") =>
            RecordResult<T>.Fail(code, message, code == RecordErrorCode.ValidationFailed ? [new RecordFieldError(field, message)] : null);

        var contractRecord = await pipeline.GetAsync(new RecordRef(ContractsModule.Entities.Contract, contractId), cancellationToken);
        if (!contractRecord.IsSuccess)
        {
            return (null, RecordResult<T>.Fail(contractRecord.Error!.Code, contractRecord.Error.Message, contractRecord.Error.FieldErrors));
        }

        var templateRecord = await pipeline.GetAsync(new RecordRef(ContractsModule.Entities.ContractTemplate, templateId), cancellationToken);
        if (!templateRecord.IsSuccess || await reader.TemplateAsync(templateId, cancellationToken) is not { } template)
        {
            return (null, Fail(RecordErrorCode.NotFound, "Şablon bulunamadı."));
        }

        var granted = await access.GetPermissionsAsync(cancellationToken);
        TemplateSource source;
        if (useDraft)
        {
            if (!granted.Contains(ContractsModule.Permissions.TemplatesEdit))
            {
                return (null, Fail(RecordErrorCode.PermissionDenied, $"Çalışma kopyasını önizlemek için '{ContractsModule.Permissions.TemplatesEdit}' izni gerekir."));
            }

            source = new TemplateSource(template.Id, template.Name, template.PublishedVersionNo ?? 0, template.Body, template.IsExample, IsDraft: true);
        }
        else
        {
            if (template.Status != TemplateStatus.Published || template.PublishedVersionNo is not { } publishedNo
                || await reader.TemplateVersionAsync(templateId, publishedNo, cancellationToken) is not { } version)
            {
                return (null, Fail(RecordErrorCode.Conflict, template.Status == TemplateStatus.Archived
                    ? "Arşivlenmiş şablondan belge üretilemez."
                    : "Şablon yayımlanmamış; belge yalnızca yayımlanmış şablon sürümünden üretilir."));
            }

            source = new TemplateSource(template.Id, version.Name, version.VersionNo, version.Body, version.IsExample, IsDraft: false);
        }

        var contract = await reader.ContractAsync(contractId, cancellationToken);
        if (contract is null)
        {
            return (null, Fail(RecordErrorCode.NotFound, "Sözleşme bulunamadı."));
        }

        if (template.ContractType is { } type && type != contract.Type)
        {
            return (null, Fail(RecordErrorCode.ValidationFailed, "Bu şablon seçilen sözleşme türü için tanımlı değil."));
        }

        if (ContractTemplateEngine.Validate(source.Body) is { Count: > 0 } issues)
        {
            return (null, Fail(RecordErrorCode.ValidationFailed, "Şablon metni geçersiz: " + issues[0].Message));
        }

        if (ContractTemplateEngine.UsesFinancials(source.Body) && !granted.Contains(ContractsModule.Permissions.FinancialsRead))
        {
            return (null, Fail(RecordErrorCode.PermissionDenied, $"Bu şablon tutar değişkenleri içeriyor; '{ContractsModule.Permissions.FinancialsRead}' izni gerekir."));
        }

        // Dondurulmuş (onaya/imzaya gitmiş) sürüm varsa belge onun içeriğinden üretilir ve kesindir; yoksa güncel taslak kopyadır.
        var frozen = await reader.VersionAsync(contractId, contract.CurrentVersionNo, cancellationToken);
        var snapshot = frozen is not null && ContractSnapshots.Deserialize(frozen.Snapshot) is { } stored
            ? stored
            : await ContractVersions.BuildSnapshotAsync(storeContext, contract, cancellationToken);
        var policy = policyProvider is null ? ContractPolicySettings.Default : await policyProvider.GetAsync(cancellationToken);
        var today = ContractPeriod.Today(time.GetUtcNow(), TimeZoneOf(policy.TimeZoneId));
        var data = await ContractDocumentDataBuilder.BuildAsync(storeContext, reader, snapshot, policy, today, cancellationToken);
        var render = ContractTemplateEngine.Render(source.Body, data);
        return (new Prepared(contractRecord.Value!, contract, snapshot, frozen is not null, source, data, render, policy), null);
    }

    private static TimeZoneInfo TimeZoneOf(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;

    private static ContractDocumentInfo Info(ContractDocument d) =>
        new(d.Id, d.ContractVersionNo, d.IsFinal, d.TemplateId, d.TemplateVersionNo, d.TemplateName, d.IsExample, d.FileName, d.SizeBytes, d.Sha256, d.CreatedBy, d.CreatedAt);

    private static ContractDocumentInfo Info(ContractDocumentMeta d) =>
        new(d.Id, d.ContractVersionNo, d.IsFinal, d.TemplateId, d.TemplateVersionNo, d.TemplateName, d.IsExample, d.FileName, d.SizeBytes, d.Sha256, d.CreatedBy, d.CreatedAt);
}
