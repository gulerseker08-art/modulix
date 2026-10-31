namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>Yükümlü taraf: taahhüdü kim yerine getirir.</summary>
public static class CommitmentObligor
{
    public const string Provider = "Provider";
    public const string Customer = "Customer";
    public const string Both = "Both";

    public static IReadOnlyList<string> All { get; } = [Provider, Customer, Both];
}

public static class CommitmentKind
{
    public const string OneTime = "OneTime";
    public const string Periodic = "Periodic";

    public static IReadOnlyList<string> All { get; } = [OneTime, Periodic];
}

public static class CommitmentRecurrence
{
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";
    public const string Quarterly = "Quarterly";
    public const string Yearly = "Yearly";

    public static IReadOnlyList<string> All { get; } = [Weekly, Monthly, Quarterly, Yearly];
}

/// <summary>Ölçüm yönü: hedefin altı ya da üstü kabul edilir. <see cref="Exactly"/>: hedefe tolerans içinde eşit olmalıdır.</summary>
public static class CommitmentDirection
{
    public const string AtLeast = "AtLeast";
    public const string AtMost = "AtMost";
    public const string Exactly = "Exactly";

    public static IReadOnlyList<string> All { get; } = [AtLeast, AtMost, Exactly];
}

public static class CommitmentStatus
{
    public const string Active = "Active";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

    public static IReadOnlyList<string> All { get; } = [Active, Completed, Cancelled];
}

/// <summary>Gerçekleşme durumu. <see cref="Breached"/> + tamamlanma tarihi: geç ya da hedefi tutturamayarak tamamlandı; tamamlanma tarihi yoksa hâlâ yerine getirilmedi.</summary>
public static class OccurrenceStatus
{
    public const string Open = "Open";
    public const string Fulfilled = "Fulfilled";
    public const string Breached = "Breached";
    public const string Waived = "Waived";
    public const string Cancelled = "Cancelled";

    public static IReadOnlyList<string> All { get; } = [Open, Fulfilled, Breached, Waived, Cancelled];
}

/// <summary>İhlal takip aktivitesinin durumu. <see cref="Unavailable"/>: aktivite modülü (CRM) kapalı ya da kurulu değil; ihlal yine kaydedilir.</summary>
public static class FollowUpState
{
    public const string None = "None";
    public const string Created = "Created";
    public const string Unavailable = "Unavailable";
    public const string Failed = "Failed";
}

/// <summary>Tekrar kuralından son tarih üretimi (saf). Tarihler gündür; ay sonu taşması geriye (ayın son gününe) kırpılır ve ilk son tarihe bağlı hesaplanır (kayma olmaz).</summary>
public static class CommitmentSchedule
{
    public const int MaxOccurrences = 120;

    /// <summary>
    /// Son tarihler. Tek seferlikte tek satır. Dönemselde <paramref name="firstDue"/>'dan başlayarak <paramref name="endDate"/>'e (dahil) kadar; bitiş yoksa
    /// <paramref name="horizon"/>'a kadar (açık uçlu taahhüt kayan ufukla genişler). En fazla <see cref="MaxOccurrences"/> satır.
    /// </summary>
    public static IReadOnlyList<(int Sequence, DateOnly Due)> DueDates(string kind, DateOnly firstDue, string? recurrence, DateOnly? endDate, DateOnly horizon)
    {
        if (kind != CommitmentKind.Periodic || recurrence is null)
        {
            return [(1, firstDue)];
        }

        var limit = endDate ?? horizon;
        var result = new List<(int, DateOnly)>();
        for (var k = 0; k < MaxOccurrences; k++)
        {
            var due = Step(firstDue, k, recurrence);
            if (due > limit)
            {
                break;
            }

            result.Add((k + 1, due));
        }

        return result;
    }

    /// <summary>İlk son tarihten <paramref name="k"/> dönem sonrası. Aylık dönemlerde gün, ayın son gününe kırpılır ancak ilk son tarihin günü korunur (31 Ocak → 28 Şubat → 31 Mart).</summary>
    public static DateOnly Step(DateOnly first, int k, string recurrence)
    {
        if (recurrence == CommitmentRecurrence.Weekly)
        {
            return first.AddDays(7 * k);
        }

        var months = recurrence switch { CommitmentRecurrence.Quarterly => 3, CommitmentRecurrence.Yearly => 12, _ => 1 } * k;
        var firstOfTarget = new DateOnly(first.Year, first.Month, 1).AddMonths(months);
        return new DateOnly(firstOfTarget.Year, firstOfTarget.Month, Math.Min(first.Day, DateTime.DaysInMonth(firstOfTarget.Year, firstOfTarget.Month)));
    }
}

/// <summary>Ölçüm değerlendirmesi (saf).</summary>
public static class CommitmentMeasure
{
    /// <summary>Gerçekleşen değer hedefi (tolerans dahil) karşılıyor mu? Hedef ya da yön yoksa ölçüm yok sayılır (null).</summary>
    public static bool? Meets(string? direction, decimal? target, decimal? tolerance, decimal actual)
    {
        if (target is not { } t || direction is null)
        {
            return null;
        }

        var slack = Math.Abs(tolerance ?? 0m);
        return direction switch
        {
            CommitmentDirection.AtLeast => actual >= t - slack,
            CommitmentDirection.AtMost => actual <= t + slack,
            CommitmentDirection.Exactly => Math.Abs(actual - t) <= slack,
            _ => null,
        };
    }
}

/// <summary>Gerçekleşmenin bağımlılık/ihlal/eskalasyon kararları (saf; "bugün" tenant gününe göre dışarıdan verilir).</summary>
public static class CommitmentTracker
{
    /// <summary>Son tarihi geçmiş ama henüz ihlal sayılmayan (tolerans süresindeki ya da yeni geçmiş) açık gerçekleşme.</summary>
    public static bool IsOverdue(string status, DateOnly? completedOn, DateOnly due, DateOnly today) =>
        status is OccurrenceStatus.Open or OccurrenceStatus.Breached && completedOn is null && today > due;

    /// <summary>Son tarih + tolerans günü geçmiş açık gerçekleşme ihlal olur.</summary>
    public static bool ShouldBreach(string status, DateOnly due, int graceDays, DateOnly today) =>
        status == OccurrenceStatus.Open && today > due.AddDays(Math.Max(0, graceDays));

    /// <summary>İhlalden sonra eskalasyon süresi dolmuş, hâlâ tamamlanmamış ve daha önce eskale edilmemiş mi?</summary>
    public static bool ShouldEscalate(string status, DateOnly? completedOn, DateTimeOffset? breachedAt, DateTimeOffset? escalatedAt, int escalateAfterDays, DateTimeOffset now) =>
        status == OccurrenceStatus.Breached && completedOn is null && escalatedAt is null && escalateAfterDays > 0 && breachedAt is { } at && at.AddDays(escalateAfterDays) <= now;

    /// <summary>Gerçekleşme kaydı yapılabilir mi? Tamamlanmış, muaf tutulmuş ya da iptal edilmiş gerçekleşme değişmez (üzerine yazılmaz).</summary>
    public static bool IsCompletable(string status, DateOnly? completedOn) =>
        status is OccurrenceStatus.Open or OccurrenceStatus.Breached && completedOn is null;

    /// <summary>
    /// Bağımlılık engeli: bağımlı olunan taahhüdün bu gerçekleşmeyle aynı ya da daha eski son tarihli, hâlâ tamamlanmamış gerçekleşmesi varsa o tarih döner
    /// (kaydedilemez); yoksa null. Muaf tutulan ve iptal edilen gerçekleşmeler engel sayılmaz.
    /// </summary>
    public static DateOnly? DependencyBlock(DateOnly due, IEnumerable<(string Status, DateOnly? CompletedOn, DateOnly Due)> dependencyOccurrences) =>
        dependencyOccurrences.Where(o => o.Due <= due && IsCompletable(o.Status, o.CompletedOn)).Select(o => (DateOnly?)o.Due).Min();
}
