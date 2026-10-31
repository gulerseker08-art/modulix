using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Contracts.Lifecycle;
using Modulix.Modules.Contracts.Records;

namespace Modulix.Modules.Contracts;

/// <summary>
/// Sözleşme yönetimi (EPIC 7, ADR-0022): şablon, sürüm, onay, imza, taahhüt ve yenileme. Sektörden bağımsızdır; müşteri ve kişiler
/// ortak kayıttır (ADR-0006), CRM tablolarına bağlanmaz. Yalnızca <c>Modulix.Core.Contracts</c>'a başvurur.
/// </summary>
public sealed class ContractsModule : IModule
{
    public const string ModuleKey = "contracts";

    public static class Permissions
    {
        public const string ContractsRead = "contracts.contracts.read";
        public const string ContractsWrite = "contracts.contracts.write";

        /// <summary>Sözleşme bedeli, kalem fiyatları ve toplamlar (Y8b, hassas alan). Olmayan kişi sözleşmeyi görür, tutarları görmez ve yazamaz.</summary>
        public const string FinancialsRead = "contracts.financials.read";

        /// <summary>İç onay bekleyen sözleşmeyi onaylar ya da reddeder (ADR-0022, dilim 2). Yazma izninden ayrıdır.</summary>
        public const string ContractsApprove = "contracts.contracts.approve";
    }

    /// <summary>Firma ayarları (Y6).</summary>
    public static class SettingKeys
    {
        public const string AllowSelfApproval = "contracts.approval.allow-self";
    }

    /// <summary>Kayıt türleri (boru hattındaki EntityKey).</summary>
    public static class Entities
    {
        public const string Contract = "contracts.contract";
        public const string ContractParty = "contracts.contract-party";
        public const string ContractLine = "contracts.contract-line";
    }

    /// <summary>Düzenlenebilir seçim listeleri (Y9b).</summary>
    public static class Lists
    {
        public const string ContractType = "contracts.contract-type";
    }

    public string Key => ModuleKey;

    public string DisplayName => "Sözleşmeler";

    IReadOnlyList<PermissionDefinition> IModule.Permissions { get; } =
    [
        new(Permissions.ContractsRead, "Sözleşmeleri, taraflarını ve kalemlerini görüntüler", "Sözleşmeler"),
        new(Permissions.ContractsWrite, "Sözleşme taslağı açar, taraf ve kalem ekler, düzenler, taslağı siler", "Sözleşmeler"),
        new(Permissions.FinancialsRead, "Sözleşme bedelini, kalem fiyatlarını ve toplamları görür ve düzenler", "Sözleşme tutarları"),
        new(Permissions.ContractsApprove, "İç onay bekleyen sözleşmeyi onaylar ya da gerekçeyle reddeder", "Sözleşmeler"),
    ];

    IReadOnlyList<SettingDefinition> IModule.Settings { get; } =
    [
        new(SettingKeys.AllowSelfApproval, "Sözleşme iç onay: kendi gönderdiğini onaylama", "Sözleşmeler", SettingType.Choice, "false")
        {
            Description = "Evet seçilirse sözleşmeyi onaya gönderen kişi onaylama yetkisine sahipse kendi gönderdiğini onaylayabilir. Varsayılan hayırdır (hazırlayan ve onaylayan ayrılır).",
            Options = [new SettingOption("false", "Hayır"), new SettingOption("true", "Evet")],
        },
    ];

    /// <summary>Devre dışı bırakılan kişinin yürürlükte olmayan ve yürürlükteki sözleşmeleri devredilir (ADR-0004).</summary>
    IReadOnlyList<OwnedRecordType> IModule.OwnedRecords { get; } =
    [
        new(Entities.Contract, "sözleşme") { Path = "sozlesmeler/{id}" },
    ];

    IReadOnlyList<RoleTemplate> IModule.RoleTemplates { get; } =
    [
        new(
            "contracts.manager",
            "Sözleşme sorumlusu",
            "Sözleşmeleri, taraflarını, kalemlerini ve tutarlarını görüntüler ve düzenler.",
            [Permissions.ContractsRead, Permissions.ContractsWrite, Permissions.FinancialsRead])
        {
            Scope = RecordScope.Own,
        },
        new(
            "contracts.approver",
            "Sözleşme onaylayıcı",
            "Sözleşmeleri tutarlarıyla görüntüler, iç onayı verir ya da reddeder.",
            [Permissions.ContractsRead, Permissions.FinancialsRead, Permissions.ContractsApprove])
        {
            Scope = RecordScope.All,
        },
        new(
            "contracts.viewer",
            "Sözleşme izleyici",
            "Sözleşmeleri tutarlar olmadan görüntüler.",
            [Permissions.ContractsRead])
        {
            Scope = RecordScope.All,
        },
    ];

    IReadOnlyList<AuditedEntity> IModule.AuditedEntities { get; } =
    [
        new(Entities.Contract, "Sözleşme", Permissions.ContractsRead, new Dictionary<string, string>
        {
            ["number"] = "Numara", ["title"] = "Başlık", ["type"] = "Tür", ["description"] = "Açıklama", ["partyId"] = "Müşteri",
            ["personId"] = "İlgili kişi", ["ownerId"] = "Sorumlu", ["previousContractId"] = "Önceki sözleşme", ["startDate"] = "Başlangıç",
            ["endDate"] = "Bitiş", ["isIndefinite"] = "Belirsiz süreli", ["currency"] = "Para birimi", ["priceIncludesTax"] = "Fiyatlar vergi dahil",
            ["contractValue"] = "Sözleşme bedeli", ["paymentTerms"] = "Ödeme koşulları", ["renewalMode"] = "Yenileme yöntemi",
            ["renewalPeriodMonths"] = "Yenileme süresi (ay)", ["noticePeriodDays"] = "Bildirim süresi (gün)", ["noticeDeadline"] = "Son bildirim tarihi",
            ["confidentiality"] = "Gizlilik", ["preparationStatus"] = "Hazırlık durumu", ["approvalStatus"] = "Onay durumu",
            ["signatureStatus"] = "İmza durumu", ["effectiveStatus"] = "Yürürlük durumu", ["statusReason"] = "Gerekçe",
            ["currentVersionNo"] = "Güncel sürüm", ["effectiveVersionNo"] = "Yürürlükteki sürüm", ["netTotal"] = "Net toplam",
            ["taxTotal"] = "Vergi", ["grossTotal"] = "Genel toplam",
        }, "title")
        {
            UserFields = ["ownerId", "createdBy", "updatedBy"],
            References = new Dictionary<string, string> { ["partyId"] = SharedEntities.Party, ["personId"] = SharedEntities.Person, ["previousContractId"] = Entities.Contract },
        },
        new(Entities.ContractParty, "Sözleşme tarafı", Permissions.ContractsRead, new Dictionary<string, string>
        {
            ["contractId"] = "Sözleşme", ["role"] = "Rol", ["partyId"] = "Taraf", ["personId"] = "Kişi", ["position"] = "Sıra", ["notes"] = "Not",
        })
        {
            References = new Dictionary<string, string> { ["contractId"] = Entities.Contract, ["partyId"] = SharedEntities.Party, ["personId"] = SharedEntities.Person },
        },
        new(Entities.ContractLine, "Sözleşme kalemi", Permissions.ContractsRead, new Dictionary<string, string>
        {
            ["contractId"] = "Sözleşme", ["position"] = "Sıra", ["kind"] = "Tür", ["productCode"] = "Kod", ["name"] = "Ad", ["description"] = "Açıklama",
            ["quantity"] = "Miktar", ["unitName"] = "Birim", ["unitPrice"] = "Birim fiyat", ["discountPercent"] = "İndirim %",
            ["taxRatePercent"] = "Vergi %", ["billingPeriod"] = "Dönem", ["net"] = "Net", ["tax"] = "Vergi", ["gross"] = "Toplam",
        }, "name")
        {
            References = new Dictionary<string, string> { ["contractId"] = Entities.Contract },
        },
    ];

    /// <summary>Hassas alanlar (Y8b): bedel, fiyat ve toplamlar. Para birimi tek başına bir şey söylemediği için hassas değildir.</summary>
    IReadOnlyList<SensitiveFields> IModule.SensitiveFields { get; } =
    [
        new(Entities.Contract, Permissions.FinancialsRead, ["contractValue", "netTotal", "taxTotal", "grossTotal", "recurringSummary"]),
        new(Entities.ContractLine, Permissions.FinancialsRead, ["unitPrice", "discountPercent", "listAmount", "lineDiscount", "net", "tax", "gross"]),
    ];

    IReadOnlyList<CustomizableEntity> IModule.CustomizableEntities { get; } =
    [
        new(Entities.Contract, "Sözleşme"),
    ];

    IReadOnlyList<LookupDefinition> IModule.Lookups => ContractsLookupDefinitions.All;

    /// <summary>Sözleşme türü (Y9b): hazır değerler örnektir; firma kendi türlerini ekler. Kod türe bakmaz (sektör koşulu yok).</summary>
    IReadOnlyList<ChoiceListDefinition> IModule.ChoiceLists { get; } =
    [
        new(
            Lists.ContractType,
            "Sözleşme türü",
            Entities.Contract,
            "type",
            Required: true,
            [],
            [
                new("Service", "Hizmet sözleşmesi"),
                new("Sales", "Satış sözleşmesi"),
                new("Framework", "Çerçeve sözleşme"),
                new("Maintenance", "Bakım / destek sözleşmesi"),
                new("Subscription", "Abonelik sözleşmesi"),
                new("Nda", "Gizlilik sözleşmesi"),
                new("Other", "Diğer"),
            ])
        {
            Default = "Service",
        },
    ];

    /// <summary>
    /// Ortak kayıtlar (ADR-0006): müşteri ve kişi, sözleşme okuma izniyle okunur; sözleşme yazma izni yeni müşteri/kişi açabilir (müşteri
    /// herhangi bir modülle başlayabilir; CRM kapalı tenant'ta da sözleşme müşterisi girilebilmelidir). CRM uzantısı (firma kartı) bu
    /// yolla oluşmaz; CRM açıksa aynı taraf orada da kullanılır.
    /// </summary>
    IReadOnlyList<SharedRecordAccess> IModule.SharedRecordAccess { get; } =
    [
        new(SharedEntities.Party, Permissions.ContractsRead, Permissions.ContractsWrite),
        new(SharedEntities.Person, Permissions.ContractsRead, Permissions.ContractsWrite),
    ];

    IReadOnlyList<SharedRecordReference> IModule.SharedRecordReferences { get; } =
    [
        new(SharedEntities.Party, Entities.Contract, "partyId", "sözleşme"),
        new(SharedEntities.Person, Entities.Contract, "personId", "sözleşme"),
        new(SharedEntities.Party, Entities.ContractParty, "partyId", "sözleşme tarafı"),
        new(SharedEntities.Person, Entities.ContractParty, "personId", "sözleşme tarafı"),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<ContractsStoreContext>();
        services.AddScoped<ContractWorkflowGate>();
        services.AddScoped<ContractReader>();
        services.AddScoped<IRecordStore, ContractRecordStore>();
        services.AddScoped<IRecordStore, ContractPartyRecordStore>();
        services.AddScoped<IRecordStore, ContractLineRecordStore>();
        services.AddScoped<IContractWorkflow, ContractWorkflow>();
    }
}
