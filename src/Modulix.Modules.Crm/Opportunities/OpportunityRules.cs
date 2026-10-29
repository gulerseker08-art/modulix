using System.Globalization;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Opportunities;

/// <summary>
/// Fırsat politikası: tenant ayarlarından gelen aşama koşulları, aşama varsayılan olasılıkları ve hareketsizlik eşiği.
/// Modül ayarları okuyamaz (yalnızca sözleşmelere bağlıdır); <see cref="IOpportunityPolicyProvider"/> API tarafından sağlanır.
/// </summary>
/// <param name="StageRequirements">Aşama anahtarı → o aşamaya geçerken dolu olması gereken alanlar (kayıt alanı adları).</param>
/// <param name="StageProbabilities">Aşama anahtarı → varsayılan kazanma olasılığı (yüzde).</param>
/// <param name="StaleDays">Son müşteri etkileşiminden bu kadar gün geçen açık fırsat "hareketsiz" sayılır.</param>
public sealed record OpportunityPolicy(
    IReadOnlyDictionary<string, IReadOnlyList<string>> StageRequirements,
    IReadOnlyDictionary<string, int> StageProbabilities,
    int StaleDays)
{
    public const int DefaultStaleDays = 30;

    public static OpportunityPolicy Default { get; } = new(
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        DefaultStaleDays);

    public IReadOnlyList<string> RequirementsOf(string stage) =>
        StageRequirements.TryGetValue(stage, out var fields) ? fields : [];

    public int? ProbabilityOf(string stage) => StageProbabilities.TryGetValue(stage, out var percent) ? percent : null;
}

public interface IOpportunityPolicyProvider
{
    Task<OpportunityPolicy> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Fırsat kuralları: saf işlevler (veritabanı ve izin gerektirmez), birim testlerle sınanır.</summary>
public static class OpportunityRules
{
    /// <summary>
    /// "Aşama=alan|alan;Aşama=alan" biçimini çözer. Biçimi bozuk girişler atlanır (ayar zaten desenle doğrulanır).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseRequirements(string? text)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in Split(text))
        {
            if (part.Split('=', 2) is [{ Length: > 0 } stage, { Length: > 0 } fields])
            {
                result[stage.Trim()] = [.. fields.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            }
        }

        return result;
    }

    /// <summary>"Aşama=yüzde;Aşama=yüzde" biçimini çözer; 0-100 dışı ya da sayı olmayan girişler atlanır.</summary>
    public static IReadOnlyDictionary<string, int> ParseProbabilities(string? text)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in Split(text))
        {
            if (part.Split('=', 2) is [{ Length: > 0 } stage, var percent]
                && int.TryParse(percent.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 100)
            {
                result[stage.Trim()] = value;
            }
        }

        return result;
    }

    private static IEnumerable<string> Split(string? text) =>
        (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---- Durum geçişleri

    /// <summary>Aşama değiştirmek için fırsat açık olmalıdır (beklemede ya da kapalıyken değil).</summary>
    public static string? CanChangeStage(string status) => status switch
    {
        OpportunityStatus.Open => null,
        OpportunityStatus.OnHold => "Beklemedeki fırsatın aşaması değiştirilemez; önce beklemeden çıkarın.",
        _ => "Kapanmış fırsatın aşaması değiştirilemez; önce yeniden açın.",
    };

    /// <summary>Kapatma (kazanma, kaybetme, iptal) ve beklemeye alma yalnızca açık fırsatta yapılır.</summary>
    public static string? CanClose(string status) => status switch
    {
        OpportunityStatus.Open => null,
        OpportunityStatus.OnHold => "Beklemedeki fırsat kapatılamaz; önce beklemeden çıkarın.",
        _ => "Fırsat zaten kapanmış.",
    };

    public static string? CanHold(string status) => status == OpportunityStatus.Open
        ? null
        : status == OpportunityStatus.OnHold ? "Fırsat zaten beklemede." : "Kapanmış fırsat beklemeye alınamaz.";

    public static string? CanResume(string status) => status == OpportunityStatus.OnHold ? null : "Yalnızca beklemedeki fırsat beklemeden çıkarılır.";

    public static string? CanReopen(string status) =>
        OpportunityStatus.IsClosed(status) ? null : "Yalnızca kapanmış (kazanılan, kaybedilen ya da iptal edilen) fırsat yeniden açılır.";

    // ---- Aşama ↔ durum tutarlılığı

    /// <summary>
    /// Durum ile aşama kategorisi tutarlı mı? Kazanıldı ↔ kazanıldı kategorisi, Kaybedildi ↔ kaybedildi kategorisi;
    /// Açık, Beklemede ve İptal edildi fırsatlar açık bir aşamada durur (iptal, son açık aşamayı korur).
    /// </summary>
    public static bool IsConsistent(string status, string stageCategory) => status switch
    {
        OpportunityStatus.Won => stageCategory == CrmModule.StageCategories.Won,
        OpportunityStatus.Lost => stageCategory == CrmModule.StageCategories.Lost,
        _ => stageCategory == CrmModule.StageCategories.Open,
    };

    // ---- Olasılık ve tahmin

    /// <summary>
    /// Kapanıştaki olasılık: kazanılan 100, kaybedilen/iptal 0. Açık fırsatta aşama değişince olasılık, yalnızca kullanıcı elle girmediyse
    /// aşamanın varsayılanına geçer; elle girilen olasılık korunur.
    /// </summary>
    public static int? ProbabilityFor(string status, bool manual, int? current, int? stageDefault) => status switch
    {
        OpportunityStatus.Won => 100,
        OpportunityStatus.Lost or OpportunityStatus.Cancelled => 0,
        _ => manual ? current : stageDefault ?? current,
    };

    /// <summary>Ağırlıklı tutar = tutar × olasılık / 100. Tutar ya da olasılık bilinmiyorsa tanımsızdır (0 değil).</summary>
    public static decimal? Weighted(decimal? amount, int? probability) =>
        amount is { } a && probability is { } p ? decimal.Round(a * p / 100m, 2, MidpointRounding.AwayFromZero) : null;

    // ---- Numara

    public static string NewNumber(DateTimeOffset createdAt)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> suffix = stackalloc char[6];
        for (var i = 0; i < suffix.Length; i++)
        {
            suffix[i] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return $"OP-{createdAt:yyyyMMdd}-{new string(suffix)}";
    }
}
