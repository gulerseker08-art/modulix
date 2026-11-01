using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;

namespace Modulix.Modules.Orders;

/// <summary>
/// Sipariş modülünün lookup alanları (ADR-0016). Müşteri ve kişi ortak kayıttır (<c>core.party</c>, <c>core.person</c>); erişim ortak
/// kaydı kullanan modüllerin bildirdiği izinlerle, CRM kapsam kısıtı varsa onunla belirlenir. Kişi lookup'ı müşteri tarafına bağımlıdır.
/// </summary>
public static class OrdersLookupDefinitions
{
    public const string OrderPerson = "orders.order.person";
    public const string OrderOwner = "orders.order.owner";

    private static readonly string[] PersonLabel = ["firstName", "lastName"];
    private static readonly string[] PersonSecondary = ["title", "email"];

    public static IReadOnlyList<LookupDefinition> All { get; } =
    [
        new(OrderPerson, OrdersModule.Entities.Order, "personId", "İlgili kişi", LookupTargetKind.Record, SharedEntities.Person)
        {
            LabelFields = PersonLabel,
            SecondaryFields = PersonSecondary,
            SearchDescription = "Ad, soyad ve e-posta",
            DependsOnField = "partyId",
            DependsOnFilter = "partyId",
            DependsOnHint = "Müşteri sipariş oluşturulurken tekliften gelir; kişiler o müşteriyle sınırlanır.",
        },
        new(OrderOwner, OrdersModule.Entities.Order, "ownerId", "Satış sorumlusu", LookupTargetKind.Member)
        {
            SearchDescription = "Üyenin görünen adı",
            PageSize = 20,
        },
    ];
}
