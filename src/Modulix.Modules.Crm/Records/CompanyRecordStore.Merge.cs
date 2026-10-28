using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

// Firma birleştirme (ADR-0013). Kaynak firma ve tarafı kaldırılır; bağlı kayıtlar hedefe taşınır, çakışanlar birleşir.
// Hiçbir şey kaydetmez: boru hattı değişiklikleri, birleştirme izini, audit'i ve olayı tek işlemde yazar.
internal sealed partial class CompanyRecordStore
{
    protected override string? MergePermission => CrmModule.Permissions.CompaniesMerge;

    string IMergeableRecordStore.MergePermission => CrmModule.Permissions.CompaniesMerge;

    string? IMergeableRecordStore.SharedEntityKey => SharedEntities.Party;

    public async Task<Guid?> SharedIdOfAsync(Guid id, CancellationToken cancellationToken) =>
        await Context.Data.Set<Company>().AsNoTracking().Where(c => c.Id == id).Select(c => (Guid?)c.PartyId).SingleOrDefaultAsync(cancellationToken);

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

    /// <summary>
    /// Önizleme ve birleştirme aynı yoldan geçer; fark, <paramref name="apply"/> ile kaynağın, tarafının silinmesi ve birleştirme
    /// izinin eklenmesidir. Önizlemede değişiklikler yalnızca bellektedir ve kaydedilmez.
    /// </summary>
    private async Task<(MergePreview? Preview, MergeOutcome? Outcome)> RunMergeAsync(MergeRequest request, bool apply, CancellationToken cancellationToken)
    {
        var data = Context.Data;
        var source = await Load(data.Set<Company>()).SingleOrDefaultAsync(c => c.Id == request.SourceId, cancellationToken);
        var target = await Load(data.Set<Company>()).SingleOrDefaultAsync(c => c.Id == request.TargetId, cancellationToken);
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
        var diffs = MergeSupport.Diff(AllFields, source, target, source.CustomFields, target.CustomFields, "partyId");
        var choices = MergeSupport.ValidateChoices(request.FieldChoices, AllFields.Select(f => f.Name));
        var blockers = new List<string>();
        var impacts = new List<MergeImpact>();

        if (source.Party.Kind != target.Party.Kind)
        {
            blockers.Add("Tüzel kişi firması ile gerçek kişi firması birleştirilemez.");
        }

        // Alan seçimleri: varsayılan hedef; "source" seçilen alan kaynaktan yazılır. Vergi no hedefe geçerken kaynağınki boşaltılır
        // (kaynak aynı işlemde silinir; benzersizlik hedef için korunur).
        var sharedBefore = SharedState(target);
        foreach (var field in AllFields.Where(f => f.Name != "partyId" && choices.GetValueOrDefault(f.Name) == MergeRequest.FromSource))
        {
            field.Write(target, field.Parse(field.Read(source)).Value);
        }

        if (choices.GetValueOrDefault("taxNumber") == MergeRequest.FromSource)
        {
            source.Party.TaxNumber = null;
        }

        target.CustomFields = MergeSupport.MergeCustom(source.CustomFields, target.CustomFields, choices);

        await MoveContactLinksAsync(source, target, impacts, cancellationToken);
        await MoveDirectoryAsync(source, target, choices, impacts, blockers, cancellationToken);
        await MoveRelationsAsync(source, target, impacts, blockers, cancellationToken);

        foreach (var opportunity in await data.Set<Opportunity>().Where(o => o.CompanyId == source.Id).ToListAsync(cancellationToken))
        {
            opportunity.CompanyId = target.Id;
            opportunity.Touch();
            MergeSupport.AddImpact(impacts, "Fırsat taşınır", 1);
        }

        foreach (var activity in await data.Set<Activity>().Where(a => a.CompanyId == source.Id).ToListAsync(cancellationToken))
        {
            activity.CompanyId = target.Id;
            activity.Touch();
            MergeSupport.AddImpact(impacts, "Aktivite taşınır", 1);
        }

        impacts = [.. impacts.GroupBy(i => i.Label).Select(g => new MergeImpact(g.Key, g.Sum(i => i.Count)))];
        target.Touch();
        if (!JsonNode.DeepEquals(sharedBefore, SharedState(target)))
        {
            target.Party.Touch();
        }

        impacts.Add(new MergeImpact("Kaynak firma kaydı kaldırılır", 1));
        var preview = new MergePreview(sourceBefore, targetBefore, diffs, impacts, blockers);
        if (!apply || blockers.Count > 0)
        {
            return (preview, null);
        }

        data.Set<Company>().Remove(source);
        data.Set<Party>().Remove(source.Party);
        MergeSupport.AddLink(data, EntityKey, source.Id, target.Id, Context.CurrentUserId);
        return (preview, new MergeOutcome(sourceBefore, targetBefore, Snapshot(target), impacts));
    }

    /// <summary>Firma–kişi ilişkileri: aynı kişinin aktif ilişkisi birleşir, diğerleri taşınır; kişilerin birincil firması yeniden türetilir.</summary>
    private async Task MoveContactLinksAsync(Company source, Company target, List<MergeImpact> impacts, CancellationToken cancellationToken)
    {
        var data = Context.Data;
        var links = data.Set<CompanyContact>();
        var points = data.Set<ContactPoint>();
        var sourceLinks = await links.Where(l => l.CompanyId == source.Id).ToListAsync(cancellationToken);
        var targetLinks = await links.Where(l => l.CompanyId == target.Id).ToListAsync(cancellationToken);
        var targetHasPrimary = targetLinks.Any(l => l.IsActive && l.IsPrimary);
        var gone = new List<Guid>();
        var added = new Dictionary<Guid, List<(Guid, DateOnly, DateTimeOffset)>>();
        var affected = new HashSet<Guid>();

        foreach (var link in sourceLinks)
        {
            affected.Add(link.ContactId);
            var twin = link.IsActive ? targetLinks.FirstOrDefault(l => l.IsActive && l.ContactId == link.ContactId) : null;
            gone.Add(link.Id);
            if (twin is null)
            {
                link.CompanyId = target.Id;
                if (link.IsActive && link.IsPrimary)
                {
                    // Hedefin ana irtibatı varsa o kalır (firmada tek ana irtibat).
                    link.IsPrimary = !targetHasPrimary;
                    targetHasPrimary = true;
                }

                link.Touch();
                if (link.IsActive)
                {
                    if (!added.TryGetValue(link.ContactId, out var list))
                    {
                        added[link.ContactId] = list = [];
                    }

                    list.Add((target.Id, link.StartDate, link.CreatedAt));
                }

                MergeSupport.AddImpact(impacts, "Kişi ilişkisi taşınır", 1);
                continue;
            }

            var roles = twin.RoleList.Union(link.RoleList, StringComparer.Ordinal).ToList();
            twin.Roles = roles.Count > 0 ? string.Join(',', roles) : null;
            twin.IsDecisionMaker |= link.IsDecisionMaker;
            twin.StartDate = link.StartDate < twin.StartDate ? link.StartDate : twin.StartDate;
            twin.Department ??= link.Department;
            twin.JobTitle ??= link.JobTitle;
            if (link.IsPrimary && !twin.IsPrimary && !targetHasPrimary)
            {
                twin.IsPrimary = true;
                targetHasPrimary = true;
            }

            twin.Touch();
            var twinPoints = await points.Where(p => p.CompanyContactId == twin.Id).ToListAsync(cancellationToken);
            foreach (var point in await points.Where(p => p.CompanyContactId == link.Id).ToListAsync(cancellationToken))
            {
                if (twinPoints.Any(t => t.Channel == point.Channel && t.NormalizedValue == point.NormalizedValue))
                {
                    points.Remove(point);
                    continue;
                }

                point.CompanyContactId = twin.Id;
                point.IsPrimary &= !twinPoints.Any(t => t.IsActive && t.IsPrimary && t.Channel == point.Channel);
                point.Touch();
                twinPoints.Add(point);
            }

            links.Remove(link);
            MergeSupport.AddImpact(impacts, "Kişi ilişkisi hedefteki ilişkiyle birleşir", 1);
        }

        var contacts = await data.Set<Contact>().Include(c => c.Person)
            .Where(c => c.CompanyId == source.Id || affected.Contains(c.Id)).ToListAsync(cancellationToken);
        foreach (var contact in contacts)
        {
            var primary = await CompanyLinks.PrimaryCompanyAsync(
                data, contact.Id, gone, added.GetValueOrDefault(contact.Id) ?? [], cancellationToken);
            if (contact.CompanyId == primary)
            {
                continue;
            }

            contact.CompanyId = primary;
            contact.Person.PartyId = primary == target.Id
                ? target.PartyId
                : primary is { } other ? await data.Set<Company>().Where(c => c.Id == other).Select(c => (Guid?)c.PartyId).SingleOrDefaultAsync(cancellationToken) : null;
            contact.Touch();
            contact.Person.Touch();
        }
    }

    /// <summary>Lokasyonlar taşınır (kod çakışması engeldir); adresler ve iletişim noktaları taşınır, aynı olanlar tek kalır.</summary>
    private async Task MoveDirectoryAsync(
        Company source, Company target, IReadOnlyDictionary<string, string> choices, List<MergeImpact> impacts, List<string> blockers, CancellationToken cancellationToken)
    {
        var data = Context.Data;
        var codes = (await data.Set<Location>().Where(l => l.CompanyId == target.Id && l.Code != null).Select(l => l.Code!).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var location in await data.Set<Location>().Where(l => l.CompanyId == source.Id).ToListAsync(cancellationToken))
        {
            if (location.Code is { } code && codes.Contains(code))
            {
                blockers.Add($"Lokasyon kodu iki firmada da var: {code}. Birleştirmeden önce birini yeniden adlandırın.");
                continue;
            }

            location.CompanyId = target.Id;
            location.Touch();
            MergeSupport.AddImpact(impacts, "Lokasyon taşınır", 1);
        }

        // Ana kayıt (telefon, e-posta, adres) için varsayılan hedefinkidir; eski alan "source" seçildiyse kaynağın ana kaydı öne geçer.
        // Eski alanlar sonuçtaki ana kaydın aynası olarak yeniden yazılır (ADR-0012): tek doğruluk kaynağı yapılandırılmış kayıtlardır.
        var addresses = data.Set<Address>();
        var targetAddresses = await addresses.Where(a => a.CompanyId == target.Id).ToListAsync(cancellationToken);
        var preferAddress = choices.GetValueOrDefault("address") == MergeRequest.FromSource || choices.GetValueOrDefault("city") == MergeRequest.FromSource;
        foreach (var address in await addresses.Where(a => a.CompanyId == source.Id).ToListAsync(cancellationToken))
        {
            if (address.LocationId is null
                && targetAddresses.Any(t => t.LocationId == null && t.Type == address.Type && t.NormalizedText == address.NormalizedText))
            {
                addresses.Remove(address);
                MergeSupport.AddImpact(impacts, "Aynı adres tek kalır", 1);
                continue;
            }

            if (address.LocationId is null && address.IsActive && address.IsPrimary
                && targetAddresses.FirstOrDefault(t => t.LocationId == null && t.IsActive && t.IsPrimary) is { } currentAddress)
            {
                // Kaynağın ana adresi öne geçecekse içerikler takas edilir (ana işareti hedefin satırında kalır).
                if (preferAddress)
                {
                    MergeSwap.Contents(currentAddress, address);
                }

                address.IsPrimary = false;
            }

            address.CompanyId = target.Id;
            address.Touch();
            targetAddresses.Add(address);
            MergeSupport.AddImpact(impacts, "Adres taşınır", 1);
        }

        var points = data.Set<ContactPoint>();
        var targetPoints = await points.Where(p => p.CompanyId == target.Id).ToListAsync(cancellationToken);
        foreach (var point in await points.Where(p => p.CompanyId == source.Id).ToListAsync(cancellationToken))
        {
            if (point.LocationId is null
                && targetPoints.Any(t => t.LocationId == null && t.Channel == point.Channel && t.NormalizedValue == point.NormalizedValue))
            {
                points.Remove(point);
                MergeSupport.AddImpact(impacts, "Aynı iletişim noktası tek kalır", 1);
                continue;
            }

            if (point.LocationId is null && point.IsActive && point.IsPrimary
                && targetPoints.FirstOrDefault(t => t.LocationId == null && t.IsActive && t.IsPrimary && t.Channel == point.Channel) is { } current)
            {
                if (choices.GetValueOrDefault(point.Channel == ContactNormalization.Phone ? "phone" : "email") == MergeRequest.FromSource)
                {
                    MergeSwap.Contents(current, point);
                }

                point.IsPrimary = false;
            }

            point.CompanyId = target.Id;
            point.Touch();
            targetPoints.Add(point);
            MergeSupport.AddImpact(impacts, "İletişim noktası taşınır", 1);
        }

        foreach (var channel in new[] { ContactNormalization.Phone, ContactNormalization.Email })
        {
            if (targetPoints.FirstOrDefault(t => t.LocationId == null && t.CompanyContactId == null && t.IsActive && t.IsPrimary && t.Channel == channel) is { } primary)
            {
                LegacyContactSync.MirrorPoint(primary, wasPrimary: true, target.Party, null);
            }
        }

        if (targetAddresses.FirstOrDefault(a => a.LocationId == null && a.IsActive && a.IsPrimary) is { } primaryAddress)
        {
            LegacyContactSync.MirrorAddress(primaryAddress, wasPrimary: true, target.Party);
        }
    }

    /// <summary>
    /// Firma–firma ilişkileri hedefe taşınır; kendine dönen (kaynak–hedef) ve yinelenen aktif ilişki kaldırılır. Sonuçta
    /// aktif ilişkilerde döngü oluşuyorsa birleştirme engellenir.
    /// </summary>
    private async Task MoveRelationsAsync(Company source, Company target, List<MergeImpact> impacts, List<string> blockers, CancellationToken cancellationToken)
    {
        var relations = Context.Data.Set<CompanyRelation>();
        var moved = await relations.Where(r => r.ParentCompanyId == source.Id || r.ChildCompanyId == source.Id).ToListAsync(cancellationToken);
        var kept = new List<CompanyRelation>();
        foreach (var relation in moved)
        {
            relation.ParentCompanyId = relation.ParentCompanyId == source.Id ? target.Id : relation.ParentCompanyId;
            relation.ChildCompanyId = relation.ChildCompanyId == source.Id ? target.Id : relation.ChildCompanyId;
            if (relation.ParentCompanyId == relation.ChildCompanyId)
            {
                relations.Remove(relation);
                MergeSupport.AddImpact(impacts, "Kendine dönen firma ilişkisi kaldırılır", 1);
                continue;
            }

            var duplicate = relation.IsActive && (kept.Any(k => k.IsActive && k.ParentCompanyId == relation.ParentCompanyId && k.ChildCompanyId == relation.ChildCompanyId)
                || await relations.AnyAsync(r => r.IsActive && r.ParentCompanyId == relation.ParentCompanyId && r.ChildCompanyId == relation.ChildCompanyId
                    && r.ParentCompanyId != source.Id && r.ChildCompanyId != source.Id, cancellationToken));
            if (duplicate)
            {
                relations.Remove(relation);
                MergeSupport.AddImpact(impacts, "Yinelenen firma ilişkisi kaldırılır", 1);
                continue;
            }

            relation.Touch();
            kept.Add(relation);
            MergeSupport.AddImpact(impacts, "Firma ilişkisi taşınır", 1);
        }

        if (kept.Count == 0)
        {
            return;
        }

        // Döngü: aktif ilişkilerin yönlü grafiğinde tüm düğümler sıralanabilmeli (Kahn).
        var removed = moved.Except(kept).ToHashSet();
        var active = (await relations.Where(r => r.IsActive).ToListAsync(cancellationToken)).Where(r => !removed.Contains(r)).ToList();
        var indegree = active.SelectMany(r => new[] { r.ParentCompanyId, r.ChildCompanyId }).Distinct().ToDictionary(id => id, _ => 0);
        foreach (var relation in active)
        {
            indegree[relation.ChildCompanyId]++;
        }

        var queue = new Queue<Guid>(indegree.Where(p => p.Value == 0).Select(p => p.Key));
        var sorted = 0;
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            sorted++;
            foreach (var relation in active.Where(r => r.ParentCompanyId == node))
            {
                if (--indegree[relation.ChildCompanyId] == 0)
                {
                    queue.Enqueue(relation.ChildCompanyId);
                }
            }
        }

        if (sorted < indegree.Count)
        {
            blockers.Add("Birleştirme firma ilişkilerinde döngü oluşturur (bir firma kendi ana firması olur).");
        }
    }
}
