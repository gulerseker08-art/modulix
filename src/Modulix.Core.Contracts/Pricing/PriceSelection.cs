namespace Modulix.Core.Contracts.Pricing;

/// <summary>Fiyat listesi önceliği; küçük değer önce gelir.</summary>
public enum PriceListScope
{
    /// <summary>Kullanıcının (yetkisi ve geçerliliği denetlenerek) açıkça seçtiği liste.</summary>
    Explicit = 1,
    Customer = 2,
    SegmentOrChannel = 3,
    TenantDefault = 4,
}

/// <summary>Kademeli fiyatlama yöntemi: <c>Volume</c> tüm miktar tek kademenin fiyatından, <c>Graduated</c> her aralık kendi fiyatından.</summary>
public enum TierMethod
{
    Volume,
    Graduated,
}

/// <summary>Miktar aralığı [<see cref="MinQuantity"/>, <see cref="MaxQuantity"/>) (üst sınır hariç; boşsa sınırsız). <see cref="IsFree"/> ücretsiz satıştır, fiyatın eksik olması değildir.</summary>
public sealed record PriceTier(decimal MinQuantity, decimal? MaxQuantity, decimal UnitPrice, bool IsFree = false);

/// <summary>Bir ürün için fiyat listesi satırı (liste başlığı bilgisiyle). Yayımlanmış liste değişmez; değişiklik yeni sürümdür.</summary>
public sealed record PriceListEntry(
    Guid ListId,
    int Version,
    string Currency,
    PriceListScope Scope,
    bool Published,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    TierMethod Method,
    IReadOnlyList<PriceTier> Tiers);

public sealed record PriceSelection(
    PriceListEntry List,
    decimal Quantity,
    decimal Total,
    bool IsFree,
    IReadOnlyList<string> Explanation);

/// <summary>
/// Fiyat seçimi (tek sunucu tarafı servis). Sıra: açık seçim → müşteri → segment/kanal → tenant varsayılanı.
/// Geçerlilik: <c>ValidFrom</c> dahil, <c>ValidTo</c> hariç (anlık, <see cref="DateTimeOffset"/>); yalnızca yayımlanmış listeler. Aynı
/// öncelikte birden fazla uygun fiyat rastgele seçilmez: <see cref="PricingErrors.PriceConflict"/> hatası verilir. Fiyat bulunamazsa
/// sıfır fiyat üretilmez: <see cref="PricingErrors.PriceNotFound"/> nedeniyle hata verilir. Açıkça seçilen liste uygun değilse
/// sessizce başka listeye düşülmez.
/// </summary>
public static class PriceSelector
{
    public static PriceSelection Select(
        IEnumerable<PriceListEntry> entries,
        decimal quantity,
        string currency,
        DateTimeOffset at,
        Guid? explicitListId = null)
    {
        if (quantity <= 0)
        {
            throw new PricingException(PricingErrors.InvalidQuantity, "Miktar sıfırdan büyük olmalıdır.");
        }

        var all = entries.Where(e => string.Equals(e.Currency, currency, StringComparison.Ordinal)).ToList();
        foreach (PriceListEntry entry in all)
        {
            ValidateTiers(entry);
        }

        if (explicitListId is { } listId)
        {
            PriceListEntry? chosen = all.SingleOrDefault(e => e.ListId == listId);
            if (chosen is null || !IsValid(chosen, at) || Price(chosen, quantity) is null)
            {
                throw new PricingException(PricingErrors.PriceNotFound,
                    "Seçilen fiyat listesi bu tarih, para birimi ve miktar için geçerli bir fiyat içermiyor.");
            }

            return Build(chosen, quantity, "Açıkça seçilen fiyat listesi.");
        }

        var candidates = all.Where(e => e.Scope != PriceListScope.Explicit && IsValid(e, at) && Price(e, quantity) is not null).ToList();
        if (candidates.Count == 0)
        {
            throw new PricingException(PricingErrors.PriceNotFound,
                $"{currency} için {at:yyyy-MM-dd} tarihinde {quantity} miktarına uygun yayımlanmış bir fiyat bulunamadı.");
        }

        PriceListScope best = candidates.Min(c => c.Scope);
        var top = candidates.Where(c => c.Scope == best).ToList();
        if (top.Count > 1)
        {
            throw new PricingException(PricingErrors.PriceConflict,
                $"Aynı öncelikte ({Describe(best)}) {top.Count} fiyat listesi çakışıyor; hangisinin uygulanacağı belirsiz.");
        }

        return Build(top[0], quantity, $"{Describe(best)} uygulandı.");
    }

    /// <summary>Aralıklar çakışamaz; fiyat negatif olamaz; ücretsiz kademenin fiyatı 0 olmalıdır.</summary>
    public static void ValidateTiers(PriceListEntry entry)
    {
        var ordered = entry.Tiers.OrderBy(t => t.MinQuantity).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            PriceTier tier = ordered[i];
            if (tier.UnitPrice < 0)
            {
                throw new PricingException(PricingErrors.NegativePrice, "Birim fiyat negatif olamaz.");
            }

            if (tier.IsFree && tier.UnitPrice != 0)
            {
                throw new PricingException(PricingErrors.NegativePrice, "Ücretsiz kademenin fiyatı 0 olmalıdır.");
            }

            if (tier.MinQuantity < 0 || (tier.MaxQuantity is { } max && max <= tier.MinQuantity))
            {
                throw new PricingException(PricingErrors.TierOverlap, "Kademe aralığı geçersiz.");
            }

            if (i > 0 && (ordered[i - 1].MaxQuantity is not { } prevMax || prevMax > tier.MinQuantity))
            {
                throw new PricingException(PricingErrors.TierOverlap, "Miktar kademeleri çakışıyor.");
            }
        }
    }

    private static bool IsValid(PriceListEntry entry, DateTimeOffset at) =>
        entry.Published && (entry.ValidFrom is null || at >= entry.ValidFrom) && (entry.ValidTo is null || at < entry.ValidTo);

    /// <summary>Toplam bedel; kademe bulunamıyorsa null (ücretsiz ise 0 ve tanım ayrı döner).</summary>
    private static decimal? Price(PriceListEntry entry, decimal quantity)
    {
        var tiers = entry.Tiers.OrderBy(t => t.MinQuantity).ToList();
        if (tiers.Count == 0 || quantity < tiers[0].MinQuantity)
        {
            return null;
        }

        if (entry.Method == TierMethod.Volume)
        {
            PriceTier? tier = tiers.LastOrDefault(t => quantity >= t.MinQuantity && (t.MaxQuantity is null || quantity < t.MaxQuantity));
            return tier is null ? null : quantity * tier.UnitPrice;
        }

        // Graduated: her aralık kendi dilimi için; bantlar boşluksuz olmalı ve miktarı tamamen kapsamalı.
        decimal total = 0m;
        decimal covered = tiers[0].MinQuantity;
        foreach (PriceTier tier in tiers)
        {
            if (tier.MinQuantity != covered)
            {
                return null;
            }

            decimal upper = tier.MaxQuantity is { } m ? Math.Min(m, quantity) : quantity;
            if (upper > tier.MinQuantity)
            {
                total += (upper - tier.MinQuantity) * tier.UnitPrice;
            }

            if (tier.MaxQuantity is null || quantity <= tier.MaxQuantity)
            {
                return total;
            }

            covered = tier.MaxQuantity.Value;
        }

        return null;
    }

    private static PriceSelection Build(PriceListEntry entry, decimal quantity, string reason)
    {
        decimal total = Price(entry, quantity)!.Value;
        bool free = total == 0m && entry.Tiers.Any(t => t.IsFree);
        string how = entry.Method == TierMethod.Volume ? "tüm miktara tek kademe fiyatı" : "her aralığa ayrı fiyat";
        return new PriceSelection(entry, quantity, total, free,
        [
            reason,
            $"Liste {entry.ListId} sürüm {entry.Version}, {entry.Currency}, yöntem: {how}.",
            free ? "Ücretsiz satış olarak tanımlı (eksik fiyat değil)." : $"Toplam bedel (indirim/vergi öncesi): {total}.",
        ]);
    }

    private static string Describe(PriceListScope scope) => scope switch
    {
        PriceListScope.Explicit => "açıkça seçilen liste",
        PriceListScope.Customer => "müşteriye özel liste",
        PriceListScope.SegmentOrChannel => "segment/kanal listesi",
        _ => "tenant varsayılan listesi",
    };
}
