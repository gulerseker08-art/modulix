namespace Modulix.Modules.Logistics.Domain;

/// <summary>
/// Sefer masrafı (ADR-0038 K10, dilim 8): yakıt, geçiş ücreti, harcırah, ceza ve diğer masraflar. Masraf operasyon kaydıdır; ödeme, kasa ve sürücü
/// hesabı bu kayıtta yoktur (Muhasebe M5, İK M4). Masraf türü firmanın seçim listesindendir (<c>logistics.expense-type</c>); kod türün adına değil
/// <b>kategorisine</b> bakar: <c>fuel</c> kategorisinde litre zorunludur (yakıt ayrı defter değil, aynı kaydın alanıdır; araç yakıt raporu buradan
/// türetilir), <c>fine</c> kategorisinde sürücüye rücu bayrağı bilgi olarak girilebilir. Durum yalnızca iş akışı komutuyla değişir: Beklemede →
/// Onaylandı / Reddedildi; reddedilen masraf düzeltilip yeniden beklemeye alınır. Onaylı masraf değişmez. Onay, firmanın bağladığı onay sürecine
/// (<c>IApprovalGate</c>) uyar. Silinmez (K12).
/// </summary>
public sealed class TripExpense : LogisticsEntity
{
    public const int TextMaxLength = 2000;
    public const int DocumentNoMaxLength = 60;
    public const int MaxOdometerKm = 9_999_999;
    public const decimal MaxLiters = 99_999.999m;

    public static class Statuses
    {
        public const string Pending = "pending";
        public const string Approved = "approved";
        public const string Rejected = "rejected";

        public static IReadOnlyList<string> All { get; } = [Pending, Approved, Rejected];
    }

    public TripExpense() => Status = Statuses.Pending;

    public Guid TripId { get; internal set; }

    /// <summary>Masraf türü (seçim listesi <c>logistics.expense-type</c> anahtarı).</summary>
    public string ExpenseTypeKey { get; internal set; } = string.Empty;

    /// <summary>Masrafın yapıldığı gün (fiş/fatura günü); ileri tarih olamaz.</summary>
    public DateOnly ExpenseDate { get; internal set; }

    /// <summary>KDV dahil toplam tutar (0'dan büyük).</summary>
    public decimal Amount { get; internal set; }

    /// <summary>ISO 4217 para birimi (üç harf). Onay talebi tutarı ve para birimini buradan okur.</summary>
    public string Currency { get; internal set; } = string.Empty;

    /// <summary>Tutarın içindeki KDV (isteğe bağlı; tutarı aşamaz).</summary>
    public decimal? VatAmount { get; internal set; }

    /// <summary>Tedarikçi (yakıt istasyonu, otoyol işletmesi, servis...): <c>core.Party</c>.</summary>
    public Guid? SupplierPartyId { get; internal set; }

    public string? DocumentNo { get; internal set; }

    /// <summary>Yakıt kategorisinde alınan litre (diğer kategorilerde boş kalır).</summary>
    public decimal? Liters { get; internal set; }

    /// <summary>Yakıt kategorisinde araç kilometre sayacı (isteğe bağlı).</summary>
    public int? OdometerKm { get; internal set; }

    /// <summary>Ceza kategorisinde sürücüye rücu edilecek mi. Yalnızca bilgidir; borç kaydı üretmez (K10).</summary>
    public bool RecoverFromDriver { get; internal set; }

    public string? Notes { get; internal set; }

    /// <summary>Durum yalnızca iş akışı komutuyla değişir (K7).</summary>
    public string Status { get; internal set; }

    /// <summary>Son kararın notu (ret gerekçesi).</summary>
    public string? StatusReason { get; internal set; }
}
