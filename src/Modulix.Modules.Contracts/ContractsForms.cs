using Modulix.Core.Contracts.Modules;

namespace Modulix.Modules.Contracts;

/// <summary>
/// Sözleşme formunun varsayılan düzeni (ADR-0021 form düzeni). Alan anahtarları kayıt alanlarının teknik adlarıdır; editör her anahtar için
/// kendi girdisini çizer, yer ve genişlik buradan (ya da firmanın düzeninden) gelir. Kaydın kimliğini taşıyan alanlar ve yalnızca ekranda
/// anlamı olan onay kutusu sabittir (gizlenemez, zorunluluğu değişmez). Sözleşme bedeli yalnızca finansal izni olana çizilir (editör süzer).
/// </summary>
public static class ContractsForms
{
    private const string General = FormDefinition.DefaultTab;
    private const string Extras = FormDefinition.ExtrasSection;

    public static IReadOnlyList<FormDefinition> All { get; } =
    [
        new(ContractsModule.Entities.Contract, "Sözleşme", [new(General, "Genel bilgiler")],
        [
            new("temel", "Temel bilgiler", "file-text"),
            new("sure", "Süre, yenileme ve bildirim", "calendar"),
            new("ticari", "Ticari koşullar", "coins"),
            new("aciklama", "Açıklama ve koşullar", "message-square"),
            new(Extras, "Ek bilgiler", "square-pen"),
        ],
        [
            new("title", "Başlık", "temel", 12, Required: true, Fixed: true),
            new("partyId", "Müşteri", "temel", 6, Required: true, Fixed: true),
            new("personId", "İlgili kişi", "temel", 6),
            new("type", "Tür", "temel", 4, Required: true, Fixed: true),
            new("ownerId", "Sorumlu", "temel", 4),
            new("confidentiality", "Gizlilik", "temel", 4),
            new("previousContractId", "Önceki sözleşme", "temel", 6),
            new("startDate", "Başlangıç tarihi", "sure", 4),
            new("endDate", "Bitiş tarihi", "sure", 4),
            new("isIndefinite", "Belirsiz süreli sözleşme", "sure", 4, Fixed: true),
            new("renewalMode", "Yenileme yöntemi", "sure", 4),
            new("renewalPeriodMonths", "Yenileme süresi (ay)", "sure", 4),
            new("noticePeriodDays", "Bildirim süresi (gün)", "sure", 4),
            new("currency", "Para birimi", "ticari", 4, Required: true, Fixed: true),
            new("priceIncludesTax", "Fiyatlandırma biçimi", "ticari", 4),
            new("contractValue", "Sözleşme bedeli (isteğe bağlı)", "ticari", 4),
            new("description", "Açıklama", "aciklama", 12),
            new("paymentTerms", "Ödeme koşulları", "aciklama", 12),
            new("hasNonStandardTerms", "Standart dışı madde var", "aciklama", 6),
            new("riskLevel", "Risk düzeyi", "aciklama", 6),
        ]),
    ];
}
