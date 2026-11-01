namespace Modulix.Modules.Orders.Lifecycle;

public static class JobStatus
{
    public const string AwaitingAssignment = "AwaitingAssignment";
    public const string Planned = "Planned";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All = [AwaitingAssignment, Planned, InProgress, Completed, Cancelled];

    public static bool IsFinal(string status) => status is Completed or Cancelled;

    /// <summary>Açık iş: tamamlanmamış ve iptal edilmemiş (gecikme, atama ve kuyruk görünümleri bunlara bakar).</summary>
    public static bool IsOpen(string status) => !IsFinal(status);
}

public static class JobKind
{
    public const string Delivery = "Delivery";
    public const string Service = "Service";
    public const string Mixed = "Mixed";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All = [Delivery, Service, Mixed, Other];
}

/// <summary>İş oluşturma stratejisi (<c>orders.jobs.strategy</c> ayarı): devir kabul edilince işlerin nasıl üretileceği.</summary>
public static class JobStrategy
{
    /// <summary>Ürün kalemleri tek "Teslimat", hizmet/paket/serbest kalemler tek "Hizmet başlangıcı" işi olur (varsayılan).</summary>
    public const string PerKind = "PerKind";

    /// <summary>Bütün kalemler tek işte toplanır.</summary>
    public const string Single = "Single";

    /// <summary>Her kalem ayrı iş olur.</summary>
    public const string PerLine = "PerLine";

    public static readonly IReadOnlyList<string> All = [PerKind, Single, PerLine];
}

public static class JobAction
{
    public const string Assign = "assign";
    public const string Start = "start";
    public const string Complete = "complete";
    public const string Cancel = "cancel";

    public static readonly IReadOnlyList<string> All = [Assign, Start, Complete, Cancel];
}

public static class JobEventKinds
{
    public const string Generated = "Generated";
    public const string CreatedManually = "CreatedManually";
    public const string Assigned = "Assigned";
    public const string Started = "Started";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string DependencyAdded = "DependencyAdded";
    public const string DependencyRemoved = "DependencyRemoved";
    public const string UnassignedAlert = "UnassignedAlert";
    public const string OverdueAlert = "OverdueAlert";
}

/// <summary>Üretime giren sipariş kalemi (iptal edilmiş ve etkin miktarı sıfır olan kalem girmez).</summary>
public sealed record JobLineInput(Guid LineId, string Name, string Kind);

/// <summary>Planlanan iş: üretim grubu anahtarı, başlık, tür, bağlı kalemler ve (varsa) bağımlı olduğu grubun anahtarı.</summary>
public sealed record PlannedJob(string GroupKey, string Title, string Kind, IReadOnlyList<Guid> LineIds, string? DependsOnGroupKey);

/// <summary>
/// Operasyon işi üretim planı (saf kural). Strateji ve sipariş kalemlerinden hangi işlerin açılacağını belirler; sonuç deterministiktir (aynı girdi aynı grup
/// anahtarlarını verir), bu yüzden yeniden deneme mükerrer iş üretmez.
/// </summary>
public static class JobGenerationPlanner
{
    public static IReadOnlyList<PlannedJob> Plan(string orderNumber, IReadOnlyList<JobLineInput> lines, string strategy, bool serviceAfterDelivery)
    {
        if (lines.Count == 0)
        {
            return [];
        }

        string Title(string what) => $"{what} — {orderNumber}";
        bool IsProduct(JobLineInput l) => l.Kind == LineKinds.Product;

        switch (strategy)
        {
            case JobStrategy.Single:
                var kinds = lines.Select(l => IsProduct(l)).Distinct().ToList();
                return
                [
                    new PlannedJob("all", Title("Sipariş işi"), kinds.Count > 1 ? JobKind.Mixed : kinds[0] ? JobKind.Delivery : JobKind.Service, [.. lines.Select(l => l.LineId)], null),
                ];

            case JobStrategy.PerLine:
                return
                [
                    .. lines.Select(l => new PlannedJob($"line:{l.LineId:N}", Title(l.Name), IsProduct(l) ? JobKind.Delivery : JobKind.Service, [l.LineId], null)),
                ];

            default:
                var jobs = new List<PlannedJob>();
                var products = lines.Where(IsProduct).Select(l => l.LineId).ToList();
                var services = lines.Where(l => !IsProduct(l)).Select(l => l.LineId).ToList();
                if (products.Count > 0)
                {
                    jobs.Add(new PlannedJob("kind:Product", Title("Teslimat"), JobKind.Delivery, products, null));
                }

                if (services.Count > 0)
                {
                    jobs.Add(new PlannedJob("kind:Service", Title("Hizmet başlangıcı"), JobKind.Service, services, serviceAfterDelivery && products.Count > 0 ? "kind:Product" : null));
                }

                return jobs;
        }
    }
}

/// <summary>Ürün/hizmet türüne göre görev şablonları (varsayılan görev ve kontrol listeleri).</summary>
public static class OperationTemplates
{
    public static readonly IReadOnlyList<string> Delivery =
    [
        "Teslimat planı hazırlandı ve tarih netleşti",
        "Ürünler hazırlandı ve sevke uygun",
        "Teslimat müşteriyle teyit edildi",
        "Teslimat yapıldı, teslim alan kaydedildi",
        "Teslim kanıtı (belge ya da tutanak) alındı",
    ];

    public static readonly IReadOnlyList<string> Service =
    [
        "Hizmet ön koşulları kontrol edildi",
        "Başlangıç toplantısı müşteriyle planlandı",
        "Ekip ve kaynaklar atandı",
        "Hizmet başlatıldı",
        "Başlangıç müşteriye bildirildi",
    ];

    public static IReadOnlyList<string> For(string kind) => kind switch
    {
        JobKind.Delivery => Delivery,
        JobKind.Service => Service,
        JobKind.Mixed => [.. Delivery, .. Service],
        _ => ["İş kapsamı gözden geçirildi", "İş tamamlandı ve sonuç kaydedildi"],
    };
}

/// <summary>İş eylemi için gereken durum bilgisi (sunucu ve arayüz aynı kuralı kullanır).</summary>
public readonly record struct JobState(string Status, bool HasAssignee, int OpenDependencies, int RequiredTasksLeft, string OrderStatus);

/// <summary>
/// İş geçiş tablosu (saf kural). Atama bekliyor/Planlandı → Devam ediyor: <c>start</c> (sorumlu atanmış, bağımlı işler tamamlanmış, sipariş onaylı);
/// Devam ediyor → Tamamlandı: <c>complete</c> (zorunlu görevler tamam); açık iş → İptal: <c>cancel</c> (gerekçeli); atama: açık işte sorumlu/ekip atanır.
/// </summary>
public static class JobTransitions
{
    public static OrderRuleResult Can(string action, JobState s)
    {
        switch (action)
        {
            case JobAction.Assign:
                return JobStatus.IsOpen(s.Status) ? OrderRuleResult.Ok : OrderRuleResult.Deny("final", "Tamamlanmış ya da iptal edilmiş işe atama yapılamaz.");

            case JobAction.Start:
                if (s.Status is not (JobStatus.AwaitingAssignment or JobStatus.Planned))
                {
                    return OrderRuleResult.Deny("not-startable", s.Status == JobStatus.InProgress ? "İş zaten devam ediyor." : "Tamamlanmış ya da iptal edilmiş iş başlatılamaz.");
                }

                if (s.OrderStatus != OrderStatus.Approved)
                {
                    return OrderRuleResult.Deny("order-not-approved", "Sipariş onaylı olmadığı için iş başlatılamaz.");
                }

                if (!s.HasAssignee)
                {
                    return OrderRuleResult.Deny("unassigned", "Önce sorumlu kullanıcı ya da ekip atanmalı.");
                }

                return s.OpenDependencies > 0
                    ? OrderRuleResult.Deny("dependency", $"Bağımlı olduğu {s.OpenDependencies} iş tamamlanmadan başlatılamaz.")
                    : OrderRuleResult.Ok;

            case JobAction.Complete:
                if (s.Status != JobStatus.InProgress)
                {
                    return OrderRuleResult.Deny("not-in-progress", "Yalnızca devam eden iş tamamlanabilir.");
                }

                return s.RequiredTasksLeft > 0
                    ? OrderRuleResult.Deny("tasks", $"{s.RequiredTasksLeft} zorunlu görev tamamlanmadan iş tamamlanamaz.")
                    : OrderRuleResult.Ok;

            case JobAction.Cancel:
                return JobStatus.IsOpen(s.Status) ? OrderRuleResult.Ok : OrderRuleResult.Deny("final", "Tamamlanmış ya da zaten iptal edilmiş iş iptal edilemez.");

            default:
                return OrderRuleResult.Deny("unknown", "Bilinmeyen eylem.");
        }
    }

    public static bool RequiresReason(string action) => action == JobAction.Cancel;

    /// <summary>İşin atama durumuna göre başlangıç durumu: sorumlu/ekip yoksa "Atama bekliyor".</summary>
    public static string InitialStatus(bool hasAssignee) => hasAssignee ? JobStatus.Planned : JobStatus.AwaitingAssignment;

    /// <summary>Atama sonrası durum: bekleyen iş atanınca Planlandı olur; diğer durumlar korunur.</summary>
    public static string StatusAfterAssign(string status, bool hasAssignee) =>
        status == JobStatus.AwaitingAssignment && hasAssignee ? JobStatus.Planned : status == JobStatus.Planned && !hasAssignee ? JobStatus.AwaitingAssignment : status;
}

/// <summary>Bağımlılık kuralları: kendine ve döngüye bağ kurulamaz.</summary>
public static class JobDependencyRules
{
    /// <summary>
    /// <paramref name="jobId"/> işi <paramref name="dependsOn"/> işine bağlanırsa döngü oluşur mu? <paramref name="edges"/>: (iş, bağımlı olduğu iş) çiftleri.
    /// </summary>
    public static bool WouldCycle(IEnumerable<(Guid Job, Guid DependsOn)> edges, Guid jobId, Guid dependsOn)
    {
        if (jobId == dependsOn)
        {
            return true;
        }

        var list = edges.ToList();
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(dependsOn);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == jobId)
            {
                return true;
            }

            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var (job, next) in list)
            {
                if (job == current)
                {
                    stack.Push(next);
                }
            }
        }

        return false;
    }
}
