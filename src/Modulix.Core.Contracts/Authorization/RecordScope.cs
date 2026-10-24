namespace Modulix.Core.Contracts.Authorization;

/// <summary>
/// Rolün kayıt kapsamı (Y8): rolün izinleri "hangi tür kaydı", kapsam "hangi kayıtları" söyler. Yalnızca sorumlu alanı
/// olan kayıt türlerine uygulanır. Kişinin birden fazla rolü varsa en geniş kapsam geçerlidir (değeri en küçük olan).
/// </summary>
public enum RecordScope
{
    /// <summary>Firmadaki bütün kayıtlar.</summary>
    All = 1,

    /// <summary>Sorumlusu kişinin kendisi ya da ekip arkadaşlarından biri olan kayıtlar.</summary>
    Team = 2,

    /// <summary>Yalnızca sorumlusu kişinin kendisi olan kayıtlar.</summary>
    Own = 3,
}
