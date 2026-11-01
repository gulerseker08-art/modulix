namespace Modulix.Modules.Support.Domain;

// Diğer modüllerle entegrasyon (ADR-0024, dilim 5). Destek başka modüllerin tablolarına bağlanmaz: ilişkili kayıt, bağlanırken kayıt boru hattı üzerinden
// çağıranın kendi yetkisiyle okunarak doğrulanır; burada yalnızca anahtar, kimlik ve bağlandığı andaki etiket saklanır (yabancı anahtar yoktur).

/// <summary>
/// Talebin başka bir modüldeki kayda (sözleşme, sipariş, teslimat) bağı. Kayıt bağlama anında çağıranın yetkisi ve veri kapsamıyla okunur, talebin müşterisiyle aynı müşteriye ait olmalıdır.
/// <see cref="Label"/> bağlandığı andaki numara ve başlıktır (kayıt sonradan değişse ya da modül kapansa da talepte okunur kalır); canlı veri değildir. Bağlantı sonradan kaldırılabilir
/// (tarihçeye yazılır); silinen kayıt bağı kendiliğinden temizlenmez, etiket kalır.
/// </summary>
public sealed class TicketLink : SupportEntity
{
    public const int EntityKeyMaxLength = 60;
    public const int LabelMaxLength = 200;

    public Guid TicketId { get; internal set; }

    /// <summary>Hedef kayıt türü (<see cref="Lifecycle.TicketLinkKinds"/>).</summary>
    public string EntityKey { get; internal set; } = string.Empty;

    public Guid RecordId { get; internal set; }

    public string Label { get; internal set; } = string.Empty;

    public Guid? CreatedBy { get; internal set; }
}
