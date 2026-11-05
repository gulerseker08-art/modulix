namespace Modulix.Core.Contracts.Automation;

// Tarama tabanlı tetikleyicilerin (ADR-0027, Dilim 2b) modül sözleşmesi. Core hiçbir modülün tablosunu okumaz; "bugün hangi kayıtlar
// bu tetikleyiciye uyuyor" sorusunu kayıt türünü beyan eden modül yanıtlar. Yanıt yalnızca kayıt kimlikleridir: koşulu, alan yetkisini ve
// aksiyonu motor kuralın kimliğiyle kayıt boru hattı üzerinden uygular, modül değer döndürmez.

/// <summary>Taramanın türü; ilgili tetikleyiciyle bire bir eşleşir.</summary>
public enum AutomationScanKind
{
    /// <summary><see cref="ScheduledTrigger"/>: kayıt türünün bütün kayıtları (koşul motorda değerlendirilir).</summary>
    Scheduled = 1,

    /// <summary><see cref="DateRelativeTrigger"/>: tarih alanı <see cref="AutomationScanQuery.TargetDate"/> gününe düşen kayıtlar.</summary>
    DateRelative = 2,

    /// <summary><see cref="InactivityTrigger"/>: ilişkide <see cref="AutomationScanQuery.InactiveBefore"/> anından beri hareketi olmayan kayıtlar.</summary>
    Inactivity = 3,
}

/// <summary>
/// Bir tarama sayfası isteği. Sayfalama anahtar tabanlıdır: modül kayıtları kimliğe göre artan sırada döndürür ve yalnızca
/// <see cref="AfterRecordId"/>'den büyük kimlikleri verir. Aynı dönemin yeniden başlatılan taraması kalındığı yerden sürer.
/// </summary>
/// <param name="Kind">Tarama türü.</param>
/// <param name="EntityKey">Taranan kayıt türü.</param>
/// <param name="Field">Yalnızca <see cref="AutomationScanKind.DateRelative"/>: tarih alanının adı (<see cref="AutomationFieldDefinition.Name"/>).</param>
/// <param name="TargetDate">Yalnızca <see cref="AutomationScanKind.DateRelative"/>: tarih alanının düşmesi gereken tenant takvim günü.</param>
/// <param name="Relation">Yalnızca <see cref="AutomationScanKind.Inactivity"/>: ilişki anahtarı.</param>
/// <param name="InactiveBefore">Yalnızca <see cref="AutomationScanKind.Inactivity"/>: bu andan sonra ilişkide hareketi olan kayıtlar dışarıda kalır.</param>
/// <param name="AfterRecordId">Bu kimlikten büyük kayıtlar; ilk sayfada boş.</param>
/// <param name="PageSize">Sayfadaki en çok kayıt sayısı.</param>
public sealed record AutomationScanQuery(
    AutomationScanKind Kind,
    string EntityKey,
    string? Field,
    DateOnly? TargetDate,
    string? Relation,
    DateTimeOffset? InactiveBefore,
    Guid? AfterRecordId,
    int PageSize);

/// <summary>Bir tarama sayfası: kimliğe göre artan kayıt kimlikleri. Sayfa <see cref="AutomationScanQuery.PageSize"/>'dan kısaysa tarama bitmiştir.</summary>
public sealed record AutomationScanPage(IReadOnlyList<Guid> RecordIds);

/// <summary>
/// Modülün kayıt kaynağı; <see cref="AutomationEntityDefinition.RecordSource"/> tipi bunu uygular ve modül <c>ConfigureServices</c>'te
/// scoped kaydeder. Yalnızca kendi tablolarını okur; tenant süzgeci ve Row-Level Security veri erişimiyle uygulanır. Kayıt değeri döndürmez.
/// </summary>
public interface IAutomationRecordSource
{
    Task<AutomationScanPage> ScanAsync(AutomationScanQuery query, CancellationToken cancellationToken);
}
