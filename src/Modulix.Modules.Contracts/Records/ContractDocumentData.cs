using System.Globalization;
using System.Text.Json;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Records;

/// <summary>
/// Belge verisi oluşturucu: sözleşme anlık görüntüsü (dondurulmuşsa dondurulmuş sürüm, değilse güncel taslak) ve ortak müşteri/kişi kaydından
/// şablon değişken değerleri, kalem tablosu ve imza alanları kurulur. Biçimler tr-TR (1.234,50 TRY; 01.11.2026). Bu değerler belgeyle birlikte
/// saklanır; müşteri kartı ya da sözleşme sonradan değişse de üretilmiş belge verisi sabittir.
/// </summary>
internal static class ContractDocumentDataBuilder
{
    private static readonly IReadOnlyDictionary<string, string> RoleLabels = new Dictionary<string, string>
    {
        [Lifecycle.PartyRoles.Customer] = "Müşteri", [Lifecycle.PartyRoles.Provider] = "Hizmet veren", [Lifecycle.PartyRoles.Guarantor] = "Kefil / garantör",
        [Lifecycle.PartyRoles.Subcontractor] = "Alt yüklenici", [Lifecycle.PartyRoles.Signatory] = "İmzacı", [Lifecycle.PartyRoles.BillingContact] = "Fatura yetkilisi",
        [Lifecycle.PartyRoles.TechnicalContact] = "Teknik yetkili", [Lifecycle.PartyRoles.Other] = "Diğer",
    };

    private static readonly IReadOnlyDictionary<string, string> RenewalLabels = new Dictionary<string, string>
    {
        [RenewalMode.None] = "Yenilemesiz", [RenewalMode.Manual] = "Manuel yenileme", [RenewalMode.Automatic] = "Otomatik yenileme",
    };

    private static readonly IReadOnlyDictionary<string, string> PeriodLabels = new Dictionary<string, string>
    {
        [BillingPeriods.Monthly] = "Aylık", [BillingPeriods.Quarterly] = "Üç aylık", [BillingPeriods.Yearly] = "Yıllık",
    };

    public static async Task<ContractDocumentData> BuildAsync(
        ContractsStoreContext context, ContractReader reader, ContractSnapshot s, ContractPolicySettings policy, DateOnly today, CancellationToken cancellationToken)
    {
        var party = await reader.PartyAsync(s.PartyId, cancellationToken);
        var person = s.PersonId is { } personId ? await reader.PersonAsync(personId, cancellationToken) : null;
        var typeLabel = (await context.ChoiceLists.GetAsync(ContractsModule.Lists.ContractType, cancellationToken)).Find(s.Type)?.Label ?? s.Type;
        var contactName = person is null ? null : $"{person.FirstName} {person.LastName}".Trim();

        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["sozlesme.numara"] = s.Number,
            ["sozlesme.baslik"] = s.Title,
            ["sozlesme.tur"] = typeLabel,
            ["sozlesme.aciklama"] = s.Description,
            ["sozlesme.surum"] = s.VersionNo.ToString(CultureInfo.InvariantCulture),
            ["sozlesme.baslangic_tarihi"] = s.StartDate is { } start ? ContractTemplateEngine.Date(start) : null,
            ["sozlesme.bitis_tarihi"] = s.IsIndefinite ? "Belirsiz süreli" : s.EndDate is { } end ? ContractTemplateEngine.Date(end) : null,
            ["sozlesme.yenileme_yontemi"] = RenewalLabels.GetValueOrDefault(s.RenewalMode, s.RenewalMode),
            ["sozlesme.yenileme_suresi_ay"] = s.RenewalPeriodMonths?.ToString(CultureInfo.InvariantCulture),
            ["sozlesme.bildirim_suresi_gun"] = s.NoticePeriodDays?.ToString(CultureInfo.InvariantCulture),
            ["sozlesme.son_bildirim_tarihi"] = s.NoticeDeadline is { } notice ? ContractTemplateEngine.Date(notice) : null,
            ["sozlesme.para_birimi"] = s.Currency,
            ["sozlesme.odeme_kosullari"] = s.PaymentTerms,
            ["sozlesme.bedel"] = s.ContractValue is { } value ? ContractTemplateEngine.Money(value, s.Currency) : null,
            ["sozlesme.net_toplam"] = ContractTemplateEngine.Money(s.NetTotal, s.Currency),
            ["sozlesme.vergi_toplam"] = ContractTemplateEngine.Money(s.TaxTotal, s.Currency),
            ["sozlesme.genel_toplam"] = ContractTemplateEngine.Money(s.GrossTotal, s.Currency),
            ["musteri.unvan"] = party?.Name,
            ["musteri.vergi_no"] = party?.TaxNumber,
            ["musteri.vergi_dairesi"] = party?.TaxOffice,
            ["musteri.adres"] = party?.Address,
            ["musteri.sehir"] = party?.City,
            ["musteri.eposta"] = party?.Email,
            ["musteri.telefon"] = party?.Phone,
            ["musteri.yetkili"] = contactName,
            ["hizmet_veren.unvan"] = policy.ProviderName,
            ["tarih.bugun"] = ContractTemplateEngine.Date(today),
        };

        var header = new[] { "#", "Kalem", "Miktar", "Birim fiyat", "Vergi %", "Dönem", "Net", "Toplam" };
        var rows = s.Lines.Where(l => l.Kind != LineKinds.Text).Select((l, i) => (IReadOnlyList<string>)
        [
            (i + 1).ToString(CultureInfo.InvariantCulture),
            l.Name + (string.IsNullOrWhiteSpace(l.Description) ? string.Empty : "\n" + l.Description),
            l.Quantity.ToString("#,0.######", Numbers) + (string.IsNullOrWhiteSpace(l.UnitName) ? string.Empty : " " + l.UnitName),
            ContractTemplateEngine.Money(l.UnitPrice, s.Currency) + (l.DiscountPercent > 0 ? $" (-%{l.DiscountPercent.ToString("0.##", Numbers)})" : string.Empty),
            l.TaxRatePercent.ToString("0.##", Numbers),
            l.BillingPeriod is { } period ? PeriodLabels.GetValueOrDefault(period, period) : "Tek seferlik",
            ContractTemplateEngine.Money(l.Net, s.Currency),
            ContractTemplateEngine.Money(l.Gross, s.Currency),
        ]).ToList();

        var totals = new List<string>
        {
            $"Tek seferlik net toplam: {ContractTemplateEngine.Money(s.NetTotal, s.Currency)}",
            $"Vergi: {ContractTemplateEngine.Money(s.TaxTotal, s.Currency)}",
            $"Genel toplam: {ContractTemplateEngine.Money(s.GrossTotal, s.Currency)}",
        };
        foreach (var recurring in Recurring(s.RecurringSummary))
        {
            // Dönemsel tutar sözleşme süresiyle çarpılıp genel toplama eklenmez; her dönem ayrı gösterilir.
            totals.Add($"{PeriodLabels.GetValueOrDefault(recurring.Period ?? string.Empty, recurring.Period ?? "Dönemsel")} toplam (dönem başına): {ContractTemplateEngine.Money(recurring.Gross, s.Currency)}");
        }

        var parties = new List<string> { $"Müşteri: {party?.Name ?? "—"}" };
        var signers = new List<SignatureSlot>
        {
            new("Müşteri adına", string.Join(" - ", new[] { party?.Name, contactName }.Where(x => !string.IsNullOrWhiteSpace(x)))),
            new("Hizmet veren adına", policy.ProviderName ?? string.Empty),
        };
        foreach (var p in s.Parties.OrderBy(p => p.Position))
        {
            var name = p.PartyId is { } pid ? (await reader.PartyAsync(pid, cancellationToken))?.Name : null;
            var personName = p.PersonId is { } pe && await reader.PersonAsync(pe, cancellationToken) is { } pp ? $"{pp.FirstName} {pp.LastName}".Trim() : null;
            var label = string.Join(" - ", new[] { name, personName }.Where(x => !string.IsNullOrWhiteSpace(x)));
            parties.Add($"{RoleLabels.GetValueOrDefault(p.Role, p.Role)}: {(label.Length > 0 ? label : "—")}");
            if (p.Role == Lifecycle.PartyRoles.Signatory && label.Length > 0)
            {
                signers.Add(new SignatureSlot("İmzacı", label));
            }
        }

        return new ContractDocumentData(values, rows, header, totals, parties, signers);
    }

    /// <summary>Belgeyle birlikte saklanan veri anlık görüntüsü (JSON).</summary>
    public static string SnapshotJson(ContractDocumentData data, int contractVersionNo, int templateVersionNo, string templateName, DateTimeOffset generatedAt) =>
        JsonSerializer.Serialize(new
        {
            contractVersionNo,
            templateVersionNo,
            templateName,
            generatedAt,
            values = data.Values,
            lineHeader = data.LineHeader,
            lines = data.LineRows,
            totals = data.LineTotals,
            parties = data.Parties,
            signers = data.Signers,
        }, JsonSerializerOptions.Web);

    private static readonly NumberFormatInfo Numbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", NumberGroupSizes = [3] };

    private sealed record RecurringTotal(string? Period, decimal Net, decimal Tax, decimal Gross);

    private static IEnumerable<RecurringTotal> Recurring(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<RecurringTotal>>(json, JsonSerializerOptions.Web) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>Yayımlanan şablon sürümü satırı yazımı; şablon geçişiyle aynı işlemde (kapı etkisi) çalışır.</summary>
internal static class ContractTemplateVersions
{
    public static void Add(ContractsStoreContext context, ContractTemplate template, int versionNo, string? notes) =>
        context.Data.Set<ContractTemplateVersion>().Add(new ContractTemplateVersion
        {
            TemplateId = template.Id,
            VersionNo = versionNo,
            Name = template.Name,
            Body = template.Body,
            Sha256 = template.BodySha256,
            IsExample = template.IsExample,
            Notes = notes is { Length: > Contract.ReasonMaxLength } ? notes[..Contract.ReasonMaxLength] : notes,
            PublishedBy = context.CurrentUserId,
        });
}

/// <summary>Üretilen belge satırı yazımı; sözleşme geçişiyle aynı işlemde (kapı etkisi) çalışır. Satır eklenir, değişmez.</summary>
internal static class ContractDocumentWriter
{
    public static void Add(ContractsStoreContext context, ContractDocument document) => context.Data.Set<ContractDocument>().Add(document);
}
