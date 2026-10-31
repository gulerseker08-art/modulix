using System.Security.Cryptography;

namespace Modulix.Modules.Contracts.Lifecycle;

// Saf kurallar (ADR-0022): veritabanı ve HTTP bilmez; birim testleriyle doğrulanır. Durum anahtarları API'de ve arayüzde aynen kullanılır.

/// <summary>Belge hazırlığı durumu.</summary>
public static class PreparationStatus
{
    public const string Draft = "Draft";
    public const string InReview = "InReview";
    public const string ReadyToPublish = "ReadyToPublish";

    public static IReadOnlyList<string> All { get; } = [Draft, InReview, ReadyToPublish];
}

/// <summary>İç onay durumu.</summary>
public static class ApprovalStatus
{
    public const string NotRequired = "NotRequired";
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Withdrawn = "Withdrawn";

    public static IReadOnlyList<string> All { get; } = [NotRequired, Pending, Approved, Rejected, Withdrawn];
}

/// <summary>İmza durumu.</summary>
public static class SignatureStatus
{
    public const string NotSent = "NotSent";
    public const string Pending = "Pending";
    public const string PartiallySigned = "PartiallySigned";
    public const string Completed = "Completed";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";

    public static IReadOnlyList<string> All { get; } = [NotSent, Pending, PartiallySigned, Completed, Rejected, Expired, Cancelled];
}

/// <summary>Yürürlük durumu.</summary>
public static class EffectiveStatus
{
    public const string NotInForce = "NotInForce";
    public const string AwaitingStart = "AwaitingStart";
    public const string Active = "Active";
    public const string Suspended = "Suspended";
    public const string Expired = "Expired";
    public const string Terminated = "Terminated";
    public const string Cancelled = "Cancelled";

    public static IReadOnlyList<string> All { get; } = [NotInForce, AwaitingStart, Active, Suspended, Expired, Terminated, Cancelled];

    /// <summary>Yürürlüğe girmiş ya da girmesi beklenen sözleşme (imzalı içerik yerinde değiştirilemez).</summary>
    public static bool IsBinding(string status) => status is AwaitingStart or Active or Suspended;
}

public static class RenewalMode
{
    public const string None = "None";
    public const string Manual = "Manual";
    public const string Automatic = "Automatic";

    public static IReadOnlyList<string> All { get; } = [None, Manual, Automatic];
}

public static class Confidentiality
{
    public const string Internal = "Internal";
    public const string Confidential = "Confidential";
    public const string Restricted = "Restricted";

    public static IReadOnlyList<string> All { get; } = [Internal, Confidential, Restricted];
}

/// <summary>Sözleşme risk düzeyi (elle girilen risk göstergesi); onay kuralları "en az bu düzey" koşuluyla kullanır.</summary>
public static class RiskLevel
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";

    public static IReadOnlyList<string> All { get; } = [Low, Medium, High];

    /// <summary>Sıra sayısı (0 düşük); bilinmeyen değer düşük sayılır.</summary>
    public static int Rank(string? level) => level switch { Medium => 1, High => 2, _ => 0 };
}

/// <summary>Onay kararı türleri (<c>ContractApprovalDecisions</c>).</summary>
public static class ApprovalDecisionKind
{
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Returned = "Returned";
}

/// <summary>Taraf/ilgili kişi rolleri; adlar Shared'daki <c>ContractPartyRoles</c> ile aynıdır.</summary>
public static class PartyRoles
{
    public const string Customer = "Customer";
    public const string Provider = "Provider";
    public const string Guarantor = "Guarantor";
    public const string Subcontractor = "Subcontractor";
    public const string Signatory = "Signatory";
    public const string BillingContact = "BillingContact";
    public const string TechnicalContact = "TechnicalContact";
    public const string Other = "Other";

    public static IReadOnlyList<string> All { get; } = [Customer, Provider, Guarantor, Subcontractor, Signatory, BillingContact, TechnicalContact, Other];
}

public static class LineKinds
{
    public const string Product = "Product";
    public const string Service = "Service";
    public const string Text = "Text";

    public static IReadOnlyList<string> All { get; } = [Product, Service, Text];
}

/// <summary>Dönemsel ücret dönemleri; boş değer tek seferliktir. Anahtarlar <c>QuoteCalculator</c>'ın dönem anahtarlarıyla aynıdır.</summary>
public static class BillingPeriods
{
    public const string Monthly = "Monthly";
    public const string Quarterly = "Quarterly";
    public const string Yearly = "Yearly";

    public static IReadOnlyList<string> All { get; } = [Monthly, Quarterly, Yearly];
}

public readonly record struct ContractRuleResult(bool Allowed, string? Code = null, string? Message = null)
{
    public static ContractRuleResult Ok { get; } = new(true);

    public static ContractRuleResult Deny(string code, string message) => new(false, code, message);
}

/// <summary>Sözleşme iş kuralları. Kurallar API seviyesinde uygulanır; arayüz yalnızca aynı kuralları gösterir.</summary>
public static class ContractRules
{
    /// <summary>
    /// Başlık, taraf ve kalemler yalnızca hazırlık taslak durumundayken ve içerik onaya/imzaya gitmemişken düzenlenir. İmzalı ya da
    /// yürürlükteki sözleşme yerinde değişmez: yeni revizyon ya da ek protokol açılır (ikinci dilim).
    /// </summary>
    public static ContractRuleResult CanEditContent(string preparation, string approval, string signature, string effective)
    {
        if (preparation != PreparationStatus.Draft)
        {
            return ContractRuleResult.Deny("not-draft", "Sözleşme taslak durumunda değil; içerik değiştirilemez.");
        }

        if (approval == ApprovalStatus.Pending)
        {
            return ContractRuleResult.Deny("approval-pending", "Onay bekleyen içerik değiştirilemez; önce onayı geri çekin.");
        }

        if (signature is SignatureStatus.Pending or SignatureStatus.PartiallySigned or SignatureStatus.Completed)
        {
            return ContractRuleResult.Deny("signature-in-progress", "İmzaya gönderilmiş ya da imzalanmış içerik değiştirilemez; yeni revizyon açın.");
        }

        if (EffectiveStatus.IsBinding(effective) || effective is EffectiveStatus.Expired or EffectiveStatus.Terminated)
        {
            return ContractRuleResult.Deny("in-force", "Yürürlüğe girmiş sözleşme yerinde değiştirilemez; ek protokol ya da yeni revizyon açın.");
        }

        return ContractRuleResult.Ok;
    }

    /// <summary>Yalnızca hiç imzalanmamış ve yürürlüğe girmemiş sözleşme silinir; diğerleri arşivlenir/iptal edilir.</summary>
    public static ContractRuleResult CanDelete(string signature, string effective)
    {
        if (signature is SignatureStatus.Completed or SignatureStatus.PartiallySigned)
        {
            return ContractRuleResult.Deny("signed", "İmzalı sözleşme silinemez; iptal ya da fesih sürecini kullanın.");
        }

        if (effective != EffectiveStatus.NotInForce)
        {
            return ContractRuleResult.Deny("in-force", "Yürürlük kaydı olan sözleşme silinemez.");
        }

        return ContractRuleResult.Ok;
    }

    /// <summary>
    /// Tarih tutarlılığı: bitiş başlangıçtan önce olamaz; belirsiz süreli sözleşmede bitiş tarihi girilmez; yenileme süresi yalnızca
    /// yenilemeli sözleşmede ve pozitiftir; bildirim süresi negatif olamaz.
    /// </summary>
    public static IReadOnlyList<(string Field, string Message)> ValidateDates(
        DateOnly? start, DateOnly? end, bool isIndefinite, string renewalMode, int? renewalPeriodMonths, int? noticePeriodDays)
    {
        var errors = new List<(string, string)>();
        if (isIndefinite && end is not null)
        {
            errors.Add(("endDate", "Belirsiz süreli sözleşmede bitiş tarihi girilmez."));
        }

        if (start is { } s && end is { } e && e < s)
        {
            errors.Add(("endDate", "Bitiş tarihi başlangıç tarihinden önce olamaz."));
        }

        if (!RenewalMode.All.Contains(renewalMode))
        {
            errors.Add(("renewalMode", "Geçersiz yenileme yöntemi."));
        }
        else if (renewalMode == RenewalMode.None && renewalPeriodMonths is not null)
        {
            errors.Add(("renewalPeriodMonths", "Yenilemesiz sözleşmede yenileme süresi girilmez."));
        }
        else if (renewalMode != RenewalMode.None && renewalPeriodMonths is null or < 1)
        {
            errors.Add(("renewalPeriodMonths", "Yenilemeli sözleşmede yenileme süresi (ay) 1 veya daha büyük olmalıdır."));
        }

        if (renewalPeriodMonths is > 120)
        {
            errors.Add(("renewalPeriodMonths", "Yenileme süresi en fazla 120 ay olabilir."));
        }

        if (noticePeriodDays is < 0 or > 3650)
        {
            errors.Add(("noticePeriodDays", "Bildirim süresi 0 ile 3650 gün arasında olmalıdır."));
        }

        return errors;
    }

    /// <summary>Son bildirim tarihi: bitiş − bildirim süresi. Belirsiz süreli ya da bitişsiz sözleşmede yoktur.</summary>
    public static DateOnly? NoticeDeadline(DateOnly? end, int? noticePeriodDays, bool isIndefinite) =>
        !isIndefinite && end is { } e && noticePeriodDays is { } days ? e.AddDays(-days) : null;

    /// <summary>Yeni sözleşme numarası: SZ-yyyyMMdd-XXXXXX (karışmayan harf/rakam alfabesi).</summary>
    public static string NewNumber(DateTimeOffset createdAt)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> suffix = stackalloc char[6];
        for (var i = 0; i < suffix.Length; i++)
        {
            suffix[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return $"SZ-{createdAt:yyyyMMdd}-{new string(suffix)}";
    }
}

/// <summary>Sürümün niçin dondurulduğu.</summary>
public static class VersionKinds
{
    public const string Approval = "Approval";
    public const string Signature = "Signature";
}

/// <summary>Dondurulmuş sürümün durumu; onay/imza kararı sürüme yazılır, sonraki revizyon eskisini geçersiz kılar.</summary>
public static class VersionStatus
{
    public const string Frozen = "Frozen";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Withdrawn = "Withdrawn";
    public const string Signed = "Signed";
    public const string Superseded = "Superseded";
}
