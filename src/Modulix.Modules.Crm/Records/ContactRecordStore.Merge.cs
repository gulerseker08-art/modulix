using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

// Kişi birleştirme (ADR-0013). Kaynak kişi ve ortak kişi kaydı kaldırılır; firma ilişkileri, iletişim noktaları, fırsat,
// aktivite ve lokasyon yetkilisi başvuruları hedef kişiye taşınır. Kaydetmez; boru hattı tek işlemde yazar.
internal sealed partial class ContactRecordStore
{
    protected override string? MergePermission => CrmModule.Permissions.ContactsMerge;

    string IMergeableRecordStore.MergePermission => CrmModule.Permissions.ContactsMerge;

    string? IMergeableRecordStore.SharedEntityKey => SharedEntities.Person;

    public async Task<Guid?> SharedIdOfAsync(Guid id, CancellationToken cancellationToken) =>
        await Context.Data.Set<Contact>().AsNoTracking().Where(c => c.Id == id).Select(c => (Guid?)c.PersonId).SingleOrDefaultAsync(cancellationToken);

    public Task<MergeLink?> FindMergeLinkAsync(Guid sourceId, CancellationToken cancellationToken) =>
        MergeSupport.FindLinkAsync(Context.Data, EntityKey, sourceId, cancellationToken);

    public async Task<MergePreview?> PreviewMergeAsync(MergeRequest request, CancellationToken cancellationToken) =>
        (await RunMergeAsync(request, apply: false, cancellationToken)).Preview;

    public async Task<MergeOutcome?> MergeAsync(MergeRequest request, CancellationToken cancellationToken)
    {
        var (preview, outcome) = await RunMergeAsync(request, apply: true, cancellationToken);
        return preview?.Blockers is { Count: > 0 } blockers
            ? throw new RecordValidationException([.. blockers.Select(b => new RecordFieldError("merge", b))])
            : outcome;
    }

    private async Task<(MergePreview? Preview, MergeOutcome? Outcome)> RunMergeAsync(MergeRequest request, bool apply, CancellationToken cancellationToken)
    {
        var data = Context.Data;
        var source = await Load(data.Set<Contact>()).SingleOrDefaultAsync(c => c.Id == request.SourceId, cancellationToken);
        var target = await Load(data.Set<Contact>()).SingleOrDefaultAsync(c => c.Id == request.TargetId, cancellationToken);
        if (source is null || target is null)
        {
            return (null, null);
        }

        // Önizleme güncel sürümleri döndürür (ekran henüz bilmez); gerçek birleştirme ekranın gördüğü sürümleri ister.
        if (apply)
        {
            EnsureRecordVersion(source, request.SourceVersion);
            EnsureRecordVersion(target, request.TargetVersion);
        }

        var sourceBefore = Snapshot(source);
        var targetBefore = Snapshot(target);

        // Firma, ilişkilerden türetilir (ADR-0011); kimlik alanları seçilemez.
        var skip = new[] { "personId", "companyId" };
        var diffs = MergeSupport.Diff(AllFields, source, target, source.CustomFields, target.CustomFields, skip);
        var choices = MergeSupport.ValidateChoices(request.FieldChoices, AllFields.Select(f => f.Name).Except(skip));
        var impacts = new List<MergeImpact>();

        var sharedBefore = SharedState(target);
        foreach (var field in AllFields.Where(f => !skip.Contains(f.Name) && choices.GetValueOrDefault(f.Name) == MergeRequest.FromSource))
        {
            field.Write(target, field.Parse(field.Read(source)).Value);
        }

        target.CustomFields = MergeSupport.MergeCustom(source.CustomFields, target.CustomFields, choices);
        target.OwnerId ??= source.OwnerId;
        target.Notes = Join(target.Notes, source.Notes, choices.GetValueOrDefault("notes"));

        var linkMap = await MoveLinksAsync(source, target, impacts, cancellationToken);
        await MovePointsAsync(source, target, linkMap, choices, impacts, cancellationToken);

        foreach (var opportunity in await data.Set<Opportunity>().Where(o => o.ContactId == source.Id).ToListAsync(cancellationToken))
        {
            opportunity.ContactId = target.Id;
            opportunity.Touch();
            MergeSupport.AddImpact(impacts, "Fırsat başvurusu taşınır", 1);
        }

        foreach (var activity in await data.Set<Activity>().Where(a => a.ContactId == source.Id).ToListAsync(cancellationToken))
        {
            activity.ContactId = target.Id;
            activity.Touch();
            MergeSupport.AddImpact(impacts, "Aktivite başvurusu taşınır", 1);
        }

        await ActivityMergeSupport.MoveLinksAsync(data, EntityKey, source.Id, target.Id, impacts, cancellationToken);
        await ActivityMergeSupport.MoveParticipantsAsync(data, source.Id, target.Id, impacts, cancellationToken);

        foreach (var location in await data.Set<Location>().Where(l => l.ContactId == source.Id).ToListAsync(cancellationToken))
        {
            location.ContactId = target.Id;
            location.Touch();
            MergeSupport.AddImpact(impacts, "Lokasyon yetkilisi başvurusu taşınır", 1);
        }

        impacts = [.. impacts.GroupBy(i => i.Label).Select(g => new MergeImpact(g.Key, g.Sum(i => i.Count)))];
        target.Touch();
        if (!JsonNode.DeepEquals(sharedBefore, SharedState(target)))
        {
            target.Person.Touch();
        }

        impacts.Add(new MergeImpact("Kaynak kişi kaydı kaldırılır", 1));
        var preview = new MergePreview(sourceBefore, targetBefore, diffs, impacts, []);
        if (!apply)
        {
            return (preview, null);
        }

        data.Set<Contact>().Remove(source);
        data.Set<Person>().Remove(source.Person);
        MergeSupport.AddLink(data, EntityKey, source.Id, target.Id, Context.CurrentUserId);
        return (preview, new MergeOutcome(sourceBefore, targetBefore, Snapshot(target), impacts));
    }

    // Notlar kaybolmasın: varsayılan hedef notu kalır, kaynağın notu da altına eklenir; "source" seçilmişse yalnızca kaynağınki.
    private static string? Join(string? target, string? source, string? choice) =>
        choice == MergeRequest.FromSource || string.IsNullOrWhiteSpace(source) || source == target
            ? target
            : string.IsNullOrWhiteSpace(target) ? source : $"{target}\n---\n{source}";

    /// <summary>
    /// Firma ilişkileri: aynı firmayla iki aktif ilişki tek kalır (roller birleşir), diğerleri hedef kişiye geçer. Kaynak
    /// ilişkinin kimliğinden hedef ilişkinin kimliğine eşleme verilir (ilişkiye özel iletişim noktaları için).
    /// </summary>
    private async Task<Dictionary<Guid, Guid>> MoveLinksAsync(Contact source, Contact target, List<MergeImpact> impacts, CancellationToken cancellationToken)
    {
        var data = Context.Data;
        var links = data.Set<CompanyContact>();
        var sourceLinks = await links.Where(l => l.ContactId == source.Id).ToListAsync(cancellationToken);
        var targetLinks = await links.Where(l => l.ContactId == target.Id).ToListAsync(cancellationToken);
        var map = new Dictionary<Guid, Guid>();
        var gone = new List<Guid>();
        var added = new List<(Guid, DateOnly, DateTimeOffset)>();

        foreach (var link in sourceLinks)
        {
            var twin = link.IsActive ? targetLinks.FirstOrDefault(l => l.IsActive && l.CompanyId == link.CompanyId) : null;
            if (twin is null)
            {
                link.ContactId = target.Id;
                link.Touch();
                if (link.IsActive)
                {
                    added.Add((link.CompanyId, link.StartDate, link.CreatedAt));
                }

                gone.Add(link.Id);
                MergeSupport.AddImpact(impacts, "Firma ilişkisi taşınır", 1);
                continue;
            }

            var roles = twin.RoleList.Union(link.RoleList, StringComparer.Ordinal).ToList();
            twin.Roles = roles.Count > 0 ? string.Join(',', roles) : null;
            twin.IsDecisionMaker |= link.IsDecisionMaker;
            twin.IsPrimary |= link.IsPrimary;
            twin.StartDate = link.StartDate < twin.StartDate ? link.StartDate : twin.StartDate;
            twin.Department ??= link.Department;
            twin.JobTitle ??= link.JobTitle;
            twin.Touch();
            map[link.Id] = twin.Id;
            link.IsPrimary = false;
            links.Remove(link);
            MergeSupport.AddImpact(impacts, "Firma ilişkisi hedefteki ilişkiyle birleşir", 1);
        }

        // Birincil firma (ADR-0011): en eski başlayan aktif ilişki; ortak kişinin çalıştığı taraf ona eşitlenir.
        var primary = await CompanyLinks.PrimaryCompanyAsync(data, target.Id, map.Keys.Concat(gone).ToList(), added, cancellationToken);
        if (target.CompanyId != primary)
        {
            target.CompanyId = primary;
            target.Person.PartyId = primary is { } companyId
                ? await data.Set<Company>().Where(c => c.Id == companyId).Select(c => (Guid?)c.PartyId).SingleOrDefaultAsync(cancellationToken)
                : null;
        }

        return map;
    }

    /// <summary>İletişim noktaları taşınır; aynı kanal ve değer (aynı ilişkide) tek kalır, ana nokta hedefinki korunur.</summary>
    private async Task MovePointsAsync(
        Contact source, Contact target, Dictionary<Guid, Guid> linkMap, IReadOnlyDictionary<string, string> choices, List<MergeImpact> impacts, CancellationToken cancellationToken)
    {
        var points = Context.Data.Set<ContactPoint>();
        var targetPoints = await points.Where(p => p.ContactId == target.Id).ToListAsync(cancellationToken);
        foreach (var point in await points.Where(p => p.ContactId == source.Id).ToListAsync(cancellationToken))
        {
            var linkId = point.CompanyContactId is { } old && linkMap.TryGetValue(old, out var twin) ? twin : point.CompanyContactId;
            if (targetPoints.Any(t => t.CompanyContactId == linkId && t.Channel == point.Channel && t.NormalizedValue == point.NormalizedValue))
            {
                points.Remove(point);
                MergeSupport.AddImpact(impacts, "Aynı iletişim noktası tek kalır", 1);
                continue;
            }

            if (point.IsActive && point.IsPrimary
                && targetPoints.FirstOrDefault(t => t.CompanyContactId == linkId && t.IsActive && t.IsPrimary && t.Channel == point.Channel) is { } current)
            {
                // Genel ana nokta için eski alan "source" seçildiyse kaynağınki öne geçer; aksi halde hedefinki kalır.
                if (linkId is null && choices.GetValueOrDefault(point.Channel == ContactNormalization.Phone ? "phone" : "email") == MergeRequest.FromSource)
                {
                    MergeSwap.Contents(current, point);
                }

                point.IsPrimary = false;
            }

            point.ContactId = target.Id;
            point.CompanyContactId = linkId;
            point.Touch();
            targetPoints.Add(point);
            MergeSupport.AddImpact(impacts, "İletişim noktası taşınır", 1);
        }

        // Eski telefon/e-posta alanları sonuçtaki genel ana noktanın aynasıdır (ADR-0012).
        foreach (var channel in new[] { ContactNormalization.Phone, ContactNormalization.Email })
        {
            if (targetPoints.FirstOrDefault(t => t.CompanyContactId == null && t.IsActive && t.IsPrimary && t.Channel == channel) is { } primary)
            {
                LegacyContactSync.MirrorPoint(primary, wasPrimary: true, null, target.Person);
            }
        }
    }
}
