using Modulix.Modules.Crm.Reporting;

namespace Modulix.Modules.Crm.Domain;

// Satış hedefi ve sürümlü satış tahmini (ADR-0026, dilim 1).

/// <summary>
/// Satış hedefi: dönem, metrik, kapsam ve para birimiyle tanımlı hedef. Tutar hedefi (para birimli) ile adet hedefi karıştırılmaz. Aynı kapsam, metrik ve
/// para biriminde dönemler çakışamaz. Değer değişikliği <see cref="SalesTargetRevision"/> olarak sürümlenir; dönem, metrik, kapsam ve para birimi sonradan değişmez.
/// </summary>
public sealed class SalesTarget : CrmEntity
{
    public const int NoteMaxLength = 500;
    public const int MaxSpanDays = 372;

    /// <summary><see cref="TargetMetric"/>.</summary>
    public string Metric { get; internal set; } = TargetMetric.WonAmount;

    /// <summary><see cref="TargetScope"/>.</summary>
    public string ScopeType { get; internal set; } = TargetScope.Tenant;

    /// <summary>Kullanıcı hedefinde hedefin sahibi (etkin üye); tenant hedefinde null.</summary>
    public Guid? ScopeUserId { get; internal set; }

    /// <summary>Tutar hedefinde ISO 4217 kodu; adet hedefinde null.</summary>
    public string? Currency { get; internal set; }

    public DateOnly PeriodStart { get; internal set; }

    /// <summary>Dönemin son günü (dahil).</summary>
    public DateOnly PeriodEnd { get; internal set; }

    public decimal Value { get; internal set; }

    public int RevisionNo { get; internal set; } = 1;

    public string? Note { get; internal set; }

    /// <summary>Güncelleme isteğindeki değişiklik gerekçesi (saklanmaz; sürüm kaydına yazılır).</summary>
    internal string? PendingReason { get; set; }
}

/// <summary>Hedef değerinin her değişikliği: eski değer, yeni değer ve gerekçe. Hedefle birlikte silinir; başka hiçbir yoldan değişmez.</summary>
public sealed class SalesTargetRevision : CrmEntity
{
    public Guid TargetId { get; internal set; }

    public int RevisionNo { get; internal set; }

    public decimal OldValue { get; internal set; }

    public decimal NewValue { get; internal set; }

    public string Reason { get; internal set; } = string.Empty;

    public Guid? ChangedBy { get; internal set; }
}

/// <summary>
/// Satış tahmini: bir dönem, kapsam (tenant ya da kullanıcı) ve raporlama para birimi için fırsat anlık görüntüsü ve yönetici düzeltmeleri.
/// Taslak → Sunuldu → Kilitlendi akışı yönetilir; kilitli sürüm değişmez. Yeniden açma gerekçelidir ve <b>yeni sürüm</b> (Taslak) üretir, önceki kilitli sürüm korunur.
/// </summary>
public sealed class ForecastSubmission : CrmEntity
{
    public const int NoteMaxLength = 1000;

    /// <summary><see cref="PeriodKind"/> adı.</summary>
    public string PeriodKind { get; internal set; } = Reporting.PeriodKind.Month.ToString();

    public DateOnly PeriodStart { get; internal set; }

    /// <summary>Dönemin son günü (dahil).</summary>
    public DateOnly PeriodEnd { get; internal set; }

    public string ScopeType { get; internal set; } = TargetScope.Tenant;

    public Guid? ScopeUserId { get; internal set; }

    public string ReportingCurrency { get; internal set; } = Opportunity.DefaultCurrency;

    /// <summary>Aynı dönem, kapsam ve para biriminde 1'den başlar; her yeniden açma bir artırır.</summary>
    public int VersionNo { get; internal set; } = 1;

    /// <summary><see cref="ForecastStatus"/>.</summary>
    public string Status { get; internal set; } = ForecastStatus.Draft;

    /// <summary>Yeniden açmayla doğduysa kaynağı olan kilitli sürüm.</summary>
    public Guid? PreviousSubmissionId { get; internal set; }

    public string? Note { get; internal set; }

    public DateTimeOffset? SubmittedAt { get; internal set; }

    public Guid? SubmittedBy { get; internal set; }

    public DateTimeOffset? LockedAt { get; internal set; }

    public Guid? LockedBy { get; internal set; }

    /// <summary>Bu sürüm yeniden açma sonucu doğduysa gerekçe.</summary>
    public string? ReopenReason { get; internal set; }

    /// <summary>Sunulan tahmin taslağa geri çekildiyse son geri çekme gerekçesi.</summary>
    public string? ReturnReason { get; internal set; }

    /// <summary>Kilit anında hesaplanan gerçekleşen satış (raporlama para biriminde); sonradan değişmez. Kısmi ise <see cref="RealizedPartial"/>.</summary>
    public decimal? RealizedAtLock { get; internal set; }

    public bool RealizedPartial { get; internal set; }

    /// <summary>Fırsatların son yenilenme anı (taslak oluşturma ve yenileme).</summary>
    public DateTimeOffset? RefreshedAt { get; internal set; }

    /// <summary>Her yazma eyleminde güncellenir; boru hattının sürüm ve audit adımlarının tetiklenmesi içindir.</summary>
    public DateTimeOffset? ChangedAt { get; internal set; }
}

/// <summary>
/// Tahmin satırı: fırsatın tahmin anındaki değerleri. <see cref="BaseAmount"/> raporlama para birimindeki net tutardır; kaynak fırsat tutarı değişmez.
/// Yönetici düzeltmesi (<see cref="AdjustmentAmount"/>, <see cref="Category"/>, <see cref="ProbabilityOverride"/>) ayrı alanlardır ve
/// <see cref="ForecastAdjustment"/> kaydıyla gerekçelenir. Kullanılan kur ve kur günü saklanır; kilitli sürüm sonradan eklenen kurdan etkilenmez.
/// </summary>
public sealed class ForecastItem : CrmEntity
{
    public const int NumberMaxLength = 30;
    public const int TitleMaxLength = 200;

    public Guid SubmissionId { get; internal set; }

    public Guid OpportunityId { get; internal set; }

    public string OpportunityNumber { get; internal set; } = string.Empty;

    public string Title { get; internal set; } = string.Empty;

    public Guid? OwnerId { get; internal set; }

    public string Stage { get; internal set; } = string.Empty;

    public decimal? SourceAmount { get; internal set; }

    public string SourceCurrency { get; internal set; } = Opportunity.DefaultCurrency;

    /// <summary>Fırsatın olasılığı (yüzde); bilinmiyorsa null (veri eksiği).</summary>
    public int? Probability { get; internal set; }

    /// <summary>Fırsattaki olasılık kullanıcı tarafından elle girildiyse true (false: aşama varsayılanı).</summary>
    public bool ProbabilityManual { get; internal set; }

    public DateOnly? ExpectedCloseDate { get; internal set; }

    public decimal? RateUsed { get; internal set; }

    public DateOnly? RateDate { get; internal set; }

    /// <summary>Tutar var ama kur bulunamadı: <see cref="BaseAmount"/> boştur ve toplama girmez.</summary>
    public bool RateMissing { get; internal set; }

    public decimal? BaseAmount { get; internal set; }

    public string BaseCategory { get; internal set; } = ForecastCategory.Pipeline;

    public string Category { get; internal set; } = ForecastCategory.Pipeline;

    public decimal? AdjustmentAmount { get; internal set; }

    public int? ProbabilityOverride { get; internal set; }
}

/// <summary>Taslak tahmindeki her yönetici düzeltmesi: eski değer, yeni değer, gerekçe, yapan ve zaman (<c>CreatedAt</c>). Değiştirilmez, silinmez.</summary>
public sealed class ForecastAdjustment : CrmEntity
{
    public const int ValueMaxLength = 100;

    public Guid SubmissionId { get; internal set; }

    public Guid? ItemId { get; internal set; }

    public Guid OpportunityId { get; internal set; }

    /// <summary><see cref="ForecastAdjustmentKind"/>.</summary>
    public string Kind { get; internal set; } = ForecastAdjustmentKind.Category;

    public string? FromValue { get; internal set; }

    public string? ToValue { get; internal set; }

    public string Reason { get; internal set; } = string.Empty;

    public Guid? ChangedBy { get; internal set; }
}
