using System.Globalization;

namespace Modulix.Modules.Crm.Quotes;

/// <summary>Değerlendirmeye giren kural (kayıttan okunur; saf değerlendirici veritabanını bilmez).</summary>
public sealed record QuoteApprovalRuleInfo(
    Guid Id, long Version, string Name, int Priority, string? Currency, decimal? MinDiscountPercent, decimal? MinGrossTotal, Guid? ApproverId);

/// <summary>Teklifle eşleşen kural ve eşleşme gerekçesi. Kural sürümü onaya gönderim tarihçesine yazılır.</summary>
public sealed record QuoteApprovalMatch(Guid RuleId, long RuleVersion, string Name, string Reason, Guid? ApproverId);

/// <summary>
/// Bir teklifin iç onay gereksinimi: hangi kurallar (ve genel eşik ayarı) onayı gerektiriyor, onay belirli onaylayanlara bağlı mı, geçerli
/// kullanıcı karar verebilir mi. <c>Required</c> doğruysa teklif onay alınmadan sunulamaz.
/// </summary>
public sealed record QuoteApprovalRequirement(
    bool Required, IReadOnlyList<QuoteApprovalMatch> Matches, string? ThresholdReason, bool Restricted, bool CanDecide, string? Note);

/// <summary>
/// Onay kuralı değerlendirmesi (saf). Bir kuralın girilmiş koşulları birlikte sağlanmalıdır (VE); kurallar arasında herhangi biri yeter (VEYA).
/// Tutar koşulu para birimi belirtilmişse yalnızca o para biriminde uygulanır. Eşikler "aşarsa" anlamındadır (büyüktür).
/// </summary>
public static class QuoteApprovalEvaluator
{
    private static readonly NumberFormatInfo Numbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", NumberGroupSizes = [3] };

    public static IReadOnlyList<QuoteApprovalMatch> Match(
        IEnumerable<QuoteApprovalRuleInfo> rules, string currency, decimal listTotal, decimal discountTotal, decimal grossTotal)
    {
        var percent = DiscountPercent(listTotal, discountTotal);
        var matches = new List<QuoteApprovalMatch>();
        foreach (var rule in rules.OrderBy(r => r.Priority).ThenBy(r => r.Name, StringComparer.Ordinal))
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
                matches.Add(new QuoteApprovalMatch(rule.Id, rule.Version, rule.Name, string.Join("; ", reasons), rule.ApproverId));
            }
        }

        return matches;
    }

    /// <summary>Toplam indirimin liste toplamına oranı (yüzde); liste toplamı sıfırsa 0.</summary>
    public static decimal DiscountPercent(decimal listTotal, decimal discountTotal) => listTotal > 0 ? discountTotal * 100m / listTotal : 0m;

    /// <summary>
    /// Karar verebilecek onaylayanlar: eşleşen kurallardan biri belirli onaylayan atamışsa yalnızca atananlar (boş küme = herkes).
    /// </summary>
    public static IReadOnlySet<Guid> RestrictedApprovers(IEnumerable<QuoteApprovalMatch> matches) =>
        matches.Where(m => m.ApproverId is not null).Select(m => m.ApproverId!.Value).ToHashSet();

    public static string HistoryNote(IEnumerable<QuoteApprovalMatch> matches, string? thresholdReason)
    {
        var parts = matches.Select(m => $"{m.Name} (sürüm {m.RuleVersion}): {m.Reason}").ToList();
        if (thresholdReason is not null)
        {
            parts.Add(thresholdReason);
        }

        return parts.Count == 0 ? "Kural eşleşmesi yok (gönüllü onay)." : $"Onay gerekçesi — {string.Join(" | ", parts)}";
    }
}
