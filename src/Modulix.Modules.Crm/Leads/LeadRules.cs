using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Leads;

/// <summary>
/// Lead durum geçişleri ve temas kuralları (saf mantık; veritabanı gerektirmez, birim testlidir).
/// <code>
/// New ──► Contacting ──► Qualified ──► Converted (yalnızca dönüşüm işlemiyle)
///  │          │             │
///  └──────────┴──► Nurturing ◄┘      (takip tarihi/açık takip aktivitesi gerekir; yeniden Contacting/Qualified olabilir)
///  Açık durumlardan (New/Contacting/Qualified/Nurturing) ──► Disqualified (neden zorunlu)
///  Disqualified ──► New/Contacting (yetkili kullanıcı, gerekçeyle yeniden açar)
///  Converted: son durum; geri alınmaz, ikinci kez dönüştürülmez.
/// </code>
/// </summary>
public static class LeadRules
{
    /// <summary>Atama yalnızca açık Lead'lerde yapılır; elenmiş Lead önce yeniden açılır, dönüşmüş Lead değişmez.</summary>
    public static string? CanAssign(string status) =>
        LeadStatus.IsOpen(status) ? null : "Yalnızca açık Lead'ler atanabilir.";

    public static string? CanStartContacting(string status) =>
        status is LeadStatus.New or LeadStatus.Nurturing ? null : "İletişime geçilebilmesi için Lead yeni ya da daha sonra takipte olmalıdır.";

    public static string? CanQualify(string status) =>
        status is LeadStatus.New or LeadStatus.Contacting or LeadStatus.Nurturing
            ? null
            : status == LeadStatus.Qualified ? "Lead zaten nitelikli." : "Bu durumdaki Lead nitelendirilemez.";

    public static string? CanNurture(string status) =>
        status is LeadStatus.New or LeadStatus.Contacting or LeadStatus.Qualified
            ? null
            : status == LeadStatus.Nurturing ? "Lead zaten daha sonra takipte (tarihi değiştirmek için yeniden takibe alın)." : "Bu durumdaki Lead takibe alınamaz.";

    public static string? CanDisqualify(string status) =>
        LeadStatus.IsOpen(status) ? null : "Yalnızca açık Lead'ler elenebilir.";

    public static string? CanReopen(string status) =>
        status == LeadStatus.Disqualified ? null
        : status == LeadStatus.Converted ? "Dönüştürülmüş Lead yeniden açılamaz."
        : "Yalnızca elenmiş Lead yeniden açılabilir.";

    /// <summary>Dönüşüm yalnızca nitelikli Lead'lerde ve yalnızca bir kez yapılır.</summary>
    public static string? CanConvert(string status) =>
        status == LeadStatus.Qualified ? null
        : status == LeadStatus.Converted ? "Lead zaten dönüştürülmüş."
        : "Yalnızca nitelikli Lead dönüştürülebilir.";

    /// <summary>Yeniden açılan Lead, ilk teması gerçekleşmişse İletişimde, değilse Yeni durumuna döner.</summary>
    public static string ReopenedStatus(DateTimeOffset? firstContactAt) =>
        firstContactAt is null ? LeadStatus.New : LeadStatus.Contacting;

    /// <summary>
    /// Tamamlanmış bir aktivite "gerçekleşen temas" mı? Toplantı tamamlanmışsa evet; arama yalnızca ulaşıldıysa; e-posta
    /// gönderildi/ulaşıldı ise. Görev ve not, sonuçsuz ya da yanıtsız arama sayılmaz. Planlanmış (tamamlanmamış) aktivite temas değildir.
    /// </summary>
    public static bool CountsAsContact(string type, string? outcome, bool completed)
    {
        if (!completed)
        {
            return false;
        }

        return type switch
        {
            "Meeting" => outcome != CrmModule.ActivityOutcomes.NoAnswer,
            "Call" => outcome == CrmModule.ActivityOutcomes.Reached,
            "Email" => outcome is CrmModule.ActivityOutcomes.Reached or CrmModule.ActivityOutcomes.Sent,
            _ => false,
        };
    }
}

/// <summary>Nitelendirme için bir Lead'in anlık görüntüsü (kural sağlayıcılar yalnızca bunu görür).</summary>
public sealed record LeadSnapshot(
    string Subject,
    string? Description,
    string? NeedDescription,
    string? ProductInterest,
    string? FirstName,
    string? LastName,
    string? CompanyName,
    string? Email,
    string? Phone,
    Guid? CompanyId,
    Guid? ContactId,
    decimal? EstimatedBudget,
    DateOnly? TargetPurchaseDate,
    string? Industry);

/// <summary>
/// Nitelendirme koşulu. Varsayılan koşul (<see cref="MinimumQualificationRule"/>) sektör bağımsızdır; bir sektör modülü kendi
/// koşulunu (ör. bütçe ya da karar verici zorunlu) servis olarak ekleyerek genişletir. Hiçbir tenant için bütçe ya da karar verici
/// bilgisi varsayılan olarak zorunlu değildir.
/// </summary>
public interface ILeadQualificationRule
{
    /// <summary>Eksikler (kullanıcıya gösterilecek); boş liste koşulun sağlandığını gösterir.</summary>
    IReadOnlyList<string> Check(LeadSnapshot lead);
}

/// <summary>Asgari koşul: bir ihtiyaç ifadesi ve ulaşılabilir bir iletişim bilgisi (e-posta, telefon ya da mevcut kişi).</summary>
public sealed class MinimumQualificationRule : ILeadQualificationRule
{
    public IReadOnlyList<string> Check(LeadSnapshot lead)
    {
        var missing = new List<string>();
        if (IsBlank(lead.NeedDescription) && IsBlank(lead.Description) && IsBlank(lead.ProductInterest))
        {
            missing.Add("İhtiyaç: açıklama, ihtiyaç ya da ilgilenilen ürün/hizmet girilmeli.");
        }

        if (IsBlank(lead.Email) && IsBlank(lead.Phone) && lead.ContactId is null)
        {
            missing.Add("İletişim: e-posta, telefon ya da mevcut kişi girilmeli.");
        }

        if (IsBlank(lead.FirstName) && IsBlank(lead.LastName) && IsBlank(lead.CompanyName) && lead.CompanyId is null && lead.ContactId is null)
        {
            missing.Add("Talep sahibi: ad, firma adı ya da mevcut firma/kişi girilmeli.");
        }

        return missing;
    }

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);
}
