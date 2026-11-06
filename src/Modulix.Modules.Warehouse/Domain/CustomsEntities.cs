namespace Modulix.Modules.Warehouse.Domain;

/// <summary>
/// Gümrük beyannamesi başlığı (ADR-0032 K6). İthalatçı ve müşavir Core firmasıdır (K2). Tescil numarası tenant içinde benzersizdir;
/// biçim kuralı (uzunluk, desen) rejim profilindedir (K7, sonraki adım), burada yalnızca yapısal denetim vardır. Rejim kodu tenant'ın
/// listesinden (K6), durum ise kategorisi koda anlam veren listeden gelir (<see cref="WarehouseModule.DeclarationStatusCategories"/>).
/// Yasal gerçek satırlardadır (<see cref="CustomsDeclarationLine"/>).
/// </summary>
public sealed class CustomsDeclaration : WarehouseEntity
{
    public const int RegistrationNoMaxLength = 30;
    public const int OfficeMaxLength = 100;
    public const int ExternalReferenceMaxLength = 100;
    public const int DescriptionMaxLength = 1000;

    /// <summary>Tescil numarası (büyük harfe çevrilir); tenant içinde benzersiz.</summary>
    public string RegistrationNo { get; internal set; } = string.Empty;

    public DateOnly RegistrationDate { get; internal set; }

    /// <summary>Gümrük idaresi (serbest metin; idare listesi doğrulanmadı).</summary>
    public string? CustomsOffice { get; internal set; }

    /// <summary>Rejim kodu (Y9b listesi <c>warehouse.customs-regime</c>); koda sabitlenmez.</summary>
    public string RegimeKey { get; internal set; } = string.Empty;

    /// <summary>Beyanname durumu (Y9b listesi <c>warehouse.declaration-status</c>); kod kategoriye bakar.</summary>
    public string StatusKey { get; internal set; } = string.Empty;

    public Guid ImporterPartyId { get; internal set; }

    /// <summary>Gümrük müşaviri (firma); beyanname müşavirsiz de girilebilir.</summary>
    public Guid? BrokerPartyId { get; internal set; }

    /// <summary>Dış sistemdeki (müşavirlik yazılımı) kayıt numarası; doluysa tenant içinde benzersizdir (K8: ikinci kayıt üretmez).</summary>
    public string? ExternalReference { get; internal set; }

    public string? Description { get; internal set; }
}

/// <summary>
/// Beyanname satırı: yasal gerçek burada tutulur (K6, K17): GTİP, kap cinsi ve adedi, brüt/net kilo, menşe, miktar ve birim. Net kilo brüt
/// kilodan büyük olamaz (değişmez). Bakiye ve çıkışlar hareket defterinden türetilir (sonraki dilimler), satırda saklanmaz. Fatura değeri
/// alan seviyesi yetkiyle korunacağı için (K15) para birimi kararıyla birlikte ayrı adımda gelir.
/// </summary>
public sealed class CustomsDeclarationLine : WarehouseEntity
{
    public const int DescriptionMaxLength = 500;

    public Guid DeclarationId { get; internal set; }

    /// <summary>Beyannamedeki sıra numarası (1'den); beyanname içinde benzersiz.</summary>
    public int LineNo { get; internal set; }

    /// <summary>GTİP: yalnızca rakamlar, metin (baştaki sıfır kaybolmaz; <c>GtipRules</c>). Katalogda yoksa formda uyarı çıkar, kayıt engellenmez.</summary>
    public string Gtip { get; internal set; } = string.Empty;

    /// <summary>Eşya tanımı.</summary>
    public string Description { get; internal set; } = string.Empty;

    /// <summary>Kap cinsi (Y9b listesi <c>warehouse.container-type</c>, K10).</summary>
    public string ContainerTypeKey { get; internal set; } = string.Empty;

    public int ContainerCount { get; internal set; }

    public decimal GrossKg { get; internal set; }

    public decimal NetKg { get; internal set; }

    /// <summary>Menşe ülke (Y9b listesi <c>warehouse.origin-country</c>, ISO 3166-1 alfa-2 anahtarlar, K10).</summary>
    public string OriginCountryKey { get; internal set; } = string.Empty;

    public decimal Quantity { get; internal set; }

    /// <summary>Miktar birimi (Y9b listesi <c>warehouse.declaration-line-unit</c>).</summary>
    public string UnitKey { get; internal set; } = string.Empty;
}

/// <summary>Beyanname satırı için saf kurallar (veritabanı gerekmez).</summary>
public static class DeclarationLineRules
{
    /// <summary>Net kilo brüt kilodan büyük olamaz (K10, K17 değişmezi).</summary>
    public static bool NetWithinGross(decimal grossKg, decimal netKg) => netKg <= grossKg;
}
