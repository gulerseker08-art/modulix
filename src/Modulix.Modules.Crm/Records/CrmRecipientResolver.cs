using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Marketing;
using Modulix.Core.Contracts.Persistence;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// CRM'in alıcı çözümleyicisi (ADR-0025, dilim 4): firma hedefinde firma–kişi ilişkisinden (birincil, karar verici, rol, hepsi) kişileri ve
/// onların kanaldaki aktif iletişim noktalarını, kişi hedefinde kişinin noktalarını, lead hedefinde lead'in kendi e-posta/telefonunu verir.
/// Kural eşleşmezse rastgele kişiye gidilmez; nedeni (<see cref="RecipientCodes"/>) hedefle birlikte döner. İletişim noktası pazarlama izni değildir:
/// izin ve engel kararı çekirdekte, pazarlama modülündedir. SMS ve WhatsApp telefon noktalarını kullanır.
/// </summary>
internal sealed class CrmRecipientResolver(IModuleData data, TimeProvider time) : IRecipientResolver, IAddressLookup
{
    private const int ChunkSize = 1000;

    public string ReadEntityKey => CrmModule.Entities.Contact;

    public bool Supports(SegmentTarget target) => true;

    public async Task<RecipientResolution> ResolveAsync(RecipientRequest request, CancellationToken cancellationToken)
    {
        var contacts = new List<ResolvedContact>();
        var unresolved = new List<UnresolvedTarget>();
        foreach (var chunk in request.TargetIds.Distinct().Chunk(ChunkSize))
        {
            var part = request.Target switch
            {
                SegmentTarget.Company => await ResolveCompaniesAsync(chunk, request, cancellationToken),
                SegmentTarget.Person => await ResolvePeopleAsync(chunk, request, cancellationToken),
                _ => await ResolveLeadsAsync(chunk, request, cancellationToken),
            };
            contacts.AddRange(part.Contacts);
            unresolved.AddRange(part.Unresolved);
        }

        return new RecipientResolution(contacts, unresolved);
    }


    /// <summary>
    /// Gönderimden hemen önce adresi yeniden çözer (ADR-0025, dilim 5): iletişim noktası pasifleşmiş/silinmiş, kişi firmadan ayrılmış (ilişki bitmiş) ya da lead'in adresi
    /// boşalmışsa sonuçta yer almaz; çağıran o alıcıyı atlar. Anlık görüntüdeki eski kimlikle yeni bir adrese gönderilmez.
    /// </summary>
    public async Task<IReadOnlyList<ResolvedAddress>> LookupAsync(
        SegmentTarget target, CommunicationChannel channel, IReadOnlyCollection<AddressLookup> items, CancellationToken cancellationToken)
    {
        var results = new List<ResolvedAddress>();
        foreach (var chunk in items.Chunk(ChunkSize))
        {
            results.AddRange(target switch
            {
                SegmentTarget.Lead => await LookupLeadsAsync(chunk, channel, cancellationToken),
                SegmentTarget.Person => await LookupPeopleAsync(chunk, channel, cancellationToken),
                _ => await LookupCompaniesAsync(chunk, channel, cancellationToken),
            });
        }

        return results;
    }

    private async Task<List<ResolvedAddress>> LookupLeadsAsync(AddressLookup[] items, CommunicationChannel channel, CancellationToken cancellationToken)
    {
        var ids = items.Select(i => i.TargetId).ToList();
        var leads = await data.Set<Lead>().AsNoTracking().Where(l => ids.Contains(l.Id)).Select(l => new { l.Id, l.Email, l.Phone }).ToListAsync(cancellationToken);
        return [.. leads
            .Select(l => new ResolvedAddress(l.Id, null, channel == CommunicationChannel.Email ? l.Email ?? string.Empty : l.Phone ?? string.Empty))
            .Where(a => a.Address.Length > 0)];
    }

    private async Task<List<ResolvedAddress>> LookupPeopleAsync(AddressLookup[] items, CommunicationChannel channel, CancellationToken cancellationToken)
    {
        var pointIds = items.Where(i => i.ContactPointId is not null).Select(i => i.ContactPointId).ToList();
        var personIds = items.Select(i => i.TargetId).ToList();
        var chName = PointChannel(channel);
        var rows = await (
            from point in data.Set<ContactPoint>().AsNoTracking()
            join contact in data.Set<Contact>().AsNoTracking() on point.ContactId equals contact.Id
            where pointIds.Contains(point.Id) && point.IsActive && point.Channel == chName && personIds.Contains(contact.PersonId)
            select new { PointId = point.Id, contact.PersonId, point.Value }).ToListAsync(cancellationToken);
        return [.. rows.Select(r => new ResolvedAddress(r.PersonId, r.PointId, r.Value))];
    }

    private async Task<List<ResolvedAddress>> LookupCompaniesAsync(AddressLookup[] items, CommunicationChannel channel, CancellationToken cancellationToken)
    {
        var chName = PointChannel(channel);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var pointIds = items.Where(i => i.ContactPointId is not null).Select(i => i.ContactPointId).ToList();
        var partyIds = items.Select(i => i.TargetId).ToList();
        var companies = await data.Set<Company>().AsNoTracking().Where(c => partyIds.Contains(c.PartyId)).Select(c => new { c.Id, c.PartyId }).ToListAsync(cancellationToken);
        var companyIds = companies.Select(c => c.Id).ToList();
        var points = await data.Set<ContactPoint>().AsNoTracking()
            .Where(p => pointIds.Contains(p.Id) && p.IsActive && p.Channel == chName)
            .Select(p => new { p.Id, p.CompanyId, p.ContactId, p.Value })
            .ToListAsync(cancellationToken);
        var relations = await data.Set<CompanyContact>().AsNoTracking()
            .Where(r => companyIds.Contains(r.CompanyId) && r.IsActive && (r.EndDate == null || r.EndDate >= today))
            .Select(r => new { r.CompanyId, r.ContactId })
            .ToListAsync(cancellationToken);

        var results = new List<ResolvedAddress>();
        foreach (var item in items)
        {
            var company = companies.FirstOrDefault(c => c.PartyId == item.TargetId);
            var point = points.FirstOrDefault(p => p.Id == item.ContactPointId);
            if (company is null || point is null)
            {
                continue;
            }

            // Firma noktası firmaya ait olmalı; kişi noktasının kişisi bu firmada hâlâ aktif ilişkide olmalı.
            var valid = point.ContactId is null
                ? point.CompanyId == company.Id
                : relations.Any(r => r.CompanyId == company.Id && r.ContactId == point.ContactId);
            if (valid)
            {
                results.Add(new ResolvedAddress(item.TargetId, point.Id, point.Value));
            }
        }

        return results;
    }

    /// <summary>CRM iletişim noktası kanalı: e-posta "Email", diğer kanallar telefon numarası kullanır.</summary>
    private static string PointChannel(CommunicationChannel channel) => channel == CommunicationChannel.Email ? "Email" : "Phone";

    private async Task<RecipientResolution> ResolveCompaniesAsync(Guid[] partyIds, RecipientRequest request, CancellationToken cancellationToken)
    {
        var channel = PointChannel(request.Channel);
        var contacts = new List<ResolvedContact>();
        var unresolved = new List<UnresolvedTarget>();

        var companies = await data.Set<Company>().AsNoTracking().Where(c => partyIds.Contains(c.PartyId)).Select(c => new { c.Id, c.PartyId }).ToListAsync(cancellationToken);
        unresolved.AddRange(partyIds.Except(companies.Select(c => c.PartyId)).Select(id => new UnresolvedTarget(id, RecipientCodes.TargetNotFound)));
        var companyIds = companies.Select(c => c.Id).ToList();

        if (request.Rule == RecipientRule.CompanyPoints)
        {
            var points = await data.Set<ContactPoint>().AsNoTracking()
                .Where(p => p.CompanyId != null && companyIds.Contains(p.CompanyId.Value) && p.ContactId == null && p.Channel == channel && p.IsActive)
                .Select(p => new { p.Id, p.CompanyId, p.Value, p.IsShared, p.IsPrimary })
                .ToListAsync(cancellationToken);
            foreach (var company in companies)
            {
                var own = points.Where(p => p.CompanyId == company.Id).ToList();
                if (own.Count == 0)
                {
                    unresolved.Add(new UnresolvedTarget(company.PartyId, RecipientCodes.NoContactPoint));
                }

                contacts.AddRange(own.Select(p => new ResolvedContact(company.PartyId, null, p.Id, p.Value, p.IsShared, p.IsPrimary)));
            }

            return new RecipientResolution(contacts, unresolved);
        }

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var relations = data.Set<CompanyContact>().AsNoTracking()
            .Where(r => companyIds.Contains(r.CompanyId) && r.IsActive && (r.EndDate == null || r.EndDate >= today));
        relations = request.Rule switch
        {
            RecipientRule.PrimaryContact => relations.Where(r => r.IsPrimary),
            RecipientRule.DecisionMaker => relations.Where(r => r.IsDecisionMaker),
            RecipientRule.Role => RoleFilter(relations, request.Role),
            _ => relations,
        };

        var matched = await relations.Select(r => new { r.Id, r.CompanyId, r.ContactId, r.Contact.PersonId, r.IsPrimary }).ToListAsync(cancellationToken);
        var contactIds = matched.Select(r => (Guid?)r.ContactId).Distinct().ToList();
        var contactPoints = await data.Set<ContactPoint>().AsNoTracking()
            .Where(p => contactIds.Contains(p.ContactId) && p.Channel == channel && p.IsActive)
            .Select(p => new { p.Id, p.ContactId, p.CompanyContactId, p.Value, p.IsShared, p.IsPrimary })
            .ToListAsync(cancellationToken);

        foreach (var company in companies)
        {
            var relationsOfCompany = matched.Where(r => r.CompanyId == company.Id).ToList();
            if (relationsOfCompany.Count == 0)
            {
                unresolved.Add(new UnresolvedTarget(company.PartyId, RecipientCodes.NoMatchingContact));
                continue;
            }

            var found = new List<ResolvedContact>();
            foreach (var relation in relationsOfCompany)
            {
                // Noktanın kendisi bir firma ilişkisine özelse yalnızca o ilişkide geçerlidir (ör. o firmadaki iş e-postası).
                found.AddRange(contactPoints
                    .Where(p => p.ContactId == relation.ContactId && (p.CompanyContactId is null || p.CompanyContactId == relation.Id))
                    .Select(p => new ResolvedContact(company.PartyId, relation.PersonId, p.Id, p.Value, p.IsShared, p.IsPrimary)));
            }

            if (found.Count == 0)
            {
                unresolved.Add(new UnresolvedTarget(company.PartyId, RecipientCodes.NoContactPoint));
            }

            contacts.AddRange(found);
        }

        return new RecipientResolution(contacts, unresolved);
    }

    private static IQueryable<CompanyContact> RoleFilter(IQueryable<CompanyContact> relations, string? role)
    {
        var token = "," + (role ?? string.Empty).Trim() + ",";
        return relations.Where(r => r.Roles != null && ("," + r.Roles + ",").Contains(token));
    }

    private async Task<RecipientResolution> ResolvePeopleAsync(Guid[] personIds, RecipientRequest request, CancellationToken cancellationToken)
    {
        var channel = PointChannel(request.Channel);
        var people = await data.Set<Contact>().AsNoTracking().Where(c => personIds.Contains(c.PersonId)).Select(c => new { c.Id, c.PersonId }).ToListAsync(cancellationToken);
        var unresolved = personIds.Except(people.Select(p => p.PersonId)).Select(id => new UnresolvedTarget(id, RecipientCodes.TargetNotFound)).ToList();
        var contactIds = people.Select(p => (Guid?)p.Id).ToList();
        var points = await data.Set<ContactPoint>().AsNoTracking()
            .Where(p => contactIds.Contains(p.ContactId) && p.Channel == channel && p.IsActive)
            .Select(p => new { p.Id, p.ContactId, p.Value, p.IsShared, p.IsPrimary })
            .ToListAsync(cancellationToken);

        var contacts = new List<ResolvedContact>();
        foreach (var person in people)
        {
            var own = points.Where(p => p.ContactId == person.Id).ToList();
            if (own.Count == 0)
            {
                unresolved.Add(new UnresolvedTarget(person.PersonId, RecipientCodes.NoContactPoint));
            }

            contacts.AddRange(own.Select(p => new ResolvedContact(person.PersonId, person.PersonId, p.Id, p.Value, p.IsShared, p.IsPrimary)));
        }

        return new RecipientResolution(contacts, unresolved);
    }

    private async Task<RecipientResolution> ResolveLeadsAsync(Guid[] leadIds, RecipientRequest request, CancellationToken cancellationToken)
    {
        var leads = await data.Set<Lead>().AsNoTracking().Where(l => leadIds.Contains(l.Id)).Select(l => new { l.Id, l.Email, l.Phone }).ToListAsync(cancellationToken);
        var unresolved = leadIds.Except(leads.Select(l => l.Id)).Select(id => new UnresolvedTarget(id, RecipientCodes.TargetNotFound)).ToList();
        var contacts = new List<ResolvedContact>();
        foreach (var lead in leads)
        {
            var address = request.Channel == CommunicationChannel.Email ? lead.Email : lead.Phone;
            if (string.IsNullOrWhiteSpace(address))
            {
                unresolved.Add(new UnresolvedTarget(lead.Id, RecipientCodes.NoContactPoint));
            }
            else
            {
                contacts.Add(new ResolvedContact(lead.Id, null, null, address, false, true));
            }
        }

        return new RecipientResolution(contacts, unresolved);
    }
}
