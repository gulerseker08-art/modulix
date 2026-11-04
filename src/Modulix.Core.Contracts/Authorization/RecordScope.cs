namespace Modulix.Core.Contracts.Authorization;

/// <summary>
/// Rolün kayıt kapsamı (Y8, ADR-0026): rolün izinleri "hangi tür kaydı", kapsam "hangi kayıtları" söyler. Yalnızca sorumlu
/// alanı olan kayıt türlerine uygulanır. Kişinin birden fazla rolü varsa her kapsamın kayıt sahibi kümesi birleştirilir
/// (<see cref="RecordScopeRules"/>). Sayısal değerler kalıcıdır ve genişliği göstermez; genişlik <see cref="RecordScopeRules.Breadth"/>'tedir.
/// </summary>
public enum RecordScope
{
    /// <summary>Firmadaki bütün kayıtlar.</summary>
    All = 1,

    /// <summary>Sorumlusu kişinin kendisi ya da ekip arkadaşlarından biri olan kayıtlar.</summary>
    Team = 2,

    /// <summary>Yalnızca sorumlusu kişinin kendisi olan kayıtlar.</summary>
    Own = 3,

    /// <summary>Sorumlusu kişinin departmanındaki herhangi biri olan kayıtlar (ADR-0026).</summary>
    Unit = 4,

    /// <summary>Sorumlusu kişinin departmanında ya da onun alt departmanlarında olan kayıtlar (ADR-0026).</summary>
    UnitAndBelow = 5,
}

/// <summary>Kayıt kapsamlarının genişlik ve kapsama kuralları; sayısal değere bakılmaz.</summary>
public static class RecordScopeRules
{
    /// <summary>
    /// Gösterim ve kaba karşılaştırma için genişlik sırası (büyük = geniş). Ekip ve departman karşılaştırılamaz kapsamlardır
    /// (biri diğerini kapsamaz); sıra yalnızca tek bir "en geniş" değer göstermek içindir, yetki kararı için <see cref="Covers"/> kullanılır.
    /// </summary>
    public static int Breadth(RecordScope scope) => scope switch
    {
        RecordScope.All => 5,
        RecordScope.UnitAndBelow => 4,
        RecordScope.Unit => 3,
        RecordScope.Team => 2,
        _ => 1,
    };

    /// <summary>Kapsamlardan gösterim için en genişi; hiç yoksa <see cref="RecordScope.Own"/>.</summary>
    public static RecordScope Widest(IEnumerable<RecordScope> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        return scopes.OrderByDescending(Breadth).DefaultIfEmpty(RecordScope.Own).First();
    }

    /// <summary>
    /// <paramref name="holder"/> kapsamındaki kişi <paramref name="wanted"/> kapsamının gördüğü her kaydı da görür mü.
    /// "Tümü" hepsini, "departman ve altı" departmanı ve kendi kayıtlarını, "departman" ve "ekip" kendi kayıtlarını kapsar;
    /// ekip ile departman birbirini kapsamaz.
    /// </summary>
    public static bool Covers(RecordScope holder, RecordScope wanted) =>
        holder == RecordScope.All
        || holder == wanted
        || wanted == RecordScope.Own
        || (holder == RecordScope.UnitAndBelow && wanted == RecordScope.Unit);

    /// <summary>
    /// Kapsamı platform tavanına (ADR-0030) indirger: tavan kapsamı kapsıyorsa kapsam aynen kalır; kapsam tavanı kapsıyorsa
    /// (daha geniş) tavan olur; ikisi karşılaştırılamazsa (ör. tavan ekip, kapsam departman) güvenli varsayılan
    /// <see cref="RecordScope.Own"/>'dır.
    /// </summary>
    public static RecordScope Clamp(RecordScope scope, RecordScope ceiling)
    {
        if (Covers(ceiling, scope))
        {
            return scope;
        }

        return Covers(scope, ceiling) ? ceiling : RecordScope.Own;
    }

    /// <summary>Kapsamlardan herhangi biri <paramref name="wanted"/> kapsamını kapsıyor mu.</summary>
    public static bool CoversAny(IEnumerable<RecordScope> holders, RecordScope wanted)
    {
        ArgumentNullException.ThrowIfNull(holders);
        return holders.Any(holder => Covers(holder, wanted));
    }
}
