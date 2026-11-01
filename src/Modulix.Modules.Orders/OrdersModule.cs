using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Orders.Lifecycle;
using Modulix.Modules.Orders.Records;

namespace Modulix.Modules.Orders;

/// <summary>
/// Sipariş ve satış devri (EPIC 8, ADR-0023): kabul edilmiş tekliften sipariş, satış devri, operasyon işi ve teslimat/hizmet başlangıcı.
/// Sektörden bağımsızdır; müşteri ve kişiler ortak kayıttır (ADR-0006), CRM tablolarına bağlanmaz. Teklifi yalnızca
/// <c>Modulix.Core.Contracts</c> üzerindeki <c>IAcceptedQuoteSource</c> ile okur. Sipariş tutarı tahsilat ya da gelir değildir.
/// </summary>
public sealed class OrdersModule : IModule
{
    public const string ModuleKey = "orders";

    public static class Permissions
    {
        public const string OrdersRead = "orders.orders.read";

        /// <summary>Kabul edilmiş tekliften sipariş oluşturur. Düzenlemeden ayrıdır.</summary>
        public const string OrdersCreate = "orders.orders.create";

        public const string OrdersWrite = "orders.orders.write";

        /// <summary>Sipariş tutarlarını, kalem fiyatlarını ve toplamları görür. Olmayan kişi siparişi görür, tutarları görmez ve tekliften sipariş oluşturamaz.</summary>
        public const string FinancialsRead = "orders.financials.read";
    }

    /// <summary>Kayıt türleri (boru hattındaki EntityKey).</summary>
    public static class Entities
    {
        public const string Order = "orders.order";
        public const string OrderLine = "orders.order-line";
    }

    public string Key => ModuleKey;

    public string DisplayName => "Siparişler";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.OrdersRead, "Siparişleri, kalemlerini ve tarihçesini görüntüler", "Siparişler"),
        new(Permissions.OrdersCreate, "Kabul edilmiş tekliften sipariş oluşturur", "Siparişler"),
        new(Permissions.OrdersWrite, "Taslak siparişin ticari alanlarını ve planlama bilgilerini düzenler", "Siparişler"),
        new(Permissions.FinancialsRead, "Sipariş tutarlarını, kalem fiyatlarını ve toplamları görür", "Sipariş tutarları"),
    ];

    /// <summary>Devre dışı bırakılan kişinin siparişleri devredilir (ADR-0004).</summary>
    IReadOnlyList<OwnedRecordType> IModule.OwnedRecords { get; } =
    [
        new(Entities.Order, "sipariş") { Path = "siparisler/{id}" },
    ];

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "orders.sales",
            "Sipariş sorumlusu (satış)",
            "Kabul edilmiş tekliften sipariş oluşturur, siparişleri tutarlarıyla görüntüler ve taslağı düzenler.",
            [Permissions.OrdersRead, Permissions.OrdersCreate, Permissions.OrdersWrite, Permissions.FinancialsRead])
        {
            Scope = RecordScope.Own,
        },
        new(
            "orders.viewer",
            "Sipariş izleyici",
            "Siparişleri tutarlar olmadan görüntüler.",
            [Permissions.OrdersRead])
        {
            Scope = RecordScope.All,
        },
    ];

    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(Entities.Order, "Sipariş", Permissions.OrdersRead, new Dictionary<string, string>
        {
            ["number"] = "Numara", ["title"] = "Başlık", ["partyId"] = "Müşteri", ["personId"] = "İlgili kişi", ["ownerId"] = "Satış sorumlusu",
            ["sourceQuoteNumber"] = "Kaynak teklif", ["sourceQuoteRevisionNo"] = "Teklif revizyonu", ["contractNumber"] = "Sözleşme", ["conversionMode"] = "Dönüşüm kipi",
            ["acceptedAt"] = "Müşteri kabul zamanı", ["acceptanceMethod"] = "Kabul yöntemi", ["acceptedByName"] = "Kabul eden", ["status"] = "Durum",
            ["handoverStatus"] = "Devir durumu", ["fulfillmentStatus"] = "Gerçekleşme", ["statusReason"] = "Gerekçe", ["customerName"] = "Müşteri ünvanı",
            ["billingAddress"] = "Fatura adresi", ["deliveryAddress"] = "Teslimat adresi", ["currency"] = "Para birimi", ["priceIncludesTax"] = "Fiyatlar vergi dahil",
            ["paymentTerms"] = "Ödeme koşulları", ["deliveryTerms"] = "Teslimat koşulları", ["commercialTerms"] = "Ticari koşullar", ["commitments"] = "Taahhütler",
            ["operationsNotes"] = "Operasyon notları", ["location"] = "Lokasyon", ["plannedStartDate"] = "Planlanan başlangıç", ["plannedDeliveryDate"] = "Planlanan teslim",
            ["netTotal"] = "Net toplam", ["taxTotal"] = "Vergi", ["grossTotal"] = "Genel toplam",
        }, "title")
        {
            UserFields = ["ownerId", "acceptedByUserId", "createdBy", "updatedBy"],
            References = new Dictionary<string, string> { ["partyId"] = SharedEntities.Party, ["personId"] = SharedEntities.Person },
        },
        new(Entities.OrderLine, "Sipariş kalemi", Permissions.OrdersRead, new Dictionary<string, string>
        {
            ["orderId"] = "Sipariş", ["position"] = "Sıra", ["kind"] = "Tür", ["productCode"] = "Kod", ["name"] = "Ad", ["description"] = "Açıklama",
            ["quantity"] = "Miktar", ["acceptedQuantity"] = "Kabul edilen miktar", ["unitName"] = "Birim", ["unitPrice"] = "Birim fiyat",
            ["taxRatePercent"] = "Vergi %", ["net"] = "Net", ["tax"] = "Vergi", ["gross"] = "Toplam", ["deliveredQuantity"] = "Teslim edilen",
            ["plannedDate"] = "Planlanan tarih",
        }, "name")
        {
            References = new Dictionary<string, string> { ["orderId"] = Entities.Order },
        },
    ];

    /// <summary>Hassas alanlar: tutarlar. Para birimi tek başına bir şey söylemediği için hassas değildir.</summary>
    IReadOnlyList<SensitiveFields> IModule.SensitiveFields { get; } =
    [
        new(Entities.Order, Permissions.FinancialsRead, ["netTotal", "taxTotal", "grossTotal"]),
        new(Entities.OrderLine, Permissions.FinancialsRead, ["unitPrice", "listAmount", "lineDiscount", "net", "tax", "gross"]),
    ];

    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Order, "Sipariş"),
    ];

    IReadOnlyList<LookupDefinition> IModule.Lookups => OrdersLookupDefinitions.All;

    /// <summary>Ortak kayıtlar (ADR-0006): müşteri ve kişi, sipariş okuma izniyle okunur. Sipariş modülü ortak kayıt oluşturmaz.</summary>
    IReadOnlyList<SharedRecordAccess> IModule.SharedRecordAccess { get; } =
    [
        new(SharedEntities.Party, Permissions.OrdersRead),
        new(SharedEntities.Person, Permissions.OrdersRead),
    ];

    IReadOnlyList<SharedRecordReference> IModule.SharedRecordReferences { get; } =
    [
        new(SharedEntities.Party, Entities.Order, "partyId", "sipariş"),
        new(SharedEntities.Person, Entities.Order, "personId", "sipariş"),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<OrdersStoreContext>();
        services.AddScoped<OrderWorkflowGate>();
        services.AddScoped<OrderReader>();
        services.AddScoped<IRecordStore, OrderRecordStore>();
        services.AddScoped<IRecordStore, OrderLineRecordStore>();
        services.AddScoped<IOrderFromQuote, OrderFromQuote>();
        services.AddScoped<IOrderQueries, OrderQueries>();
        services.AddScoped<IActivityLinkTarget, OrderLinkTarget>();
    }
}
