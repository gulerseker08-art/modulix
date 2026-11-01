namespace Modulix.Modules.Contracts.Domain;

// Yenileme, hatırlatma ve fesih varlıkları (ADR-0022, dilim 8). Bir sözleşme dönemi (bitiş tarihi) için en fazla bir yenileme kararı vardır; hatırlatmalar sözleşme,
// dönem, tür ve eşik başına bir kez üretilir. Satırlar eklenir, değişmez.

/// <summary>
/// Bir sözleşme dönemi için verilmiş yenileme kararı: yenileme taslağı açıldı, otomatik uzatıldı ya da yenilenmeyecek. (Sözleşme, <see cref="PeriodEnd"/>) çifti tekildir:
/// aynı dönem için ikinci karar (mükerrer taslak, çift uzatma, çelişen karar) üretilemez. Otomatik uzatma yeni bir sözleşme değil, aynı sözleşmenin yeni dönemidir;
/// ticari yeniden görüşme ve yeni sözleşme ise taslak yoluyla yapılır ve normal onay sürecinden geçer.
/// </summary>
public sealed class ContractRenewal : ContractsEntity
{
    public const int NoteMaxLength = 1000;

    public Guid ContractId { get; internal set; }

    /// <summary>Kararın verildiği dönemin bitiş tarihi (karar anındaki <c>EndDate</c>).</summary>
    public DateOnly PeriodEnd { get; internal set; }

    /// <summary><see cref="Lifecycle.RenewalOutcome"/>.</summary>
    public string Outcome { get; internal set; } = string.Empty;

    /// <summary>Otomatik uzatmada yeni dönemin bitişi.</summary>
    public DateOnly? NewEndDate { get; internal set; }

    /// <summary>Yenileme taslağı olarak açılan sözleşme.</summary>
    public Guid? ResultContractId { get; internal set; }

    /// <summary><see cref="Lifecycle.NonRenewalReason"/>; yalnızca yenilenmeyecek kararında.</summary>
    public string? Reason { get; internal set; }

    public string? Note { get; internal set; }

    /// <summary>"User" ya da "Scheduler".</summary>
    public string Source { get; internal set; } = "User";

    public Guid? DecidedBy { get; internal set; }
}

/// <summary>
/// Hatırlatma işareti (idempotency): sözleşme, dönem anahtarı, tür ve eşik için tek satır. Aktivite açıldıysa kimliği; açılamadıysa durumu. Kaçırılan eşikler
/// (zamanlanmış iş uzun süre çalışmadıysa) "Skipped" olarak işaretlenir ve sonradan geriye dönük aktivite açılmaz.
/// </summary>
public sealed class ContractReminder : ContractsEntity
{
    public Guid ContractId { get; internal set; }

    /// <summary>Hatırlatmanın bağlı olduğu dönem: yenileme/bildirim/eskalasyonda dönemin bitişi, onay hatırlatmasında onay hedef tarihi.</summary>
    public DateOnly PeriodKey { get; internal set; }

    /// <summary><see cref="Lifecycle.ReminderKind"/>.</summary>
    public string Kind { get; internal set; } = string.Empty;

    /// <summary>Gün eşiği (90, 60, 30, 7...); eşiksiz türlerde 0.</summary>
    public int ThresholdDays { get; internal set; }

    /// <summary><see cref="Lifecycle.ReminderStatus"/>.</summary>
    public string Status { get; internal set; } = string.Empty;

    public Guid? ActivityId { get; internal set; }

    public string? Note { get; internal set; }
}
