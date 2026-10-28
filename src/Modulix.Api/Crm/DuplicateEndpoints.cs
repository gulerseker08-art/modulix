using System.Text;
using System.Text.Json.Nodes;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Crm;
using Modulix.Shared.Crm;

namespace Modulix.Api.Crm;

/// <summary>
/// Mükerrer önerisi (ADR-0013): <c>GET /api/crm/companies/duplicates</c> ve <c>GET /api/crm/contacts/duplicates</c>. Kayıt
/// açmadan ya da düzenlerken olası eşleşmeleri verir; hiçbir şeyi birleştirmez ya da bağlamaz. Aramalar boru hattından
/// geçer: yalnızca çağıranın görebildiği CRM kayıtları döner. Kesin eşleşme yalnızca vergi numarasıdır; ad, e-posta ve telefon
/// "olası eşleşme"dir (paylaşılan santral/e-posta noktaları eşleşme sayılmaz).
/// </summary>
internal static class DuplicateEndpoints
{
    private const int Limit = 10;

    // Ünvandaki bu sözcükler eşleşmeyi bozmasın ("Acme A.Ş." ile "ACME Anonim Şirketi").
    private static readonly HashSet<string> LegalWords =
    [
        "as", "a", "s", "anonim", "sirketi", "ltd", "sti", "limited", "san", "tic", "ve", "sanayi", "ticaret", "ic", "dis", "inc", "llc", "gmbh",
    ];

    public static void Map(RouteGroupBuilder crm)
    {
        crm.MapGet("/companies/duplicates", CompaniesAsync).RequirePermission(CrmModule.Permissions.CompaniesRead);
        crm.MapGet("/contacts/duplicates", ContactsAsync).RequirePermission(CrmModule.Permissions.ContactsRead);
    }

    private static async Task<IResult> CompaniesAsync(
        string? name, string? taxNumber, string? email, string? phone, Guid? excludeId, IRecordPipeline pipeline, CancellationToken ct)
    {
        var found = new Dictionary<string, Candidate>();

        if (taxNumber?.Trim() is { Length: > 0 } number)
        {
            var exact = await pipeline.QueryAsync(Query(CrmModule.Entities.Company, null, ("taxNumber", number)), ct);
            if (!exact.IsSuccess)
            {
                return RecordEndpoints.ToProblem(exact.Error!);
            }

            foreach (var item in exact.Value!.Items.Where(i => i.Id != excludeId))
            {
                Add(found, item, "Aynı vergi numarası", exact: true);
            }

            // Aynı numaralı taraf başka bir modülde (ya da kapsam dışında) olabilir; yalnızca asgari kimlik döner.
            var parties = await pipeline.QueryAsync(Query(SharedEntities.Party, null, ("taxNumber", number)), ct);
            if (parties.IsSuccess)
            {
                var known = found.Values.Select(c => c.SharedId).ToHashSet();
                foreach (var party in parties.Value!.Items.Where(p => !known.Contains(p.Id)))
                {
                    var own = excludeId is { } self
                        && (await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Company, self), ct)).Value?.Values["partyId"]?.GetValue<string>() == party.Id.ToString();
                    if (!own)
                    {
                        found[$"p{party.Id}"] = new Candidate(null, party.Id, party.Values, true, ["Aynı vergi numarası (başka bir modülde ya da erişiminiz dışında kayıtlı)"]);
                    }
                }
            }
        }

        if (name is { Length: > 0 } && NameKey(name) is { Length: >= 3 } key)
        {
            // Arama ünvandaki en uzun sözcükle yapılır; benzerlik ünvan anahtarıyla ayrıca denetlenir.
            var term = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(t => t.Length).First();
            var similar = await pipeline.QueryAsync(Query(CrmModule.Entities.Company, term), ct);
            if (!similar.IsSuccess)
            {
                return RecordEndpoints.ToProblem(similar.Error!);
            }

            foreach (var item in similar.Value!.Items.Where(i => i.Id != excludeId && SimilarNames(key, NameKey(i.Values["name"]?.GetValue<string>() ?? string.Empty))))
            {
                Add(found, item, "Ad benzer", exact: false);
            }
        }

        foreach (var (value, label) in new[] { (email, "Aynı e-posta"), (phone, "Aynı telefon") })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var byPoint = await pipeline.QueryAsync(Query(CrmModule.Entities.Company, null, ("contactValue", value.Trim())), ct);
            if (byPoint.IsSuccess)
            {
                foreach (var item in byPoint.Value!.Items.Where(i => i.Id != excludeId))
                {
                    Add(found, item, label, exact: false);
                }
            }
        }

        return Results.Ok(Ordered(found, "name"));
    }

    private static async Task<IResult> ContactsAsync(
        string? firstName, string? lastName, string? email, string? phone, Guid? excludeId, IRecordPipeline pipeline, CancellationToken ct)
    {
        var found = new Dictionary<string, Candidate>();

        if (lastName?.Trim() is { Length: > 1 } last)
        {
            var byName = await pipeline.QueryAsync(Query(CrmModule.Entities.Contact, last), ct);
            if (!byName.IsSuccess)
            {
                return RecordEndpoints.ToProblem(byName.Error!);
            }

            var wanted = Fold($"{firstName} {last}");
            foreach (var item in byName.Value!.Items.Where(i => i.Id != excludeId
                && Fold($"{i.Values["firstName"]?.GetValue<string>()} {i.Values["lastName"]?.GetValue<string>()}") == wanted))
            {
                Add(found, item, "Aynı ad ve soyad", exact: false);
            }
        }

        foreach (var (value, label) in new[] { (email, "Aynı e-posta"), (phone, "Aynı telefon") })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var byPoint = await pipeline.QueryAsync(Query(CrmModule.Entities.Contact, null, ("contactValue", value.Trim())), ct);
            if (byPoint.IsSuccess)
            {
                foreach (var item in byPoint.Value!.Items.Where(i => i.Id != excludeId))
                {
                    Add(found, item, label, exact: false);
                }
            }
        }

        return Results.Ok(Ordered(found, null));
    }

    private sealed record Candidate(Guid? Id, Guid? SharedId, JsonObject Values, bool Exact, List<string> Reasons);

    private static RecordQuery Query(string entityKey, string? search, params (string Name, string Value)[] filters) =>
        new(entityKey, 1, Limit * 2, search, filters.ToDictionary(f => f.Name, f => f.Value));

    private static void Add(Dictionary<string, Candidate> found, RecordData item, string reason, bool exact)
    {
        var shared = item.Values.TryGetPropertyValue("partyId", out var p) && Guid.TryParse(p?.GetValue<string>(), out var partyId)
            ? partyId
            : item.Values.TryGetPropertyValue("personId", out var q) && Guid.TryParse(q?.GetValue<string>(), out var personId) ? personId : (Guid?)null;
        var key = item.Id.ToString();
        found[key] = found.TryGetValue(key, out var existing)
            ? existing with { Exact = existing.Exact || exact, Reasons = [.. existing.Reasons, reason] }
            : new Candidate(item.Id, shared, item.Values, exact, [reason]);
    }

    private static List<DuplicateCandidateDto> Ordered(Dictionary<string, Candidate> found, string? nameField) =>
    [
        .. found.Values
            .OrderByDescending(c => c.Exact)
            .ThenByDescending(c => c.Reasons.Count)
            .Take(Limit)
            .Select(c => new DuplicateCandidateDto(
                c.Id,
                c.SharedId,
                nameField is not null
                    ? c.Values[nameField]?.GetValue<string>() ?? string.Empty
                    : $"{c.Values["firstName"]?.GetValue<string>()} {c.Values["lastName"]?.GetValue<string>()}".Trim(),
                c.Values["taxNumber"]?.GetValue<string>(),
                c.Values["city"]?.GetValue<string>(),
                c.Values["email"]?.GetValue<string>(),
                c.Values["phone"]?.GetValue<string>(),
                c.Exact ? CrmMatchKinds.Exact : CrmMatchKinds.Similar,
                c.Reasons)),
    ];

    /// <summary>Küçük harf, aksansız, noktalamasız, tek boşluk.</summary>
    private static string Fold(string text)
    {
        var map = new Dictionary<char, char> { ['ş'] = 's', ['ı'] = 'i', ['ğ'] = 'g', ['ü'] = 'u', ['ö'] = 'o', ['ç'] = 'c', ['İ'] = 'i' };
        var builder = new StringBuilder();
        foreach (var c in text.Trim())
        {
            // Büyük harfli Türkçe karakterler de katlanır ("Ş" → "s"); önce doğrudan, sonra küçültüp eşlenir.
            var lower = map.TryGetValue(c, out var mapped) ? mapped : char.ToLowerInvariant(c);
            lower = map.TryGetValue(lower, out var folded) ? folded : lower;
            builder.Append(char.IsLetterOrDigit(lower) ? lower : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NameKey(string name) =>
        string.Join(' ', Fold(name).Split(' ').Where(t => !LegalWords.Contains(t)));

    // Aynı anahtar ya da biri ötekinin başı (sözcük sınırında) ve yeterince uzun: "acme" ~ "acme lojistik".
    private static bool SimilarNames(string a, string b) =>
        a.Length >= 3 && b.Length >= 3 && (a == b || (Math.Min(a.Length, b.Length) >= 4 && (a.StartsWith(b + " ") || b.StartsWith(a + " "))));
}
