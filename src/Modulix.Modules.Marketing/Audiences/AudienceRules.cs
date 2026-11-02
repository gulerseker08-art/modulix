using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Marketing;

namespace Modulix.Modules.Marketing.Audiences;

/// <summary>Hedef kitle satırı durumları.</summary>
public static class AudienceStatuses
{
    public const string Eligible = "Eligible";

    /// <summary>Alıcı bulundu ama elendi (izin, engel, mükerrer, sınır...).</summary>
    public const string Excluded = "Excluded";

    /// <summary>Hedef için alıcı bulunamadı (kural eşleşmedi, iletişim noktası yok).</summary>
    public const string Unresolved = "Unresolved";
}

/// <param name="Address">Normalize adres; kalıcı snapshot'a yazılmaz (kişisel veri kopyalanmaz), gönderimden önce yeniden çözülür.</param>
public sealed record AudienceRow(Guid TargetId, Guid? PersonId, Guid? ContactPointId, string? Address, string Status, string? Code);

/// <summary>
/// Sayıların anlamı: <c>Targets</c> hedef firma/kişi/lead; <c>ResolvedTargets</c> en az bir alıcısı bulunan hedef; <c>Persons</c> çözümlenen farklı kişi;
/// <c>UniqueAddresses</c> elemeden önceki benzersiz iletişim noktası; <c>Eligible</c> uygun alıcı; <c>Excluded</c> elenen alıcı. Firma başına birden çok kişi
/// olabildiği için alıcı sayısı hedef sayısından farklıdır.
/// </summary>
public sealed record AudienceSummary(
    int Targets,
    int ResolvedTargets,
    int Persons,
    int UniqueAddresses,
    int Eligible,
    int Excluded,
    IReadOnlyDictionary<string, int> Exclusions,
    IReadOnlyDictionary<string, int> Unresolved);

public sealed record AudienceResult(AudienceSummary Summary, IReadOnlyList<AudienceRow> Rows);

/// <summary>Seçim aşamasının çıktısı: adres kontrolüne gidecek adaylar ve bu aşamada elenenler.</summary>
public sealed record AudienceSelection(
    IReadOnlyList<AudienceRow> Candidates, IReadOnlyList<AudienceRow> Others, int Targets, int ResolvedTargets, int Persons, IReadOnlyDictionary<string, int> Unresolved);

/// <summary>
/// Hedef kitle kuralları (ADR-0025, dilim 4). Saf sınıf: veritabanına dokunmaz. İki aşama: <see cref="Select"/> çözülmüş kişilerden adayları seçer
/// (geçersiz/paylaşılan adres, aynı kişi, hedef başına sınır, aynı adres); <see cref="Finish"/> adayları izin ve engel kararlarıyla uygun/elenen ayırır.
/// Segment üyeliği gönderim izni vermez: karar her zaman <see cref="ICommunicationPreferences"/>'tandır ve gönderimden önce yeniden verilir.
/// </summary>
public static class AudienceRules
{
    public static AudienceSelection Select(
        IReadOnlyList<Guid> targets, RecipientResolution resolution, CommunicationChannel channel, int? maxPerTarget)
    {
        var others = new List<AudienceRow>();
        var candidates = new List<AudienceRow>();
        var unresolved = new Dictionary<string, int>(StringComparer.Ordinal);
        var resolvedTargets = new HashSet<Guid>();
        var persons = new HashSet<Guid>();
        var seenPersons = new HashSet<Guid>();
        var seenAddresses = new HashSet<string>(StringComparer.Ordinal);

        foreach (var missing in resolution.Unresolved.DistinctBy(u => u.TargetId))
        {
            Count(unresolved, missing.Code);
            others.Add(new AudienceRow(missing.TargetId, null, null, null, AudienceStatuses.Unresolved, missing.Code));
        }

        var unresolvedIds = resolution.Unresolved.Select(u => u.TargetId).ToHashSet();
        foreach (var target in targets)
        {
            var contacts = resolution.Contacts
                .Where(c => c.TargetId == target)
                .OrderByDescending(c => c.IsPrimary)
                .ThenBy(c => c.ContactPointId)
                .ToList();
            if (contacts.Count == 0)
            {
                // Çözümleyici hiçbir şey söylemediyse de hedef sessizce kaybolmaz.
                if (!unresolvedIds.Contains(target))
                {
                    Count(unresolved, RecipientCodes.NoContactPoint);
                    others.Add(new AudienceRow(target, null, null, null, AudienceStatuses.Unresolved, RecipientCodes.NoContactPoint));
                }

                continue;
            }

            resolvedTargets.Add(target);
            var kept = 0;
            var personsOfTarget = new HashSet<Guid>();
            foreach (var contact in contacts)
            {
                if (contact.PersonId is { } person)
                {
                    persons.Add(person);
                }

                var normalized = CommunicationAddress.Normalize(channel, contact.Address);
                string? code = null;
                if (normalized is null)
                {
                    code = CommunicationCodes.InvalidAddress;
                }
                else if (contact.IsShared)
                {
                    code = RecipientCodes.SharedAddress;
                }
                else if (contact.PersonId is { } p && (seenPersons.Contains(p) || personsOfTarget.Contains(p)))
                {
                    code = RecipientCodes.DuplicatePerson;
                }
                else if (maxPerTarget is { } max && kept >= max)
                {
                    code = RecipientCodes.PerTargetLimit;
                }
                else if (!seenAddresses.Add(normalized))
                {
                    code = RecipientCodes.DuplicateAddress;
                }

                if (code is not null)
                {
                    others.Add(new AudienceRow(target, contact.PersonId, contact.ContactPointId, normalized, AudienceStatuses.Excluded, code));
                    continue;
                }

                kept++;
                if (contact.PersonId is { } chosen)
                {
                    seenPersons.Add(chosen);
                    personsOfTarget.Add(chosen);
                }

                candidates.Add(new AudienceRow(target, contact.PersonId, contact.ContactPointId, normalized, AudienceStatuses.Eligible, null));
            }
        }

        return new AudienceSelection(candidates, others, targets.Count, resolvedTargets.Count, persons.Count, unresolved);
    }

    public static AudienceResult Finish(AudienceSelection selection, IReadOnlyDictionary<string, CommunicationDecision> decisions)
    {
        var rows = new List<AudienceRow>(selection.Candidates.Count + selection.Others.Count);
        var exclusions = new Dictionary<string, int>(StringComparer.Ordinal);
        var eligible = 0;
        foreach (var candidate in selection.Candidates)
        {
            var decision = candidate.Address is { } address && decisions.TryGetValue(address, out var found)
                ? found
                : new CommunicationDecision(CommunicationOutcome.Unknown, CommunicationCodes.Unknown, "İzin bilinmiyor.");
            if (decision.IsAllowed)
            {
                eligible++;
                rows.Add(candidate);
            }
            else
            {
                Count(exclusions, decision.Code);
                rows.Add(candidate with { Status = AudienceStatuses.Excluded, Code = decision.Code });
            }
        }

        foreach (var other in selection.Others)
        {
            if (other.Status == AudienceStatuses.Excluded)
            {
                Count(exclusions, other.Code!);
            }

            rows.Add(other);
        }

        var uniqueAddresses = selection.Candidates.Select(c => c.Address).Distinct(StringComparer.Ordinal).Count();
        var excluded = rows.Count(r => r.Status == AudienceStatuses.Excluded);
        var summary = new AudienceSummary(
            selection.Targets, selection.ResolvedTargets, selection.Persons, uniqueAddresses, eligible, excluded, exclusions, selection.Unresolved);
        return new AudienceResult(summary, rows);
    }

    private static void Count(Dictionary<string, int> counts, string code) => counts[code] = counts.GetValueOrDefault(code) + 1;
}
