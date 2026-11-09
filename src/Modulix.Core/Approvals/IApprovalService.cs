namespace Modulix.Core.Approvals;

/// <summary>İş kuralı ihlali: kullanıcıya gösterilir (422).</summary>
public sealed class ApprovalException(string message) : Exception(message);

/// <summary>Eşzamanlı değişiklik (409): kayıt başkası tarafından değiştirildi.</summary>
public sealed class ApprovalConflictException(string message) : Exception(message);

/// <summary>Kullanıcı bu işlemi yapamaz (403): izni olsa bile bu adımın onaycısı değildir.</summary>
public sealed class ApprovalForbiddenException(string message) : Exception(message);

public sealed record ApprovalDefinitionInput(
    string Name, string? Description, string? Currency, IReadOnlyList<ApprovalStepDefinition> Steps, string? TargetEntityKey = null);

/// <summary>Süreç bir kayıt türüne bağlıysa <see cref="RecordId"/> zorunludur; tutar ve para birimi kayıttan okunur, girilen değer yok sayılır.</summary>
public sealed record ApprovalRequestInput(Guid DefinitionId, string Title, string? Description, decimal? Amount, string? Currency, Guid? RecordId = null);

public enum ApprovalRequestView
{
    /// <summary>Kararımı bekleyenler.</summary>
    Waiting = 1,

    /// <summary>Açtığım talepler.</summary>
    Mine = 2,

    /// <summary>Görebildiğim bütün talepler.</summary>
    All = 3,
}

public sealed record ApprovalRequestQuery(
    ApprovalRequestView View, ApprovalRequestStatus? Status, Guid? DefinitionId, string? Search, string? Sort, int Page, int PageSize)
{
    public const int MaxPageSize = 100;
}

public sealed record ApprovalStepDetail(
    Guid Id, int Order, string Name, Guid? ApproverUserId, Guid? ApproverRoleId, string ApproverName, ApprovalStepStatus Status,
    DateTimeOffset? ActivatedAt, DateTimeOffset? DueAt, bool IsOverdue, bool IsOrphaned, Guid? DecidedBy, string? DecidedByName,
    DateTimeOffset? DecidedAt, string? Comment, string? OnBehalfOfName = null, string? EscalationName = null, bool IsEscalated = false);

public sealed record ApprovalRequestDetail(
    Guid Id, Guid DefinitionId, string DefinitionName, int DefinitionRevision, string Title, string? Description, decimal? Amount, string? Currency,
    Guid RequesterId, string RequesterName, ApprovalRequestStatus Status, DateTimeOffset SubmittedAt, DateTimeOffset? CompletedAt, long Version,
    IReadOnlyList<ApprovalStepDetail> Steps, bool CanDecide, bool CanWithdraw,
    string? TargetEntityKey = null, Guid? TargetRecordId = null, string? TargetLabel = null, string? TargetLink = null);

/// <summary>
/// Kural kaynaklı, kayda bağlı talep (Dilim 4c-2). <see cref="Input"/> içindeki kayıt sürecin bağlı olduğu türdendir; tutar ve para birimi
/// kayıttan okunur. <see cref="RequesterId"/> talebi "açmış sayılan" kişidir (kaydın sorumlusu); dört göz onun için geçerlidir.
/// <see cref="SourceKey"/> açan kural adımının tekilleştirme anahtarıdır.
/// </summary>
public sealed record ApprovalRecordRequest(ApprovalRequestInput Input, Guid RequesterId, string SourceKey);

/// <summary>Kural kaynaklı talep açma sonucu. <see cref="AlreadyExisted"/>: aynı adım ya da aynı kayıt için bekleyen talep zaten vardı.</summary>
public sealed record ApprovalOpenResult(ApprovalRequestDetail Request, bool AlreadyExisted);

/// <summary>Kayıt ekranındaki onay paneli: kayıt türüne bağlı açık süreçler ve kaydın son talebi.</summary>
public sealed record RecordApprovalView(IReadOnlyList<ApproverOption> Processes, Contracts.Approvals.RecordApprovalStatus? Latest);

public sealed record ApprovalRequestPage(IReadOnlyList<ApprovalRequestDetail> Items, int Page, int PageSize, int TotalCount);

public sealed record ApprovalDayCount(DateOnly Day, int Approved, int Rejected);

public sealed record ApprovalProcessCount(string Name, int Open);

/// <summary>Pano özeti: yalnızca kullanıcının görebildiği talepler sayılır.</summary>
public sealed record ApprovalOverview(
    int WaitingForMe, int Overdue, int MyOpen, int Orphaned, int ApprovedLast30Days, int RejectedLast30Days, decimal? AverageDecisionHours,
    IReadOnlyList<ApprovalDayCount> Trend, IReadOnlyList<ApprovalProcessCount> OpenByProcess);

public sealed record ApprovalDelegationInput(Guid? PrincipalUserId, Guid DelegateUserId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Reason);

public sealed record ApprovalDelegationDetail(
    Guid Id, Guid PrincipalUserId, string PrincipalName, Guid DelegateUserId, string DelegateName, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    string? Reason, bool IsActive, DateTimeOffset? RevokedAt);

public sealed record ApproverOption(Guid Id, string Name);

public sealed record ApproverOptions(IReadOnlyList<ApproverOption> People, IReadOnlyList<ApproverOption> Roles);

/// <summary>
/// Genel onay (ADR-0027 Dilim 4a). Mevcut teklif ve sözleşme onaylarının yerine geçmez. Her yazma <c>core.approval-definition</c> ya da
/// <c>core.approval-request</c> kaydı olarak denetime yazılır. <c>seeAll</c> parametreleri çağıranın <c>core.approvals.manage</c>
/// iznini taşır; görünürlük sorgu seviyesinde uygulanır.
/// </summary>
public interface IApprovalService
{
    Task<IReadOnlyList<ApprovalDefinition>> ListDefinitionsAsync(bool activeOnly, CancellationToken cancellationToken);

    Task<ApprovalDefinition> CreateDefinitionAsync(ApprovalDefinitionInput input, CancellationToken cancellationToken);

    /// <summary>Tanım yoksa null.</summary>
    Task<ApprovalDefinition?> UpdateDefinitionAsync(Guid id, ApprovalDefinitionInput input, long? expectedVersion, CancellationToken cancellationToken);

    /// <summary>Tanım yoksa null. Kapatılan tanımla yeni talep açılamaz; açık talepler sürer.</summary>
    Task<ApprovalDefinition?> SetDefinitionActiveAsync(Guid id, bool active, CancellationToken cancellationToken);

    Task<ApproverOptions> ApproverOptionsAsync(CancellationToken cancellationToken);

    Task<ApprovalOverview> OverviewAsync(bool seeAll, CancellationToken cancellationToken);

    Task<ApprovalRequestPage> ListRequestsAsync(ApprovalRequestQuery query, bool seeAll, CancellationToken cancellationToken);

    /// <summary>Talep yoksa ya da kullanıcı göremiyorsa null.</summary>
    Task<ApprovalRequestDetail?> GetRequestAsync(Guid id, bool seeAll, CancellationToken cancellationToken);

    Task<ApprovalRequestDetail> OpenRequestAsync(ApprovalRequestInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Kural aksiyonunun kayda bağlı talebi (Dilim 4c-2). Talep sahibi çağıran değil <see cref="ApprovalRecordRequest.RequesterId"/>'dir;
    /// bu yüzden yalnızca sunucu içi çağrılır (uç nokta yoktur). İdempotent: aynı <see cref="ApprovalRecordRequest.SourceKey"/> ya da aynı
    /// kayıt için bekleyen talep varsa yenisi açılmaz, mevcut döner. Süreç yok/kapalıysa, kayda bağlı değilse ya da dört göz sağlanamıyorsa
    /// <see cref="ApprovalException"/>.
    /// </summary>
    Task<ApprovalOpenResult> OpenForRecordAsync(ApprovalRecordRequest request, CancellationToken cancellationToken);

    /// <summary>Kullanıcının karar verebildiği etkin adımı onaylar ya da reddeder (ret notu zorunlu). Talep yoksa ya da göremiyorsa null.</summary>
    Task<ApprovalRequestDetail?> DecideAsync(Guid id, bool approve, string? comment, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>Talep sahibi açık talebi geri çeker. Talep yoksa ya da göremiyorsa null.</summary>
    Task<ApprovalRequestDetail?> WithdrawAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Etkin adımın onaycısını değiştirir (yönetici). Talep yoksa null.</summary>
    Task<ApprovalRequestDetail?> ReassignAsync(Guid id, Guid stepId, Guid? userId, Guid? roleId, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>Süresi geçen etkin adımları işler (arka plan): eskalasyon onaycısını açar, ilgilileri bir kez bilgilendirir. İşlenen adım sayısını döner.</summary>
    Task<int> EscalateDueAsync(CancellationToken cancellationToken);

    /// <summary>Kaydın korunan alanı (tutar, para birimi) değiştiyse kayda bağlı bekleyen talepleri geçersiz kılar. Geçersizleşen talep sayısını döner.</summary>
    Task<int> InvalidateForRecordAsync(Contracts.Records.RecordRef record, IReadOnlyCollection<string> changedFields, CancellationToken cancellationToken);

    /// <summary>Kayıt ekranının onay paneli. Kullanıcı kaydı okuyamıyorsa null.</summary>
    Task<RecordApprovalView?> RecordViewAsync(Contracts.Records.RecordRef record, CancellationToken cancellationToken);

    /// <summary>Kullanıcının verdiği ve aldığı vekâletler; <c>seeAll</c> ise firmanın hepsi.</summary>
    Task<IReadOnlyList<ApprovalDelegationDetail>> ListDelegationsAsync(bool seeAll, CancellationToken cancellationToken);

    /// <summary>Vekâlet açar. Başkası adına yalnızca yönetici açabilir (<c>seeAll</c>).</summary>
    Task<ApprovalDelegationDetail> CreateDelegationAsync(ApprovalDelegationInput input, bool seeAll, CancellationToken cancellationToken);

    /// <summary>Vekâleti geri alır. Vekâlet yoksa ya da kullanıcının değilse null.</summary>
    Task<ApprovalDelegationDetail?> RevokeDelegationAsync(Guid id, bool seeAll, CancellationToken cancellationToken);
}
