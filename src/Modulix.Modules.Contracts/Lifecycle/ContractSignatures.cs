using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Records;

namespace Modulix.Modules.Contracts.Lifecycle;

public sealed record SignatoryInput(string Kind, Guid? UserId, string? Name, string? Email, string? Capacity, bool IsRequired = true);

/// <param name="Method"><see cref="SignatureMethod"/>.</param>
/// <param name="Mode"><see cref="SignatureMode"/>.</param>
public sealed record StartSignatureInput(Guid DocumentId, string Method, string Mode, int? ValidityDays, IReadOnlyList<SignatoryInput> Signatories);

/// <summary>Manuel imza kaydı: imzalı belge taraması ve imza tarihi. Doğrulayan yetkili kayıttan (oturum) gelir.</summary>
public sealed record ManualSignatureInput(DateOnly SignedOn, string? Note, string FileName, byte[] Content);

/// <summary>Sağlayıcı olayı (adaptörün normalleştirdiği biçim). <see cref="Reference"/> Modulix'in istekte verdiği referanstır.</summary>
public sealed record ProviderEventInput(
    string Provider, string EventId, int Sequence, string Type, string Reference, int? SignatoryPosition, DateTimeOffset OccurredAt, string? Assurance = null, string? Note = null);

public sealed record ProviderEventResult(string Outcome, string? Message = null);

public sealed record SignatoryView(
    Guid Id, int Position, string Kind, Guid? UserId, string Name, string? Email, string? Capacity, bool IsRequired, string Status, DateTimeOffset? SentAt,
    DateTimeOffset? SignedAt, string? Assurance, Guid? VerifiedBy, DateTimeOffset? VerifiedAt, Guid? EvidenceFileId, string? EvidenceFileName, string? DeclineReason,
    int ReminderCount, DateTimeOffset? LastReminderAt, bool CanSignNow);

public sealed record SignatureEventView(
    Guid Id, string Source, string EventType, Guid? SignatoryId, string? ProviderEventId, int? Sequence, string Outcome, string? Note, Guid? ActorId, DateTimeOffset At);

public sealed record SignatureRequestView(
    Guid Id, int VersionNo, Guid DocumentId, string DocumentSha256, string Method, string? Provider, string? ProviderReference, bool IsSimulation, string Mode, string Status, DateTimeOffset ExpiresAt,
    bool IsExpired, DateTimeOffset? CompletedAt, string? CloseReason, Guid? CreatedBy, DateTimeOffset CreatedAt, IReadOnlyList<SignatoryView> Signatories,
    IReadOnlyList<SignatureEventView> Events);

/// <summary>İmza özeti: sözleşmenin imza durumu, istekler (yeniden eskiye) ve bu tenant'ta hangi yöntemlerin açık olduğu.</summary>
public sealed record ContractSignatureOverview(
    string SignatureStatus, int CurrentVersionNo, IReadOnlyList<SignatureRequestView> Requests, bool ProviderEnabled, string? ProviderName, bool ProviderIsSimulation, int ValidityDays);

public sealed record SignatureFileContent(Guid Id, string FileName, string ContentType, byte[] Content, string Sha256);

/// <summary>
/// Sözleşme imzası (ADR-0022, dilim 6). İmza isteği onaylı (kesin) belgeye bağlıdır; imzacılar sıralı ya da paralel imzalar. İki yöntem ayrıdır ve eşdeğer sayılmaz:
/// <b>manuel</b> (imzalı belge taraması yüklenir, yetkili kişi doğrular; güvence düzeyi <see cref="SignatureAssurance.ManualScan"/>) ve <b>sağlayıcı</b>
/// (olaylar webhook ile gelir; simülasyon hukuki değer taşımaz). Yazmalar iş akışı kapısıyla sözleşme güncellemesiyle aynı işlemde yapılır; sürüm denetimi (409) geçerlidir.
/// Hiçbir yöntem müşteriye e-posta ya da imza isteği göndermez; hatırlatma yalnızca kaydedilir.
/// </summary>
public interface IContractSignatures
{
    Task<RecordResult<ContractSignatureOverview>> GetAsync(Guid contractId, CancellationToken cancellationToken);

    Task<RecordResult<ContractSignatureOverview>> StartAsync(Guid contractId, long version, StartSignatureInput input, CancellationToken cancellationToken);

    Task<RecordResult<ContractSignatureOverview>> RecordManualAsync(Guid contractId, Guid signatoryId, ManualSignatureInput input, CancellationToken cancellationToken);

    Task<RecordResult<ContractSignatureOverview>> DeclineAsync(Guid contractId, Guid signatoryId, string reason, CancellationToken cancellationToken);

    Task<RecordResult<ContractSignatureOverview>> RemindAsync(Guid contractId, Guid signatoryId, CancellationToken cancellationToken);

    Task<RecordResult<ContractSignatureOverview>> CancelAsync(Guid contractId, string reason, CancellationToken cancellationToken);

    /// <summary>Süresi dolmuş açık isteği sonlandırır (kullanıcı eylemi ya da zamanlanmış iş). Süre dolmadıysa 409.</summary>
    Task<RecordResult<ContractSignatureOverview>> ExpireAsync(Guid contractId, CancellationToken cancellationToken);

    /// <summary>Yalnızca simülasyon isteğinde: sağlayıcının yapacağı olayı (görüntüleme, imza, ret, süre aşımı) simüle eder; gerçek olayla aynı yoldan uygulanır.</summary>
    Task<RecordResult<ContractSignatureOverview>> SimulateAsync(Guid contractId, int signatoryPosition, string type, string? note, CancellationToken cancellationToken);

    /// <summary>Sağlayıcı olayını uygular (sistem kimliği): idempotent (aynı olay kimliği bir kez), sıra denetimli (olay numarası artmalı).</summary>
    Task<RecordResult<ProviderEventResult>> ApplyProviderEventAsync(ProviderEventInput input, CancellationToken cancellationToken);

    Task<RecordResult<SignatureFileContent>> GetEvidenceAsync(Guid contractId, Guid fileId, CancellationToken cancellationToken);
}

internal sealed class ContractSignatures(
    IRecordPipeline pipeline,
    ContractWorkflowGate gate,
    ICurrentAccess access,
    ContractReader reader,
    ContractsStoreContext storeContext,
    ITenantContextAccessor tenant,
    TimeProvider time,
    IContractDocuments documents,
    IContractSignatureProviders providers,
    IContractPolicyProvider? policy = null) : IContractSignatures
{
    private const string Key = ContractsModule.Entities.Contract;
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromHours(1);

    private sealed record Loaded(RecordData Record, Contract Contract, ContractPolicySettings Policy);

    // ---- okuma

    public async Task<RecordResult<ContractSignatureOverview>> GetAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var (loaded, error) = await LoadAsync<ContractSignatureOverview>(contractId, null, cancellationToken);
        return loaded is null ? error! : RecordResult<ContractSignatureOverview>.Ok(await OverviewAsync(loaded, cancellationToken));
    }

    public async Task<RecordResult<SignatureFileContent>> GetEvidenceAsync(Guid contractId, Guid fileId, CancellationToken cancellationToken)
    {
        var (loaded, error) = await LoadAsync<SignatureFileContent>(contractId, ContractsModule.Permissions.DocumentsDownload, cancellationToken);
        if (loaded is null)
        {
            return error!;
        }

        return await reader.SignatureFileAsync(contractId, fileId, cancellationToken) is { } file
            ? RecordResult<SignatureFileContent>.Ok(new SignatureFileContent(file.Id, file.FileName, file.ContentType, file.Content, file.Sha256))
            : RecordResult<SignatureFileContent>.Fail(RecordErrorCode.NotFound, "Dosya bulunamadı.");
    }

    // ---- başlatma

    public async Task<RecordResult<ContractSignatureOverview>> StartAsync(Guid contractId, long version, StartSignatureInput input, CancellationToken cancellationToken)
    {
        var (loaded, error) = await LoadAsync<ContractSignatureOverview>(contractId, ContractsModule.Permissions.SignaturesManage, cancellationToken);
        if (loaded is null)
        {
            return error!;
        }

        var c = loaded.Contract;
        if (c.ApprovalStatus != ApprovalStatus.Approved || c.PreparationStatus != PreparationStatus.ReadyToPublish)
        {
            return Conflict("Yalnızca onaylanmış ve yayıma hazır sözleşme imzaya gönderilir.");
        }

        if (c.SignatureStatus is SignatureStatus.Pending or SignatureStatus.PartiallySigned or SignatureStatus.Completed)
        {
            return Conflict(c.SignatureStatus == SignatureStatus.Completed ? "Bu sürüm zaten imzalandı; değişiklik için yeni revizyon açın." : "Açık bir imza isteği var; önce onu sonlandırın.");
        }

        if (c.EffectiveStatus is EffectiveStatus.Expired or EffectiveStatus.Terminated or EffectiveStatus.Cancelled)
        {
            return Conflict("Süresi dolmuş, feshedilmiş ya da iptal edilmiş sözleşme imzaya gönderilemez.");
        }

        var errors = new List<RecordFieldError>();
        var provider = providers.Find(loaded.Policy.SignatureProvider);
        if (!SignatureMethod.All.Contains(input.Method))
        {
            errors.Add(new RecordFieldError("method", "Yöntem Manual ya da Provider olmalıdır."));
        }
        else if (input.Method == SignatureMethod.Provider && provider is null)
        {
            errors.Add(new RecordFieldError("method", "Bu firmada imza sağlayıcısı etkin değil (Sözleşme ayarları → İmza sağlayıcısı)."));
        }

        if (!SignatureMode.All.Contains(input.Mode))
        {
            errors.Add(new RecordFieldError("mode", "Kip Sequential ya da Parallel olmalıdır."));
        }

        var validity = input.ValidityDays ?? loaded.Policy.SignatureValidityDays;
        if (validity is < 1 or > 90)
        {
            errors.Add(new RecordFieldError("validityDays", "Geçerlilik 1 ile 90 gün arasında olmalıdır."));
        }

        var members = await storeContext.Members.ListActiveAsync(cancellationToken);
        var resolved = ResolveSignatories(input, members, input.Method == SignatureMethod.Provider, errors);

        var document = (await documents.ListAsync(contractId, cancellationToken)) is { IsSuccess: true } list ? list.Value!.FirstOrDefault(d => d.Id == input.DocumentId) : null;
        if (document is null)
        {
            errors.Add(new RecordFieldError("documentId", "Belge bulunamadı."));
        }
        else if (!document.IsFinal || document.ContractVersionNo != c.CurrentVersionNo)
        {
            errors.Add(new RecordFieldError("documentId", "İmzaya yalnızca güncel sürümün kesin (dondurulmuş sürümden üretilmiş) belgesi gönderilir."));
        }
        else if (document.IsExample)
        {
            errors.Add(new RecordFieldError("documentId", "Örnek metinden üretilen belge imzaya gönderilemez; kurumunuzun onaylı şablonunu kullanın."));
        }

        if (errors.Count > 0 || document is null)
        {
            return RecordResult<ContractSignatureOverview>.Fail(RecordErrorCode.ValidationFailed, "İmza isteği doğrulanamadı.", errors);
        }

        var now = time.GetUtcNow();
        var expiresAt = now.AddDays(validity);
        // Başlangıçta sırası gelen imzacılar (sıralı kipte ilk zorunlu, paralelde hepsi) "gönderildi" olur; diğerleri bekler.
        var infos = resolved.Select((s, i) => new SignatoryInfo(new Guid(i + 1, 0, 0, new byte[8]), i + 1, s.IsRequired, SignatoryStatus.Waiting)).ToList();
        var sequential = input.Mode == SignatureMode.Sequential;
        var activePositions = SignatureFlow.Active(input.Mode, infos).Select(id => infos.First(i => i.Id == id).Position).ToHashSet();
        var newSignatories = resolved.Select((s, i) => new NewSignatory(
            i + 1, s.Kind, s.UserId, s.Name, s.Email, s.Capacity, s.IsRequired,
            activePositions.Contains(i + 1) ? SignatoryStatus.Sent : SignatoryStatus.Waiting, activePositions.Contains(i + 1) ? now : null)).ToList();

        string? providerReference = null;
        if (input.Method == SignatureMethod.Provider)
        {
            // Referans tenant kimliğini taşır: sağlayıcının webhook çağrısı oturumsuzdur, tenant bu referanstan çözülür (gizli değildir; kimlik doğrulama imzadadır).
            providerReference = $"{tenant.Current?.TenantId:N}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()}";
            await provider!.CreateRequestAsync(
                new ProviderSignatureRequest(
                    providerReference, document.FileName, document.Sha256,
                    [.. newSignatories.Select(s => new ProviderSignatory(s.Position, s.Name, s.Email, s.Capacity, s.IsRequired))], expiresAt, sequential),
                cancellationToken);
        }

        var request = new NewSignatureRequest(
            document.Id, document.Sha256, input.Method, input.Method == SignatureMethod.Provider ? provider!.Name : null, providerReference, input.Mode, expiresAt, newSignatories);
        var result = await CommitAsync(
            loaded, version, [ContractsModule.Permissions.SignaturesManage], ContractEventKinds.SignatureRequested,
            $"{(input.Method == SignatureMethod.Provider ? "Sağlayıcı: " + provider!.DisplayName : "Manuel imza")}; {newSignatories.Count} imzacı; belge {document.FileName}",
            Set(("signatureStatus", SignatureStatus.Pending)),
            (ctx, contract, _) =>
            {
                ContractSignatureWriter.Create(ctx, contract, request);
                return Task.CompletedTask;
            },
            cancellationToken);

        if (!result.IsSuccess)
        {
            if (providerReference is not null)
            {
                await provider!.CancelRequestAsync(providerReference, cancellationToken);
            }

            return AsOverview(result);
        }

        return await GetAsync(contractId, cancellationToken);
    }

    // ---- manuel imza

    public async Task<RecordResult<ContractSignatureOverview>> RecordManualAsync(Guid contractId, Guid signatoryId, ManualSignatureInput input, CancellationToken cancellationToken)
    {
        var (state, error) = await OpenStateAsync(contractId, ContractsModule.Permissions.SignaturesVerify, cancellationToken);
        if (state is null)
        {
            return error!;
        }

        var (loaded, request, signatories) = (state.Loaded, state.Request, state.Signatories);
        if (request.Method != SignatureMethod.Manual)
        {
            return Conflict("Bu istek sağlayıcı yöntemiyle yürüyor; imza sağlayıcıdan gelir. Manuel kayıt yalnızca manuel isteklerde yapılır.");
        }

        var target = signatories.FirstOrDefault(s => s.Id == signatoryId);
        if (target is null)
        {
            return RecordResult<ContractSignatureOverview>.Fail(RecordErrorCode.NotFound, "İmzacı bulunamadı.");
        }

        var infos = Infos(signatories);
        var (allowed, why) = SignatureFlow.CanSign(request.Mode, infos, signatoryId);
        if (!allowed)
        {
            return Conflict(why ?? "Bu imzacı şimdi imzalayamaz.");
        }

        // Doğrulayan, imzacının kendisi olamaz: iç imzacının imzasını başka bir yetkili doğrular.
        if (target.UserId is { } signerUser && signerUser == tenant.Current?.UserId)
        {
            return RecordResult<ContractSignatureOverview>.Fail(RecordErrorCode.PermissionDenied, "Kendi imzanızı doğrulayamazsınız; başka bir yetkili doğrulamalıdır.");
        }

        var errors = new List<RecordFieldError>();
        if (SignatureFileRules.Validate(input.FileName, input.Content, out var contentType) is { } fileError)
        {
            errors.Add(new RecordFieldError("file", fileError));
        }

        var now = time.GetUtcNow();
        var today = ContractPeriod.Today(now, Zone(loaded.Policy));
        if (input.SignedOn > today)
        {
            errors.Add(new RecordFieldError("signedOn", "İmza tarihi gelecekte olamaz."));
        }
        else if (input.SignedOn < ContractPeriod.Today(request.CreatedAt, Zone(loaded.Policy)))
        {
            errors.Add(new RecordFieldError("signedOn", "İmza tarihi isteğin açıldığı tarihten önce olamaz."));
        }

        if (errors.Count > 0)
        {
            return RecordResult<ContractSignatureOverview>.Fail(RecordErrorCode.ValidationFailed, "İmza kaydı doğrulanamadı.", errors);
        }

        var signedAt = new DateTimeOffset(input.SignedOn.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
        var file = new NewSignatureFile(SignatureFileRules.SafeName(input.FileName), contentType, input.Content, Convert.ToHexString(SHA256.HashData(input.Content)));
        var after = infos.Select(i => i.Id == signatoryId ? i with { Status = SignatoryStatus.Signed } : i).ToList();
        var actor = tenant.Current?.UserId;
        var change = new SignatoryChange(signatoryId, SignatoryStatus.Signed, SignedAt: signedAt, Assurance: SignatureAssurance.ManualScan, VerifiedBy: actor, VerifiedAt: now, AttachFile: true);
        var events = new List<NewSignatureEvent>
        {
            new("User", SignatureEventType.Signed, signatoryId, null, null, SignatureEventOutcome.Applied,
                string.IsNullOrWhiteSpace(input.Note) ? $"Manuel imza kaydedildi ({SignatureAssurance.ManualScan}); belge {file.FileName}." : input.Note.Trim(), actor),
        };

        var result = await ApplyAsync(state, after, [change], events, file, null, null, [ContractsModule.Permissions.SignaturesVerify], cancellationToken);
        return result.IsSuccess ? await GetAsync(contractId, cancellationToken) : AsOverview(result);
    }

    // ---- ret / hatırlatma / iptal / süre aşımı

    public async Task<RecordResult<ContractSignatureOverview>> DeclineAsync(Guid contractId, Guid signatoryId, string reason, CancellationToken cancellationToken)
    {
        var (state, error) = await OpenStateAsync(contractId, ContractsModule.Permissions.SignaturesManage, cancellationToken);
        if (state is null)
        {
            return error!;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return RecordResult<ContractSignatureOverview>.Fail(RecordErrorCode.ValidationFailed, "Gerekçe zorunludur.", [new RecordFieldError("reason", "Gerekçe zorunludur.")]);
        }

        if (state.Request.Method != SignatureMethod.Manual)
        {
            return Conflict("Sağlayıcı yöntemli istekte ret sağlayıcıdan gelir.");
        }

        var target = state.Signatories.FirstOrDefault(s => s.Id == signatoryId);
        if (target is null)
        {
            return RecordResult<ContractSignatureOverview>.Fail(RecordErrorCode.NotFound, "İmzacı bulunamadı.");
        }

        if (!SignatoryStatus.IsOpen(target.Status))
        {
            return Conflict("Bu imzacı için ret kaydedilemez.");
        }

        var declined = await DeclineCoreAsync(state, target, reason.Trim(), "User", null, null, [ContractsModule.Permissions.SignaturesManage], cancellationToken);
        return declined.IsSuccess ? await GetAsync(contractId, cancellationToken) : AsOverview(declined);
    }

    public async Task<RecordResult<ContractSignatureOverview>> RemindAsync(Guid contractId, Guid signatoryId, CancellationToken cancellationToken)
    {
        var (state, error) = await OpenStateAsync(contractId, ContractsModule.Permissions.SignaturesManage, cancellationToken);
        if (state is null)
        {
            return error!;
        }

        var target = state.Signatories.FirstOrDefault(s => s.Id == signatoryId);
        if (target is null)
        {
            return RecordResult<ContractSignatureOverview>.Fail(RecordErrorCode.NotFound, "İmzacı bulunamadı.");
        }

        if (!SignatureFlow.Active(state.Request.Mode, Infos(state.Signatories)).Contains(signatoryId) || target.Status == SignatoryStatus.Waiting)
        {
            return Conflict("Hatırlatma yalnızca sırası gelmiş, henüz imzalamamış imzacı için kaydedilir.");
        }

        var now = time.GetUtcNow();
        if (target.LastReminderAt is { } last && now - last < ReminderInterval)
        {
            return Conflict("Aynı imzacıya en fazla saatte bir hatırlatma kaydedilir.");
        }

        // Hatırlatma yalnızca kaydedilir; Modulix müşteriye e-posta ya da bildirim göndermez (gerçek gönderim sağlayıcıya ya da kullanıcıya aittir).
        var events = new List<NewSignatureEvent> { new("User", SignatureEventType.Reminded, signatoryId, null, null, SignatureEventOutcome.Applied, "Hatırlatma kaydedildi (bildirim gönderilmedi).", tenant.Current?.UserId) };
        var result = await ApplyAsync(state, Infos(state.Signatories), [new SignatoryChange(signatoryId, Remind: true)], events, null, null, null, [ContractsModule.Permissions.SignaturesManage], cancellationToken);
        return result.IsSuccess ? await GetAsync(state.Loaded.Contract.Id, cancellationToken) : AsOverview(result);
    }

    public async Task<RecordResult<ContractSignatureOverview>> CancelAsync(Guid contractId, string reason, CancellationToken cancellationToken)
    {
        var (state, error) = await OpenStateAsync(contractId, ContractsModule.Permissions.SignaturesManage, cancellationToken, allowExpired: true);
        if (state is null)
        {
            return error!;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return RecordResult<ContractSignatureOverview>.Fail(RecordErrorCode.ValidationFailed, "Gerekçe zorunludur.", [new RecordFieldError("reason", "Gerekçe zorunludur.")]);
        }

        var text = reason.Trim();
        var result = await CommitAsync(
            state.Loaded, null, [ContractsModule.Permissions.SignaturesManage], ContractEventKinds.SignatureCancelled, text, Set(("signatureStatus", SignatureStatus.Cancelled)),
            (ctx, contract, ct) => ContractSignatureWriter.CancelOpenAsync(ctx, contract, text, ct), cancellationToken);
        if (!result.IsSuccess)
        {
            return AsOverview(result);
        }

        if (state.Request.ProviderReference is { } reference && providers.Find(state.Request.Provider) is { } provider)
        {
            await provider.CancelRequestAsync(reference, cancellationToken);
        }

        return await GetAsync(contractId, cancellationToken);
    }

    public async Task<RecordResult<ContractSignatureOverview>> ExpireAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var (state, error) = await OpenStateAsync(contractId, ContractsModule.Permissions.SignaturesManage, cancellationToken, allowExpired: true);
        if (state is null)
        {
            return error!;
        }

        if (state.Request.ExpiresAt > time.GetUtcNow())
        {
            return Conflict("İmza isteğinin süresi henüz dolmadı.");
        }

        var expired = await ExpireCoreAsync(state, "User", null, null, [ContractsModule.Permissions.SignaturesManage], cancellationToken);
        return expired.IsSuccess ? await GetAsync(contractId, cancellationToken) : AsOverview(expired);
    }

    // ---- sağlayıcı olayları

    public async Task<RecordResult<ContractSignatureOverview>> SimulateAsync(Guid contractId, int signatoryPosition, string type, string? note, CancellationToken cancellationToken)
    {
        var (state, error) = await OpenStateAsync(contractId, ContractsModule.Permissions.SignaturesManage, cancellationToken);
        if (state is null)
        {
            return error!;
        }

        if (providers.Find(state.Request.Provider) is not { IsSimulation: true } provider || state.Request.ProviderReference is not { } reference)
        {
            return Conflict("Simülasyon yalnızca simülasyon sağlayıcısıyla açılmış istekte kullanılabilir.");
        }

        var input = new ProviderEventInput(provider.Name, $"sim-{Guid.NewGuid():N}", state.Request.LastEventSequence + 1, type, reference, signatoryPosition, time.GetUtcNow(), null, note);
        var result = await ApplyEventAsync(input, [ContractsModule.Permissions.SignaturesManage], cancellationToken);
        if (!result.IsSuccess)
        {
            return RecordResult<ContractSignatureOverview>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
        }

        return result.Value!.Outcome == SignatureEventOutcome.Applied
            ? await GetAsync(contractId, cancellationToken)
            : Conflict(result.Value.Message ?? "Olay uygulanamadı.");
    }

    public Task<RecordResult<ProviderEventResult>> ApplyProviderEventAsync(ProviderEventInput input, CancellationToken cancellationToken) =>
        ApplyEventAsync(input, [ContractsModule.Permissions.SignaturesProviderEvents], cancellationToken);

    private async Task<RecordResult<ProviderEventResult>> ApplyEventAsync(ProviderEventInput input, string[] permissions, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (providers.Find(input.Provider) is not { } provider)
        {
            return RecordResult<ProviderEventResult>.Fail(RecordErrorCode.NotFound, "Sağlayıcı bulunamadı.");
        }

        if (string.IsNullOrWhiteSpace(input.EventId) || input.EventId.Length > ContractSignatureEvent.EventIdMaxLength)
        {
            errors.Add(new RecordFieldError("eventId", "Olay kimliği zorunludur (en fazla 100 karakter)."));
        }

        if (!SignatureEventType.FromProvider.Contains(input.Type))
        {
            errors.Add(new RecordFieldError("type", "Bilinmeyen olay türü."));
        }

        if (input.Sequence < 1)
        {
            errors.Add(new RecordFieldError("sequence", "Olay numarası 1 ya da büyük olmalıdır."));
        }

        if (errors.Count > 0)
        {
            return RecordResult<ProviderEventResult>.Fail(RecordErrorCode.ValidationFailed, "Olay doğrulanamadı.", errors);
        }

        var request = await reader.SignatureRequestByReferenceAsync(input.Reference, cancellationToken);
        if (request is null || !string.Equals(request.Provider, provider.Name, StringComparison.Ordinal))
        {
            return RecordResult<ProviderEventResult>.Fail(RecordErrorCode.NotFound, "İstek bulunamadı.");
        }

        var (loaded, error) = await LoadAsync<ProviderEventResult>(request.ContractId, null, cancellationToken);
        if (loaded is null)
        {
            return error!;
        }

        // Idempotency: aynı olay kimliği ikinci kez uygulanmaz (sağlayıcı yeniden dener); yanıt yine başarılıdır.
        if (await reader.SignatureEventSeenAsync(request.Id, input.EventId, cancellationToken))
        {
            return RecordResult<ProviderEventResult>.Ok(new ProviderEventResult(SignatureEventOutcome.Duplicate, "Bu olay daha önce işlendi."));
        }

        var signatories = await reader.SignatoriesAsync([request.Id], cancellationToken);
        var state = new OpenState(loaded, request, signatories);
        var target = input.SignatoryPosition is { } position ? signatories.FirstOrDefault(s => s.Position == position) : null;
        var outcome = SignatureFlow.ClassifyProviderEvent(false, request.LastEventSequence, input.Sequence);
        string? note = null;

        if (!SignatureRequestStatus.IsOpen(request.Status))
        {
            (outcome, note) = (SignatureEventOutcome.Ignored, $"İstek artık açık değil ({request.Status}).");
        }
        else if (outcome == SignatureEventOutcome.OutOfOrder)
        {
            note = $"Olay numarası {input.Sequence}, uygulanan son numara {request.LastEventSequence}; sırası bozuk olay uygulanmadı.";
        }
        else if (input.Type != SignatureEventType.Expired && target is null)
        {
            (outcome, note) = (SignatureEventOutcome.Rejected, "İmzacı bulunamadı.");
        }
        else if (input.Type == SignatureEventType.Signed && request.ExpiresAt <= time.GetUtcNow())
        {
            (outcome, note) = (SignatureEventOutcome.Rejected, "İmza isteğinin süresi dolmuş; imza kabul edilmedi.");
        }
        else if (input.Type == SignatureEventType.Signed && !SignatureFlow.CanSign(request.Mode, Infos(signatories), target!.Id).Allowed)
        {
            (outcome, note) = (SignatureEventOutcome.Rejected, SignatureFlow.CanSign(request.Mode, Infos(signatories), target!.Id).Reason);
        }

        var logged = new NewSignatureEvent("Provider", input.Type, target?.Id, input.EventId, input.Sequence, outcome, note ?? input.Note, null);
        if (outcome != SignatureEventOutcome.Applied)
        {
            // Uygulanmayan olay yine de kaydedilir (denetim izi); sözleşme durumu değişmez.
            var skipped = await ApplyAsync(state, Infos(signatories), [], [logged], null, null, null, permissions, cancellationToken);
            return skipped.IsSuccess
                ? RecordResult<ProviderEventResult>.Ok(new ProviderEventResult(outcome, note))
                : RecordResult<ProviderEventResult>.Fail(skipped.Error!.Code, skipped.Error.Message, skipped.Error.FieldErrors);
        }

        RecordResult<RecordData> applied;
        switch (input.Type)
        {
            case SignatureEventType.Sent:
            case SignatureEventType.Viewed:
                {
                    // Görüntüleme: gönderilmiş imzacı "görüntüledi" olur; teslim ("Sent") yalnızca kaydedilir.
                    SignatoryChange[] change = input.Type == SignatureEventType.Viewed && target!.Status == SignatoryStatus.Sent ? [new SignatoryChange(target.Id, SignatoryStatus.Viewed)] : [];
                    applied = await ApplyAsync(state, Infos(signatories), change, [logged], null, input.Sequence, null, permissions, cancellationToken);
                    break;
                }

            case SignatureEventType.Signed:
                {
                    var assurance = provider.IsSimulation
                        ? SignatureAssurance.SimulatedElectronic
                        : input.Assurance == SignatureAssurance.ProviderQualified ? SignatureAssurance.ProviderQualified : SignatureAssurance.ProviderElectronic;
                    var after = Infos(signatories).Select(i => i.Id == target!.Id ? i with { Status = SignatoryStatus.Signed } : i).ToList();
                    var change = new SignatoryChange(target!.Id, SignatoryStatus.Signed, SignedAt: input.OccurredAt, Assurance: assurance);
                    applied = await ApplyAsync(state, after, [change], [logged], null, input.Sequence, null, permissions, cancellationToken);
                    break;
                }

            case SignatureEventType.Declined:
                applied = await DeclineCoreAsync(state, target!, string.IsNullOrWhiteSpace(input.Note) ? "Sağlayıcıda reddedildi." : input.Note.Trim(), "Provider", input.EventId, input.Sequence, permissions, cancellationToken);
                break;

            default:
                applied = await ExpireCoreAsync(state, "Provider", input.EventId, input.Sequence, permissions, cancellationToken);
                break;
        }

        return applied.IsSuccess
            ? RecordResult<ProviderEventResult>.Ok(new ProviderEventResult(SignatureEventOutcome.Applied))
            : RecordResult<ProviderEventResult>.Fail(applied.Error!.Code, applied.Error.Message, applied.Error.FieldErrors);
    }

    // ---- ortak çekirdek

    private sealed record OpenState(Loaded Loaded, ContractSignatureRequest Request, IReadOnlyList<ContractSignatory> Signatories);

    private async Task<(OpenState? State, RecordResult<ContractSignatureOverview>? Error)> OpenStateAsync(
        Guid contractId, string permission, CancellationToken cancellationToken, bool allowExpired = false)
    {
        var (loaded, error) = await LoadAsync<ContractSignatureOverview>(contractId, permission, cancellationToken);
        if (loaded is null)
        {
            return (null, error);
        }

        var request = await reader.OpenSignatureRequestAsync(contractId, cancellationToken);
        if (request is null)
        {
            return (null, Conflict("Açık bir imza isteği yok."));
        }

        if (!allowExpired && request.ExpiresAt <= time.GetUtcNow())
        {
            return (null, Conflict("İmza isteğinin süresi doldu; önce isteği sonlandırın (süre dolumu) ve gerekirse yeniden başlatın."));
        }

        return (new OpenState(loaded, request, await reader.SignatoriesAsync([request.Id], cancellationToken)), null);
    }

    private Task<RecordResult<RecordData>> DeclineCoreAsync(
        OpenState state, ContractSignatory target, string reason, string source, string? providerEventId, int? sequence, string[] permissions, CancellationToken cancellationToken)
    {
        var after = Infos(state.Signatories).Select(i => i.Id == target.Id ? i with { Status = SignatoryStatus.Declined } : i).ToList();
        var changes = new List<SignatoryChange> { new(target.Id, SignatoryStatus.Declined, DeclineReason: reason) };
        var events = new List<NewSignatureEvent> { new(source, SignatureEventType.Declined, target.Id, providerEventId, sequence, SignatureEventOutcome.Applied, reason, source == "Provider" ? null : tenant.Current?.UserId) };
        return ApplyAsync(state, after, changes, events, null, sequence, reason, permissions, cancellationToken);
    }

    private async Task<RecordResult<RecordData>> ExpireCoreAsync(
        OpenState state, string source, string? providerEventId, int? sequence, string[] permissions, CancellationToken cancellationToken)
    {
        var changes = state.Signatories.Where(s => SignatoryStatus.IsOpen(s.Status)).Select(s => new SignatoryChange(s.Id, SignatoryStatus.Expired)).ToList();
        var events = new List<NewSignatureEvent> { new(source, SignatureEventType.Expired, null, providerEventId, sequence, SignatureEventOutcome.Applied, "İmza isteğinin süresi doldu.", source == "Provider" ? null : tenant.Current?.UserId) };
        return await CommitAsync(
            state.Loaded, null, permissions, ContractEventKinds.SignatureExpired, "İmza isteğinin süresi doldu.", Set(("signatureStatus", SignatureStatus.Expired)),
            (ctx, contract, ct) => ContractSignatureWriter.ApplyAsync(
                ctx, contract, new SignatureMutation(state.Request.Id, changes, SignatureRequestStatus.Expired, "İmza isteğinin süresi doldu.", null, sequence, events), ct),
            cancellationToken);
    }

    /// <summary>
    /// İmzacı değişikliklerini uygular: değişiklik sonrası imzacı özetinden (<paramref name="after"/>) istek ve sözleşme durumu hesaplanır, sıralı kipte sırası gelen
    /// imzacılar açılır, tamamlanınca sürüm imzalı olur ve yürürlük durumu belirlenir. Hepsi sözleşme güncellemesiyle aynı işlemdedir.
    /// </summary>
    private async Task<RecordResult<RecordData>> ApplyAsync(
        OpenState state, IReadOnlyList<SignatoryInfo> after, IReadOnlyList<SignatoryChange> changes, IReadOnlyList<NewSignatureEvent> events, NewSignatureFile? file, int? sequence,
        string? closeReason, string[] permissions, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var request = state.Request;
        var status = changes.Count == 0 ? request.Status : SignatureFlow.Evaluate(after);
        var allChanges = new List<SignatoryChange>(changes);
        var allEvents = new List<NewSignatureEvent>(events);
        var values = new JsonObject();
        string eventKind = ContractEventKinds.SignatureEventLogged;
        string? mutationClose = closeReason;
        DateTimeOffset? completedAt = null;
        var markSigned = false;

        if (changes.Count > 0)
        {
            if (status == SignatureRequestStatus.Rejected)
            {
                // Zorunlu imzacı reddetti: istek reddedilir, diğer açık imzacılar iptal edilir.
                foreach (var s in after.Where(i => SignatoryStatus.IsOpen(i.Status)))
                {
                    allChanges.Add(new SignatoryChange(s.Id, SignatoryStatus.Cancelled));
                }

                eventKind = ContractEventKinds.SignatureDeclined;
                values["signatureStatus"] = SignatureStatus.Rejected;
            }
            else if (status == SignatureRequestStatus.Completed)
            {
                // Tamamlandı: açık kalan isteğe bağlı imzacılar kapatılır, sürüm imzalı olur, yürürlük durumu tarihe göre belirlenir.
                foreach (var s in after.Where(i => SignatoryStatus.IsOpen(i.Status)))
                {
                    allChanges.Add(new SignatoryChange(s.Id, SignatoryStatus.Cancelled));
                }

                completedAt = now;
                markSigned = true;
                eventKind = ContractEventKinds.SignatureCompleted;
                allEvents.Add(new NewSignatureEvent("System", SignatureEventType.Completed, null, null, null, SignatureEventOutcome.Applied, "Zorunlu imzacıların hepsi imzaladı.", null));
                var c = state.Loaded.Contract;
                values["signatureStatus"] = SignatureStatus.Completed;
                values["effectiveVersionNo"] = c.CurrentVersionNo;
                values["effectiveStatus"] = ContractDates.StatusOn(ContractPeriod.Today(now, Zone(state.Loaded.Policy)), c.StartDate, c.EndDate, c.IsIndefinite);
            }
            else
            {
                eventKind = changes.Any(c => c.Status == SignatoryStatus.Signed) ? ContractEventKinds.SignatorySigned : ContractEventKinds.SignatureEventLogged;
                values["signatureStatus"] = status == SignatureRequestStatus.PartiallySigned ? SignatureStatus.PartiallySigned : SignatureStatus.Pending;

                // Sıradaki imzacı(lar) açılır.
                foreach (var id in SignatureFlow.Active(request.Mode, after))
                {
                    if (after.First(i => i.Id == id).Status == SignatoryStatus.Waiting)
                    {
                        allChanges.Add(new SignatoryChange(id, SignatoryStatus.Sent, SentAt: now));
                        allEvents.Add(new NewSignatureEvent("System", SignatureEventType.Sent, id, null, null, SignatureEventOutcome.Applied, null, null));
                    }
                }
            }
        }
        else if (events.Any(e => e.EventType == SignatureEventType.Reminded))
        {
            eventKind = ContractEventKinds.SignatureReminded;
        }

        var mutation = new SignatureMutation(
            request.Id, allChanges, changes.Count == 0 ? null : status, status is SignatureRequestStatus.Rejected ? mutationClose : null, completedAt, sequence, allEvents, file, markSigned);
        return await CommitAsync(
            state.Loaded, null, permissions, eventKind, events.FirstOrDefault()?.Note, values,
            (ctx, contract, ct) => ContractSignatureWriter.ApplyAsync(ctx, contract, mutation, ct), cancellationToken);
    }

    private async Task<RecordResult<RecordData>> CommitAsync(
        Loaded loaded, long? version, IReadOnlyList<string> permissions, string eventKind, string? reason, JsonObject values,
        Func<ContractsStoreContext, Contract, CancellationToken, Task> effect, CancellationToken cancellationToken)
    {
        using (gate.Open(permissions, eventKind, reason, effect))
        {
            return await pipeline.UpdateAsync(
                new RecordCommand(Key, loaded.Contract.Id, values, version ?? loaded.Record.Version) { Reason = $"contract.{eventKind}" }, cancellationToken);
        }
    }

    // ---- yardımcılar

    private async Task<(Loaded? Loaded, RecordResult<T>? Error)> LoadAsync<T>(Guid contractId, string? permission, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(Key, contractId), cancellationToken);
        if (!record.IsSuccess)
        {
            return (null, RecordResult<T>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors));
        }

        if (permission is not null && !(await access.GetPermissionsAsync(cancellationToken)).Contains(permission))
        {
            return (null, RecordResult<T>.Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{permission}' izni gerekir."));
        }

        var contract = await reader.ContractAsync(contractId, cancellationToken);
        if (contract is null)
        {
            return (null, RecordResult<T>.Fail(RecordErrorCode.NotFound, "Sözleşme bulunamadı."));
        }

        return (new Loaded(record.Value!, contract, policy is null ? ContractPolicySettings.Default : await policy.GetAsync(cancellationToken)), null);
    }

    private async Task<ContractSignatureOverview> OverviewAsync(Loaded loaded, CancellationToken cancellationToken)
    {
        var requests = await reader.SignatureRequestsAsync(loaded.Contract.Id, cancellationToken);
        var ids = requests.Select(r => r.Id).ToList();
        var signatories = ids.Count == 0 ? [] : await reader.SignatoriesAsync(ids, cancellationToken);
        var events = ids.Count == 0 ? [] : await reader.SignatureEventsAsync(ids, cancellationToken);
        var files = await reader.SignatureFilesAsync(loaded.Contract.Id, cancellationToken);
        var now = time.GetUtcNow();
        var provider = providers.Find(loaded.Policy.SignatureProvider);

        var views = requests.Select(r =>
        {
            var mine = signatories.Where(s => s.RequestId == r.Id).ToList();
            var infos = Infos(mine);
            var open = SignatureRequestStatus.IsOpen(r.Status);
            return new SignatureRequestView(
                r.Id, r.VersionNo, r.DocumentId, r.DocumentSha256, r.Method, r.Provider, r.ProviderReference, providers.Find(r.Provider)?.IsSimulation == true, r.Mode, r.Status, r.ExpiresAt,
                open && r.ExpiresAt <= now, r.CompletedAt, r.CloseReason, r.CreatedBy, r.CreatedAt,
                [.. mine.Select(s => new SignatoryView(
                    s.Id, s.Position, s.Kind, s.UserId, s.Name, s.Email, s.Capacity, s.IsRequired, s.Status, s.SentAt, s.SignedAt, s.Assurance, s.VerifiedBy, s.VerifiedAt,
                    s.EvidenceFileId, files.FirstOrDefault(f => f.Id == s.EvidenceFileId)?.FileName, s.DeclineReason, s.ReminderCount, s.LastReminderAt,
                    open && SignatureFlow.CanSign(r.Mode, infos, s.Id).Allowed))],
                [.. events.Where(e => e.RequestId == r.Id).Select(e => new SignatureEventView(e.Id, e.Source, e.EventType, e.SignatoryId, e.ProviderEventId, e.Sequence, e.Outcome, e.Note, e.ActorId, e.CreatedAt))]);
        }).ToList();

        return new ContractSignatureOverview(
            loaded.Contract.SignatureStatus, loaded.Contract.CurrentVersionNo, views, provider is not null, provider?.Name, provider?.IsSimulation == true, loaded.Policy.SignatureValidityDays);
    }

    private static List<SignatoryInfo> Infos(IEnumerable<ContractSignatory> signatories) =>
        [.. signatories.OrderBy(s => s.Position).Select(s => new SignatoryInfo(s.Id, s.Position, s.IsRequired, s.Status))];

    private sealed record ResolvedSignatory(string Kind, Guid? UserId, string Name, string? Email, string? Capacity, bool IsRequired);

    private static List<ResolvedSignatory> ResolveSignatories(StartSignatureInput input, IReadOnlyList<TenantMember> members, bool needEmail, List<RecordFieldError> errors)
    {
        var result = new List<ResolvedSignatory>();
        if (input.Signatories.Count is < 1 or > 20)
        {
            errors.Add(new RecordFieldError("signatories", "En az bir, en fazla yirmi imzacı girin."));
            return result;
        }

        var usedUsers = new HashSet<Guid>();
        for (var i = 0; i < input.Signatories.Count; i++)
        {
            var s = input.Signatories[i];
            var field = $"signatories[{i}]";
            if (!SignatoryKind.All.Contains(s.Kind))
            {
                errors.Add(new RecordFieldError(field, "İmzacı türü Internal ya da External olmalıdır."));
                continue;
            }

            var capacity = string.IsNullOrWhiteSpace(s.Capacity) ? null : s.Capacity.Trim();
            if (capacity is { Length: > ContractSignatory.CapacityMaxLength })
            {
                errors.Add(new RecordFieldError(field, "İmza sıfatı çok uzun."));
                continue;
            }

            if (s.Kind == SignatoryKind.Internal)
            {
                var member = s.UserId is { } id ? members.FirstOrDefault(m => m.UserId == id) : null;
                if (member is null)
                {
                    errors.Add(new RecordFieldError(field, "İç imzacı bu firmanın etkin üyesi olmalıdır."));
                }
                else if (!usedUsers.Add(member.UserId))
                {
                    errors.Add(new RecordFieldError(field, "Aynı kişi iki kez imzacı olamaz."));
                }
                else
                {
                    result.Add(new ResolvedSignatory(s.Kind, member.UserId, member.DisplayName, null, capacity, s.IsRequired));
                }

                continue;
            }

            var name = s.Name?.Trim();
            var email = string.IsNullOrWhiteSpace(s.Email) ? null : s.Email.Trim();
            if (string.IsNullOrEmpty(name) || name.Length < 2 || name.Length > ContractSignatory.NameMaxLength)
            {
                errors.Add(new RecordFieldError(field, "Dış imzacının adı zorunludur (2-150 karakter)."));
            }
            else if (email is not null && (email.Length > ContractSignatory.EmailMaxLength || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || !email.Contains('.')))
            {
                errors.Add(new RecordFieldError(field, "Geçerli bir e-posta adresi girin."));
            }
            else if (email is null && needEmail)
            {
                errors.Add(new RecordFieldError(field, "Sağlayıcı yönteminde dış imzacının e-postası zorunludur."));
            }
            else
            {
                result.Add(new ResolvedSignatory(s.Kind, null, name!, email, capacity, s.IsRequired));
            }
        }

        if (errors.Count == 0 && !result.Any(s => s.IsRequired))
        {
            errors.Add(new RecordFieldError("signatories", "En az bir zorunlu imzacı olmalıdır."));
        }

        return result;
    }

    private static TimeZoneInfo Zone(ContractPolicySettings policy) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(policy.TimeZoneId, out var zone) ? zone : TimeZoneInfo.Utc;

    private static JsonObject Set(params (string Field, string? Value)[] fields)
    {
        var values = new JsonObject();
        foreach (var (field, value) in fields)
        {
            values[field] = value;
        }

        return values;
    }

    private static RecordResult<ContractSignatureOverview> Conflict(string message) =>
        RecordResult<ContractSignatureOverview>.Fail(RecordErrorCode.Conflict, message, [new RecordFieldError("signatureStatus", message)]);

    private static RecordResult<ContractSignatureOverview> AsOverview(RecordResult<RecordData> result) =>
        result.IsSuccess ? RecordResult<ContractSignatureOverview>.Ok(null!) : RecordResult<ContractSignatureOverview>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
}

/// <summary>Sözleşme sistem kimliklerinin izin çapası (oturumsuz çağıran sistem adına çalışır); olay işlemez.</summary>
internal sealed class ContractSystemAnchor : Modulix.Core.Contracts.Events.IEventSubscriber
{
    public Task HandleAsync(Modulix.Core.Contracts.Events.EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}
