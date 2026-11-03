namespace Modulix.Modules.Crm.Reporting;

/// <summary>Bir kur kaydı: 1 <paramref name="From"/> = <paramref name="Rate"/> <paramref name="To"/>, <paramref name="ValidOn"/> gününden itibaren.</summary>
public sealed record RateEntry(string From, string To, decimal Rate, DateOnly ValidOn);

/// <summary>Bulunan kur: <see cref="Inverse"/> ters çiftten (1/kur) türetildiyse true; <see cref="ValidOn"/> kullanılan kaydın günü.</summary>
public sealed record RateLookup(decimal Rate, DateOnly ValidOn, bool Inverse);

/// <summary>
/// Kur çözümleme (saf). Belirli bir güne göre o güne ya da öncesine ait en yeni kayıt alınır; doğrudan çift yoksa ters çift kullanılır.
/// Üçüncü para birimi üzerinden çapraz kur <b>türetilmez</b> ve kur bulunamazsa 1 varsayılmaz: çağıran "kur yok" durumunu raporlamak zorundadır.
/// Gelecek tarihli kur, o günden önceki hesaba girmez.
/// </summary>
public sealed class RateBook(IEnumerable<RateEntry> rates)
{
    private readonly ILookup<(string, string), RateEntry> byPair = rates
        .Where(r => r.Rate > 0)
        .ToLookup(r => (r.From.ToUpperInvariant(), r.To.ToUpperInvariant()));

    public static RateBook Empty { get; } = new([]);

    /// <summary>Aynı para birimi için kur 1'dir (kayıt gerekmez). Bulunamazsa null.</summary>
    public RateLookup? Find(string from, string to, DateOnly asOf)
    {
        var source = from.ToUpperInvariant();
        var target = to.ToUpperInvariant();
        if (source == target)
        {
            return new RateLookup(1m, asOf, false);
        }

        var direct = byPair[(source, target)].Where(r => r.ValidOn <= asOf).OrderByDescending(r => r.ValidOn).FirstOrDefault();
        if (direct is not null)
        {
            return new RateLookup(direct.Rate, direct.ValidOn, false);
        }

        var reverse = byPair[(target, source)].Where(r => r.ValidOn <= asOf).OrderByDescending(r => r.ValidOn).FirstOrDefault();
        return reverse is null ? null : new RateLookup(1m / reverse.Rate, reverse.ValidOn, true);
    }

    /// <summary>Tutarı çevirir; kur yoksa <c>Converted</c> null döner (sessizce 1 kabul edilmez).</summary>
    public (decimal? Converted, RateLookup? Rate) Convert(decimal amount, string from, string to, DateOnly asOf)
    {
        var rate = Find(from, to, asOf);
        return rate is null ? (null, null) : (amount * rate.Rate, rate);
    }
}
