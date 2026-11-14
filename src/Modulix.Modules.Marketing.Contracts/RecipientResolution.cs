using Modulix.Core.Contracts.Communication;

namespace Modulix.Modules.Marketing.Contracts;

// Alıcı çözümleme sözleşmesi (ADR-0025, dilim 4): hedef kitle "firma" ya da "kişi" kimlikleridir; bir kanalda ulaşılacak kişi ve iletişim
// noktası ise iletişim noktalarını tutan modülün işidir. Pazarlama modülü CRM tablolarına dokunmaz; bu sözleşmeyle ister, sonucu
// kendi kurallarıyla (tekilleştirme, sınır, izin ve engel kontrolü) işler.

/// <summary>Firma hedefinde hangi kişilerin seçileceği. Kural eşleşmezse rastgele kişiye gidilmez; hedef "alıcı bulunamadı" nedeniyle elenir.</summary>
public enum RecipientRule
{
    /// <summary>Firmanın birincil (aktif) irtibat kişisi.</summary>
    PrimaryContact = 1,

    /// <summary>Karar verici olarak işaretli aktif kişiler.</summary>
    DecisionMaker = 2,

    /// <summary>Belirli role sahip aktif kişiler (<see cref="RecipientRequest.Role"/>).</summary>
    Role = 3,

    /// <summary>Firmanın bütün aktif kişileri.</summary>
    AllContacts = 4,

    /// <summary>Kişi değil, firmanın kendi iletişim noktaları (genel e-posta, santral).</summary>
    CompanyPoints = 5,
}

/// <param name="Target">Hedef türü; <see cref="SegmentTarget.Person"/> ve <see cref="SegmentTarget.Lead"/> hedeflerinde <paramref name="Rule"/> yok sayılır.</param>
/// <param name="TargetIds">Firma hedefinde taraf, kişi hedefinde ortak kişi, lead hedefinde lead kimlikleri.</param>
public sealed record RecipientRequest(
    SegmentTarget Target, IReadOnlyCollection<Guid> TargetIds, CommunicationChannel Channel, RecipientRule Rule, string? Role = null);

/// <summary>Bir hedef için bulunan alıcı. <see cref="Address"/> kaynağın ham değeridir (motor normalize eder).</summary>
/// <param name="PersonId">Alıcı kişinin ortak kişi kimliği; firma noktası ve lead için boş olabilir.</param>
/// <param name="ContactPointId">Adresin geldiği iletişim noktası kaydı; lead'de boş.</param>
public sealed record ResolvedContact(Guid TargetId, Guid? PersonId, Guid? ContactPointId, string Address, bool IsShared, bool IsPrimary);

/// <summary>Alıcısı bulunamayan hedef ve nedeni (<see cref="RecipientCodes"/>).</summary>
public sealed record UnresolvedTarget(Guid TargetId, string Code);

public sealed record RecipientResolution(IReadOnlyList<ResolvedContact> Contacts, IReadOnlyList<UnresolvedTarget> Unresolved);

/// <summary>Alıcı çözümlemede ve uygunlukta kullanılan kararlı neden kodları (arayüz ve raporlar buna göre sayar).</summary>
public static class RecipientCodes
{
    /// <summary>Firmanın kuralı karşılayan kişisi yok (ör. birincil irtibat tanımlı değil).</summary>
    public const string NoMatchingContact = "no-matching-contact";

    /// <summary>Kişi bulundu ama seçilen kanalda etkin iletişim noktası yok.</summary>
    public const string NoContactPoint = "no-contact-point";

    /// <summary>Hedef kayıt bulunamadı (silinmiş ya da kapsam dışı).</summary>
    public const string TargetNotFound = "target-not-found";

    /// <summary>Firma başına alıcı sınırı aşıldı.</summary>
    public const string PerTargetLimit = "per-target-limit";

    /// <summary>Aynı kişi başka bir kayıt üzerinden zaten seçildi.</summary>
    public const string DuplicatePerson = "duplicate-person";

    /// <summary>Aynı adres bu kitlede zaten var.</summary>
    public const string DuplicateAddress = "duplicate-address";

    /// <summary>Adres birden fazla kayda ait (paylaşılan); izin kişiye değil adrese bağlı olduğundan otomatik gönderilmez.</summary>
    public const string SharedAddress = "shared-address";
}

/// <summary>
/// Bir modülün alıcı çözümleyicisi. Modül <c>ConfigureServices</c>'te scoped kaydeder. Yalnızca okur; tenant filtresi ve RLS kaynağın veri
/// erişimiyle uygulanır. Hangi hedef türlerini çözebildiği <see cref="Supports"/> ile bildirilir.
/// </summary>
public interface IRecipientResolver
{
    /// <summary>Çağıranın okuyabilmesi gereken kayıt türü (ör. iletişim noktası); okuyamayan kişi için alıcılar çözülmez.</summary>
    string ReadEntityKey { get; }

    bool Supports(SegmentTarget target);

    Task<RecipientResolution> ResolveAsync(RecipientRequest request, CancellationToken cancellationToken);
}
