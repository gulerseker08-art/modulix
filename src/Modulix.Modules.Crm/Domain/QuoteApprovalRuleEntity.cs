namespace Modulix.Modules.Crm.Domain;

/// <summary>
/// Teklif iç onay kuralı (ADR-0019, dilim 4). Kuralın tüm koşulları birlikte sağlanırsa (VE) teklif iç onay gerektirir; birden çok kural varsa
/// herhangi biri yeter (VEYA). En az bir koşul zorunludur. <see cref="ApproverId"/> doluysa onay yalnızca o kullanıcıdan kabul edilir;
/// boşsa <c>crm.quotes.approve</c> iznine sahip herkes onaylayabilir. Kural sürümü (<c>Version</c>) onaya gönderim kaydına yazılır.
/// </summary>
public sealed class QuoteApprovalRule : CrmEntity
{
    public const int NameMaxLength = 150;
    public const int DescriptionMaxLength = 500;

    public string Name { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    /// <summary>Küçük sayı önce değerlendirilir ve gösterilir; eşleşme sonucunu değiştirmez.</summary>
    public int Priority { get; internal set; }

    public bool IsActive { get; internal set; } = true;

    /// <summary>Boşsa her para birimi; doluysa yalnızca bu para birimindeki teklifler (tutar koşulu için anlamlıdır).</summary>
    public string? Currency { get; internal set; }

    /// <summary>Teklifin toplam indirim yüzdesi bunu aşarsa koşul sağlanır.</summary>
    public decimal? MinDiscountPercent { get; internal set; }

    /// <summary>Genel toplam bunu aşarsa koşul sağlanır.</summary>
    public decimal? MinGrossTotal { get; internal set; }

    /// <summary>Boşsa onay izni olan herkes; doluysa yalnızca bu kullanıcı onaylayabilir.</summary>
    public Guid? ApproverId { get; internal set; }
}
