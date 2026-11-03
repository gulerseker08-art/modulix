using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Modules.Marketing.Domain;

// Kampanya içeriği, çalıştırma ve teslimat (ADR-0025, dilim 5). İçerik sürümlüdür ve değişmez; çalıştırma hedef kitle anlık görüntüsüne ve bir içerik
// sürümüne bağlıdır, onaylandıktan sonra değiştirilemez; teslimat satırı alıcı başına bir gönderimdir (günlük satırı).

public static class RunStatuses
{
    public const string Draft = "Draft";
    public const string PendingApproval = "PendingApproval";
    public const string Approved = "Approved";
    public const string Running = "Running";
    public const string Paused = "Paused";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

    public static IReadOnlyList<string> All { get; } = [Draft, PendingApproval, Approved, Running, Paused, Completed, Cancelled];
}

public static class DeliveryStatuses
{
    /// <summary>Sırada; henüz sağlayıcıya verilmedi.</summary>
    public const string Queued = "Queued";

    /// <summary>İşleniyor (sağlayıcı çağrısı sürüyor ya da sonucu yazılmadı). Uzun süre bu durumda kalan satır "belirsiz" sayılır; kör yeniden deneme yoktur.</summary>
    public const string Sending = "Sending";

    /// <summary>Sağlayıcı iletiyi kabul etti. "Teslim edildi" değildir.</summary>
    public const string Accepted = "Accepted";

    public const string Failed = "Failed";

    /// <summary>Gönderimden hemen önceki denetimlerden biri geçmedi (izin, engel, adres, sıklık sınırı).</summary>
    public const string Skipped = "Skipped";

    /// <summary>Sonuç bilinmiyor (kabulden sonra zaman aşımı gibi). Otomatik yeniden denenmez.</summary>
    public const string Unknown = "Unknown";

    public const string Cancelled = "Cancelled";

    public static IReadOnlyList<string> All { get; } = [Queued, Sending, Accepted, Failed, Skipped, Unknown, Cancelled];
}

/// <summary>Kampanya içeriği sürümü (değişmez). Düz metindir; kullanıcı girdisi sağlayıcıya iletilirken kodlanır. Yeni sürüm yeni kayıttır.</summary>
public sealed class CampaignContent : MarketingEntity
{
    public const int SubjectMaxLength = 200;
    public const int BodyMaxLength = 10000;

    public Guid CampaignId { get; internal set; }

    public int VersionNo { get; internal set; }

    public string? Subject { get; internal set; }

    public string Body { get; internal set; } = string.Empty;

    public Guid? CreatedBy { get; internal set; }
}

/// <summary>
/// Çalıştırma: bir hedef kitle anlık görüntüsünün bir içerik sürümüyle gönderimi. Durum yalnızca eylemlerle değişir; içerik, anlık görüntü ve zaman yalnızca
/// taslakta düzenlenir (onaydan sonra değişiklik için önce taslağa alınır ve onay yeniden alınır). Tekrar çalıştırma yeni çalıştırmadır.
/// </summary>
public sealed class CampaignRun : MarketingEntity, IOwnedEntity
{
    public const int NoteMaxLength = 500;

    public Guid CampaignId { get; internal set; }

    public Guid? SnapshotId { get; internal set; }

    public Guid? ContentId { get; internal set; }

    /// <summary>Planlanan başlangıç (UTC); boşsa onaydan sonra hemen başlatılabilir.</summary>
    public DateTimeOffset? ScheduledAt { get; internal set; }

    public string Status { get; internal set; } = RunStatuses.Draft;

    /// <summary>Son durum değişikliğinin gerekçesi (ret, taslağa alma, iptal).</summary>
    public string? Note { get; internal set; }

    public Guid? OwnerId { get; internal set; }

    public Guid? SubmittedBy { get; internal set; }

    public DateTimeOffset? SubmittedAt { get; internal set; }

    public Guid? ApprovedBy { get; internal set; }

    public DateTimeOffset? ApprovedAt { get; internal set; }

    /// <summary>Onaylandığı anda bağlı olan anlık görüntü ve içerik; başlatmada aynı oldukları doğrulanır.</summary>
    public Guid? ApprovedSnapshotId { get; internal set; }

    public Guid? ApprovedContentId { get; internal set; }

    public DateTimeOffset? StartedAt { get; internal set; }

    public DateTimeOffset? CompletedAt { get; internal set; }

    public Guid? CreatedBy { get; internal set; }

    public Guid? UpdatedBy { get; internal set; }
}

/// <summary>
/// Teslimat satırı (günlük): alıcı başına bir gönderim. Adres saklanmaz (gönderimden önce yeniden çözülür); <see cref="IdempotencyKey"/> tenant, çalıştırma,
/// kanal ve normalize adresten türetilir ve (çalıştırma, anahtar) benzersizdir: aynı alıcıya aynı çalıştırmada ikinci satır açılamaz.
/// </summary>
public sealed class CampaignDelivery : IJournalEntity, ICompanyShared
{
    public const int KeyLength = 64;
    public const int CodeMaxLength = 60;
    public const int ProviderIdMaxLength = 200;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; private set; }

    public Guid RunId { get; set; }

    public Guid TargetId { get; set; }

    public Guid? PersonId { get; set; }

    public Guid? ContactPointId { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public string Status { get; set; } = DeliveryStatuses.Queued;

    /// <summary>Atlama/hata nedeni (izin kodu, "frequency-limit", "address-gone", sağlayıcı hata kodu).</summary>
    public string? Code { get; set; }

    public string? ProviderMessageId { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Test gönderimi izi (günlük, ADR-0025 dilim 11): kimin, hangi içerik sürümünü kendi adresine denediği. Adres saklanmaz; yalnızca sonuç ve kod tutulur.
/// Kampanyaya yabancı anahtar yoktur: test izi kampanyanın silinmesini engellemez.
/// </summary>
public sealed class CampaignTestSend : IJournalEntity, ICompanyShared
{
    public const int OutcomeMaxLength = 20;
    public const int CodeMaxLength = 60;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; private set; }

    public Guid CampaignId { get; set; }

    public Guid ContentId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Sağlayıcı sonucu (<see cref="Modulix.Core.Contracts.Marketing.ProviderOutcome"/> adı).</summary>
    public string Outcome { get; set; } = string.Empty;

    public string? Code { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
