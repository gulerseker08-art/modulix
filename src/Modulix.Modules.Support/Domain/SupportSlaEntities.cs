namespace Modulix.Modules.Support.Domain;

// SLA entity'leri (ADR-0024 dilim 2). Takvim ve politika yönetilen kayıtlardır (kayıt boru hattı); örnek, duraklatma ve eskalasyon kayıtlarını SLA motoru ve zamanlayıcı yazar
// (genel uçlarda yoktur). Her SLA örneği politika ve takvimin o andaki anlık görüntüsünü taşır: sonradan politika ya da takvim değişse de açık kayıt sessizce değişmez.

/// <summary>İş takvimi: saat dilimi, haftalık çalışma pencereleri ve tatiller (JSON). 7/24 takvim saat dilimine bakmaz.</summary>
public sealed class SlaCalendar : SupportEntity
{
    public const int NameMaxLength = 100;
    public const int ZoneMaxLength = 64;
    public const int WeeklyMaxLength = 4000;
    public const int HolidaysMaxLength = 20000;

    public string Name { get; internal set; } = string.Empty;

    public string TimeZoneId { get; internal set; } = "Europe/Istanbul";

    public bool IsAlwaysOpen { get; internal set; }

    /// <summary>Çalışma pencereleri JSON'u: <c>[{"day":1,"start":"09:00","end":"18:00"}]</c> (0=Pazar … 6=Cumartesi).</summary>
    public string? WeeklyHours { get; internal set; }

    /// <summary>Tatiller JSON'u: <c>[{"date":"2026-10-29","name":"…","start":null,"end":null}]</c>; start/end verilirse yarım gün.</summary>
    public string? Holidays { get; internal set; }

    public bool IsActive { get; internal set; } = true;

    public Guid? CreatedBy { get; internal set; }

    public Guid? UpdatedBy { get; internal set; }
}

/// <summary>
/// SLA politikası: hangi taleplere (tür, kategori, öncelik, müşteri) hangi ilk yanıt ve çözüm hedefinin, hangi takvimle, hangi bekleme kurallarıyla ve hangi eskalasyon
/// adımlarıyla uygulanacağı. <see cref="Revision"/> her değişiklikte artar; açık kayıtlar anlık görüntüyle etkilenmez.
/// </summary>
public sealed class SlaPolicy : SupportEntity
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;
    public const int RulesMaxLength = 4000;
    public const int MaxMinutes = 525600;

    public string Name { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    public bool IsActive { get; internal set; } = true;

    /// <summary>Hiçbir özel politika uymazsa kullanılan yedek politika (kısıtsız olmalı; tenant başına en çok bir tane).</summary>
    public bool IsDefault { get; internal set; }

    /// <summary>Eşit özgüllükte küçük olan kazanır.</summary>
    public int Precedence { get; internal set; } = 100;

    public string? TypeKey { get; internal set; }

    public Guid? CategoryId { get; internal set; }

    public string? Priority { get; internal set; }

    public Guid? PartyId { get; internal set; }

    /// <summary>Yalnızca bu sözleşmeye bağlı taleplere uygulanır (sözleşmenin destek kapsamı). Başka modülün kaydı olduğundan yabancı anahtar yoktur; kaydedilirken çağıranın yetkisiyle doğrulanır.</summary>
    public Guid? ContractId { get; internal set; }

    /// <summary>İlk insan yanıtı hedefi (dakika); boşsa bu ölçüt izlenmez.</summary>
    public int? FirstResponseMinutes { get; internal set; }

    public int? ResolutionMinutes { get; internal set; }

    /// <summary>Boşsa 7/24.</summary>
    public Guid? CalendarId { get; internal set; }

    public bool PauseOnWaitingCustomer { get; internal set; } = true;

    /// <summary>Varsayılan hayır: iç ekip beklemesi müşteri beklemesiyle eşit değildir ve SLA'yı durdurmaz.</summary>
    public bool PauseOnWaitingInternal { get; internal set; }

    /// <summary>Eskalasyon kuralları JSON'u (<see cref="Sla.EscalationRule"/>).</summary>
    public string? EscalationRules { get; internal set; }

    public int Revision { get; internal set; } = 1;

    public Guid? CreatedBy { get; internal set; }

    public Guid? UpdatedBy { get; internal set; }
}

/// <summary>
/// Bir talebin bir ölçütü (ilk yanıt ya da çözüm) için bir döngüdeki SLA örneği. Döngü, yeniden açmada artar; önceki döngüler saklanır. Süre iş takviminde hesaplanır:
/// <see cref="WorkedSeconds"/> duraklatma öncesi birikmiş iş süresi, <see cref="RunningSince"/> sayacın son (yeniden) başladığı andır; hedef anı bunlardan türetilir.
/// </summary>
public sealed class SlaInstance : SupportEntity
{
    public const int SnapshotMaxLength = 40000;

    public Guid TicketId { get; internal set; }

    public int Cycle { get; internal set; } = 1;

    /// <summary><see cref="Sla.SlaMetric"/>.</summary>
    public string Metric { get; internal set; } = string.Empty;

    /// <summary><see cref="Sla.SlaStatus"/>.</summary>
    public string Status { get; internal set; } = Sla.SlaStatus.Running;

    public Guid PolicyId { get; internal set; }

    public int PolicyRevision { get; internal set; }

    public string PolicyName { get; internal set; } = string.Empty;

    /// <summary>Politika + takvim anlık görüntüsü (JSON).</summary>
    public string Snapshot { get; internal set; } = string.Empty;

    public int TargetMinutes { get; internal set; }

    public DateTimeOffset StartedAt { get; internal set; }

    /// <summary>Sayaç çalışıyorsa son (yeniden) başlama anı; duraklatılmış ya da tamamlanmışsa boş.</summary>
    public DateTimeOffset? RunningSince { get; internal set; }

    public long WorkedSeconds { get; internal set; }

    /// <summary>Çalışan sayaç için hedef anı; duraklatılmış ya da tamamlanmış sayaçta son hesaplanan (tamamlananda) ya da boş (duraklatılmışta).</summary>
    public DateTimeOffset? DueAt { get; internal set; }

    /// <summary>İlk uyarı eşiğinin anı (hedefin yüzde 100'ünden küçük en küçük eşik); "risk altında" süzgeci bunu kullanır.</summary>
    public DateTimeOffset? WarningAt { get; internal set; }

    public DateTimeOffset? CompletedAt { get; internal set; }

    /// <summary>İhlal anı: gerçek hedef anı (işin çalıştığı an değil).</summary>
    public DateTimeOffset? BreachedAt { get; internal set; }

    public string? CancelReason { get; internal set; }
}

/// <summary>Duraklatma aralığı: hangi durum nedeniyle, ne zaman başladı ve bitti.</summary>
public sealed class SlaPause : SupportEntity
{
    public Guid InstanceId { get; internal set; }

    public Guid TicketId { get; internal set; }

    public DateTimeOffset StartedAt { get; internal set; }

    public DateTimeOffset? EndedAt { get; internal set; }

    /// <summary>Duraklatan talep durumu (WaitingCustomer ya da WaitingInternal).</summary>
    public string Reason { get; internal set; } = string.Empty;
}

/// <summary>
/// Eskalasyon uygulaması. (Tenant, örnek, kural) benzersizdir: iş tekrar ya da paralel çalışsa, yarıda kesilse de aynı eylem ikinci kez uygulanmaz.
/// </summary>
public sealed class SlaEscalation : SupportEntity
{
    public const int OutcomeMaxLength = 200;

    public Guid InstanceId { get; internal set; }

    public Guid TicketId { get; internal set; }

    public string RuleId { get; internal set; } = string.Empty;

    public string Metric { get; internal set; } = string.Empty;

    public int Percent { get; internal set; }

    public string Action { get; internal set; } = string.Empty;

    /// <summary>Eşiğin gerçek anı; uygulama anı <c>CreatedAt</c>'tir.</summary>
    public DateTimeOffset ThresholdAt { get; internal set; }

    public string? Outcome { get; internal set; }
}
