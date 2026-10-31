namespace Modulix.Modules.Contracts.Domain;

// Taahhüt ve yükümlülük varlıkları (ADR-0022, dilim 7). Taahhüt tanımdır; her dönemsel gerçekleşme ayrı bir satırdır (<see cref="ContractCommitmentOccurrence"/>) ve
// tamamlanmış satır üzerine yazılmaz. Taahhüt kaydı toplu kökdür: gerçekleşme değişiklikleri taahhüt güncellemesiyle aynı işlemde (sürüm denetimli) yazılır.

/// <summary>
/// Sözleşmeden doğan taahhüt/yükümlülük: teslimat, eğitim, kurulum, belge sağlama, raporlama, hizmet seviyesi gibi sektörden bağımsız bir tanım. Yükümlü taraf,
/// sorumlu kullanıcı, takvim (tek seferlik ya da dönemsel), ölçüm ölçütü (hedef ve tolerans), bağımlılık ve ihlal/eskalasyon ayarlarını taşır. Ceza ya da finansal sonuç
/// <b>uygulanmaz</b>; <see cref="ConsequenceNote"/> yalnızca sözleşmedeki ilgili maddeyi anan bir nottur.
/// </summary>
public sealed class ContractCommitment : ContractsEntity
{
    public const int TitleMaxLength = 200;
    public const int TextMaxLength = 2000;
    public const int MetricMaxLength = 100;
    public const int UnitMaxLength = 30;

    public Guid ContractId { get; internal set; }

    public string Title { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    /// <summary>Kategori (Y9b listesi <c>contracts.commitment-category</c>): teslimat, eğitim, kurulum, belge, raporlama, hizmet seviyesi, ödeme, diğer.</summary>
    public string Category { get; internal set; } = string.Empty;

    /// <summary><see cref="Lifecycle.CommitmentObligor"/>: yükümlü taraf.</summary>
    public string Obligor { get; internal set; } = Lifecycle.CommitmentObligor.Provider;

    /// <summary>Sorumlu kullanıcı (tenant üyesi); ihlal takip aktivitesi ona atanır.</summary>
    public Guid? ResponsibleUserId { get; internal set; }

    /// <summary>Eskalasyon kullanıcısı: ihlal belirli gün geçince ikinci takip aktivitesi ona açılır.</summary>
    public Guid? EscalationUserId { get; internal set; }

    /// <summary><see cref="Lifecycle.CommitmentKind"/>.</summary>
    public string Kind { get; internal set; } = Lifecycle.CommitmentKind.OneTime;

    public DateOnly StartDate { get; internal set; }

    /// <summary>Tek seferlikte son tarih; dönemsel taahhütte ilk gerçekleşmenin son tarihi (sonrakiler tekrar kuralıyla türetilir).</summary>
    public DateOnly DueDate { get; internal set; }

    /// <summary><see cref="Lifecycle.CommitmentRecurrence"/>; yalnızca dönemselde.</summary>
    public string? Recurrence { get; internal set; }

    /// <summary>Tekrarın bittiği tarih (dahil). Boşsa sözleşme bitişi; o da yoksa (belirsiz süreli) açık uçludur ve gerçekleşmeler kayan ufukla üretilir.</summary>
    public DateOnly? RecurrenceEndDate { get; internal set; }

    /// <summary>Son tarihten sonra ihlal sayılmadan önce tanınan gün (tolerans süresi).</summary>
    public int GraceDays { get; internal set; }

    /// <summary>İhlalden bu kadar gün sonra eskalasyon yapılır; 0 kapalıdır.</summary>
    public int EscalateAfterDays { get; internal set; }

    /// <summary>Ölçüm ölçütünün adı (ör. "Aylık erişilebilirlik"); boşsa tamamlanma ölçülmez, yalnızca yapıldı/yapılmadı kaydedilir.</summary>
    public string? MetricName { get; internal set; }

    public string? Unit { get; internal set; }

    /// <summary><see cref="Lifecycle.CommitmentDirection"/>.</summary>
    public string? Direction { get; internal set; }

    public decimal? TargetValue { get; internal set; }

    /// <summary>Hedeften kabul edilen mutlak sapma (hedefin yanlış yönde ne kadarı hâlâ karşılanmış sayılır).</summary>
    public decimal? Tolerance { get; internal set; }

    /// <summary>Bu taahhüdün gerçekleşmesi, bağımlı olduğu taahhüdün aynı ya da daha eski tarihli gerçekleşmeleri tamamlanmadan kaydedilemez.</summary>
    public Guid? DependsOnCommitmentId { get; internal set; }

    /// <summary>Sözleşmedeki sonuç maddesine atıf (serbest not). Modulix hiçbir ceza ya da finansal sonuç uygulamaz.</summary>
    public string? ConsequenceNote { get; internal set; }

    /// <summary><see cref="Lifecycle.CommitmentStatus"/>; sistem alanı.</summary>
    public string Status { get; internal set; } = Lifecycle.CommitmentStatus.Active;

    public Guid? CreatedBy { get; internal set; }
}

/// <summary>
/// Taahhüdün bir gerçekleşmesi (tek seferlikte bir, dönemselde her dönem için bir satır). Tamamlanma, ihlal ve muafiyet bu satırda saklanır; önceki dönemlerin
/// kayıtları sonraki dönemlerce değiştirilmez. <see cref="BreachedAt"/> dolu ve <see cref="CompletedOn"/> dolu ise geç ya da hedefi tutturamayarak tamamlandı demektir.
/// </summary>
public sealed class ContractCommitmentOccurrence : ContractsEntity
{
    public const int NoteMaxLength = 1000;

    public Guid CommitmentId { get; internal set; }

    public Guid ContractId { get; internal set; }

    /// <summary>Dönem sırası (1'den); taahhüt içinde tekildir (mükerrer üretim engeli).</summary>
    public int Sequence { get; internal set; }

    public DateOnly DueDate { get; internal set; }

    /// <summary><see cref="Lifecycle.OccurrenceStatus"/>.</summary>
    public string Status { get; internal set; } = Lifecycle.OccurrenceStatus.Open;

    public DateOnly? CompletedOn { get; internal set; }

    public Guid? CompletedBy { get; internal set; }

    public DateTimeOffset? RecordedAt { get; internal set; }

    public decimal? ActualValue { get; internal set; }

    /// <summary>Ölçümlü taahhütte hedef (tolerans dahil) karşılandı mı; ölçümsüzde boş.</summary>
    public bool? MetTarget { get; internal set; }

    public string? Note { get; internal set; }

    public Guid? EvidenceFileId { get; internal set; }

    public DateTimeOffset? BreachedAt { get; internal set; }

    public string? BreachReason { get; internal set; }

    /// <summary><see cref="Lifecycle.FollowUpState"/>: ihlal takip aktivitesinin durumu.</summary>
    public string FollowUpStatus { get; internal set; } = Lifecycle.FollowUpState.None;

    public Guid? FollowUpActivityId { get; internal set; }

    public DateTimeOffset? EscalatedAt { get; internal set; }

    public Guid? EscalationActivityId { get; internal set; }

    public Guid? WaivedBy { get; internal set; }

    public string? WaiveReason { get; internal set; }
}

/// <summary>Taahhüt gerçekleşmesinin kanıt dosyası (PDF, PNG, JPEG; dosya imzası denetlenir, her zaman indirme olarak sunulur).</summary>
public sealed class ContractCommitmentFile : ContractsEntity
{
    public const int FileNameMaxLength = 160;

    public Guid ContractId { get; internal set; }

    public Guid OccurrenceId { get; internal set; }

    public string FileName { get; internal set; } = string.Empty;

    public string ContentType { get; internal set; } = string.Empty;

    public int SizeBytes { get; internal set; }

    public string Sha256 { get; internal set; } = string.Empty;

    public byte[] Content { get; internal set; } = [];

    public Guid? CreatedBy { get; internal set; }
}
