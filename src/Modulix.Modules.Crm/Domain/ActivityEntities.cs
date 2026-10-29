namespace Modulix.Modules.Crm.Domain;

/// <summary>Bir aktiviteye bağlı alt kayıt (ilişki, katılımcı); kapsam ve gizlilik aktivite üzerinden uygulanır.</summary>
public interface IActivityChild
{
    Guid ActivityId { get; }
}

/// <summary>
/// Aktivitenin bir iş kaydıyla ilişkisi. Hedef, (varlık anahtarı, kayıt kimliği) çiftiyle gösterilir; böylece CRM dışındaki
/// modüller (sözleşme, sipariş, destek kaydı...) aktivite çekirdeği değişmeden kendi kayıtlarını bağlar. Hedefin varlığı,
/// tenant'ı ve çağıranın erişimi yazarken <c>IActivityLinkTarget</c> ile doğrulanır; veritabanında hedefe yabancı anahtar YOKTUR
/// (hedef başka modülün tablosunda olabilir, kayıt silinse de geçmiş korunur, bkz. ADR-0015). Aktivitenin firma/kişi/fırsat/Lead
/// sütunları "doğrudan" ilişkilerdir ve zaman akışında bu satırlarla birleşir; aynı aktivite bir kez görünür.
/// </summary>
public sealed class ActivityLink : CrmEntity, IActivityChild
{
    public const int EntityKeyMaxLength = 60;

    public Guid ActivityId { get; internal set; }

    public string EntityKey { get; internal set; } = string.Empty;

    public Guid RecordId { get; internal set; }

    /// <summary>Birincil ilişki: aktivite başına en fazla bir tane (filtreli benzersiz indeks).</summary>
    public bool IsPrimary { get; internal set; }
}

/// <summary>
/// Aktivite katılımcısı. Dahili kullanıcı (<see cref="UserId"/>), harici CRM kişisi (<see cref="ContactId"/>) ya da serbest e-posta
/// (<see cref="Email"/>) olabilir; tam olarak biri dolu olur. Rol (zorunlu/isteğe bağlı/izleyici) sorumluluk DEĞİLDİR: birincil
/// sorumlu <c>Activity.OwnerId</c>'dir. Harici katılımcı olmak CRM kaydına erişim vermez; erişim yalnızca kullanıcı rolleriyle olur.
/// </summary>
public sealed class ActivityParticipant : CrmEntity, IActivityChild
{
    public const int EmailMaxLength = 254;
    public const int NameMaxLength = 200;

    public Guid ActivityId { get; internal set; }

    public Guid? UserId { get; internal set; }

    public Guid? ContactId { get; internal set; }

    public string? Email { get; internal set; }

    public string? DisplayName { get; internal set; }

    public string Role { get; internal set; } = Activities.ParticipantRole.Required;

    /// <summary>Normalleştirilmiş e-posta (aynı kişiyi iki kez eklemeyi engeller).</summary>
    public string? NormalizedEmail { get; internal set; }
}
