using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Marketing;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Marketing.Domain;

namespace Modulix.Modules.Marketing.Runs;

/// <param name="Outcome">Sağlayıcı sonucu (kabul edildi, geçici/kalıcı hata, belirsiz).</param>
public sealed record TestSendResult(string Outcome, string? Code);

/// <summary>
/// İçerik sürümünü kendi adresinize deneme (ADR-0025, dilim 11). Alıcı <b>yalnızca</b> çağıran kullanıcının kendi hesap e-postasıdır (istekten adres alınmaz), bu yüzden
/// izin/engel denetimi gerekmez ve kimseye pazarlama iletisi gitmez. Değişkenler örnek değerlerle (<see cref="ContentTemplate.Sample"/>) doldurulur; konu "[TEST]" ile başlar,
/// gövdenin başında ve sonunda test olduğu yazar; abonelik bağlantısı eklenmez. Günde kullanıcı başına en çok <see cref="DailyLimit"/> gönderim vardır (sıklık sayacı
/// ayrı "test:" anahtarıyla tutulur, gerçek pazarlama sınırını tüketmez). Sağlayıcı seçilmemişse gönderilmez. Her deneme günlüğe yazılır (adres olmadan).
/// </summary>
public interface ICampaignTestSends
{
    Task<RecordResult<TestSendResult>> SendAsync(Guid contentId, CancellationToken cancellationToken);
}

internal sealed class CampaignTestSends(
    IRecordPipeline pipeline,
    IMarketingPolicyProvider policyProvider,
    IEnumerable<IMessageProvider> providers,
    INotificationRecipients recipients,
    IFrequencyLimiter frequency,
    IJournalWriter journal,
    ICurrentAccess permissions,
    ITenantContextAccessor tenant,
    ITenantClock clock,
    TimeProvider time) : ICampaignTestSends
{
    public const int DailyLimit = 10;

    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(30);

    public async Task<RecordResult<TestSendResult>> SendAsync(Guid contentId, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(MarketingModule.Permissions.CampaignsWrite, cancellationToken))
        {
            return Fail(RecordErrorCode.PermissionDenied, $"Bu işlem için '{MarketingModule.Permissions.CampaignsWrite}' izni gerekir.");
        }

        // Alıcı oturumdaki kullanıcıdır; sistem kimliği (işçi) ve üyeliği kapalı kullanıcı test gönderemez.
        if (tenant.Current?.UserId is not { } userId || userId == Guid.Empty)
        {
            return Fail(RecordErrorCode.PermissionDenied, "Test gönderimi yalnızca oturum açmış kullanıcı için yapılır.");
        }

        var loaded = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.CampaignContent, contentId), cancellationToken);
        if (!loaded.IsSuccess)
        {
            return RecordResult<TestSendResult>.Fail(loaded.Error!.Code, loaded.Error.Message, loaded.Error.FieldErrors);
        }

        var campaignId = Guid.Parse(loaded.Value!.Values["campaignId"]!.GetValue<string>());
        var campaign = await pipeline.GetAsync(new RecordRef(MarketingModule.Entities.Campaign, campaignId), cancellationToken);
        if (!campaign.IsSuccess)
        {
            return RecordResult<TestSendResult>.Fail(campaign.Error!.Code, campaign.Error.Message, campaign.Error.FieldErrors);
        }

        var channel = Enum.Parse<CommunicationChannel>(campaign.Value!.Values["channel"]!.GetValue<string>());
        if (channel != CommunicationChannel.Email)
        {
            return Fail(RecordErrorCode.Conflict, "Test gönderimi yalnızca e-posta kampanyaları için vardır; telefon numaranız kayıtlı değildir.");
        }

        var subject = loaded.Value.Values["subject"]?.GetValue<string>();
        var body = loaded.Value.Values["body"]!.GetValue<string>();
        if (ContentRules.Validate(channel, subject, body) is { Count: > 0 } problems)
        {
            return Fail(RecordErrorCode.ValidationFailed, problems[0].Message);
        }

        var policy = await policyProvider.GetAsync(cancellationToken);
        var provider = policy.HasProvider ? providers.FirstOrDefault(p => p.Key == policy.Provider) : null;
        if (provider is null || !provider.Supports(channel))
        {
            return Fail(RecordErrorCode.Conflict, "Gönderim sağlayıcısı yapılandırılmamış ya da bu kanalı desteklemiyor; test gönderilemez.");
        }

        var self = await recipients.FindAsync(userId, cancellationToken);
        var address = self is null ? null : CommunicationAddress.Normalize(channel, self.Email);
        if (address is null)
        {
            return Fail(RecordErrorCode.Conflict, "Hesabınızda geçerli bir e-posta adresi yok.");
        }

        var today = (await clock.GetAsync(cancellationToken)).Today;
        if (!await frequency.TryReserveAsync(channel, $"test:{address}", today, DailyLimit, cancellationToken))
        {
            return Fail(RecordErrorCode.Conflict, $"Günlük test gönderimi sınırına ({DailyLimit}) ulaşıldı; yarın yeniden deneyin.");
        }

        var request = new MessageRequest(
            Guid.NewGuid().ToString("N"), channel, address,
            subject is null ? "[TEST]" : $"[TEST] {ContentTemplate.Render(subject, ContentTemplate.Sample)}",
            $"Bu bir test iletisidir; hiçbir alıcıya gönderilmedi. Değişkenler örnek değerlerle doldurulmuştur.\n\n{ContentTemplate.Render(body, ContentTemplate.Sample).TrimEnd()}\n\n--\nTest iletisi: abonelik bağlantısı eklenmedi.");
        ProviderResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProviderTimeout);
            result = await provider.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            result = new ProviderResult(ProviderOutcome.Unknown, null, ex is OperationCanceledException ? "timeout" : "provider-exception");
        }

        journal.Add(new CampaignTestSend
        {
            CampaignId = campaignId,
            ContentId = contentId,
            UserId = userId,
            Outcome = result.Outcome.ToString(),
            Code = result.ErrorCode,
            CreatedAt = time.GetUtcNow(),
        });
        await journal.SaveAsync(cancellationToken);

        // Başarısız denemede hak geri verilmez: sağlayıcıya gitmiş olabilir (belirsiz) ve kötüye kullanım sayacı şişirilemez.
        return RecordResult<TestSendResult>.Ok(new TestSendResult(result.Outcome.ToString(), result.ErrorCode));
    }

    private static RecordResult<TestSendResult> Fail(RecordErrorCode code, string message) => RecordResult<TestSendResult>.Fail(code, message, null);
}
