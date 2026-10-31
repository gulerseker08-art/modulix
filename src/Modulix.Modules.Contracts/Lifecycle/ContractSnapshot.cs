using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modulix.Modules.Contracts.Lifecycle;

// Dondurulmuş sürüm anlık görüntüsü (ADR-0021, dilim 2): onaya/imzaya giden içerik değişmez JSON olarak saklanır ve SHA-256 özeti
// alınır. Onay ve imza bu özete bağlıdır; başlık ya da kalem sonradan değişse de sürüm aynı kalır. Karşılaştırma iki anlık görüntü
// arasındaki alan, kalem ve taraf farklarını verir.

public sealed record SnapshotLine(
    Guid StableKey, int Position, string Kind, string? ProductCode, string Name, string? Description, decimal Quantity, string? UnitName,
    decimal UnitPrice, decimal DiscountPercent, decimal TaxRatePercent, string? BillingPeriod, decimal Net, decimal Tax, decimal Gross);

public sealed record SnapshotParty(Guid Id, string Role, Guid? PartyId, Guid? PersonId, int Position, string? Notes);

public sealed record ContractSnapshot(
    string Number,
    int VersionNo,
    string Title,
    string Type,
    string? Description,
    Guid PartyId,
    Guid? PersonId,
    DateOnly? StartDate,
    DateOnly? EndDate,
    bool IsIndefinite,
    string Currency,
    bool PriceIncludesTax,
    decimal? ContractValue,
    string? PaymentTerms,
    string RenewalMode,
    int? RenewalPeriodMonths,
    int? NoticePeriodDays,
    DateOnly? NoticeDeadline,
    string Confidentiality,
    decimal NetTotal,
    decimal TaxTotal,
    decimal GrossTotal,
    string? RecurringSummary,
    IReadOnlyList<SnapshotParty> Parties,
    IReadOnlyList<SnapshotLine> Lines)
{
    /// <summary>Hassas finansal alanlar maskelenmiş kopya (izni olmayan okuyucuya).</summary>
    public ContractSnapshot WithoutFinancials() => this with
    {
        ContractValue = null,
        NetTotal = 0m,
        TaxTotal = 0m,
        GrossTotal = 0m,
        RecurringSummary = null,
        Lines = [.. Lines.Select(l => l with { UnitPrice = 0m, DiscountPercent = 0m, Net = 0m, Tax = 0m, Gross = 0m })],
    };

    /// <summary>Alanların adı → metin değeri (karşılaştırma ve görüntüleme için; kalem ve taraflar hariç).</summary>
    public IReadOnlyDictionary<string, string?> HeaderFields() => new Dictionary<string, string?>
    {
        ["title"] = Title, ["type"] = Type, ["description"] = Description, ["partyId"] = PartyId.ToString(), ["personId"] = PersonId?.ToString(),
        ["startDate"] = StartDate?.ToString("yyyy-MM-dd"), ["endDate"] = EndDate?.ToString("yyyy-MM-dd"), ["isIndefinite"] = IsIndefinite ? "true" : "false",
        ["currency"] = Currency, ["priceIncludesTax"] = PriceIncludesTax ? "true" : "false", ["contractValue"] = ContractValue?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        ["paymentTerms"] = PaymentTerms, ["renewalMode"] = RenewalMode, ["renewalPeriodMonths"] = RenewalPeriodMonths?.ToString(), ["noticePeriodDays"] = NoticePeriodDays?.ToString(),
        ["noticeDeadline"] = NoticeDeadline?.ToString("yyyy-MM-dd"), ["confidentiality"] = Confidentiality,
        ["netTotal"] = NetTotal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), ["taxTotal"] = TaxTotal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        ["grossTotal"] = GrossTotal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
    };
}

public static class ContractSnapshots
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>Finansal alanlar: izni olmayan okuyucuya maskelenir, karşılaştırmada gösterilmez.</summary>
    public static readonly IReadOnlySet<string> FinancialFields =
        new HashSet<string>(StringComparer.Ordinal) { "contractValue", "netTotal", "taxTotal", "grossTotal", "unitPrice", "discountPercent", "net", "tax", "gross" };

    public static string Serialize(ContractSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

    public static ContractSnapshot? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ContractSnapshot>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>JSON metninin SHA-256 özeti (onaltılık, büyük harf). Aynı içerik aynı özeti verir.</summary>
    public static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
}

public sealed record FieldChange(string Field, string? Before, string? After);

/// <summary><see cref="Change"/>: Added, Removed, Changed.</summary>
public sealed record LineChange(Guid StableKey, string Change, string Name, IReadOnlyList<FieldChange> Fields);

public sealed record PartyChange(Guid Id, string Change, string Role, IReadOnlyList<FieldChange> Fields);

public sealed record SnapshotDiff(IReadOnlyList<FieldChange> Header, IReadOnlyList<LineChange> Lines, IReadOnlyList<PartyChange> Parties)
{
    public bool IsEmpty => Header.Count == 0 && Lines.Count == 0 && Parties.Count == 0;
}

/// <summary>İki anlık görüntü arasındaki fark; kalemler kararlı anahtarla, taraflar kimlikle eşlenir.</summary>
public static class SnapshotComparer
{
    public static SnapshotDiff Compare(ContractSnapshot before, ContractSnapshot after, bool includeFinancials)
    {
        var headerBefore = before.HeaderFields();
        var headerAfter = after.HeaderFields();
        var header = headerBefore.Keys
            .Where(k => includeFinancials || !ContractSnapshots.FinancialFields.Contains(k))
            .Where(k => headerBefore[k] != headerAfter[k])
            .Select(k => new FieldChange(k, headerBefore[k], headerAfter[k]))
            .ToList();

        var lines = new List<LineChange>();
        var beforeLines = before.Lines.ToDictionary(l => l.StableKey);
        var afterLines = after.Lines.ToDictionary(l => l.StableKey);
        foreach (var key in beforeLines.Keys.Union(afterLines.Keys))
        {
            beforeLines.TryGetValue(key, out var b);
            afterLines.TryGetValue(key, out var a);
            if (b is null)
            {
                lines.Add(new LineChange(key, "Added", a!.Name, LineFields(null, a, includeFinancials)));
            }
            else if (a is null)
            {
                lines.Add(new LineChange(key, "Removed", b.Name, LineFields(b, null, includeFinancials)));
            }
            else
            {
                var fields = LineFields(b, a, includeFinancials).Where(f => f.Before != f.After).ToList();
                if (fields.Count > 0)
                {
                    lines.Add(new LineChange(key, "Changed", a.Name, fields));
                }
            }
        }

        var parties = new List<PartyChange>();
        var beforeParties = before.Parties.ToDictionary(p => p.Id);
        var afterParties = after.Parties.ToDictionary(p => p.Id);
        foreach (var id in beforeParties.Keys.Union(afterParties.Keys))
        {
            beforeParties.TryGetValue(id, out var b);
            afterParties.TryGetValue(id, out var a);
            if (b is null)
            {
                parties.Add(new PartyChange(id, "Added", a!.Role, PartyFields(null, a)));
            }
            else if (a is null)
            {
                parties.Add(new PartyChange(id, "Removed", b.Role, PartyFields(b, null)));
            }
            else
            {
                var fields = PartyFields(b, a).Where(f => f.Before != f.After).ToList();
                if (fields.Count > 0)
                {
                    parties.Add(new PartyChange(id, "Changed", a.Role, fields));
                }
            }
        }

        return new SnapshotDiff(header, lines, parties);
    }

    private static List<FieldChange> LineFields(SnapshotLine? b, SnapshotLine? a, bool financials)
    {
        static string? Money(decimal? v) => v?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        static string? Qty(decimal? v) => v?.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        var list = new List<FieldChange>
        {
            new("kind", b?.Kind, a?.Kind), new("productCode", b?.ProductCode, a?.ProductCode), new("name", b?.Name, a?.Name),
            new("description", b?.Description, a?.Description), new("quantity", Qty(b?.Quantity), Qty(a?.Quantity)), new("unitName", b?.UnitName, a?.UnitName),
            new("taxRatePercent", Qty(b?.TaxRatePercent), Qty(a?.TaxRatePercent)), new("billingPeriod", b?.BillingPeriod, a?.BillingPeriod),
        };
        if (financials)
        {
            list.AddRange(
            [
                new("unitPrice", Qty(b?.UnitPrice), Qty(a?.UnitPrice)), new("discountPercent", Qty(b?.DiscountPercent), Qty(a?.DiscountPercent)),
                new("net", Money(b?.Net), Money(a?.Net)), new("tax", Money(b?.Tax), Money(a?.Tax)), new("gross", Money(b?.Gross), Money(a?.Gross)),
            ]);
        }

        return list;
    }

    private static List<FieldChange> PartyFields(SnapshotParty? b, SnapshotParty? a) =>
    [
        new("role", b?.Role, a?.Role), new("partyId", b?.PartyId?.ToString(), a?.PartyId?.ToString()),
        new("personId", b?.PersonId?.ToString(), a?.PersonId?.ToString()), new("notes", b?.Notes, a?.Notes),
    ];
}
