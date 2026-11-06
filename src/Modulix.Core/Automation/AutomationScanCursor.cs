using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Automation;

/// <summary>
/// Bir kural sürümünün bir tenant gününde tarama ilerlemesi (ADR-0027, Dilim 2b). Tarama sayfa sayfa ilerler ve tur sınırları nedeniyle
/// birden çok turda sürebilir; <see cref="AfterRecordId"/> kalınan yeri tutar. Tenant günü (<see cref="PeriodKey"/>) bittikten sonra aynı
/// sürüm için ertesi gün yeni bir imleç açılır. Kayıt değeri saklanmaz.
/// </summary>
public sealed class AutomationScanCursor : ITenantEntity, ICompanyShared
{
    public const int PeriodKeyLength = 20;

    private AutomationScanCursor()
    {
        // EF Core için
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RuleId { get; private set; }

    public Guid RuleVersionId { get; private set; }

    /// <summary>Tenant takvim günü ("yyyy-MM-dd"); tekilleştirme anahtarının dönem parçasıdır.</summary>
    public string PeriodKey { get; private set; } = string.Empty;

    /// <summary>Son işlenen kayıt kimliği; ilk sayfada boş.</summary>
    public Guid? AfterRecordId { get; private set; }

    /// <summary>Bu dönemde kuyruğa alınan çalışma sayısı.</summary>
    public int RecordsEnqueued { get; private set; }

    /// <summary>Kayıt kaynağı son sayfaya ulaştı; bu dönem için tarama bitti.</summary>
    public bool Completed { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static string PeriodOf(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Çalışmaların tekilleştirme anahtarının "tetikleyici" parçası: aynı gün ikinci tarama aynı kayıtta ikinci çalışma açmaz.</summary>
    public static string DedupeTrigger(string periodKey) => "scan:" + periodKey;

    public static AutomationScanCursor Start(Guid tenantId, Guid ruleId, Guid ruleVersionId, string periodKey, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            RuleId = ruleId,
            RuleVersionId = ruleVersionId,
            PeriodKey = periodKey,
            StartedAt = now,
            UpdatedAt = now,
        };

    /// <summary>Bir sayfa işlendi; <paramref name="last"/> sayfanın son kayıt kimliği, <paramref name="finished"/> kaynak bitti mi.</summary>
    public void Advance(Guid? last, int enqueued, bool finished, DateTimeOffset now)
    {
        AfterRecordId = last ?? AfterRecordId;
        RecordsEnqueued += enqueued;
        Completed = finished;
        UpdatedAt = now;
    }
}

/// <summary>Taramanın ne zaman yapılacağı ve kaynağa nasıl sorulacağı (saf mantık; veritabanı ve modül bilmez).</summary>
public static class AutomationScanPlan
{
    /// <summary>
    /// Bugünkü tarama zamanı geldi mi? <see cref="ScheduledTrigger"/> tenant saatinde "SS:dd" anından sonra; tarihe bağlı ve hareketsizlik
    /// taramaları günün ilk turunda. Kaçırılan günler geriye dönük taranmaz (tek tenant günü, tek tarama).
    /// </summary>
    public static bool IsDue(AutomationTrigger trigger, TenantDay day)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(day);

        if (trigger is not ScheduledTrigger scheduled)
        {
            return AutomationTriggerMatcher.IsScan(trigger);
        }

        if (!TimeOnly.TryParseExact(scheduled.TimeOfDay, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var at))
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTime(day.UtcNow, day.Zone);
        return TimeOnly.FromDateTime(local.DateTime) >= at;
    }

    /// <summary>
    /// Tetikleyiciye göre kaynağa sorulacak sayfa isteği. <see cref="DateRelativeTrigger"/>: "tarihten <c>OffsetDays</c> gün sonra" bugün olması
    /// için tarih alanı bugün − OffsetDays gününe düşmelidir; negatif fark (tarihe N gün kala) hedef günü ileri alır.
    /// </summary>
    public static AutomationScanQuery? QueryFor(
        AutomationTrigger trigger, string entityKey, TenantDay day, Guid? after, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(day);

        return trigger switch
        {
            ScheduledTrigger => new AutomationScanQuery(AutomationScanKind.Scheduled, entityKey, null, null, null, null, after, pageSize),
            DateRelativeTrigger relative => new AutomationScanQuery(
                AutomationScanKind.DateRelative, entityKey, FieldName(entityKey, relative.Field), day.Today.AddDays(-relative.OffsetDays),
                null, null, after, pageSize),
            InactivityTrigger inactivity => new AutomationScanQuery(
                AutomationScanKind.Inactivity, entityKey, null, null, inactivity.Relation, day.StartOfDay(day.Today).AddDays(-inactivity.Days),
                after, pageSize),
            _ => null,
        };
    }

    // Alan anahtarı "kayıt türü.ad"; kayıt kaynağı alanın kayıt değerlerindeki adını (Name) bekler.
    private static string FieldName(string entityKey, string fieldKey) =>
        fieldKey.StartsWith(entityKey + ".", StringComparison.Ordinal) ? fieldKey[(entityKey.Length + 1)..] : fieldKey;
}
