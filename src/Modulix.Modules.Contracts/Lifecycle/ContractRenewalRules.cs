namespace Modulix.Modules.Contracts.Lifecycle;

public static class RenewalOutcome
{
    public const string DraftCreated = "DraftCreated";
    public const string AutoExtended = "AutoExtended";
    public const string NotRenewed = "NotRenewed";

    public static IReadOnlyList<string> All { get; } = [DraftCreated, AutoExtended, NotRenewed];
}

/// <summary>Yenilememe nedeni (sabit liste; ek açıklama notla verilir). Kaybetme/yenilememe raporlaması bu anahtarlarla yapılır.</summary>
public static class NonRenewalReason
{
    public const string Price = "Price";
    public const string Competitor = "Competitor";
    public const string NoLongerNeeded = "NoLongerNeeded";
    public const string ServiceQuality = "ServiceQuality";
    public const string CustomerClosed = "CustomerClosed";
    public const string Other = "Other";

    public static IReadOnlyList<string> All { get; } = [Price, Competitor, NoLongerNeeded, ServiceQuality, CustomerClosed, Other];
}

public static class ReminderKind
{
    /// <summary>Yenileme/bildirim tarihi yaklaşıyor (eşik gün).</summary>
    public const string Renewal = "Renewal";

    /// <summary>Son bildirim tarihi geçti ve karar yok: eskalasyon.</summary>
    public const string Escalation = "Escalation";

    /// <summary>İç onay hedef tarihi geçti.</summary>
    public const string Approval = "Approval";
}

public static class ReminderStatus
{
    public const string Created = "Created";
    public const string Skipped = "Skipped";
    public const string Unavailable = "Unavailable";
    public const string Failed = "Failed";

    /// <summary>Yeniden denenebilir durumlar.</summary>
    public static bool IsRetryable(string status) => status is Failed;
}

/// <summary>Yenileme, hatırlatma, otomatik uzatma ve fesih kuralları (saf; "bugün" tenant gününe göre dışarıdan verilir).</summary>
public static class ContractRenewalRules
{
    public static readonly IReadOnlyList<int> DefaultThresholds = [90, 60, 30, 7];

    /// <summary>"90,60,30,7" → tekil, büyükten küçüğe, 1–365 arası. Geçersiz ya da boş girdi varsayılanı verir.</summary>
    public static IReadOnlyList<int> ParseThresholds(string? text)
    {
        var values = (text ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : -1).Where(n => n is >= 1 and <= 365).Distinct().OrderByDescending(n => n).ToList();
        return values.Count == 0 ? DefaultThresholds : values;
    }

    /// <summary>
    /// Hatırlatma hedefi: son bildirim tarihi (yenilememe/fesih bildirimi bu tarihe kadar yapılmalıdır); yoksa bitiş tarihi. Belirsiz süreli sözleşmede hedef yoktur.
    /// </summary>
    public static DateOnly? Target(DateOnly? noticeDeadline, DateOnly? endDate, bool isIndefinite) => isIndefinite ? null : noticeDeadline ?? endDate;

    /// <summary>
    /// Şimdi üretilecek hatırlatmalar. Hedefe kalan gün eşiğe eşit ya da altına inmiş eşiklerden daha önce işlenmemiş olanlar adaydır; zamanlanmış iş uzun süre çalışmadıysa
    /// birden çok eşik birikmiş olabilir: yalnızca <b>en dar</b> (en küçük) aday için aktivite açılır, daha geniş (kaçırılan) adaylar <paramref name="missed"/> olarak döner ve
    /// "atlandı" işaretlenir. Hedef geçmişse (kalan gün negatif) hatırlatma üretilmez; eskalasyon ayrı bir kuraldır.
    /// </summary>
    public static (int? Fire, IReadOnlyList<int> Missed) DueThresholds(DateOnly today, DateOnly target, IReadOnlyList<int> thresholds, IReadOnlySet<int> alreadyRecorded)
    {
        var daysLeft = target.DayNumber - today.DayNumber;
        if (daysLeft < 0)
        {
            return (null, []);
        }

        var due = thresholds.Where(t => daysLeft <= t && !alreadyRecorded.Contains(t)).OrderBy(t => t).ToList();
        return due.Count == 0 ? (null, []) : (due[0], [.. due.Skip(1)]);
    }

    /// <summary>Son bildirim tarihi geçmiş mi? (Karar yoksa eskalasyon.)</summary>
    public static bool NoticePassed(DateOnly today, DateOnly? noticeDeadline) => noticeDeadline is { } d && today > d;

    /// <summary>
    /// Otomatik uzatma uygun mu? Koşullar birlikte: otomatik yenilemeli, yenileme süresi tanımlı, belirsiz süreli değil, imzalı ve yürürlükte, tenant otomatik yenilemeye açıkça izin
    /// vermiş, son bildirim tarihi geçmiş (yenilememe bildirimi yapılabilecek süre bitmiş), dönem bitişi geldi ve bu dönem için karar yok. Değilse nedeni döner.
    /// </summary>
    public static (bool Eligible, string? Reason) AutoRenewal(
        string renewalMode, int? periodMonths, bool isIndefinite, string signatureStatus, string effectiveStatus, bool tenantAllows, DateOnly? noticeDeadline, DateOnly? endDate,
        bool decisionExists, DateOnly today)
    {
        if (renewalMode != RenewalMode.Automatic) { return (false, "Yenileme yöntemi otomatik değil."); }
        if (periodMonths is not > 0) { return (false, "Yenileme süresi tanımlı değil."); }
        if (isIndefinite || endDate is null) { return (false, "Belirsiz süreli sözleşme yenilenmez."); }
        if (signatureStatus != SignatureStatus.Completed || effectiveStatus != EffectiveStatus.Active) { return (false, "Yalnızca imzalı ve yürürlükteki sözleşme otomatik uzatılır."); }
        if (!tenantAllows) { return (false, "Firma otomatik yenilemeye izin vermemiş (ayar kapalı)."); }
        if (decisionExists) { return (false, "Bu dönem için yenileme kararı zaten var."); }
        if (!NoticePassed(today, noticeDeadline) && noticeDeadline is not null) { return (false, "Son bildirim tarihi henüz geçmedi."); }
        return today < endDate.Value ? (false, "Dönem bitişi henüz gelmedi.") : (true, null);
    }

    /// <summary>Sonraki dönem: mevcut bitişin ertesi günü başlar, yenileme süresi kadar takvim ayı sürer (sabit 365 gün değil).</summary>
    public static (DateOnly Start, DateOnly End) NextTerm(DateOnly currentEnd, int months)
    {
        var start = ContractPeriod.NextTermStart(currentEnd);
        return (start, ContractPeriod.TermEnd(start, months));
    }

    /// <summary>Yenileme taslağı için önerilen süre (ay): tanımlı yenileme süresi, yoksa mevcut dönemin takvim ayı uzunluğu.</summary>
    public static int SuggestedMonths(int? renewalPeriodMonths, DateOnly? start, DateOnly? end) =>
        renewalPeriodMonths is > 0 ? renewalPeriodMonths.Value : start is { } s && end is { } e ? Math.Max(1, ContractApprovalEvaluator.TermMonths(s, e, false) ?? 12) : 12;

    /// <summary>
    /// Fesih: yürürlükteki sözleşme feshedilir (<paramref name="effectiveDate"/> bugün ya da geçmişse hemen, gelecekse planlanır). Bildirim süresinden kısa sürede fesih için
    /// onay gerekir. Sonuç: izin verildi mi, hemen mi, bildirim süresi kısa mı.
    /// </summary>
    public static (bool Allowed, string? Reason, bool Immediate, bool ShortNotice) Terminate(
        string effectiveStatus, string signatureStatus, DateOnly effectiveDate, DateOnly today, int? noticePeriodDays)
    {
        if (!EffectiveStatus.IsBinding(effectiveStatus) || signatureStatus != SignatureStatus.Completed)
        {
            return (false, "Yalnızca imzalı ve yürürlükteki sözleşme feshedilir (taslak ya da imzasız sözleşme iptal edilir).", false, false);
        }

        var shortNotice = noticePeriodDays is > 0 && effectiveDate < today.AddDays(noticePeriodDays.Value);
        return (true, null, effectiveDate <= today, shortNotice);
    }
}
