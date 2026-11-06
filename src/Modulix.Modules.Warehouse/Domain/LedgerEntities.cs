namespace Modulix.Modules.Warehouse.Domain;

/// <summary>
/// Parti (ADR-0032 K3): bir stok kartının belirli bir malike ait, isteğe bağlı olarak bir beyanname satırına bağlı partisi. Aynı ürün farklı malik ya da beyanname
/// satırıyla ayrı partide, dolayısıyla ayrı bakiyededir. <see cref="OwnerPartyId"/> malı kimin olduğu, <see cref="BillToPartyId"/> ardiye/fatura kime kesileceğidir
/// (boşsa malik). Parti oluşturulduktan sonra ürün, malik ve beyanname satırı değişmez; hareketi olan parti silinemez.
/// </summary>
public sealed class Lot : WarehouseEntity
{
    public const int LotNoMaxLength = 50;
    public const int DescriptionMaxLength = 500;

    /// <summary>Tenant içinde benzersiz parti numarası.</summary>
    public string LotNo { get; internal set; } = string.Empty;

    public Guid StockItemId { get; internal set; }

    /// <summary>Malik (firma, ortak kayıt).</summary>
    public Guid OwnerPartyId { get; internal set; }

    /// <summary>Faturalanan taraf (firma); boşsa malik.</summary>
    public Guid? BillToPartyId { get; internal set; }

    /// <summary>Partinin dayandığı beyanname satırı (yasal gerçek, K6); gümrüksüz depoda boştur.</summary>
    public Guid? DeclarationLineId { get; internal set; }

    public string? Description { get; internal set; }

    /// <summary>Pasif parti yeni hareket almaz; geçmişi ve bakiyesi korunur.</summary>
    public bool IsActive { get; internal set; } = true;
}

/// <summary>
/// Stok hareketi (K13): defterin <b>yalnızca eklenen</b> satırı. Oluşturulduktan sonra değiştirilmez ve silinmez (uygulama reddeder, veritabanı tetikleyicisi de reddeder);
/// düzeltme güncelleme değil <see cref="ReversesMovementId"/> ile ters kayıttır. Hareketin kim tarafından, ne zaman ve hangi işlem grubuyla yapıldığı sunucuca
/// yazılır (istemciden gelen değer yok sayılır). <see cref="IdempotencyKey"/> doluysa tenant içinde benzersizdir: barkod/terminal tekrarı çift hareket üretmez.
/// </summary>
public sealed class StockMovement : WarehouseEntity
{
    public const int DocumentMaxLength = 200;
    public const int IdempotencyKeyMaxLength = 100;

    /// <summary>Hareket türü (Y9b listesi <c>warehouse.movement-type</c>); kod türün adına değil kategorisine bakar.</summary>
    public string TypeKey { get; internal set; } = string.Empty;

    public Guid LotId { get; internal set; }

    /// <summary>Çıkış yapılan lokasyon (çıkış ve transferde dolu).</summary>
    public Guid? FromLocationId { get; internal set; }

    /// <summary>Giriş yapılan lokasyon (giriş ve transferde dolu).</summary>
    public Guid? ToLocationId { get; internal set; }

    public decimal Quantity { get; internal set; }

    /// <summary>Hareketin yazıldığı an (UTC); sunucu koyar.</summary>
    public DateTimeOffset OccurredAt { get; internal set; }

    public Guid UserId { get; internal set; }

    public Guid MembershipId { get; internal set; }

    /// <summary>Aynı işlemde yazılan hareketleri (ve denetim kaydını) bağlayan kimlik.</summary>
    public Guid CorrelationId { get; internal set; }

    /// <summary>Neden (Y9b listesi <c>warehouse.movement-reason</c>, kategorisiz); isteğe bağlı.</summary>
    public string? ReasonKey { get; internal set; }

    public string? DocumentReference { get; internal set; }

    public string? IdempotencyKey { get; internal set; }

    /// <summary>Bu hareket hangi hareketin tersidir; bir hareket en çok bir kez geri alınır.</summary>
    public Guid? ReversesMovementId { get; internal set; }
}

/// <summary>
/// Bir partinin bir lokasyondaki bakiyesi. <b>Defterden türetilir</b>: yalnızca hareket kaydı ile aynı işlemde değişir, doğrudan değiştiren uç nokta yoktur (K13). Satır
/// sürümü eşzamanlılık belirteci olarak çalışır: aynı bakiyeye eşzamanlı çıkışlardan biri çakışma (409) alır, bakiye eksiye düşemez (veritabanı kısıtı da bunu korur).
/// </summary>
public sealed class StockBalance : WarehouseEntity
{
    public Guid LotId { get; internal set; }

    public Guid LocationId { get; internal set; }

    public decimal Quantity { get; internal set; }
}

/// <summary>Hareket türü kategorileri: kodun anladığı anlam (K18). Tenant türleri adlandırır, kategoriyi seçer.</summary>
public static class MovementCategories
{
    /// <summary>Giriş: hedef lokasyona stok ekler.</summary>
    public const string Inbound = "in";

    /// <summary>Çıkış: kaynak lokasyondan stok düşer.</summary>
    public const string Outbound = "out";

    /// <summary>Transfer: kaynaktan hedefe taşır (toplam değişmez).</summary>
    public const string Transfer = "transfer";

    /// <summary>Düzeltme: önceki bir hareketin <b>ters kaydı</b>; parti, lokasyon ve miktar özgün hareketten gelir.</summary>
    public const string Adjustment = "adjustment";
}

/// <summary>Hareketin bakiyeye etkisi: bir lokasyonda artış ya da azalış.</summary>
public readonly record struct BalanceEffect(Guid LocationId, decimal Delta);

/// <summary>Defter için saf kurallar (veritabanı gerekmez).</summary>
public static class LedgerRules
{
    /// <summary>
    /// Kategoriye göre lokasyon biçimi: giriş yalnızca hedef, çıkış yalnızca kaynak, transfer ikisi (farklı) ister. Düzeltme (ters kayıt) lokasyonu
    /// özgün hareketten türetir, bu yüzden burada aranmaz. Hata iletisi ya da <c>null</c> döner.
    /// </summary>
    public static string? CheckShape(string category, Guid? from, Guid? to) => category switch
    {
        MovementCategories.Inbound when from is not null => "Giriş hareketinde kaynak lokasyon verilmez.",
        MovementCategories.Inbound when to is null => "Giriş için hedef lokasyon zorunludur.",
        MovementCategories.Outbound when to is not null => "Çıkış hareketinde hedef lokasyon verilmez.",
        MovementCategories.Outbound when from is null => "Çıkış için kaynak lokasyon zorunludur.",
        MovementCategories.Transfer when from is null || to is null => "Transfer için kaynak ve hedef lokasyon zorunludur.",
        MovementCategories.Transfer when from == to => "Transferde kaynak ve hedef lokasyon aynı olamaz.",
        MovementCategories.Inbound or MovementCategories.Outbound or MovementCategories.Transfer => null,
        _ => "Bu hareket türü doğrudan kaydedilemez.",
    };

    /// <summary>Hareketin bakiyeye etkileri: önce azalışlar, sonra artışlar (yetersiz stok denetimi azalıştan başlar).</summary>
    public static IReadOnlyList<BalanceEffect> Effects(Guid? from, Guid? to, decimal quantity)
    {
        var effects = new List<BalanceEffect>(2);
        if (from is { } source)
        {
            effects.Add(new BalanceEffect(source, -quantity));
        }

        if (to is { } target)
        {
            effects.Add(new BalanceEffect(target, quantity));
        }

        return effects;
    }

    /// <summary>
    /// Ters kaydın lokasyonları: özgün hareketin kaynağı hedef, hedefi kaynak olur (girişin tersi çıkış, çıkışın tersi giriş, transferin tersi geri transfer).
    /// </summary>
    public static (Guid? From, Guid? To) Reverse(Guid? originalFrom, Guid? originalTo) => (originalTo, originalFrom);
}
