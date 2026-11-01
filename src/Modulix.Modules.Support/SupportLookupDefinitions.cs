using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;

namespace Modulix.Modules.Support;

/// <summary>
/// Destek modülünün lookup alanları (ADR-0016). Müşteri ve kişi ortak kayıttır (<c>core.party</c>, <c>core.person</c>); erişim ortak kaydı
/// kullanan modüllerin bildirdiği izinlerle, CRM kapsam kısıtı varsa onunla belirlenir. Kişi lookup'ı müşteri tarafına bağımlıdır.
/// </summary>
public static class SupportLookupDefinitions
{
    public const string TicketParty = "support.ticket.party";
    public const string TicketPerson = "support.ticket.person";
    public const string TicketCategory = "support.ticket.category";
    public const string TicketQueue = "support.ticket.queue";
    public const string TicketOwner = "support.ticket.owner";
    public const string TicketParent = "support.ticket.parent";
    public const string CategoryParent = "support.category.parent";
    public const string CategoryQueue = "support.category.queue";
    public const string PolicyCalendar = "support.sla-policy.calendar";
    public const string PolicyCategory = "support.sla-policy.category";
    public const string PolicyParty = "support.sla-policy.party";
    public const string RuleCategory = "support.routing-rule.category";
    public const string RuleQueue = "support.routing-rule.queue";
    public const string RuleParty = "support.routing-rule.party";
    public const string RuleOwner = "support.routing-rule.owner";

    private static readonly string[] PartyLabel = ["name"];
    private static readonly string[] PartySecondary = ["city", "taxNumber"];
    private static readonly string[] PersonLabel = ["firstName", "lastName"];
    private static readonly string[] PersonSecondary = ["title", "email"];
    private static readonly string[] NameLabel = ["name"];
    private static readonly string[] TicketLabel = ["subject"];
    private static readonly string[] TicketSecondary = ["number"];

    public static IReadOnlyList<LookupDefinition> All { get; } =
    [
        new(TicketParty, SupportModule.Entities.Ticket, "partyId", "Müşteri", LookupTargetKind.Record, SharedEntities.Party)
        {
            LabelFields = PartyLabel,
            SecondaryFields = PartySecondary,
            SearchDescription = "Ünvan, vergi numarası, e-posta, telefon ve şehir",
        },
        new(TicketPerson, SupportModule.Entities.Ticket, "personId", "İlgili kişi", LookupTargetKind.Record, SharedEntities.Person)
        {
            LabelFields = PersonLabel,
            SecondaryFields = PersonSecondary,
            SearchDescription = "Ad, soyad ve e-posta",
            DependsOnField = "partyId",
            DependsOnFilter = "partyId",
            DependsOnHint = "Müşteri seçiliyse kişiler o müşteriyle sınırlanır.",
        },
        new(TicketCategory, SupportModule.Entities.Ticket, "categoryId", "Kategori", LookupTargetKind.Record, SupportModule.Entities.Category)
        {
            LabelFields = NameLabel,
            SearchDescription = "Kategori adı",
        },
        new(TicketQueue, SupportModule.Entities.Ticket, "queueId", "Kuyruk", LookupTargetKind.Record, SupportModule.Entities.Queue)
        {
            LabelFields = NameLabel,
            SearchDescription = "Kuyruk adı",
        },
        new(TicketOwner, SupportModule.Entities.Ticket, "ownerId", "Sorumlu", LookupTargetKind.Member)
        {
            SearchDescription = "Üyenin görünen adı",
            PageSize = 20,
        },
        new(TicketParent, SupportModule.Entities.Ticket, "parentTicketId", "İlişkili talep", LookupTargetKind.Record, SupportModule.Entities.Ticket)
        {
            LabelFields = TicketLabel,
            SecondaryFields = TicketSecondary,
            SearchDescription = "Konu ve talep numarası",
            ViewPath = "destek/talepler/{id}",
        },
        new(CategoryParent, SupportModule.Entities.Category, "parentId", "Üst kategori", LookupTargetKind.Record, SupportModule.Entities.Category)
        {
            LabelFields = NameLabel,
            SearchDescription = "Kategori adı",
        },
        new(CategoryQueue, SupportModule.Entities.Category, "defaultQueueId", "Varsayılan kuyruk", LookupTargetKind.Record, SupportModule.Entities.Queue)
        {
            LabelFields = NameLabel,
            SearchDescription = "Kuyruk adı",
        },
        new(PolicyCalendar, SupportModule.Entities.SlaPolicy, "calendarId", "Takvim", LookupTargetKind.Record, SupportModule.Entities.SlaCalendar)
        {
            LabelFields = NameLabel,
            SearchDescription = "Takvim adı",
        },
        new(PolicyCategory, SupportModule.Entities.SlaPolicy, "categoryId", "Kategori", LookupTargetKind.Record, SupportModule.Entities.Category)
        {
            LabelFields = NameLabel,
            SearchDescription = "Kategori adı",
        },
        new(PolicyParty, SupportModule.Entities.SlaPolicy, "partyId", "Müşteri", LookupTargetKind.Record, SharedEntities.Party)
        {
            LabelFields = PartyLabel,
            SecondaryFields = PartySecondary,
            SearchDescription = "Ünvan, vergi numarası, e-posta, telefon ve şehir",
        },
        new(RuleCategory, SupportModule.Entities.RoutingRule, "categoryId", "Kategori", LookupTargetKind.Record, SupportModule.Entities.Category)
        {
            LabelFields = NameLabel,
            SearchDescription = "Kategori adı",
        },
        new(RuleQueue, SupportModule.Entities.RoutingRule, "queueId", "Kuyruk", LookupTargetKind.Record, SupportModule.Entities.Queue)
        {
            LabelFields = NameLabel,
            SearchDescription = "Kuyruk adı",
        },
        new(RuleParty, SupportModule.Entities.RoutingRule, "partyId", "Müşteri", LookupTargetKind.Record, SharedEntities.Party)
        {
            LabelFields = PartyLabel,
            SecondaryFields = PartySecondary,
            SearchDescription = "Ünvan, vergi numarası, e-posta, telefon ve şehir",
        },
        new(RuleOwner, SupportModule.Entities.RoutingRule, "ownerId", "Sorumlu", LookupTargetKind.Member)
        {
            SearchDescription = "Üyenin görünen adı",
            PageSize = 20,
        },
    ];
}
