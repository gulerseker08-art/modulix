using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Approvals;

public enum ApprovalRequestStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Withdrawn = 4,

    /// <summary>Bağlı kaydın tutarı ya da para birimi değişti; verilmiş onaylar geçersiz (Ç6a.2).</summary>
    Invalidated = 5,
}

/// <summary>Talebin bağlandığı kayıt: tür, kimlik ve açılış anındaki görünen adı.</summary>
public sealed record ApprovalRecordTarget(string EntityKey, Guid RecordId, string Label);

public enum ApprovalStepStatus
{
    /// <summary>Önceki adımları bekliyor.</summary>
    Waiting = 1,

    /// <summary>Karar bekliyor.</summary>
    Active = 2,
    Approved = 3,
    Rejected = 4,

    /// <summary>Talep reddedildi ya da geri çekildi; bu adıma gerek kalmadı.</summary>
    Cancelled = 5,
}

/// <summary>
/// Onay talebi (ADR-0027 Dilim 4a, Ç6a.2). Başlık, tutar ve adımlar açılış anının kopyasıdır ve sonradan değişmez: tutar değişecekse
/// talep geri çekilip yeniden açılır, verilmiş onay değişen tutara taşınmaz. Karar verme <see cref="Version"/> ile korunur; eşzamanlı iki
/// karardan biri çakışma alır, adım çift ilerlemez. Görünürlük kişiye bağlıdır (talep sahibi, onaycı, yönetici), şirkete değil.
/// </summary>
public sealed class ApprovalRequest : ITenantEntity, ICompanyShared
{
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int CommentMaxLength = 1000;
    public const int SourceKeyMaxLength = 200;

    private ApprovalRequest()
    {
        // EF Core için
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid DefinitionId { get; private set; }

    public string DefinitionName { get; private set; } = string.Empty;

    public int DefinitionRevision { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public decimal? Amount { get; private set; }

    public string? Currency { get; private set; }

    public Guid RequesterId { get; private set; }

    public string? TargetEntityKey { get; private set; }

    public Guid? TargetRecordId { get; private set; }

    /// <summary>Kaydın açılış anındaki görünen adı (anlık görüntü).</summary>
    public string? TargetLabel { get; private set; }

    /// <summary>
    /// Talebi açan kural adımının tekilleştirme anahtarı (Dilim 4c-2). Aynı anahtarla ikinci talep açılmaz: kural motorunun en az bir kez
    /// teslimi ikinci talep üretmez. Elle açılan talepte boş.
    /// </summary>
    public string? SourceKey { get; private set; }

    public ApprovalRequestStatus Status { get; private set; }

    public DateTimeOffset SubmittedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Eşzamanlılık belirteci: her karar ve atamada bir artar.</summary>
    public long Version { get; private set; }

    /// <summary>
    /// Talebi ve adım kopyalarını üretir. Tutar eşiğine takılmayan adım eklenmez; uygulanacak adım kalmazsa talep kendiliğinden onaylanır
    /// (süreç sahibi eşik altını onaysız bırakmıştır).
    /// </summary>
    public static (ApprovalRequest Request, List<ApprovalRequestStep> Steps) Open(
        ApprovalDefinition definition, string title, string? description, decimal? amount, string? currency, Guid requesterId, DateTimeOffset now,
        ApprovalRecordTarget? target = null, string? sourceKey = null)
    {
        if (sourceKey is not null && (sourceKey.Length is 0 || sourceKey.Length > SourceKeyMaxLength))
        {
            throw new ArgumentException("Kural adımı anahtarı geçersiz.");
        }

        if ((definition.TargetEntityKey is null) != (target is null) || (target is not null && target.EntityKey != definition.TargetEntityKey))
        {
            throw new ArgumentException(definition.TargetEntityKey is null ? "Bu süreç bir kayda bağlanmaz." : "Bu süreç için kayıt seçin.");
        }

        string trimmedTitle = (title ?? string.Empty).Trim();
        if (trimmedTitle.Length is 0 or > TitleMaxLength)
        {
            throw new ArgumentException($"Başlık 1-{TitleMaxLength} karakter olmalı.");
        }

        string? trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmedDescription?.Length > DescriptionMaxLength)
        {
            throw new ArgumentException($"Açıklama en çok {DescriptionMaxLength} karakter olabilir.");
        }

        string? code = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();
        if (definition.Currency is not null && (amount is null || code != definition.Currency))
        {
            throw new ArgumentException($"Bu süreçte tutar {definition.Currency} olarak girilmeli.");
        }

        if (definition.Currency is null && (amount is not null) != (code is not null))
        {
            throw new ArgumentException("Tutar ve para birimi birlikte girilmeli.");
        }

        if (amount < 0)
        {
            throw new ArgumentException("Tutar negatif olamaz.");
        }

        ApprovalRequest request = new()
        {
            Id = Guid.CreateVersion7(),
            DefinitionId = definition.Id,
            DefinitionName = definition.Name,
            DefinitionRevision = definition.Revision,
            Title = trimmedTitle,
            Description = trimmedDescription,
            Amount = amount,
            Currency = code,
            RequesterId = requesterId,
            TargetEntityKey = target?.EntityKey,
            TargetRecordId = target?.RecordId,
            TargetLabel = target is null ? null : target.Label.Length > TitleMaxLength ? target.Label[..TitleMaxLength] : target.Label,
            SourceKey = sourceKey,
            Status = ApprovalRequestStatus.Pending,
            SubmittedAt = now,
            Version = 1,
        };

        List<ApprovalRequestStep> steps = definition.Steps
            .Where(s => s.MinAmount is null || amount >= s.MinAmount)
            .Select(s => ApprovalRequestStep.Create(request.Id, s))
            .ToList();
        request.ActivateNext(steps, now);
        return (request, steps);
    }

    /// <summary>Etkin adımı onaylar: aynı sıradaki etkin adımlar bitince sonraki sıra açılır, adım kalmadıysa talep onaylanır.</summary>
    public void Approve(ApprovalRequestStep step, IReadOnlyList<ApprovalRequestStep> steps, Guid actorId, string? comment, DateTimeOffset now, Guid? onBehalfOf = null)
    {
        step.Decide(ApprovalStepStatus.Approved, actorId, comment, now, onBehalfOf);
        if (!steps.Any(s => s.Status == ApprovalStepStatus.Active))
        {
            ActivateNext(steps, now);
        }

        Version++;
    }

    /// <summary>Reddetme bütün talebi kapatır; kalan adımlar iptal olur.</summary>
    public void Reject(ApprovalRequestStep step, IReadOnlyList<ApprovalRequestStep> steps, Guid actorId, string comment, DateTimeOffset now, Guid? onBehalfOf = null)
    {
        step.Decide(ApprovalStepStatus.Rejected, actorId, comment, now, onBehalfOf);
        Close(ApprovalRequestStatus.Rejected, steps, now);
    }

    public void Withdraw(IReadOnlyList<ApprovalRequestStep> steps, DateTimeOffset now) =>
        Close(ApprovalRequestStatus.Withdrawn, steps, now);

    public void Invalidate(IReadOnlyList<ApprovalRequestStep> steps, DateTimeOffset now) =>
        Close(ApprovalRequestStatus.Invalidated, steps, now);

    /// <summary>Atama değişikliği talebin sürümünü artırır: aynı anda verilen karar çakışma alır.</summary>
    public void Touch() => Version++;

    private void ActivateNext(IReadOnlyList<ApprovalRequestStep> steps, DateTimeOffset now)
    {
        List<ApprovalRequestStep> waiting = steps.Where(s => s.Status == ApprovalStepStatus.Waiting).ToList();
        if (waiting.Count == 0)
        {
            Status = ApprovalRequestStatus.Approved;
            CompletedAt = now;
            return;
        }

        int next = waiting.Min(s => s.StepOrder);
        foreach (ApprovalRequestStep step in waiting.Where(s => s.StepOrder == next))
        {
            step.Activate(now);
        }
    }

    private void Close(ApprovalRequestStatus status, IReadOnlyList<ApprovalRequestStep> steps, DateTimeOffset now)
    {
        foreach (ApprovalRequestStep open in steps.Where(s => s.Status is ApprovalStepStatus.Waiting or ApprovalStepStatus.Active))
        {
            open.Cancel();
        }

        Status = status;
        CompletedAt = now;
        Version++;
    }
}

/// <summary>Talebin adım kopyası: onaycı, süre ve karar. Tanım değişse de başlatıldığı haliyle kalır.</summary>
public sealed class ApprovalRequestStep : ITenantEntity, ICompanyShared
{
    private ApprovalRequestStep()
    {
        // EF Core için
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RequestId { get; private set; }

    public int StepOrder { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public Guid? ApproverUserId { get; private set; }

    public Guid? ApproverRoleId { get; private set; }

    public int? DueHours { get; private set; }

    public ApprovalStepStatus Status { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public DateTimeOffset? DueAt { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? Comment { get; private set; }

    /// <summary>Süre aşılınca karar verebilecek kişi (açılış anının kopyası).</summary>
    public Guid? EscalationUserId { get; private set; }

    public Guid? EscalationRoleId { get; private set; }

    /// <summary>Süre aşımı işlendiği an; doluysa adım bir daha işlenmez ve eskalasyon onaycısı karar verebilir.</summary>
    public DateTimeOffset? EscalatedAt { get; private set; }

    /// <summary>Karar vekâleten verildiyse adına karar verilen kişi.</summary>
    public Guid? OnBehalfOf { get; private set; }

    internal static ApprovalRequestStep Create(Guid requestId, ApprovalStepDefinition definition) => new()
    {
        Id = Guid.CreateVersion7(),
        RequestId = requestId,
        StepOrder = definition.Order,
        Name = definition.Name,
        ApproverUserId = definition.ApproverUserId,
        ApproverRoleId = definition.ApproverRoleId,
        DueHours = definition.DueHours,
        EscalationUserId = definition.EscalateToUserId,
        EscalationRoleId = definition.EscalateToRoleId,
        Status = ApprovalStepStatus.Waiting,
    };

    /// <summary>Etkin adımın onaycısını değiştirir (ayrılan onaycı süreci sahipsiz bırakmasın, Ç6a.1).</summary>
    public void Reassign(Guid? userId, Guid? roleId)
    {
        ApproverUserId = userId;
        ApproverRoleId = roleId;
    }

    public void Escalate(DateTimeOffset now) => EscalatedAt ??= now;

    internal void Activate(DateTimeOffset now)
    {
        Status = ApprovalStepStatus.Active;
        ActivatedAt = now;
        DueAt = DueHours is { } hours ? now.AddHours(hours) : null;
    }

    internal void Decide(ApprovalStepStatus status, Guid actorId, string? comment, DateTimeOffset now, Guid? onBehalfOf)
    {
        string? trimmed = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (trimmed?.Length > ApprovalRequest.CommentMaxLength)
        {
            throw new ArgumentException($"Not en çok {ApprovalRequest.CommentMaxLength} karakter olabilir.");
        }

        Status = status;
        DecidedBy = actorId;
        DecidedAt = now;
        Comment = trimmed;
        OnBehalfOf = onBehalfOf;
    }

    internal void Cancel() => Status = ApprovalStepStatus.Cancelled;
}
