using System.Text.Json;
using System.Text.RegularExpressions;

namespace Modulix.Modules.Support.Sla;

/// <summary>SLA ölçütü: ilk insan yanıtı ve çözüm ayrı izlenir.</summary>
public static class SlaMetric
{
    public const string FirstResponse = "FirstResponse";
    public const string Resolution = "Resolution";

    public static readonly IReadOnlyList<string> All = [FirstResponse, Resolution];
}

/// <summary>SLA örneği durumu. Sonuç (karşılandı/kaçırıldı) durumdan değil <c>BreachedAt</c>'ten okunur.</summary>
public static class SlaStatus
{
    public const string Running = "Running";
    public const string Paused = "Paused";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All = [Running, Paused, Completed, Cancelled];
}

/// <summary>Eskalasyon eylemleri ayrı eylemlerdir: sahibe bildirim, adlı kullanıcıya (ör. ekip lideri) yükseltme, yeniden atama.</summary>
public static class SlaEscalationAction
{
    public const string NotifyOwner = "NotifyOwner";
    public const string NotifyUser = "NotifyUser";
    public const string Reassign = "Reassign";

    public static readonly IReadOnlyList<string> All = [NotifyOwner, NotifyUser, Reassign];
}

/// <summary>Politikadaki bir eskalasyon kuralı: ölçüt hedefinin yüzdesine ulaşılınca eylem. 100 hedefin kendisidir (ihlal), 100 üstü ihlal sonrasıdır.</summary>
public sealed record EscalationRule(string Id, string Metric, int Percent, string Action, Guid? UserId, Guid? QueueId);

public static class EscalationRuleParser
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex IdPattern = new("^[a-z0-9-]{1,20}$", RegexOptions.Compiled);

    public const int MaxRules = 20;

    public static (IReadOnlyList<EscalationRule> Rules, IReadOnlyList<(string Field, string Message)> Errors) Parse(string? json)
    {
        var errors = new List<(string, string)>();
        List<EscalationRule> rules = [];
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                rules = JsonSerializer.Deserialize<List<EscalationRule>>(json, Json) ?? [];
            }
            catch (JsonException)
            {
                return ([], [("escalationRules", "Eskalasyon kuralları geçerli bir JSON listesi olmalıdır.")]);
            }
        }

        if (rules.Count > MaxRules)
        {
            errors.Add(("escalationRules", $"En çok {MaxRules} eskalasyon kuralı tanımlanabilir."));
        }

        foreach (var rule in rules)
        {
            if (rule.Id is null || !IdPattern.IsMatch(rule.Id) || !SlaMetric.All.Contains(rule.Metric) || rule.Percent is < 1 or > 500
                || !SlaEscalationAction.All.Contains(rule.Action))
            {
                errors.Add(("escalationRules", "Her kural için küçük harf/rakam/tire kimliği, ölçüt (FirstResponse|Resolution), 1-500 yüzde ve geçerli eylem verin."));
                break;
            }

            if (rule.Action == SlaEscalationAction.NotifyUser && rule.UserId is null)
            {
                errors.Add(("escalationRules", "Kullanıcıya bildirim kuralı için kullanıcı seçin."));
            }

            if (rule.Action == SlaEscalationAction.Reassign && rule.UserId is null && rule.QueueId is null)
            {
                errors.Add(("escalationRules", "Yeniden atama kuralı için kullanıcı ve/veya kuyruk seçin."));
            }
        }

        if (rules.GroupBy(r => r.Id).Any(g => g.Count() > 1))
        {
            errors.Add(("escalationRules", "Kural kimlikleri benzersiz olmalıdır."));
        }

        return (rules, errors);
    }

    public static string ToJson(IEnumerable<EscalationRule> rules) => JsonSerializer.Serialize(rules, Json);
}

/// <summary>Politika seçiminde adaylar (yalnızca seçime gereken alanlar).</summary>
public sealed record SlaPolicyCandidate(
    Guid Id, string Name, int Precedence, bool IsDefault, bool IsActive, string? TypeKey, Guid? CategoryId, string? Priority, Guid? PartyId);

/// <summary>Talebin politika seçimine giren özellikleri. <see cref="CategoryChain"/>: önce talebin kategorisi, sonra üst kategorisi.</summary>
public sealed record SlaTicketFacts(string TypeKey, IReadOnlyList<Guid> CategoryChain, string Priority, Guid? PartyId);

/// <summary>
/// Politika seçimi (kesin sıra). Yalnızca etkin ve kısıtlarının hepsi talebe uyan politikalar aday olur. Aday sırası: (1) tanımlı kısıt sayısı çok olan (daha özel),
/// (2) öncelik sayısı küçük olan (<c>Precedence</c>), (3) ad (büyük/küçük harf duyarsız), (4) kimlik. Kısıtsız "varsayılan" politika yalnızca başka hiçbir politika uymazsa seçilir.
/// Hiçbiri uymazsa talep hedefsizdir (null): sessizce varsayılan uydurulmaz.
/// </summary>
public static class SlaPolicySelector
{
    public static SlaPolicyCandidate? Select(IEnumerable<SlaPolicyCandidate> candidates, SlaTicketFacts facts)
    {
        var matching = candidates.Where(c => c.IsActive && Matches(c, facts)).ToList();
        var specific = matching.Where(c => !c.IsDefault).OrderByDescending(Score).ThenBy(c => c.Precedence)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id).FirstOrDefault();
        return specific ?? matching.Where(c => c.IsDefault).OrderBy(c => c.Precedence).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id).FirstOrDefault();
    }

    public static int Score(SlaPolicyCandidate c) =>
        (c.TypeKey is null ? 0 : 1) + (c.CategoryId is null ? 0 : 1) + (c.Priority is null ? 0 : 1) + (c.PartyId is null ? 0 : 1);

    private static bool Matches(SlaPolicyCandidate c, SlaTicketFacts f) =>
        (c.TypeKey is null || string.Equals(c.TypeKey, f.TypeKey, StringComparison.OrdinalIgnoreCase))
        && (c.CategoryId is null || f.CategoryChain.Contains(c.CategoryId.Value))
        && (c.Priority is null || c.Priority == f.Priority)
        && (c.PartyId is null || c.PartyId == f.PartyId);
}
