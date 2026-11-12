using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Credentials;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Persistence;

namespace Modulix.Core.Credentials;

/// <summary>
/// Süreli belgenin kural yeteneği (ADR-0038 dilim 2b, ADR-0027): "bitişine N gün kala" tarihe bağlı tetikleyici, zamanlanmış tarama ve kayıt
/// olayları. Eşik gün sayısı koda değil firmanın kuralına yazılır (K3). Kural kaydı kuralın kimliğiyle kayıt boru hattından okur: belgeye erişim
/// sahip kaydın erişimi olduğundan, araç okuyamayan kimliğin kuralı araç belgelerini görmez.
/// </summary>
public static class CredentialAutomation
{
    public const string ValidTo = "validTo";
    public const string ValidFrom = "validFrom";

    public static AutomationCapabilities Capabilities { get; } = new()
    {
        Entities =
        [
            new(CredentialEntities.Credential, "Süreli belge") { RecordSource = typeof(CredentialAutomationRecordSource), LabelField = "documentNo" },
        ],
        Events =
        [
            new("core.credential.created", CredentialEntities.Credential, DomainEventTypes.RecordCreated, "Süreli belge eklendi"),
            new("core.credential.updated", CredentialEntities.Credential, DomainEventTypes.RecordUpdated, "Süreli belge güncellendi"),
        ],
        Fields =
        [
            new(CredentialEntities.Credential, "documentNo", "Belge no", AutomationFieldType.Text),
            new(CredentialEntities.Credential, "typeKey", "Belge türü (anahtar)", AutomationFieldType.Text)
            {
                Hint = "Belge türü listesindeki anahtar (ör. Inspection, Src).",
            },
            new(CredentialEntities.Credential, "ownerEntityKey", "Sahip kayıt türü", AutomationFieldType.Text)
            {
                Hint = "Belgenin bağlı olduğu kayıt türünün anahtarı (araç, sürücü…); türleri modüller bildirir.",
            },
            new(CredentialEntities.Credential, "issuedBy", "Veren kurum", AutomationFieldType.Text),
            new(CredentialEntities.Credential, ValidFrom, "Başlangıç tarihi", AutomationFieldType.Date),
            new(CredentialEntities.Credential, ValidTo, "Bitiş tarihi", AutomationFieldType.Date)
            {
                Hint = "\"Bitişine 30 gün kala\" gibi uyarılar için tarihe bağlı tetikleyicide kullanın.",
            },
            new(CredentialEntities.Credential, "status", "Durum", AutomationFieldType.Choice)
            {
                Options = [new(CredentialStatuses.Active, "Geçerli"), new(CredentialStatuses.Revoked, "İptal edildi")],
            },
        ],
    };
}

/// <summary>
/// Süreli belgenin tarama kaynağı: yalnızca kayıt kimlikleri döner (kimliğe göre artan, anahtar tabanlı sayfa). Zamanlanmış ve tarihe bağlı
/// tarama yalnızca <b>geçerli</b> (iptal edilmemiş) belgeleri kapsar: iptal edilen belge için bitiş uyarısı anlamsızdır.
/// </summary>
internal sealed class CredentialAutomationRecordSource(IModuleData data) : IAutomationRecordSource
{
    public async Task<AutomationScanPage> ScanAsync(AutomationScanQuery query, CancellationToken cancellationToken)
    {
        if (query.EntityKey != CredentialEntities.Credential)
        {
            throw new InvalidOperationException("Bilinmeyen kayıt türü.");
        }

        var credentials = data.Set<Credential>().AsNoTracking().Where(c => c.Status == CredentialStatuses.Active);
        if (query.AfterRecordId is { } cursor)
        {
            credentials = credentials.Where(c => c.Id.CompareTo(cursor) > 0);
        }

        credentials = query.Kind switch
        {
            AutomationScanKind.Scheduled => credentials,
            AutomationScanKind.DateRelative when query.Field == CredentialAutomation.ValidTo && query.TargetDate is { } target =>
                credentials.Where(c => c.ValidTo == target),
            AutomationScanKind.DateRelative when query.Field == CredentialAutomation.ValidFrom && query.TargetDate is { } target =>
                credentials.Where(c => c.ValidFrom == target),
            AutomationScanKind.DateRelative => throw new InvalidOperationException("Desteklenmeyen tarih alanı."),
            _ => throw new InvalidOperationException("Desteklenmeyen tarama türü."),
        };

        var ids = await credentials.OrderBy(c => c.Id).Select(c => c.Id).Take(query.PageSize).ToListAsync(cancellationToken);
        return new AutomationScanPage(ids);
    }
}
