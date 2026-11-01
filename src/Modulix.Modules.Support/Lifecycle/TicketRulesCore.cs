using System.Globalization;
using System.Text.RegularExpressions;

namespace Modulix.Modules.Support.Lifecycle;

// Dilim 3 saf kuralları (ADR-0024): yönetilebilir öncelik matrisi, yönlendirme kuralı seçimi, kategoriye bağlı zorunlu alanlar, mükerrer benzerliği ve birleştirme kuralları.
// Veri erişimi yoktur; deterministik ve birim testlidir.

/// <summary>Yönetilebilir etki × aciliyet matrisi. Tenant hücre tanımlamadıysa yerleşik varsayılan (<see cref="PriorityMatrix.Compute(string, string)"/>) geçerlidir.</summary>
public static class PriorityMatrixRules
{
    /// <summary>Hücre geçersizse (bilinmeyen düzey/öncelik) hata; eksik hücre yerleşik matristen alınır.</summary>
    public static IReadOnlyList<(string Field, string Message)> Validate(IReadOnlyCollection<(string Impact, string Urgency, string Priority)> cells)
    {
        var errors = new List<(string, string)>();
        if (cells.Any(c => !TicketLevel.All.Contains(c.Impact)))
        {
            errors.Add(("impact", $"Etki şunlardan biri olmalıdır: {string.Join(", ", TicketLevel.All)}."));
        }

        if (cells.Any(c => !TicketLevel.All.Contains(c.Urgency)))
        {
            errors.Add(("urgency", $"Aciliyet şunlardan biri olmalıdır: {string.Join(", ", TicketLevel.All)}."));
        }

        if (cells.Any(c => !TicketPriority.All.Contains(c.Priority)))
        {
            errors.Add(("priority", $"Öncelik şunlardan biri olmalıdır: {string.Join(", ", TicketPriority.All)}."));
        }

        if (cells.GroupBy(c => (c.Impact, c.Urgency)).Any(g => g.Count() > 1))
        {
            errors.Add(("cells", "Her etki × aciliyet hücresi için tek değer verilebilir."));
        }

        return errors;
    }

    /// <summary>Tenant matrisiyle öncelik; hücre yoksa yerleşik matris.</summary>
    public static string Compute(IReadOnlyDictionary<(string Impact, string Urgency), string> custom, string impact, string urgency) =>
        custom.TryGetValue((impact, urgency), out var priority) ? priority : PriorityMatrix.Compute(impact, urgency);
}

/// <summary>Yönlendirme kuralı (bellekteki görünüm). Kısıtlar boşsa her talebe uyar; eylem kuyruk ve/veya sorumludur.</summary>
public sealed record RoutingRule(
    Guid Id, string Name, int Order, bool IsActive, string? Channel, string? TypeKey, Guid? CategoryId, string? Priority, Guid? PartyId, Guid? QueueId, Guid? OwnerId);

/// <summary>Yönlendirmeye giren talep özellikleri. <see cref="CategoryChain"/>: önce talebin kategorisi, sonra üst kategorisi.</summary>
public sealed record RoutingFacts(string Channel, string TypeKey, IReadOnlyList<Guid> CategoryChain, string Priority, Guid? PartyId);

/// <summary>
/// Öncelik sıralı yönlendirme (ilk eşleşme kazanır). Yalnızca etkin ve kısıtlarının hepsi uyan kurallar aday olur. Sıra: (1) <c>Order</c> küçük olan, (2) eşit sırada daha çok
/// kısıtı olan (daha özel), (3) ad (harf duyarsız), (4) kimlik. Kural yalnızca talep açılırken bir kez değerlendirilir (zincirleme/döngü yoktur). Hiçbiri uymazsa
/// kategori/varsayılan kuyruk mantığı sürer.
/// </summary>
public static class RoutingRuleSelector
{
    public static RoutingRule? Select(IEnumerable<RoutingRule> rules, RoutingFacts facts) =>
        rules.Where(r => r.IsActive && Matches(r, facts))
            .OrderBy(r => r.Order).ThenByDescending(Specificity).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).FirstOrDefault();

    public static int Specificity(RoutingRule r) =>
        (r.Channel is null ? 0 : 1) + (r.TypeKey is null ? 0 : 1) + (r.CategoryId is null ? 0 : 1) + (r.Priority is null ? 0 : 1) + (r.PartyId is null ? 0 : 1);

    private static bool Matches(RoutingRule r, RoutingFacts f) =>
        (r.Channel is null || r.Channel == f.Channel)
        && (r.TypeKey is null || string.Equals(r.TypeKey, f.TypeKey, StringComparison.OrdinalIgnoreCase))
        && (r.CategoryId is null || f.CategoryChain.Contains(r.CategoryId.Value))
        && (r.Priority is null || r.Priority == f.Priority)
        && (r.PartyId is null || r.PartyId == f.PartyId);
}

/// <summary>Kategoriye bağlı zorunlu alanlar (talep alanı adları). Yalnızca izin verilen alanlar zorunlu yapılabilir.</summary>
public static class CategoryFieldRules
{
    public static readonly IReadOnlyList<string> Allowed = ["description", "personId", "reporterEmail", "reporterPhone", "eventDate", "expectedOutcome"];

    public static IReadOnlyList<string> Parse(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? [] : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    public static IReadOnlyList<(string Field, string Message)> Validate(string? csv)
    {
        var unknown = Parse(csv).Where(f => !Allowed.Contains(f)).ToList();
        return unknown.Count == 0 ? [] : [("requiredFields", $"Geçersiz alan: {string.Join(", ", unknown)}. İzin verilenler: {string.Join(", ", Allowed)}.")];
    }

    /// <summary>Zorunlu alanların hangileri boş: <paramref name="isFilled"/> alan adı → dolu mu.</summary>
    public static IReadOnlyList<string> Missing(IEnumerable<string> required, Func<string, bool> isFilled) => [.. required.Distinct(StringComparer.Ordinal).Where(f => !isFilled(f))];
}

/// <summary>Konu benzerliği (mükerrer uyarısı): küçük harfe çevrilmiş, noktalamasız, en az 3 harfli sözcüklerin Jaccard benzerliği.</summary>
public static partial class SubjectSimilarity
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal) { "için", "ile", "bir", "ama", "gibi", "veya", "ve", "bu", "şu" };

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    public static IReadOnlySet<string> Tokens(string? text) =>
        NonWord().Split((text ?? string.Empty).ToLower(Turkish)).Where(t => t.Length >= 3 && !Stop.Contains(t)).ToHashSet(StringComparer.Ordinal);

    public static double Score(string? a, string? b)
    {
        var x = Tokens(a);
        var y = Tokens(b);
        if (x.Count == 0 || y.Count == 0)
        {
            return 0;
        }

        return (double)x.Intersect(y).Count() / x.Union(y).Count();
    }
}

/// <summary>Olası mükerrer ölçütü: aynı başvuran kimliği (müşteri, e-posta ya da telefon) ve açık talep; aynı kategori ya da konu benzerliği eşiği.</summary>
public static class DuplicateRules
{
    public const double SimilarityThreshold = 0.5;

    public static bool IsPossibleDuplicate(
        string? probeSubject, Guid? probeCategory, string? candidateSubject, Guid? candidateCategory, bool sameApplicant) =>
        sameApplicant && (SubjectSimilarity.Score(probeSubject, candidateSubject) >= SimilarityThreshold
            || (probeCategory is not null && probeCategory == candidateCategory && SubjectSimilarity.Score(probeSubject, candidateSubject) > 0));
}

/// <summary>Birleştirme için talep özeti.</summary>
public readonly record struct MergeFacts(Guid Id, string Status, Guid? PartyId, string? ReporterEmail, string? ReporterPhone);

/// <summary>
/// Birleştirme kuralları: aynı olayın tekrarı tek talepte toplanır, ancak farklı müşterilerin ayrı talepleri körlemesine birleştirilmez. Birleştirilen (kaynak) talep son duruma geçer ve
/// hedefe işaret eder; kaynağın geçmişi, mesajları ve dış anahtarı kaynakta kalır. Kapanmış ya da iptal edilmiş hedefe birleştirilmez (önce yeniden açılır).
/// </summary>
public static class MergeRules
{
    public static TicketRuleResult Can(MergeFacts source, MergeFacts target)
    {
        if (source.Id == target.Id)
        {
            return TicketRuleResult.Deny("same-ticket", "Talep kendisiyle birleştirilemez.");
        }

        if (TicketStatus.IsFinal(source.Status) || source.Status == TicketStatus.Resolved)
        {
            return TicketRuleResult.Deny("source-closed", "Çözülmüş, kapanmış, iptal edilmiş ya da zaten birleştirilmiş talep birleştirilemez.");
        }

        if (TicketStatus.IsFinal(target.Status))
        {
            return TicketRuleResult.Deny("target-closed", "Kapanmış, iptal edilmiş ya da birleştirilmiş talebe birleştirilemez; hedef talebi önce yeniden açın.");
        }

        return SameApplicant(source, target)
            ? TicketRuleResult.Ok
            : TicketRuleResult.Deny("different-customer", "Farklı müşterilerin talepleri birleştirilemez; ilişkili talep olarak bağlayın.");
    }

    public static bool SameApplicant(MergeFacts a, MergeFacts b)
    {
        if (a.PartyId is not null || b.PartyId is not null)
        {
            return a.PartyId == b.PartyId;
        }

        return (!string.IsNullOrWhiteSpace(a.ReporterEmail) && string.Equals(a.ReporterEmail, b.ReporterEmail, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(a.ReporterPhone) && a.ReporterPhone == b.ReporterPhone);
    }
}
