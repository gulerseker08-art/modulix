using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Modules.Contracts.Domain;

// Sözleşme modülü entity'leri (ADR-0022). Alanlar kayıt depolarından (Records) doğrulanarak yazılır; uzunluk sınırları hem
// doğrulamada hem veritabanı kolonlarında aynı sabitlerden gelir. Müşteri ve kişiler ortak kayıttır (core.party / core.person).

/// <summary>Tüm sözleşme kayıtlarının ortak alanları. TenantId kaydederken aktif tenant'tan atanır.</summary>
public abstract class ContractsEntity : ITenantEntity, ICompanyShared
{
    protected ContractsEntity()
    {
        Id = Guid.CreateVersion7();
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        Version = 1;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Her değişiklikte artar; eşzamanlı güncellemeleri yakalamak için kullanılır (409).</summary>
    public long Version { get; private set; }

    /// <summary>Firmanın tanımladığı özel alanların değerleri (Y9a), JSON nesnesi; boşsa null.</summary>
    public string? CustomFields { get; internal set; }

    internal void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}

/// <summary>Bir kullanıcıya atanabilen kayıt. Sorumlu, aktif tenant'ın etkin üyesi olmalıdır.</summary>
public interface IOwnedEntity
{
    Guid? OwnerId { get; }
}

/// <summary>
/// Sözleşme ana kaydı. Başlık güncel taslağı taşır; onaya/imzaya giden içerik sürüm tablosunda dondurulur (ikinci dilim).
/// Hazırlık, onay, imza ve yürürlük durumları ayrı alanlardır ve yalnızca iş akışı eylemleriyle yazılır.
/// </summary>
public sealed class Contract : ContractsEntity, IOwnedEntity
{
    public const int NumberMaxLength = 30;
    public const int TitleMaxLength = 200;
    public const int TextMaxLength = 4000;
    public const int StatusMaxLength = 20;
    public const int ReasonMaxLength = 500;
    public const int CurrencyLength = 3;
    public const int SummaryMaxLength = 2000;

    /// <summary>Tenant içinde benzersiz numara (SZ-20261031-AB12CD).</summary>
    public string Number { get; internal set; } = string.Empty;

    public string Title { get; internal set; } = string.Empty;

    /// <summary>Sözleşme türü (Y9b listesi <c>contracts.contract-type</c>).</summary>
    public string Type { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    /// <summary>Müşteri: ortak taraf kaydı (core.party, ADR-0006).</summary>
    public Guid PartyId { get; internal set; }

    /// <summary>Müşteri tarafındaki ana ilgili kişi (core.person); seçildiyse müşteri tarafına bağlı olmalıdır.</summary>
    public Guid? PersonId { get; internal set; }

    public Guid? OwnerId { get; internal set; }

    /// <summary>Yenilenen/değiştirilen önceki sözleşme (aynı tenant).</summary>
    public Guid? PreviousContractId { get; internal set; }

    /// <summary>Kaynak teklif (kabul edilmiş revizyon); CRM tablosuna bağ yoktur, kimlik ve numara anlık görüntüdür. Sunucu yazar.</summary>
    public Guid? SourceQuoteId { get; internal set; }

    public string? SourceQuoteNumber { get; internal set; }

    public int? SourceQuoteRevisionNo { get; internal set; }

    /// <summary>Müşterinin kabul ettiği teklif belgesinin SHA-256 özeti (teklif sürümü sabitlenmiştir).</summary>
    public string? SourceQuoteSha256 { get; internal set; }

    /// <summary>Kaynak teklifin fırsatı (fırsat ekranından sözleşme sürecini izlemek için).</summary>
    public Guid? SourceOpportunityId { get; internal set; }

    public DateOnly? StartDate { get; internal set; }

    /// <summary>Belirsiz süreli sözleşmede boştur.</summary>
    public DateOnly? EndDate { get; internal set; }

    public bool IsIndefinite { get; internal set; }

    public string Currency { get; internal set; } = "TRY";

    public bool PriceIncludesTax { get; internal set; }

    /// <summary>Elle girilen toplam sözleşme bedeli (kalem toplamından bağımsız, ör. çerçeve sözleşme üst limiti). Hassas alan.</summary>
    public decimal? ContractValue { get; internal set; }

    public string? PaymentTerms { get; internal set; }

    /// <summary><see cref="Lifecycle.RenewalMode"/>.</summary>
    public string RenewalMode { get; internal set; } = Lifecycle.RenewalMode.None;

    /// <summary>Yenileme süresi (ay); yenilemesiz sözleşmede boş.</summary>
    public int? RenewalPeriodMonths { get; internal set; }

    /// <summary>Yenilememe/fesih bildirimi için bitişten önce gereken gün sayısı.</summary>
    public int? NoticePeriodDays { get; internal set; }

    /// <summary>Son bildirim tarihi: bitiş − bildirim süresi. Sunucu hesaplar; istemci yazamaz.</summary>
    public DateOnly? NoticeDeadline { get; internal set; }

    /// <summary><see cref="Lifecycle.Confidentiality"/>.</summary>
    public string Confidentiality { get; internal set; } = Lifecycle.Confidentiality.Internal;

    /// <summary><see cref="Lifecycle.PreparationStatus"/>.</summary>
    public string PreparationStatus { get; internal set; } = Lifecycle.PreparationStatus.Draft;

    /// <summary><see cref="Lifecycle.ApprovalStatus"/>.</summary>
    public string ApprovalStatus { get; internal set; } = Lifecycle.ApprovalStatus.NotRequired;

    /// <summary><see cref="Lifecycle.SignatureStatus"/>.</summary>
    public string SignatureStatus { get; internal set; } = Lifecycle.SignatureStatus.NotSent;

    /// <summary><see cref="Lifecycle.EffectiveStatus"/>.</summary>
    public string EffectiveStatus { get; internal set; } = Lifecycle.EffectiveStatus.NotInForce;

    /// <summary>Son geçişin gerekçesi (ret, iptal, fesih...).</summary>
    public string? StatusReason { get; internal set; }

    /// <summary>Güncel taslak sürüm numarası (1'den başlar; her revizyonda artar).</summary>
    public int CurrentVersionNo { get; internal set; } = 1;

    /// <summary>Yürürlükteki (imzalanmış) sürüm; henüz yoksa boş.</summary>
    public int? EffectiveVersionNo { get; internal set; }

    // ---- Sunucu hesabı (QuoteCalculator); istemci yazamaz. Tek seferlik kalemlerin toplamı; dönemsel kalemler özet JSON'dadır.

    public decimal NetTotal { get; internal set; }

    public decimal TaxTotal { get; internal set; }

    public decimal GrossTotal { get; internal set; }

    /// <summary>Dönemsel kalemlerin dönem başına özeti (JSON); sözleşme süresiyle çarpılmış genel toplam üretilmez.</summary>
    public string? RecurringSummary { get; internal set; }

    public DateTimeOffset? CalculatedAt { get; internal set; }

    public Guid? CreatedBy { get; internal set; }

    public Guid? UpdatedBy { get; internal set; }
}

/// <summary>
/// Sözleşmenin tarafı ya da ilgili kişisi (müşteri dışındaki taraflar, imzacı, fatura/teknik yetkili...). Taraf ortak kayıttır;
/// kişi tek başına da (örneğin müşteri yetkilisi) eklenebilir.
/// </summary>
public sealed class ContractParty : ContractsEntity
{
    public const int RoleMaxLength = 30;
    public const int NotesMaxLength = 1000;

    public Guid ContractId { get; internal set; }

    /// <summary><see cref="Lifecycle.PartyRoles"/>.</summary>
    public string Role { get; internal set; } = Lifecycle.PartyRoles.Other;

    public Guid? PartyId { get; internal set; }

    public Guid? PersonId { get; internal set; }

    public int Position { get; internal set; }

    public string? Notes { get; internal set; }
}

/// <summary>
/// Sözleşme kalemi: ürün/hizmet bilgisi kopya olarak saklanır (katalog ya da teklif sonradan değişse de kalem aynı kalır).
/// Tutarlar sunucuda hesaplanır.
/// </summary>
public sealed class ContractLine : ContractsEntity
{
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 50;
    public const int TextMaxLength = 2000;
    public const int KindMaxLength = 20;
    public const int UnitMaxLength = 50;
    public const int PeriodMaxLength = 20;

    public Guid ContractId { get; internal set; }

    /// <summary>Revizyonlar ve teklif kalemi arasında izleme için kararlı anahtar.</summary>
    public Guid StableKey { get; internal set; }

    public int Position { get; internal set; }

    /// <summary><see cref="Lifecycle.LineKinds"/>.</summary>
    public string Kind { get; internal set; } = Lifecycle.LineKinds.Service;

    public string? ProductCode { get; internal set; }

    public string Name { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    public decimal Quantity { get; internal set; } = 1m;

    public string? UnitName { get; internal set; }

    public decimal UnitPrice { get; internal set; }

    public decimal DiscountPercent { get; internal set; }

    public decimal TaxRatePercent { get; internal set; }

    /// <summary>Boşsa tek seferlik; doluysa dönem (<see cref="Lifecycle.BillingPeriods"/>).</summary>
    public string? BillingPeriod { get; internal set; }

    /// <summary>Kaynak teklif kaleminin kararlı anahtarı (tekliften oluşturulduysa).</summary>
    public Guid? SourceQuoteLineKey { get; internal set; }

    /// <summary>Kalemin geldiği teklif. Sözleşme iptal edilince boşaltılır (kalem yeniden sözleşmeye alınabilir); aynı teklif kalemi ikinci kez kullanılamaz.</summary>
    public Guid? SourceQuoteId { get; internal set; }

    // ---- Sunucu hesabı

    public decimal ListAmount { get; internal set; }

    public decimal LineDiscount { get; internal set; }

    public decimal Net { get; internal set; }

    public decimal Tax { get; internal set; }

    public decimal Gross { get; internal set; }
}

/// <summary>Sözleşme tarihçesi: her geçiş ve işaret (kim, ne zaman, hangi durumdan hangisine, gerekçe). Eklenir, değişmez.</summary>
public sealed class ContractEvent : ContractsEntity
{
    public const int KindMaxLength = 40;
    public const int StatusSummaryMaxLength = 100;

    public Guid ContractId { get; internal set; }

    public string Kind { get; internal set; } = string.Empty;

    public Guid? ActorId { get; internal set; }

    public string? FromStatus { get; internal set; }

    public string? ToStatus { get; internal set; }

    public string? Reason { get; internal set; }
}

/// <summary>
/// Dondurulmuş sözleşme sürümü: onaya (ya da imzaya) giden içeriğin değişmez anlık görüntüsü ve SHA-256 özeti. Başlık ve kalemler
/// sonradan değişse de bu satır aynı kalır; onay ve imza kararları bu özete bağlıdır. Sözleşme başına sürüm numarası tekildir.
/// </summary>
public sealed class ContractVersion : ContractsEntity
{
    public const int KindMaxLength = 20;
    public const int HashLength = 64;

    public Guid ContractId { get; internal set; }

    public int VersionNo { get; internal set; }

    /// <summary><see cref="Lifecycle.VersionKinds"/>: niçin donduruldu (Approval, Signature).</summary>
    public string Kind { get; internal set; } = Lifecycle.VersionKinds.Approval;

    /// <summary><see cref="Lifecycle.VersionStatus"/>.</summary>
    public string Status { get; internal set; } = Lifecycle.VersionStatus.Frozen;

    /// <summary>Anlık görüntü (JSON, <c>ContractSnapshot</c>).</summary>
    public string Snapshot { get; internal set; } = string.Empty;

    public string Sha256 { get; internal set; } = string.Empty;

    /// <summary>Dondurma gerekçesi / değişiklik notu.</summary>
    public string? Reason { get; internal set; }

    public Guid? CreatedBy { get; internal set; }
}

/// <summary>
/// Sözleşme şablonu (ADR-0022, dilim 4). <see cref="Body"/> çalışma kopyasıdır (taslak düzenleme); yayımlama değişmez bir
/// <see cref="ContractTemplateVersion"/> üretir ve belgeler yalnızca yayımlanmış sürümle üretilir. Düzenleme ve yayımlama ayrı izinlerdir.
/// Örnek metinler <see cref="IsExample"/> ile açıkça işaretlenir; hukuki metin kurumun sorumluluğundadır.
/// </summary>
public sealed class ContractTemplate : ContractsEntity
{
    public const int NameMaxLength = 150;
    public const int ScopeMaxLength = 100;
    public const int DescriptionMaxLength = 2000;
    public const int BodyMaxLength = Lifecycle.ContractTemplateEngine.BodyMaxLength;

    public string Name { get; internal set; } = string.Empty;

    /// <summary><see cref="Lifecycle.TemplateLanguages"/>.</summary>
    public string Language { get; internal set; } = "tr";

    /// <summary>Boşsa her sözleşme türünde kullanılır; doluysa yalnızca o türde (sözleşme türü liste anahtarı).</summary>
    public string? ContractType { get; internal set; }

    /// <summary>Kullanım kapsamı (serbest metin, ör. "Hizmet sözleşmeleri").</summary>
    public string? UsageScope { get; internal set; }

    public string? Description { get; internal set; }

    public string Body { get; internal set; } = string.Empty;

    /// <summary>Çalışma kopyasının özeti; son yayımlanan sürümün özetinden farklıysa "yayımlanmamış değişiklik" vardır.</summary>
    public string BodySha256 { get; internal set; } = string.Empty;

    public bool IsExample { get; internal set; }

    /// <summary><see cref="Lifecycle.TemplateStatus"/>.</summary>
    public string Status { get; internal set; } = Lifecycle.TemplateStatus.Draft;

    /// <summary>Son yayımlanan sürüm numarası; hiç yayımlanmadıysa boş.</summary>
    public int? PublishedVersionNo { get; internal set; }

    public Guid? CreatedBy { get; internal set; }

    public Guid? UpdatedBy { get; internal set; }
}

/// <summary>Yayımlanmış şablon sürümü: metnin değişmez kopyası. Şablon sonradan değişse de bu satır ve ondan üretilen belgeler aynı kalır.</summary>
public sealed class ContractTemplateVersion : ContractsEntity
{
    public Guid TemplateId { get; internal set; }

    public int VersionNo { get; internal set; }

    public string Name { get; internal set; } = string.Empty;

    public string Body { get; internal set; } = string.Empty;

    public string Sha256 { get; internal set; } = string.Empty;

    public bool IsExample { get; internal set; }

    public string? Notes { get; internal set; }

    public Guid? PublishedBy { get; internal set; }
}

/// <summary>
/// Üretilen sözleşme belgesi (PDF). Hangi sözleşme sürümü, şablon sürümü ve veri anlık görüntüsüyle üretildiği, dosyanın SHA-256 özeti ve kimliği
/// saklanır; satır değişmez. <see cref="IsFinal"/>: sözleşme sürümü dondurulmuşsa (onaya/imzaya gitmiş) belge kesin içeriktir; aksi halde taslak kopyadır.
/// </summary>
public sealed class ContractDocument : ContractsEntity
{
    public const int FileNameMaxLength = 160;

    public Guid ContractId { get; internal set; }

    public int ContractVersionNo { get; internal set; }

    public bool IsFinal { get; internal set; }

    public Guid TemplateId { get; internal set; }

    public int TemplateVersionNo { get; internal set; }

    public string TemplateName { get; internal set; } = string.Empty;

    public bool IsExample { get; internal set; }

    public string FileName { get; internal set; } = string.Empty;

    public string ContentType { get; internal set; } = "application/pdf";

    public int SizeBytes { get; internal set; }

    public string Sha256 { get; internal set; } = string.Empty;

    /// <summary>Belgenin üretildiği veri (JSON: değişken değerleri, kalemler, taraflar); müşteri kartı ya da sözleşme sonradan değişse de belge verisi sabittir.</summary>
    public string Snapshot { get; internal set; } = string.Empty;

    public byte[] Content { get; internal set; } = [];

    public Guid? CreatedBy { get; internal set; }
}
