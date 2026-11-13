using System.Globalization;
using System.Linq.Expressions;
using Modulix.Core.Contracts.Marketing;
using Modulix.Core.Contracts.Persistence;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// CRM'in pazarlama segmenti alan kaynağı (ADR-0025 K3): firma, kişi ve lead alanlarını "koşulu sağlayan hedef kimlikleri" sorgusu olarak sunar.
/// Yalnızca CRM'de gerçekten bulunan alanlar listelenir (firma büyüklüğü, bölge, ürün sahipliği CRM'de yok, bu yüzden yoktur). Çağıranın veri
/// kapsamı (kendi/ekip/hepsi) sorguya uygulanır; kayıt görme yetkisi motorun alan kataloğunda (<see cref="SegmentFieldDefinition.ReadEntityKey"/>) denetlenir.
/// Hedef kimlikleri: firma → ortak taraf kimliği, kişi → ortak kişi kimliği, lead → lead kimliği.
/// </summary>
internal sealed class CrmSegmentFieldSource(IModuleData data) : ISegmentFieldSource
{
    private const string Prefix = "crm.";

    private static readonly IReadOnlyDictionary<string, string> LeadStatuses = new Dictionary<string, string>
    {
        [LeadStatus.New] = "Yeni", [LeadStatus.Contacting] = "İletişimde", [LeadStatus.Qualified] = "Nitelikli",
        [LeadStatus.Nurturing] = "Beslemede", [LeadStatus.Disqualified] = "Elendi", [LeadStatus.Converted] = "Dönüştürüldü",
    };

    public string ModuleKey => CrmModule.ModuleKey;

    // Koşullar bu türlerin kapsamlarıyla ayrı ayrı değerlendirilir (ADR-0030): hedef firma, kişi, lead; "açık/kazanılan fırsat" koşulu fırsat.
    public IReadOnlyList<string> ScopeEntityKeys { get; } =
        [CrmModule.Entities.Company, CrmModule.Entities.Contact, CrmModule.Entities.Lead, CrmModule.Entities.Opportunity];

    public IReadOnlyList<SegmentFieldDefinition> Fields { get; } =
    [
        new("crm.company.name", "Firma adı", "Firma", SegmentTarget.Company, SegmentFieldType.Text, CrmModule.Entities.Company),
        new("crm.company.city", "Şehir", "Firma", SegmentTarget.Company, SegmentFieldType.Text, CrmModule.Entities.Company),
        new("crm.company.industry", "Sektör", "Firma", SegmentTarget.Company, SegmentFieldType.Text, CrmModule.Entities.Company)
        {
            Hint = "Sektör kataloğundaki ad (ör. Gıda ve içecek); katalog öncesi girilmiş serbest metinler de adıyla eşleşir. Büyük/küçük harf duyarsızdır.",
        },
        new("crm.company.status", "Müşteri durumu", "Firma", SegmentTarget.Company, SegmentFieldType.Choice, CrmModule.Entities.Company)
        {
            Hint = "Firmanın durum listesindeki anahtar (ör. Customer).",
        },
        new("crm.company.source", "Kaynak", "Firma", SegmentTarget.Company, SegmentFieldType.Text, CrmModule.Entities.Company),
        new("crm.company.tags", "Etiket", "Firma", SegmentTarget.Company, SegmentFieldType.Text, CrmModule.Entities.Company)
        {
            Hint = "Etiketler virgülle ayrılmış tek metindir; 'içerir' operatörünü kullanın.",
        },
        new("crm.company.owner", "Sorumlu", "Firma", SegmentTarget.Company, SegmentFieldType.Id, CrmModule.Entities.Company),
        new("crm.company.createdAt", "Oluşturma tarihi", "Firma", SegmentTarget.Company, SegmentFieldType.Date, CrmModule.Entities.Company)
        {
            Hint = "Gün sınırları firmanın saat dilimine göredir.",
        },
        new("crm.company.contacts", "Kişisi var", "İlişkiler", SegmentTarget.Company, SegmentFieldType.Relation, CrmModule.Entities.Contact),
        new("crm.company.openOpportunity", "Açık fırsatı var", "İlişkiler", SegmentTarget.Company, SegmentFieldType.Relation, CrmModule.Entities.Opportunity)
        {
            Hint = "Açık ya da beklemedeki fırsat; yalnızca erişiminizdeki fırsatlar sayılır.",
        },
        new("crm.company.wonOpportunity", "Kazanılmış fırsatı var", "İlişkiler", SegmentTarget.Company, SegmentFieldType.Relation, CrmModule.Entities.Opportunity)
        {
            Hint = "İsteğe bağlı değer: son N gün içinde kapanmış (kapanış tarihine göre); yalnızca erişiminizdeki fırsatlar sayılır.",
        },

        new("crm.contact.title", "Unvan", "Kişi", SegmentTarget.Person, SegmentFieldType.Text, CrmModule.Entities.Contact),
        new("crm.contact.companyIndustry", "Firmasının sektörü", "Kişi", SegmentTarget.Person, SegmentFieldType.Text, CrmModule.Entities.Contact),
        new("crm.contact.createdAt", "Oluşturma tarihi", "Kişi", SegmentTarget.Person, SegmentFieldType.Date, CrmModule.Entities.Contact),

        new("crm.lead.status", "Durum", "Lead", SegmentTarget.Lead, SegmentFieldType.Choice, CrmModule.Entities.Lead) { Options = LeadStatuses },
        new("crm.lead.source", "Kaynak", "Lead", SegmentTarget.Lead, SegmentFieldType.Text, CrmModule.Entities.Lead),
        new("crm.lead.country", "Ülke", "Lead", SegmentTarget.Lead, SegmentFieldType.Text, CrmModule.Entities.Lead),
        new("crm.lead.city", "Şehir", "Lead", SegmentTarget.Lead, SegmentFieldType.Text, CrmModule.Entities.Lead),
        new("crm.lead.industry", "Sektör", "Lead", SegmentTarget.Lead, SegmentFieldType.Text, CrmModule.Entities.Lead),
        new("crm.lead.utmCampaign", "UTM kampanyası", "Lead", SegmentTarget.Lead, SegmentFieldType.Text, CrmModule.Entities.Lead),
        new("crm.lead.createdAt", "Oluşturma tarihi", "Lead", SegmentTarget.Lead, SegmentFieldType.Date, CrmModule.Entities.Lead)
        {
            Hint = "Gün sınırları firmanın saat dilimine göredir.",
        },
    ];

    public IQueryable<Guid> Match(SegmentFieldDefinition field, SegmentLeaf leaf, SegmentEvaluationContext context)
    {
        if (!field.Key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new FormatException($"CRM kaynağı '{field.Key}' alanını tanımaz.");
        }

        List<Guid?>? OwnersOf(string entityKey) => context.OwnersFor(entityKey)?.Select(o => (Guid?)o).ToList();

        return field.Target switch
        {
            SegmentTarget.Company => MatchCompany(field.Key, leaf, context, OwnersOf(CrmModule.Entities.Company), OwnersOf(CrmModule.Entities.Opportunity)),
            SegmentTarget.Person => MatchPerson(field.Key, leaf, context, OwnersOf(CrmModule.Entities.Contact)),
            _ => MatchLead(field.Key, leaf, context, OwnersOf(CrmModule.Entities.Lead)),
        };
    }

    private IQueryable<Guid> MatchCompany(
        string key, SegmentLeaf leaf, SegmentEvaluationContext context, List<Guid?>? owners, List<Guid?>? opportunityOwners)
    {
        var companies = data.Set<Company>().AsNoTracking2();
        if (owners is not null)
        {
            companies = companies.Where(c => owners.Contains(c.OwnerId));
        }

        IQueryable<Company> filtered = key switch
        {
            "crm.company.name" => Predicates.Text(companies, c => c.Party.Name, leaf),
            "crm.company.city" => Predicates.Text(companies, c => c.Party.City, leaf),
            "crm.company.industry" => Predicates.Text(companies, c => c.Party.Industry!.Name ?? c.Industry, leaf),
            "crm.company.status" => Predicates.Text(companies, c => c.Status, leaf),
            "crm.company.source" => Predicates.Text(companies, c => c.Source, leaf),
            "crm.company.tags" => Predicates.Text(companies, c => c.Tags, leaf),
            "crm.company.owner" => Predicates.Id(companies, c => c.OwnerId, leaf),
            "crm.company.createdAt" => Predicates.Date(companies, c => (DateTimeOffset?)c.CreatedAt, leaf, context),
            "crm.company.contacts" => Exists(companies, leaf, c => data.Set<Contact>().Any(k => k.CompanyId == c.Id)),
            "crm.company.openOpportunity" => Exists(companies, leaf, c => Opportunities(opportunityOwners).Any(o =>
                o.CompanyId == c.Id && (o.Status == OpportunityStatus.Open || o.Status == OpportunityStatus.OnHold))),
            "crm.company.wonOpportunity" => WonOpportunity(companies, leaf, context, opportunityOwners),
            _ => throw new FormatException($"CRM firma alanı tanınmıyor: {key}."),
        };
        return filtered.Select(c => c.PartyId);
    }

    private IQueryable<Guid> MatchPerson(string key, SegmentLeaf leaf, SegmentEvaluationContext context, List<Guid?>? owners)
    {
        var contacts = data.Set<Contact>().AsNoTracking2();
        if (owners is not null)
        {
            // Firmaya bağlı kişi firmanın kapsamıyla, firmasız kişi kendi sorumlusuyla görünür (ADR-0010).
            var companies = data.Set<Company>().AsNoTracking2();
            contacts = contacts.Where(k => k.CompanyId != null
                ? companies.Any(c => c.Id == k.CompanyId && owners.Contains(c.OwnerId))
                : owners.Contains(k.OwnerId));
        }

        IQueryable<Contact> filtered = key switch
        {
            "crm.contact.title" => Predicates.Text(contacts, k => k.Person.Title, leaf),
            "crm.contact.companyIndustry" => Predicates.Text(contacts, k => data.Set<Company>().Where(c => c.Id == k.CompanyId).Select(c => c.Party.Industry!.Name ?? c.Industry).FirstOrDefault(), leaf),
            "crm.contact.createdAt" => Predicates.Date(contacts, k => (DateTimeOffset?)k.CreatedAt, leaf, context),
            _ => throw new FormatException($"CRM kişi alanı tanınmıyor: {key}."),
        };
        return filtered.Select(k => k.PersonId);
    }

    private IQueryable<Guid> MatchLead(string key, SegmentLeaf leaf, SegmentEvaluationContext context, List<Guid?>? owners)
    {
        var leads = data.Set<Lead>().AsNoTracking2();
        if (owners is not null)
        {
            leads = leads.Where(l => owners.Contains(l.OwnerId));
        }

        IQueryable<Lead> filtered = key switch
        {
            "crm.lead.status" => Predicates.Text(leads, l => l.Status, leaf),
            "crm.lead.source" => Predicates.Text(leads, l => l.Source, leaf),
            "crm.lead.country" => Predicates.Text(leads, l => l.Country, leaf),
            "crm.lead.city" => Predicates.Text(leads, l => l.City, leaf),
            "crm.lead.industry" => Predicates.Text(leads, l => l.IndustryEntry!.Name ?? l.Industry, leaf),
            "crm.lead.utmCampaign" => Predicates.Text(leads, l => l.UtmCampaign, leaf),
            "crm.lead.createdAt" => Predicates.Date(leads, l => (DateTimeOffset?)l.CreatedAt, leaf, context),
            _ => throw new FormatException($"CRM müşteri adayı alanı tanınmıyor: {key}."),
        };
        return filtered.Select(l => l.Id);
    }

    private IQueryable<Opportunity> Opportunities(List<Guid?>? owners)
    {
        var all = data.Set<Opportunity>().AsNoTracking2();
        return owners is null ? all : all.Where(o => owners.Contains(o.OwnerId));
    }

    private static IQueryable<Company> Exists(IQueryable<Company> source, SegmentLeaf leaf, Expression<Func<Company, bool>> exists) =>
        leaf.Operator == SegmentOperator.Exists ? source.Where(exists) : source.Where(Not(exists));

    private IQueryable<Company> WonOpportunity(IQueryable<Company> source, SegmentLeaf leaf, SegmentEvaluationContext context, List<Guid?>? owners)
    {
        var days = leaf.Values.Count > 0 ? int.Parse(leaf.Values[0], CultureInfo.InvariantCulture) : (int?)null;
        var from = days is { } n ? context.Today.AddDays(-(n - 1)) : DateOnly.MinValue;
        var today = context.Today;
        var all = Opportunities(owners);
        Expression<Func<Company, bool>> exists = c => all.Any(o =>
            o.CompanyId == c.Id && o.Status == OpportunityStatus.Won && o.ActualCloseDate >= from && o.ActualCloseDate <= today);
        return Exists(source, leaf, exists);
    }

    private static Expression<Func<Company, bool>> Not(Expression<Func<Company, bool>> expression) =>
        Expression.Lambda<Func<Company, bool>>(Expression.Not(expression.Body), expression.Parameters);
}

internal static class QueryableExtensions
{
    /// <summary>İzleme kapalı salt okunur sorgu (kaynaklar hiçbir şeyi değiştirmez).</summary>
    public static IQueryable<T> AsNoTracking2<T>(this IQueryable<T> source)
        where T : class =>
        Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(source);
}

/// <summary>
/// Koşulları ifade ağacı olarak kurar (EF SQL'e çevirir). NULL: eşitlik, içerir, aralık ve tarih operatörleri boş değerle eşleşmez;
/// <c>Ne</c> boş değeri de kapsar; <c>Empty</c> boş ya da boş metindir. Tarih gün sınırları tenant saat dilimindendir (alt sınır dahil, üst sınır hariç).
/// </summary>
internal static class Predicates
{
    public static IQueryable<T> Text<T>(IQueryable<T> source, Expression<Func<T, string?>> selector, SegmentLeaf leaf)
    {
        var x = selector.Body;
        var values = leaf.Values;
        Expression body = leaf.Operator switch
        {
            SegmentOperator.Eq => Expression.Equal(x, Const(values[0])),
            SegmentOperator.Ne => Expression.OrElse(Expression.Equal(x, Expression.Constant(null, typeof(string))), Expression.NotEqual(x, Const(values[0]))),
            SegmentOperator.Contains => Expression.AndAlso(
                Expression.NotEqual(x, Expression.Constant(null, typeof(string))),
                Expression.Call(x, typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!, Const(values[0]))),
            SegmentOperator.In => Expression.Call(
                typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)], Expression.Constant(values.ToList()), x),
            SegmentOperator.Empty => Expression.OrElse(Expression.Equal(x, Expression.Constant(null, typeof(string))), Expression.Equal(x, Const(string.Empty))),
            SegmentOperator.NotEmpty => Expression.AndAlso(Expression.NotEqual(x, Expression.Constant(null, typeof(string))), Expression.NotEqual(x, Const(string.Empty))),
            _ => throw new FormatException($"Metin alanında '{leaf.Operator}' kullanılamaz."),
        };
        return source.Where(Expression.Lambda<Func<T, bool>>(body, selector.Parameters));
    }

    public static IQueryable<T> Id<T>(IQueryable<T> source, Expression<Func<T, Guid?>> selector, SegmentLeaf leaf)
    {
        var x = selector.Body;
        var null_ = Expression.Constant(null, typeof(Guid?));
        Expression body = leaf.Operator switch
        {
            SegmentOperator.Eq => Expression.Equal(x, GuidConst(leaf.Values[0])),
            SegmentOperator.In => Expression.Call(
                typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Guid?)], Expression.Constant(leaf.Values.Select(v => (Guid?)Guid.Parse(v)).ToList()), x),
            SegmentOperator.Empty => Expression.Equal(x, null_),
            SegmentOperator.NotEmpty => Expression.NotEqual(x, null_),
            _ => throw new FormatException($"Kimlik alanında '{leaf.Operator}' kullanılamaz."),
        };
        return source.Where(Expression.Lambda<Func<T, bool>>(body, selector.Parameters));
    }

    public static IQueryable<T> Date<T>(IQueryable<T> source, Expression<Func<T, DateTimeOffset?>> selector, SegmentLeaf leaf, SegmentEvaluationContext context)
    {
        var x = selector.Body;
        Expression body = leaf.Operator switch
        {
            SegmentOperator.Empty => Expression.Equal(x, Expression.Constant(null, typeof(DateTimeOffset?))),
            SegmentOperator.NotEmpty => Expression.NotEqual(x, Expression.Constant(null, typeof(DateTimeOffset?))),
            SegmentOperator.Between => Range(x, context.StartOfDay(ParseDay(leaf.Values[0])), context.StartOfDay(ParseDay(leaf.Values[1]).AddDays(1))),
            SegmentOperator.Before => Range(x, null, context.StartOfDay(ParseDay(leaf.Values[0]))),
            SegmentOperator.After => Range(x, context.StartOfDay(ParseDay(leaf.Values[0]).AddDays(1)), null),
            SegmentOperator.LastDays => Range(
                x, context.StartOfDay(context.Today.AddDays(-(Days(leaf) - 1))), context.StartOfDay(context.Today.AddDays(1))),
            SegmentOperator.NextDays => Range(x, context.StartOfDay(context.Today), context.StartOfDay(context.Today.AddDays(Days(leaf) + 1))),
            _ => throw new FormatException($"Tarih alanında '{leaf.Operator}' kullanılamaz."),
        };
        return source.Where(Expression.Lambda<Func<T, bool>>(body, selector.Parameters));
    }

    /// <summary>from ≤ x &lt; to (sınırlardan biri boş olabilir). Boş değer hiçbir aralıkla eşleşmez.</summary>
    private static Expression Range(Expression x, DateTimeOffset? from, DateTimeOffset? to)
    {
        Expression? body = null;
        if (from is { } start)
        {
            body = Expression.GreaterThanOrEqual(x, Expression.Constant(start, typeof(DateTimeOffset?)));
        }

        if (to is { } end)
        {
            var upper = Expression.LessThan(x, Expression.Constant(end, typeof(DateTimeOffset?)));
            body = body is null ? upper : Expression.AndAlso(body, upper);
        }

        return body!;
    }

    private static Expression Const(string value) => Expression.Constant(value, typeof(string));

    private static Expression GuidConst(string value) => Expression.Constant(Guid.Parse(value), typeof(Guid?));

    private static DateOnly ParseDay(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static int Days(SegmentLeaf leaf) => int.Parse(leaf.Values[0], CultureInfo.InvariantCulture);
}
