using System.Globalization;

namespace Modulix.Modules.Crm.Quotes;

/// <summary>Değerlendirmeye giren kural (kayıttan okunur; saf değerlendirici veritabanını bilmez).</summary>
public sealed record QuoteApprovalRuleInfo(
    Guid Id, long Version, string Name, int Priority, string? Currency, decimal? MinDiscountPercent, decimal? MinGrossTotal, Guid? ApproverId, int Step = 1);

/// <summary>Teklifle eşleşen kural ve eşleşme gerekçesi. Kural sürümü onaya gönderim tarihçesine yazılır.</summary>
public sealed record QuoteApprovalMatch(Guid RuleId, long RuleVersion, string Name, string Reason, Guid? ApproverId, int Step = 1);

/// <summary>Bir onay adımı: adlı onaylayanlar (boşsa onay izni olan herkes), tamamlanma durumu ve onaylayanlar (vekil onayı adına onayladığı kişiyle sayılır).</summary>
public sealed record QuoteApprovalStep(int Step, IReadOnlyList<Guid> Approvers, bool Completed, IReadOnlyList<Guid> ApprovedBy);

/// <summary>Verilmiş bir adım onayı: kim verdi ve kimler adına sayılır (kendisi + o an vekili olduğu kişiler).</summary>
public sealed record QuoteApprovalGiven(Guid Actor, IReadOnlySet<Guid> OnBehalfOf);

/// <summary>
/// Bir teklifin iç onay gereksinimi: hangi kurallar (ve genel eşik ayarı) onayı gerektiriyor, adımlar ve ilerleme, geçerli kullanıcı şu anki adımda
/// karar verebilir mi. <c>Required</c> doğruysa teklif onay alınmadan sunulamaz. <c>CurrentStep</c> 0 ise bütün adımlar tamamlanmıştır.
/// </summary>
public sealed record QuoteApprovalRequirement(
    bool Required,
    IReadOnlyList<QuoteApprovalMatch> Matches,
    string? ThresholdReason,
    bool Restricted,
    bool CanDecide,
    string? Note,
    IReadOnlyList<QuoteApprovalStep> Steps,
    int CurrentStep,
    bool ActingAsDelegate);

/// <summary>
/// Onay kuralı değerlendirmesi (saf). Bir kuralın girilmiş koşulları birlikte sağlanmalıdır (VE); kurallar arasında herhangi biri yeter (VEYA).
/// Tutar koşulu para birimi belirtilmişse yalnızca o para biriminde uygulanır. Eşikler "aşarsa" anlamındadır (büyüktür).
/// <para>
/// Çok adımlı onay: eşleşen kurallar adım numarasına göre gruplanır ve adımlar sırayla tamamlanır. Bir adımda adlı onaylayan varsa hepsinin
/// (ya da vekillerinin) onayı gerekir; yoksa onay izni olan herhangi birinin tek onayı yeter. Genel eşik ayarı tek başına tetiklenmişse tek,
/// adsız bir adım vardır.
/// </para>
/// </summary>
public static class QuoteApprovalEvaluator
{
    private static readonly NumberFormatInfo Numbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", NumberGroupSizes = [3] };

    public static IReadOnlyList<QuoteApprovalMatch> Match(
        IEnumerable<QuoteApprovalRuleInfo> rules, string currency, decimal listTotal, decimal discountTotal, decimal grossTotal)
    {
        var percent = DiscountPercent(listTotal, discountTotal);
        var matches = new List<QuoteApprovalMatch>();
        foreach (var rule in rules.OrderBy(r => r.Step).ThenBy(r => r.Priority).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            var reasons = new List<string>();
            var matched = true;
            if (rule.MinDiscountPercent is { } minPercent)
            {
                if (percent > minPercent)
                {
                    reasons.Add($"İndirim %{percent.ToString("0.##", Numbers)} (eşik %{minPercent.ToString("0.##", Numbers)})");
                }
                else
                {
                    matched = false;
                }
            }

            if (matched && rule.MinGrossTotal is { } minGross)
            {
                var sameCurrency = rule.Currency is null || string.Equals(rule.Currency, currency, StringComparison.Ordinal);
                if (sameCurrency && grossTotal > minGross)
                {
                    reasons.Add($"Genel toplam {grossTotal.ToString("N2", Numbers)} {currency} (eşik {minGross.ToString("N2", Numbers)})");
                }
                else
                {
                    matched = false;
                }
            }

            if (matched && reasons.Count > 0)
            {
                matches.Add(new QuoteApprovalMatch(rule.Id, rule.Version, rule.Name, string.Join("; ", reasons), rule.ApproverId, Math.Clamp(rule.Step, 1, 9)));
            }
        }

        return matches;
    }

    /// <summary>Toplam indirimin liste toplamına oranı (yüzde); liste toplamı sıfırsa 0.</summary>
    public static decimal DiscountPercent(decimal listTotal, decimal discountTotal) => listTotal > 0 ? discountTotal * 100m / listTotal : 0m;

    /// <summary>Tüm adımlardaki adlı onaylayanlar (boş küme = herkes). Geriye dönük uyumluluk için korunur.</summary>
    public static IReadOnlySet<Guid> RestrictedApprovers(IEnumerable<QuoteApprovalMatch> matches) =>
        matches.Where(m => m.ApproverId is not null).Select(m => m.ApproverId!.Value).ToHashSet();

    /// <summary>
    /// Adımları ve ilerlemeyi hesaplar. Onaylar zaman sırasıyla, o anki açık adıma sayılır: adlı adımda yalnızca adı geçen (ya da adına vekâlet edilen)
    /// kişiler ilerletir; adsız adımı herhangi bir onay tamamlar. Eşleşme yoksa (yalnızca genel eşik) tek adsız adım vardır.
    /// </summary>
    public static IReadOnlyList<QuoteApprovalStep> Steps(IEnumerable<QuoteApprovalMatch> matches, IEnumerable<QuoteApprovalGiven> approvals)
    {
        var groups = matches.GroupBy(m => m.Step).OrderBy(g => g.Key)
            .Select(g => (Step: g.Key, Approvers: (IReadOnlyList<Guid>)[.. g.Where(m => m.ApproverId is not null).Select(m => m.ApproverId!.Value).Distinct()]))
            .ToList();
        if (groups.Count == 0)
        {
            groups.Add((1, []));
        }

        var result = new List<QuoteApprovalStep>();
        var approvedBy = new List<HashSet<Guid>>();
        foreach (var _ in groups)
        {
            approvedBy.Add([]);
        }

        var index = 0;
        foreach (var approval in approvals)
        {
            if (index >= groups.Count)
            {
                break;
            }

            var (_, approvers) = groups[index];
            if (approvers.Count == 0)
            {
                approvedBy[index].Add(approval.Actor);
            }
            else
            {
                foreach (var principal in approval.OnBehalfOf.Where(approvers.Contains))
                {
                    approvedBy[index].Add(principal);
                }
            }

            if (IsComplete(approvers, approvedBy[index]))
            {
                index++;
            }
        }

        for (var i = 0; i < groups.Count; i++)
        {
            result.Add(new QuoteApprovalStep(groups[i].Step, groups[i].Approvers, IsComplete(groups[i].Approvers, approvedBy[i]), [.. approvedBy[i]]));
        }

        return result;
    }

    /// <summary>Şu anki (ilk tamamlanmamış) adım; hepsi tamamsa null.</summary>
    public static QuoteApprovalStep? Current(IReadOnlyList<QuoteApprovalStep> steps) => steps.FirstOrDefault(s => !s.Completed);

    /// <summary>Kullanıcı şu anki adımda karar verebilir mi? <paramref name="onBehalfOf"/>: kendisi + vekili olduğu kişiler.</summary>
    public static bool CanDecide(QuoteApprovalStep? current, IReadOnlySet<Guid> onBehalfOf) =>
        current is null || current.Approvers.Count == 0 || current.Approvers.Any(onBehalfOf.Contains);

    /// <summary>Bu onay verilirse adımların tamamı tamamlanır mı (yani teklif onaylanmış sayılır)?</summary>
    public static bool CompletesAll(IEnumerable<QuoteApprovalMatch> matches, IEnumerable<QuoteApprovalGiven> approvals, QuoteApprovalGiven next) =>
        Steps(matches, approvals.Append(next)).All(s => s.Completed);

    public static string HistoryNote(IEnumerable<QuoteApprovalMatch> matches, string? thresholdReason)
    {
        var list = matches.ToList();
        var parts = list.Select(m => $"{m.Name} (sürüm {m.RuleVersion}{(list.Any(x => x.Step != m.Step) ? $", adım {m.Step}" : string.Empty)}): {m.Reason}").ToList();
        if (thresholdReason is not null)
        {
            parts.Add(thresholdReason);
        }

        return parts.Count == 0 ? "Kural eşleşmesi yok (gönüllü onay)." : $"Onay gerekçesi — {string.Join(" | ", parts)}";
    }

    private static bool IsComplete(IReadOnlyList<Guid> approvers, HashSet<Guid> approvedBy) =>
        approvers.Count == 0 ? approvedBy.Count > 0 : approvers.All(approvedBy.Contains);
}
