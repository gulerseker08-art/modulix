using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Eski tekil alanlar ile yapılandırılmış iletişim/adres kayıtları arasındaki köprü (ADR-0012). Doğruluk kaynağı yeni
/// kayıtlardır; <c>Party.Phone/Email/City/Address</c> ve <c>Person.Phone/Email</c> ana (aktif) kaydın aynasıdır.
/// Eski API ile bu alanları yazmak ana kaydı günceller ya da açar; yeni uçlardan ana kaydı değiştirmek aynayı günceller.
/// Böylece iki ayrı gerçek kalmaz: ayna her zaman ana kayıttan türer.
/// </summary>
internal static class LegacyContactSync
{
    // ---- Eski alan → yapılandırılmış kayıt ----

    /// <summary>Firma yazılırken: Phone/Email/City/Address değiştiyse firma düzeyindeki ana noktaları/adresi eşitler.</summary>
    public static async Task FromCompanyAsync(IModuleData data, Company company, CancellationToken cancellationToken)
    {
        var stored = await data.Set<Party>().AsNoTracking().Where(p => p.Id == company.PartyId)
            .Select(p => new { p.Phone, p.Email, p.City, p.Address }).SingleOrDefaultAsync(cancellationToken);
        var party = company.Party;
        if (stored?.Phone != party.Phone)
        {
            await UpsertPointAsync(data, company.Id, null, ContactNormalization.Phone, party.Phone, shared: true, cancellationToken);
        }

        if (stored?.Email != party.Email)
        {
            await UpsertPointAsync(data, company.Id, null, ContactNormalization.Email, party.Email, shared: true, cancellationToken);
        }

        if (stored?.Address != party.Address || stored?.City != party.City)
        {
            await UpsertAddressAsync(data, company.Id, party.Address, party.City, cancellationToken);
        }
    }

    /// <summary>Kişi yazılırken: Phone/Email değiştiyse kişinin genel (ilişkiye özel olmayan) ana noktalarını eşitler.</summary>
    public static async Task FromContactAsync(IModuleData data, Contact contact, CancellationToken cancellationToken)
    {
        var stored = await data.Set<Person>().AsNoTracking().Where(p => p.Id == contact.PersonId)
            .Select(p => new { p.Phone, p.Email }).SingleOrDefaultAsync(cancellationToken);
        if (stored?.Phone != contact.Person.Phone)
        {
            await UpsertPointAsync(data, null, contact.Id, ContactNormalization.Phone, contact.Person.Phone, shared: false, cancellationToken);
        }

        if (stored?.Email != contact.Person.Email)
        {
            await UpsertPointAsync(data, null, contact.Id, ContactNormalization.Email, contact.Person.Email, shared: false, cancellationToken);
        }
    }

    private static async Task UpsertPointAsync(
        IModuleData data, Guid? companyId, Guid? contactId, string channel, string? value, bool shared, CancellationToken cancellationToken)
    {
        var points = data.Set<ContactPoint>();
        var query = points.Where(p => p.Channel == channel && p.IsPrimary && p.IsActive && p.LocationId == null && p.CompanyContactId == null);
        query = companyId is { } company ? query.Where(p => p.CompanyId == company) : query.Where(p => p.ContactId == contactId);
        var existing = await query.FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(value))
        {
            if (existing is not null)
            {
                existing.IsActive = false;
                existing.IsPrimary = false;
                existing.Touch();
            }

            return;
        }

        if (existing is not null)
        {
            existing.Value = value;
            existing.NormalizedValue = ContactNormalization.Lenient(channel, value);
            existing.Touch();
            return;
        }

        points.Add(new ContactPoint
        {
            CompanyId = companyId, ContactId = contactId, Channel = channel, Type = shared && channel == ContactNormalization.Phone ? "Switchboard" : "Work",
            Value = value, NormalizedValue = ContactNormalization.Lenient(channel, value), IsPrimary = true, IsShared = shared,
        });
    }

    private static async Task UpsertAddressAsync(IModuleData data, Guid companyId, string? line, string? city, CancellationToken cancellationToken)
    {
        var addresses = data.Set<Address>();
        var existing = await addresses.FirstOrDefaultAsync(
            a => a.CompanyId == companyId && a.LocationId == null && a.IsPrimary && a.IsActive, cancellationToken);
        if (string.IsNullOrWhiteSpace(line) && string.IsNullOrWhiteSpace(city))
        {
            if (existing is not null)
            {
                existing.IsActive = false;
                existing.IsPrimary = false;
                existing.Touch();
            }

            return;
        }

        if (existing is null)
        {
            existing = new Address { CompanyId = companyId, Type = "Office", IsPrimary = true };
            addresses.Add(existing);
        }
        else
        {
            existing.Touch();
        }

        existing.Line1 = line ?? string.Empty;
        existing.Line2 = null;
        existing.City = city;
        existing.NormalizedText = Normalized(existing);
    }

    public static string Normalized(Address a) =>
        ContactNormalization.FoldText(a.Line1, a.Line2, a.District, a.City, a.PostalCode, a.Country);

    // ---- Yapılandırılmış kayıt → eski alan (ayna) ----

    /// <summary>Nokta kaydedilirken aynayı günceller. <paramref name="wasPrimary"/>: kayıtlı halde aktif ana nokta mıydı?</summary>
    public static void MirrorPoint(ContactPoint point, bool wasPrimary, Party? party, Person? person)
    {
        if (point.LocationId is not null || point.CompanyContactId is not null)
        {
            return;
        }

        var primary = point.IsPrimary && point.IsActive;
        if (!primary && !wasPrimary)
        {
            return;
        }

        var value = primary ? point.Value : null;
        if (party is not null)
        {
            var current = point.Channel == ContactNormalization.Phone ? party.Phone : party.Email;
            if (current != value)
            {
                if (point.Channel == ContactNormalization.Phone) { party.Phone = value; } else { party.Email = value; }
                party.Touch();
            }
        }
        else if (person is not null)
        {
            var current = point.Channel == ContactNormalization.Phone ? person.Phone : person.Email;
            if (current != value)
            {
                if (point.Channel == ContactNormalization.Phone) { person.Phone = value; } else { person.Email = value; }
                person.Touch();
            }
        }
    }

    /// <summary>Firma düzeyindeki adres kaydedilirken/silinirken <c>Party.Address/City</c> aynasını günceller.</summary>
    public static void MirrorAddress(Address address, bool wasPrimary, Party party)
    {
        if (address.LocationId is not null)
        {
            return;
        }

        var primary = address.IsPrimary && address.IsActive;
        if (!primary && !wasPrimary)
        {
            return;
        }

        var text = primary ? string.Join(' ', new[] { address.Line1, address.Line2 }.Where(s => !string.IsNullOrWhiteSpace(s))) : null;
        text = string.IsNullOrEmpty(text) ? null : text[..Math.Min(text.Length, Party.AddressMaxLength)];
        var city = primary ? address.City : null;
        if (party.Address != text || party.City != city)
        {
            party.Address = text;
            party.City = city;
            party.Touch();
        }
    }
}
