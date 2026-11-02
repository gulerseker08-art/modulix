namespace Modulix.Modules.Marketing.Domain;

// Kampanya yanıtı (ADR-0025, dilim 7): kampanya iletisine gelen yanıtın çalışma kaydı. Yanıt her zaman gerçekten gönderilmiş bir iletiye (teslimat satırı) bağlanır;
// adres burada saklanmaz. Durum yalnızca eylemlerle değişir; lead, aktivite ve çıkış uygulaması yanıttan başlatılır.

public static class ResponseKinds
{
    public const string Interested = "Interested";
    public const string Question = "Question";
    public const string MeetingRequest = "MeetingRequest";
    public const string NotInterested = "NotInterested";

    /// <summary>"Bir daha yazmayın": pazarlama iznini reddeder (yanıttan çıkış uygulanır).</summary>
    public const string OptOut = "OptOut";

    public const string Complaint = "Complaint";
    public const string Other = "Other";

    public static IReadOnlyList<string> All { get; } = [Interested, Question, MeetingRequest, NotInterested, OptOut, Complaint, Other];
}

public static class ResponseStatuses
{
    public const string New = "New";
    public const string InProgress = "InProgress";

    /// <summary>Yanıttan bir lead açıldı ya da bağlandı.</summary>
    public const string Converted = "Converted";

    public const string Closed = "Closed";

    public static IReadOnlyList<string> All { get; } = [New, InProgress, Converted, Closed];
}

/// <summary>
/// Kampanya yanıtı. Kampanya, çalıştırma, teslimat ve hedef kimlikleri gönderim kaydından kopyalanır (istemci veremez); lead ve aktivite başka bir modülün
/// kaydıdır, yalnızca kimlikle tutulur (yabancı anahtar yok). Sorumlusu kaydı açan kullanıcıdır; "üstlen" eylemiyle devredilir.
/// </summary>
public sealed class CampaignResponse : MarketingEntity, IOwnedEntity
{
    public const int NoteMaxLength = 2000;
    public const int ReasonMaxLength = 500;

    public Guid CampaignId { get; internal set; }

    public Guid? RunId { get; internal set; }

    public Guid DeliveryId { get; internal set; }

    public Guid TargetId { get; internal set; }

    public Guid? PersonId { get; internal set; }

    public string Kind { get; internal set; } = ResponseKinds.Other;

    public string? Note { get; internal set; }

    public DateTimeOffset ReceivedAt { get; internal set; }

    public string Status { get; internal set; } = ResponseStatuses.New;

    public Guid? OwnerId { get; internal set; }

    public Guid? LeadId { get; internal set; }

    public Guid? ActivityId { get; internal set; }

    public DateTimeOffset? OptOutAppliedAt { get; internal set; }

    public string? ClosedReason { get; internal set; }

    public Guid? CreatedBy { get; internal set; }

    public Guid? UpdatedBy { get; internal set; }
}
