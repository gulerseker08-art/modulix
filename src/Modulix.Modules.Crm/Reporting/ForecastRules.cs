namespace Modulix.Modules.Crm.Reporting;

/// <summary>Tahmin kategorileri birbirini dışlar. "Kazanıldı" kategori değil gerçek sonuçtur (gerçekleşen satış).</summary>
public static class ForecastCategory
{
    public const string Omitted = "Omitted";
    public const string Pipeline = "Pipeline";
    public const string BestCase = "BestCase";
    public const string Commit = "Commit";

    public static IReadOnlyList<string> All { get; } = [Omitted, Pipeline, BestCase, Commit];
}

public static class ForecastStatus
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Locked = "Locked";

    public static IReadOnlyList<string> All { get; } = [Draft, Submitted, Locked];
}

public static class ForecastAdjustmentKind
{
    public const string Category = "Category";
    public const string Amount = "Amount";
    public const string Probability = "Probability";

    public static IReadOnlyList<string> All { get; } = [Category, Amount, Probability];
}

public static class TargetMetric
{
    /// <summary>Kazanılan satış tutarı (para birimli).</summary>
    public const string WonAmount = "WonAmount";

    /// <summary>Kazanılan fırsat adedi. Tutar hedefiyle karıştırılmaz.</summary>
    public const string WonCount = "WonCount";

    public static IReadOnlyList<string> All { get; } = [WonAmount, WonCount];
}

public static class TargetScope
{
    public const string Tenant = "Tenant";
    public const string User = "User";

    public static IReadOnlyList<string> All { get; } = [Tenant, User];
}

/// <summary>Kategori eşikleri (tenant ayarı): olasılık bu eşiklerden büyük ya da eşitse kategori varsayılanı Taahhüt / En İyi Durum olur.</summary>
public sealed record ForecastPolicy(int CommitMinProbability = 80, int BestCaseMinProbability = 50)
{
    public static ForecastPolicy Default { get; } = new();
}

/// <summary>
/// Tahmin iş kuralları (saf işlevler). Ağırlıklı tahmin kategorilerden ayrı bir yöntemdir; kategoriler ile olasılık toplamları üst üste eklenmez.
/// </summary>
public static class ForecastRules
{
    public const int ReasonMaxLength = 1000;

    /// <summary>
    /// Varsayılan kategori: olasılık yoksa Pipeline (veri eksiği olarak da bildirilir), 0 ise Hariç, eşiklere göre Taahhüt / En İyi Durum, aksi halde Pipeline.
    /// </summary>
    public static string DefaultCategory(int? probability, ForecastPolicy policy) => probability switch
    {
        null => ForecastCategory.Pipeline,
        0 => ForecastCategory.Omitted,
        { } p when p >= policy.CommitMinProbability => ForecastCategory.Commit,
        { } p when p >= policy.BestCaseMinProbability => ForecastCategory.BestCase,
        _ => ForecastCategory.Pipeline,
    };

    /// <summary>İzinli durum geçişleri. Kilitli tahmin değişmez: yeniden açma yeni sürüm üretir (bu tablodan değil, <c>Reopen</c> eyleminden).</summary>
    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        (ForecastStatus.Draft, ForecastStatus.Submitted) => true,
        (ForecastStatus.Submitted, ForecastStatus.Draft) => true,
        (ForecastStatus.Submitted, ForecastStatus.Locked) => true,
        _ => false,
    };

    /// <summary>Nihai tutar = temel tutar + düzeltme; düzeltme yoksa temel tutar. Kaynak fırsatın tutarı değişmez.</summary>
    public static decimal? FinalAmount(decimal? baseAmount, decimal? adjustment) =>
        baseAmount is null ? null : baseAmount + (adjustment ?? 0m);

    /// <summary>Etkin olasılık: yönetici düzeltmesi varsa o, yoksa fırsatın olasılığı.</summary>
    public static int? EffectiveProbability(int? probability, int? probabilityOverride) => probabilityOverride ?? probability;

    public static decimal Weighted(decimal finalAmount, int? effectiveProbability) =>
        effectiveProbability is { } p ? finalAmount * p / 100m : 0m;

    /// <summary>
    /// Senaryolar: <b>Taahhüt</b> = gerçekleşen + açık taahhüt; <b>İyimser</b> = gerçekleşen + taahhüt + en iyi durum; <b>Ağırlıklı</b> = gerçekleşen + Σ(nihai tutar × etkin olasılık).
    /// Hariç kategori hiçbir senaryoya girmez; kategori ve ağırlıklı tahmin birbirinin üstüne eklenmez.
    /// </summary>
    public static ForecastScenarios Scenarios(decimal realized, IEnumerable<ForecastLine> lines)
    {
        decimal commit = 0, bestCase = 0, pipeline = 0, weighted = 0;
        foreach (var line in lines)
        {
            if (line.FinalAmount is not { } amount)
            {
                continue;
            }

            switch (line.Category)
            {
                case ForecastCategory.Commit:
                    commit += amount;
                    break;
                case ForecastCategory.BestCase:
                    bestCase += amount;
                    break;
                case ForecastCategory.Pipeline:
                    pipeline += amount;
                    break;
            }

            if (line.Category != ForecastCategory.Omitted)
            {
                weighted += Weighted(amount, line.EffectiveProbability);
            }
        }

        return new ForecastScenarios(realized, commit, bestCase, pipeline, weighted, realized + commit, realized + commit + bestCase, realized + weighted);
    }

    public static string? ValidateReason(string? reason)
    {
        var text = reason?.Trim();
        return string.IsNullOrEmpty(text) ? "Gerekçe zorunludur."
            : text.Length > ReasonMaxLength ? $"Gerekçe en fazla {ReasonMaxLength} karakter olabilir."
            : null;
    }
}

/// <summary>Tahmin satırının hesap için gereken alanları (kategori, nihai tutar, etkin olasılık).</summary>
public sealed record ForecastLine(string Category, decimal? FinalAmount, int? EffectiveProbability);

/// <summary>Kategori toplamları ve kümülatif senaryolar. <see cref="Realized"/> kazanılmış gerçek satıştır.</summary>
public sealed record ForecastScenarios(
    decimal Realized, decimal Commit, decimal BestCase, decimal Pipeline, decimal WeightedOpen,
    decimal CommitScenario, decimal OptimisticScenario, decimal WeightedScenario);

/// <summary>Bir hedef kaydının hesap için gereken alanları.</summary>
public sealed record TargetSlice(DateOnly Start, DateOnly EndExclusive, decimal Value);

/// <summary>Hedef çözümleme sonucu: dönemi tam kaplayan hedef toplamı ya da neden bulunamadığı.</summary>
public sealed record ResolvedTarget(decimal? Value, string? Problem);

public static class TargetRules
{
    /// <summary>
    /// Bir kapsam (tenant ya da kullanıcı) için dönemin hedefi: dönemi <b>tam ve boşluksuz</b> kaplayan hedef dilimlerinin toplamı. Aynı kapsamda
    /// dilimler çakışamayacağından (kayıt anında engellenir) toplam mükerrer olmaz. Boşluk varsa hedef eksiktir; kısmi toplam hedef diye sunulmaz.
    /// </summary>
    public static ResolvedTarget Resolve(IEnumerable<TargetSlice> slices, ReportPeriod period)
    {
        var inside = slices.Where(s => s.Start >= period.Start && s.EndExclusive <= period.EndExclusive).OrderBy(s => s.Start).ToList();
        if (inside.Count == 0)
        {
            return new ResolvedTarget(null, null);
        }

        var cursor = period.Start;
        foreach (var slice in inside)
        {
            if (slice.Start != cursor)
            {
                return new ResolvedTarget(null, "Dönem için tanımlı hedefler dönemi tam kaplamıyor (boşluk ya da çakışma var).");
            }

            cursor = slice.EndExclusive;
        }

        return cursor == period.EndExclusive
            ? new ResolvedTarget(inside.Sum(s => s.Value), null)
            : new ResolvedTarget(null, "Dönem için tanımlı hedefler dönemin sonuna kadar uzanmıyor.");
    }

    /// <summary>İki aralık [başlangıç, bitiş) çakışıyor mu?</summary>
    public static bool Overlaps(DateOnly startA, DateOnly endA, DateOnly startB, DateOnly endB) => startA < endB && startB < endA;

    /// <summary>Alt hedeflerin toplamı üst hedefi aşıyorsa aşım tutarı; aksi halde null. Üst ve alt hedefler birlikte toplanmaz.</summary>
    public static decimal? ChildExcess(decimal parent, decimal childrenTotal) => childrenTotal > parent ? childrenTotal - parent : null;
}
