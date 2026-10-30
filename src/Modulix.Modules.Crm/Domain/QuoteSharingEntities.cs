namespace Modulix.Modules.Crm.Domain;

// Teklif belgesi, paylaşım bağlantısı ve müşteri kararı (ADR-0019, dilim 3). Üçü de teklif toplamının (aggregate) parçasıdır:
// yazma yalnızca teklif geçişiyle aynı işlemde yapılır (QuoteWorkflowGate.Effect), böylece sürüm denetimi ve audit ortaktır.

/// <summary>
/// Müşteriye sunulan teklif belgesinin sabitlenmiş kopyası. Teklif sunulduğunda üretilir; PDF dosyası, içeriğin SHA-256 özeti ve
/// belgenin üretildiği andaki veri özeti (<see cref="Snapshot"/>) birlikte saklanır. Eklenir, değiştirilmez: firma kartı ya da katalog
/// sonradan değişse de müşterinin gördüğü belge aynı kalır. Maliyet ve iç not hiçbir zaman içine girmez.
/// </summary>
public sealed class QuoteDocument : CrmEntity
{
    public const int FileNameMaxLength = 120;
    public const int HashLength = 64;

    public Guid QuoteId { get; internal set; }

    public int RevisionNo { get; internal set; }

    public string FileName { get; internal set; } = string.Empty;

    public string ContentType { get; internal set; } = "application/pdf";

    public int SizeBytes { get; internal set; }

    /// <summary>PDF baytlarının SHA-256 özeti (onaltılık, büyük harf). Müşteri kabulü bu özete bağlanır.</summary>
    public string Sha256 { get; internal set; } = string.Empty;

    /// <summary>Şablon sürümü; belge tasarımı değişse de eski belge eski haliyle kalır.</summary>
    public int TemplateVersion { get; internal set; } = 1;

    /// <summary>Müşteri sayfasının gösterdiği veri (JSON); PDF ile aynı kaynaktan üretilir.</summary>
    public string Snapshot { get; internal set; } = string.Empty;

    public byte[] Content { get; internal set; } = [];

    public Guid? CreatedBy { get; internal set; }
}

/// <summary>
/// Müşteri paylaşım bağlantısı. Belirteç yalnızca oluşturulurken bir kez gösterilir; veritabanında yalnızca SHA-256 özeti durur.
/// Bağlantı belirli bir belgeye bağlıdır, süresi dolar ve iptal edilebilir.
/// </summary>
public sealed class QuoteShareLink : CrmEntity
{
    public const int NameMaxLength = 200;
    public const int EmailMaxLength = 254;

    public Guid QuoteId { get; internal set; }

    public Guid DocumentId { get; internal set; }

    public string TokenHash { get; internal set; } = string.Empty;

    /// <summary>Belirtecin son karakterleri; yöneticinin bağlantıyı tanıması içindir, tek başına işe yaramaz.</summary>
    public string TokenHint { get; internal set; } = string.Empty;

    public string? RecipientName { get; internal set; }

    public string? RecipientEmail { get; internal set; }

    public DateTimeOffset ExpiresAt { get; internal set; }

    public DateTimeOffset? RevokedAt { get; internal set; }

    public Guid? RevokedBy { get; internal set; }

    public string? RevokeReason { get; internal set; }

    /// <summary>İlk görüntüleme; sonraki görüntülemeler yazılmaz (müşterinin her açışı teklif sürümünü değiştirmez).</summary>
    public DateTimeOffset? FirstViewedAt { get; internal set; }

    public Guid? CreatedBy { get; internal set; }
}

/// <summary>
/// Müşterinin kabul ya da ret kararı ve kanıtı: kim, hangi belgeyi (özet), hangi bağlantıyla, hangi onay metniyle karar verdi.
/// Teklif geçişiyle aynı işlemde eklenir; eklenir, değiştirilmez.
/// </summary>
public sealed class QuoteDecision : CrmEntity
{
    public const int NameMaxLength = 200;
    public const int StatementMaxLength = 1000;
    public const int AgentMaxLength = 300;

    public Guid QuoteId { get; internal set; }

    public Guid ShareLinkId { get; internal set; }

    public Guid DocumentId { get; internal set; }

    public string DocumentSha256 { get; internal set; } = string.Empty;

    /// <summary><c>Accepted</c> ya da <c>Rejected</c>.</summary>
    public string Decision { get; internal set; } = string.Empty;

    public string SignerName { get; internal set; } = string.Empty;

    public string? SignerEmail { get; internal set; }

    public string? SignerTitle { get; internal set; }

    public string? Reason { get; internal set; }

    /// <summary>Müşterinin onayladığı metnin kendisi (karar anında ekranda gösterilen cümle).</summary>
    public string? TermsStatement { get; internal set; }

    /// <summary>İstemci adresi, son bölümü maskelenmiş (192.168.1.x); kanıt amaçlıdır, kimlik değildir.</summary>
    public string? ClientAddressMasked { get; internal set; }

    public string? UserAgent { get; internal set; }
}
