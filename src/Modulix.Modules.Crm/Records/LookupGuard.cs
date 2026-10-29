using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Lookup alanlarının kayıt sırasındaki sunucu doğrulaması (ADR-0016). Arayüzün lookup'ı yalnızca kolaylıktır: gönderilen
/// kimliklerin görünürlüğü ve birbiriyle ilişkisi burada yeniden denetlenir. Değişmeyen değer (ekran kaydı yeniden
/// gönderirken) denetlenmez; böylece sonradan görünmez ya da ilişkisiz kalmış eski bağ, ilgisiz bir alan düzenlenirken engel olmaz.
/// </summary>
internal static class LookupGuard
{
    /// <summary>Firma bağı değiştiyse çağıranın görebildiği bir firma olmalıdır; aksi halde "bulunamadı" (varlığı belli edilmez).</summary>
    public static async Task AddCompanyErrorAsync(
        List<RecordFieldError> errors, CrmStoreContext context, string field, Guid? value, Guid? stored, CancellationToken cancellationToken)
    {
        if (value is { } id && id != stored && !await CompanyLinks.CanSeeCompanyAsync(context, id, cancellationToken))
        {
            errors.Add(new RecordFieldError(field, "Firma bulunamadı."));
        }
    }

    public static async Task AddContactErrorAsync(
        List<RecordFieldError> errors, CrmStoreContext context, string field, Guid? value, Guid? stored, CancellationToken cancellationToken)
    {
        if (value is { } id && id != stored && !await CompanyLinks.CanSeeContactAsync(context, id, cancellationToken))
        {
            errors.Add(new RecordFieldError(field, "Kişi bulunamadı."));
        }
    }

    /// <summary>
    /// Bağımlı lookup: kişi, seçili firmayla ilişkili olmalıdır (birincil firma ya da aktif kişi-firma ilişkisi). Kişi ya da firma
    /// değiştiyse denetlenir; ikisinden biri boşsa kural yoktur. Başka bir hata varsa tekrar edilmez.
    /// </summary>
    public static async Task AddContactCompanyErrorAsync(
        List<RecordFieldError> errors, CrmStoreContext context, string contactField, Guid? contactId, Guid? companyId,
        Guid? storedContactId, Guid? storedCompanyId, CancellationToken cancellationToken)
    {
        if (contactId is not { } contact || companyId is not { } company
            || (contact == storedContactId && company == storedCompanyId)
            || errors.Any(e => e.Field == contactField))
        {
            return;
        }

        if (!await ContactBelongsToCompanyAsync(context, contact, company, cancellationToken))
        {
            errors.Add(new RecordFieldError(contactField, "Kişi seçilen firmayla ilişkili değil."));
        }
    }

    /// <summary>Bağımlı lookup: fırsat, seçili firmanın fırsatı olmalıdır.</summary>
    public static async Task AddOpportunityCompanyErrorAsync(
        List<RecordFieldError> errors, CrmStoreContext context, Guid? opportunityId, Guid? companyId,
        Guid? storedOpportunityId, Guid? storedCompanyId, CancellationToken cancellationToken)
    {
        if (opportunityId is not { } opportunity || companyId is not { } company
            || (opportunity == storedOpportunityId && company == storedCompanyId)
            || errors.Any(e => e.Field == "opportunityId"))
        {
            return;
        }

        if (!await context.Data.Set<Opportunity>().AsNoTracking().AnyAsync(o => o.Id == opportunity && o.CompanyId == company, cancellationToken))
        {
            errors.Add(new RecordFieldError("opportunityId", "Fırsat seçilen firmaya ait değil."));
        }
    }

    public static Task<bool> ContactBelongsToCompanyAsync(CrmStoreContext context, Guid contactId, Guid companyId, CancellationToken cancellationToken) =>
        context.Data.Set<Contact>().AsNoTracking().AnyAsync(
            c => c.Id == contactId
                && (c.CompanyId == companyId
                    || context.Data.Set<CompanyContact>().Any(l => l.ContactId == c.Id && l.CompanyId == companyId && l.IsActive)),
            cancellationToken);
}
