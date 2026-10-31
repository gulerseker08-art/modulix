namespace Modulix.Modules.Contracts.Domain;

// İmza varlıkları (ADR-0022, dilim 6). Alanlar iş akışı servisinden yazılır (genel kayıt ucu yoktur); satırlar sözleşme silinince kalır
// (imzalı sözleşme silinemez), imza isteği ve olay günlüğü değişmez kanıt niteliğindedir.

/// <summary>
/// Bir sözleşme sürümünün imza isteği: hangi (kesin) belge, kimler, hangi yöntemle ve ne zamana kadar. Belgenin SHA-256 özeti istekle birlikte sabitlenir:
/// imzalanan şey bu özetle tanımlanır. Sürüm başına birden çok istek olabilir (reddedilen, süresi dolan ya da iptal edilen istekten sonra yenisi), aynı anda
/// yalnızca biri açıktır.
/// </summary>
public sealed class ContractSignatureRequest : ContractsEntity
{
    public const int ReferenceMaxLength = 100;

    public Guid ContractId { get; internal set; }

    public int VersionNo { get; internal set; }

    public Guid DocumentId { get; internal set; }

    /// <summary>İmzalanacak belgenin SHA-256 özeti (istek anında belgeden alınır).</summary>
    public string DocumentSha256 { get; internal set; } = string.Empty;

    /// <summary><see cref="Lifecycle.SignatureMethod"/>.</summary>
    public string Method { get; internal set; } = Lifecycle.SignatureMethod.Manual;

    /// <summary>Sağlayıcı adı (yöntem Provider ise); örn. "simulation".</summary>
    public string? Provider { get; internal set; }

    /// <summary>Sağlayıcıdaki istek kimliği; olaylar bununla eşleşir.</summary>
    public string? ProviderReference { get; internal set; }

    /// <summary><see cref="Lifecycle.SignatureMode"/>.</summary>
    public string Mode { get; internal set; } = Lifecycle.SignatureMode.Sequential;

    /// <summary><see cref="Lifecycle.SignatureRequestStatus"/>.</summary>
    public string Status { get; internal set; } = Lifecycle.SignatureRequestStatus.Pending;

    public DateTimeOffset ExpiresAt { get; internal set; }

    public DateTimeOffset? CompletedAt { get; internal set; }

    /// <summary>İptal/ret/süre aşımı gerekçesi.</summary>
    public string? CloseReason { get; internal set; }

    /// <summary>Sağlayıcıdan uygulanan son olay numarası (olay sırası denetimi).</summary>
    public int LastEventSequence { get; internal set; }

    public Guid? CreatedBy { get; internal set; }
}

/// <summary>
/// İmzacı: sıra, rol, zorunluluk ve durum. İç imzacı tenant üyesidir (<see cref="UserId"/>), dış imzacı müşteri tarafıdır (ad ve isteğe bağlı e-posta;
/// e-posta kişisel veridir, yalnızca sözleşmeye erişimi olanlar görür). <see cref="Assurance"/> imzanın nasıl kanıtlandığını söyler ve düzeyler
/// birbirine eşit sayılmaz.
/// </summary>
public sealed class ContractSignatory : ContractsEntity
{
    public const int NameMaxLength = 150;
    public const int CapacityMaxLength = 100;
    public const int EmailMaxLength = 254;

    public Guid RequestId { get; internal set; }

    public Guid ContractId { get; internal set; }

    /// <summary>İmza sırası (1'den); sıralı kipte küçük önce imzalar.</summary>
    public int Position { get; internal set; }

    /// <summary><see cref="Lifecycle.SignatoryKind"/>.</summary>
    public string Kind { get; internal set; } = Lifecycle.SignatoryKind.External;

    /// <summary>İç imzacının kullanıcısı.</summary>
    public Guid? UserId { get; internal set; }

    public string Name { get; internal set; } = string.Empty;

    public string? Email { get; internal set; }

    /// <summary>İmza sıfatı (ör. "Müşteri yetkilisi", "Genel müdür").</summary>
    public string? Capacity { get; internal set; }

    public bool IsRequired { get; internal set; } = true;

    /// <summary><see cref="Lifecycle.SignatoryStatus"/>.</summary>
    public string Status { get; internal set; } = Lifecycle.SignatoryStatus.Waiting;

    public DateTimeOffset? SentAt { get; internal set; }

    public DateTimeOffset? SignedAt { get; internal set; }

    /// <summary><see cref="Lifecycle.SignatureAssurance"/>; imzalanmadıysa boş.</summary>
    public string? Assurance { get; internal set; }

    /// <summary>Manuel imzayı doğrulayan yetkili ve zamanı (yalnızca manuel yöntemde).</summary>
    public Guid? VerifiedBy { get; internal set; }

    public DateTimeOffset? VerifiedAt { get; internal set; }

    /// <summary>Manuel imza kanıtı (yüklenen imzalı belge).</summary>
    public Guid? EvidenceFileId { get; internal set; }

    public string? DeclineReason { get; internal set; }

    public int ReminderCount { get; internal set; }

    public DateTimeOffset? LastReminderAt { get; internal set; }
}

/// <summary>
/// İmza olay günlüğü (eklenir, değişmez): kullanıcı eylemleri, sağlayıcı olayları ve sistem olayları. Sağlayıcı olayı kimliği istek içinde tekildir
/// (idempotency); tekrarlanan ya da sırası bozuk olay da kaydedilir ama uygulanmaz (<see cref="Outcome"/>).
/// </summary>
public sealed class ContractSignatureEvent : ContractsEntity
{
    public const int EventIdMaxLength = 100;
    public const int NoteMaxLength = 500;

    public Guid RequestId { get; internal set; }

    public Guid ContractId { get; internal set; }

    public Guid? SignatoryId { get; internal set; }

    /// <summary>"User", "Provider" ya da "System".</summary>
    public string Source { get; internal set; } = "User";

    /// <summary><see cref="Lifecycle.SignatureEventType"/>.</summary>
    public string EventType { get; internal set; } = string.Empty;

    /// <summary>Sağlayıcının olay kimliği; istek içinde tekil (yalnızca sağlayıcı olaylarında).</summary>
    public string? ProviderEventId { get; internal set; }

    /// <summary>Sağlayıcının olay sıra numarası.</summary>
    public int? Sequence { get; internal set; }

    /// <summary><see cref="Lifecycle.SignatureEventOutcome"/>.</summary>
    public string Outcome { get; internal set; } = Lifecycle.SignatureEventOutcome.Applied;

    public string? Note { get; internal set; }

    public Guid? ActorId { get; internal set; }
}

/// <summary>
/// Manuel imza kanıtı: yüklenen imzalı belge taraması. Yalnızca PDF, PNG ve JPEG kabul edilir (dosya imzası denetlenir); dosya sunucuda üretilmediği
/// için antivirüs taraması yoktur, bu yüzden her zaman indirme (attachment) olarak sunulur ve tarayıcıda çizilmez.
/// </summary>
public sealed class ContractSignatureFile : ContractsEntity
{
    public const int FileNameMaxLength = 160;

    public Guid ContractId { get; internal set; }

    public Guid SignatoryId { get; internal set; }

    public string FileName { get; internal set; } = string.Empty;

    public string ContentType { get; internal set; } = string.Empty;

    public int SizeBytes { get; internal set; }

    public string Sha256 { get; internal set; } = string.Empty;

    public byte[] Content { get; internal set; } = [];

    public Guid? CreatedBy { get; internal set; }
}
