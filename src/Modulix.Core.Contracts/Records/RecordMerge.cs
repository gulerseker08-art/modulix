using System.Text.Json.Nodes;

namespace Modulix.Core.Contracts.Records;

// Kayıt birleştirme (ADR-0013): kaynak kayıt hedef kayda katılır, kaynak kaldırılır. Birleştirme ayrı bir izin ister ve
// boru hattından geçer (izin, kapsam, sürüm, tek işlem, audit, olay); depo yalnızca değişiklikleri hazırlar.

/// <summary>
/// Birleştirme isteği. Her iki kaydın sürümü zorunludur (ekranın gördüğü hal); <see cref="FieldChoices"/> alan adı → "source"
/// veya "target" (verilmeyen alan hedeften kalır).
/// </summary>
public sealed record MergeRequest(
    Guid SourceId,
    Guid TargetId,
    long SourceVersion,
    long TargetVersion,
    IReadOnlyDictionary<string, string>? FieldChoices = null)
{
    public const string FromSource = "source";
    public const string FromTarget = "target";
}

/// <summary>İki kaydın aynı alandaki farklı değerleri; <see cref="Suggested"/> hedef boşken kaynağı önerir.</summary>
public sealed record MergeFieldDiff(string Field, JsonNode? Source, JsonNode? Target, string Suggested);

/// <summary>Birleştirmenin etkisi: taşınan, birleşen veya kaldırılan kayıtlar (ör. "3 kişi ilişkisi taşınır").</summary>
public sealed record MergeImpact(string Label, int Count);

/// <summary>Birleştirme önizlemesi. <see cref="Blockers"/> doluysa birleştirme yapılamaz.</summary>
public sealed record MergePreview(
    RecordData Source,
    RecordData Target,
    IReadOnlyList<MergeFieldDiff> Fields,
    IReadOnlyList<MergeImpact> Impacts,
    IReadOnlyList<string> Blockers);

/// <summary>Hazırlanan birleştirmenin sonucu (henüz kaydedilmemiş). Kaynağın birleştirme öncesi hali audit ve olaya yazılır.</summary>
public sealed record MergeOutcome(
    RecordData SourceBefore,
    RecordData TargetBefore,
    RecordData TargetAfter,
    IReadOnlyList<MergeImpact> Impacts);

/// <summary>Kaynak kaydın hangi kayda birleştirildiği (eski kimlikten hedefe iz).</summary>
public sealed record MergeLink(string EntityKey, Guid SourceId, Guid TargetId, DateTimeOffset MergedAt, Guid? MergedBy);

/// <summary>
/// Birleştirilebilen kayıt deposu. Çağıranın kaynağı ve hedefi okuyabildiği (kapsam dahil) boru hattında doğrulanmıştır.
/// Depo hiçbir şey kaydetmez; çözülemeyen bir bağlılık varsa önizleme engel bildirir ve birleştirme
/// <see cref="RecordValidationException"/> ile reddedilir.
/// </summary>
public interface IMergeableRecordStore : IRecordStore
{
    /// <summary>Birleştirme izni (ör. "crm.companies.merge").</summary>
    string MergePermission { get; }

    /// <summary>Kaydın ortak kaydının türü (ör. "core.party"); ortak kaydı olmayan depoda null.</summary>
    string? SharedEntityKey { get; }

    /// <summary>Kaydın ortak kaydının kimliği; kayıt yoksa null.</summary>
    Task<Guid?> SharedIdOfAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Kaynak/hedef yoksa null. İki kaydın sürümü isteğe uymuyorsa <see cref="RecordConflictException"/>.</summary>
    Task<MergePreview?> PreviewMergeAsync(MergeRequest request, CancellationToken cancellationToken);

    /// <summary>Birleştirmeyi hazırlar; kaydetmez. Kaynak/hedef yoksa null.</summary>
    Task<MergeOutcome?> MergeAsync(MergeRequest request, CancellationToken cancellationToken);

    /// <summary>Bu kayıt daha önce başka bir kayda birleştirildiyse iz; yoksa null.</summary>
    Task<MergeLink?> FindMergeLinkAsync(Guid sourceId, CancellationToken cancellationToken);
}

/// <summary>
/// Başka bir modülün ortak kayda başvuran alanını (<see cref="SharedRecords.SharedRecordReference"/>) birleştirmede yeni
/// kayda taşıyabilen depo. Taşıyamayan depo birleştirmeyi engeller: başvuru eski kayda bakar kalmaz.
/// </summary>
public interface IReferenceMover
{
    /// <summary>
    /// <paramref name="field"/> alanı <paramref name="fromId"/> olan kayıtları <paramref name="toId"/>'ye çevirir (kaydetmez).
    /// Taşınan kayıt sayısı; alanı tanımayan depo <see cref="NotSupportedException"/> fırlatır.
    /// </summary>
    Task<int> RepointAsync(string field, Guid fromId, Guid toId, CancellationToken cancellationToken);
}
