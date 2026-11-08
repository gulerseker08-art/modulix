using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;

namespace Modulix.Core.Contracts.Events;

/// <summary>
/// Modülün beyan ettiği iş olayı (ADR-0039, Ç0.4); <see cref="Modules.IModule.BusinessEvents"/> ile bildirilir.
/// <list type="bullet">
///   <item><see cref="EventType"/>: "modül.kayıt.fiil" (ör. "crm.opportunity.won"); modül anahtarıyla başlar ve değişmez.
///   Sürüm tür adının parçası değildir, <see cref="SchemaVersion"/>'dadır ("crm.opportunity.won.v1" yazımı tür + sürüm demektir).</item>
///   <item><see cref="SchemaVersion"/>: alan eklemek artırmaz; bir alanın anlamı ya da biçimi değişince artar (ADR-0007 kuralı).
///   Katalog, bu sürümü okuyamayan aboneliği açılışta reddeder.</item>
///   <item><see cref="EntityKey"/>: olayın kaydı; zarfın kayıt türü ve hassas alan süzgecinin dayanağı.</item>
///   <item><see cref="Fields"/>: payload'da bulunabilen alanlar. Kaydın alan adları aynı anlam ve biçimle kullanılır; böylece kaydın
///   hassas alanı (Y8b) iş olayında da izni olmayan aboneden gizlenir. Kayıtta karşılığı olmayan türetilmiş alan (ör. "companyCreated")
///   beyan edilir ve hassas sayılmaz; hassas değer türetilmiş alana konmaz. Beyan dışı alanla yayın reddedilir.</item>
/// </list>
/// </summary>
public sealed record BusinessEventDefinition(
    string EventType,
    int SchemaVersion,
    string EntityKey,
    string DisplayName,
    IReadOnlyList<string> Fields);

/// <summary>
/// Yayınlanacak iş olayı (ADR-0039). Payload düzdür: <c>{ "alan": değer }</c>; kayıt olaylarındaki <c>{ alan: { old, new } }</c> farkı
/// değildir. Olay "ne oldu"yu taşır; kaydın güncel hâline ihtiyaç duyan abone kaydı boru hattından kendi izniyle okur.
/// <list type="bullet">
///   <item><see cref="RecordVersion"/>: kaydın komuttan sonraki sürümü; zarfın sıra numarası olur (aynı komutun ürettiği kayıt
///   olayıyla aynı). Boşsa olay sırasızdır.</item>
///   <item><see cref="CompanyId"/>: işletici şirket (ADR-0008); ortak kayıtta boş.</item>
///   <item><see cref="Source"/>: denetim kaydıyla aynı kaynak; abonenin içinden yayınlanan olayda yayıncı arka plan işine çevirir.</item>
/// </list>
/// </summary>
public sealed record BusinessEvent(string EventType, Guid RecordId, JsonObject Payload)
{
    public long? RecordVersion { get; init; }

    public Guid? CompanyId { get; init; }

    public string? Reason { get; init; }

    public ExecutionSource Source { get; init; } = ExecutionSource.Api;
}

/// <summary>
/// İş olayı yayıncısı (ADR-0039). Açık iş komutu (ör. fırsatı kazan, müşteri adayını dönüştür) durum değişikliğini kayıt boru hattından
/// yaptıktan sonra olayı yayınlar; ikisi <see cref="IRecordPipeline.RunAtomicAsync{T}"/> içinde aynı veritabanı işlemindedir: birlikte
/// yazılır ya da hiçbiri. Yayıncı izin denetlemez (komut kendi iznini denetlemiştir); payload yalnızca komutun döndürdüğü, çağıranın
/// görebildiği değerleri taşır. Tenant oturumdan alınır. Beyansız tür, beyan dışı alan, boş tenant ya da kayıt kimliği programlama
/// hatasıdır ve istisnayla reddedilir.
/// </summary>
public interface IBusinessEventPublisher
{
    Task PublishAsync(BusinessEvent businessEvent, CancellationToken cancellationToken);
}
