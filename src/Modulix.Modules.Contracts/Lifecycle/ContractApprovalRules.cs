using System.Text.Json;
using Modulix.Core.Contracts.Approvals;

namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>Değerlendirmeye giren kural (kayıttan okunur; saf değerlendirici veritabanını bilmez).</summary>
public sealed record ContractApprovalRuleInfo(
    Guid Id, long Version, string Name, int Priority, int Step, string? ContractType, string? Currency, decimal? MinGrossTotal, decimal? MinDiscountPercent,
    int? MinTermMonths, bool RequiresNonStandardTerms, string? MinRiskLevel, Guid? ApproverId);

/// <summary>
/// Sözleşmenin kural değerlendirmesine giren özeti. <see cref="TermMonths"/> belirsiz süreli sözleşmede (ya da tarihleri eksikse) null'dır;
/// belirsiz süreli sözleşme süre koşulunu sağlar (sonsuz süre en uzun süredir), tarihleri eksik sözleşme sağlamaz (zaten onaya gönderilemez).
/// </summary>
public sealed record ContractApprovalFacts(
    string Type, string Currency, decimal GrossTotal, decimal MaxLineDiscountPercent, int? TermMonths, bool IsIndefinite, bool HasNonStandardTerms, string RiskLevel);

/// <summary>Sözleşmeyle eşleşen kural. Kural adı ve sürümü plana yazılır; <see cref="Reason"/> tutar içermez (finansal izni olmayana da gösterilebilir).</summary>
public sealed record ContractApprovalMatch(Guid RuleId, long RuleVersion, string Name, int Step, Guid? ApproverId, string Reason);

/// <summary>Onaya gönderimde dondurulan plan: eşleşen kurallar ve genel not. Eşleşme yoksa tek, adsız adım vardır (onay izni olan herkes onaylar).</summary>
public sealed record ContractApprovalPlan(IReadOnlyList<ContractApprovalMatch> Matches, string? Note = null)
{
    public IEnumerable<ApprovalStepSpec> Specs() => Matches.Select(m => new ApprovalStepSpec(m.Step, m.ApproverId));
}

/// <summary>
/// Sözleşme onay kuralı değerlendirmesi (saf). Bir kuralın girilmiş koşulları birlikte sağlanmalıdır (VE); kurallar arasında herhangi biri
/// yeter (VEYA). Eşikler "aşarsa" anlamındadır (büyüktür). Tür ve para birimi yalnızca daraltır; en az bir ölçüt (tutar, indirim, süre,
/// standart dışı madde, risk) zorunludur. Adım hesabı CRM ile ortaktır (<see cref="ApprovalFlow"/>).
/// </summary>
public static class ContractApprovalEvaluator
{
    public static IReadOnlyList<ContractApprovalMatch> Match(IEnumerable<ContractApprovalRuleInfo> rules, ContractApprovalFacts facts)
    {
        var matches = new List<ContractApprovalMatch>();
        foreach (var rule in rules.OrderBy(r => r.Step).ThenBy(r => r.Priority).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            if (rule.ContractType is { Length: > 0 } type && !string.Equals(type, facts.Type, StringComparison.Ordinal))
            {
                continue;
            }

            var reasons = new List<string>();
            var matched = true;

            if (rule.MinGrossTotal is { } minGross)
            {
                var sameCurrency = rule.Currency is null || string.Equals(rule.Currency, facts.Currency, StringComparison.Ordinal);
                if (sameCurrency && facts.GrossTotal > minGross)
                {
                    reasons.Add("Sözleşme toplamı kuraldaki tutar eşiğini aşıyor");
                }
                else
                {
                    matched = false;
                }
            }

            if (matched && rule.MinDiscountPercent is { } minDiscount)
            {
                if (facts.MaxLineDiscountPercent > minDiscount)
                {
                    reasons.Add("Kalem indirimi kuraldaki eşiği aşıyor");
                }
                else
                {
                    matched = false;
                }
            }

            if (matched && rule.MinTermMonths is { } minTerm)
            {
                if (facts.IsIndefinite)
                {
                    reasons.Add("Belirsiz süreli sözleşme");
                }
                else if (facts.TermMonths is { } term && term > minTerm)
                {
                    reasons.Add($"Süre {term} ay (eşik {minTerm})");
                }
                else
                {
                    matched = false;
                }
            }

            if (matched && rule.RequiresNonStandardTerms)
            {
                if (facts.HasNonStandardTerms)
                {
                    reasons.Add("Standart dışı madde işaretli");
                }
                else
                {
                    matched = false;
                }
            }

            if (matched && rule.MinRiskLevel is { Length: > 0 } minRisk)
            {
                if (RiskLevel.Rank(facts.RiskLevel) >= RiskLevel.Rank(minRisk))
                {
                    reasons.Add($"Risk düzeyi en az {RiskLabel(minRisk)}");
                }
                else
                {
                    matched = false;
                }
            }

            if (matched && reasons.Count > 0)
            {
                matches.Add(new ContractApprovalMatch(rule.Id, rule.Version, rule.Name, Math.Clamp(rule.Step, 1, 9), rule.ApproverId, string.Join("; ", reasons)));
            }
        }

        return matches;
    }

    /// <summary>Kuralın en az bir ölçütü var mı? (Yalnızca tür/para birimi kural değildir.)</summary>
    public static bool HasCriterion(decimal? minGross, decimal? minDiscount, int? minTermMonths, bool requiresNonStandard, string? minRisk) =>
        minGross is not null || minDiscount is not null || minTermMonths is not null || requiresNonStandard || !string.IsNullOrEmpty(minRisk);

    /// <summary>
    /// Takvim ayı olarak süre: başlangıçtan bitişe (dahil) kaç ay; kısmi ay tam sayılır (1 Kasım–31 Ekim = 12, 1 Kasım–1 Kasım ertesi yıl = 13).
    /// Belirsiz süreli ya da tarihi eksik/tutarsız sözleşmede null.
    /// </summary>
    public static int? TermMonths(DateOnly? start, DateOnly? end, bool isIndefinite)
    {
        if (isIndefinite || start is not { } s || end is not { } e || e < s)
        {
            return null;
        }

        for (var months = 1; months <= 1200; months++)
        {
            if (ContractPeriod.TermEnd(s, months) >= e)
            {
                return months;
            }
        }

        return 1200;
    }

    /// <summary>Onaya gönderimde eşleşmeyi geçmişe yazan not (tarihçe/plan).</summary>
    public static string Note(IReadOnlyList<ContractApprovalMatch> matches)
    {
        if (matches.Count == 0)
        {
            return "Kural eşleşmesi yok (gönüllü onay): onay izni olan bir kişi onaylar.";
        }

        var multiStep = matches.Select(m => m.Step).Distinct().Count() > 1;
        return "Onay gerekçesi — " + string.Join(" | ", matches.Select(m => $"{m.Name} (sürüm {m.RuleVersion}{(multiStep ? $", adım {m.Step}" : string.Empty)}): {m.Reason}"));
    }

    public static string RiskLabel(string level) => level switch { RiskLevel.High => "Yüksek", RiskLevel.Medium => "Orta", _ => "Düşük" };
}

public static class ContractApprovalPlans
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(ContractApprovalPlan plan) => JsonSerializer.Serialize(plan, Json);

    /// <summary>Plan yoksa ya da bozuksa (eski sürüm) eşleşmesiz plan: tek adsız adım. Hata fırlatmaz.</summary>
    public static ContractApprovalPlan Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ContractApprovalPlan([]);
        }

        try
        {
            return JsonSerializer.Deserialize<ContractApprovalPlan>(json, Json) ?? new ContractApprovalPlan([]);
        }
        catch (JsonException)
        {
            return new ContractApprovalPlan([]);
        }
    }
}
