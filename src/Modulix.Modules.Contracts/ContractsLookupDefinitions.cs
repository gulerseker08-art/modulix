using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;

namespace Modulix.Modules.Contracts;

/// <summary>
/// Sözleşme modülünün lookup alanları (ADR-0016). Müşteri ve kişi ortak kayıttır (<c>core.party</c>, <c>core.person</c>); erişim ortak
/// kaydı kullanan modüllerin bildirdiği izinlerle, CRM kapsam kısıtı varsa onunla belirlenir. Kişi lookup'ı müşteri tarafına bağımlıdır.
/// </summary>
public static class ContractsLookupDefinitions
{
    public const string ContractParty = "contracts.contract.party";
    public const string ContractPerson = "contracts.contract.person";
    public const string ContractOwner = "contracts.contract.owner";
    public const string ContractPrevious = "contracts.contract.previous";
    public const string PartyParty = "contracts.contract-party.party";
    public const string PartyPerson = "contracts.contract-party.person";
    public const string RuleApprover = "contracts.approval-rule.approver";
    public const string DelegationFrom = "contracts.approval-delegation.from";
    public const string DelegationTo = "contracts.approval-delegation.to";

    private static readonly string[] PartyLabel = ["name"];
    private static readonly string[] PartySecondary = ["city", "taxNumber"];
    private static readonly string[] PersonLabel = ["firstName", "lastName"];
    private static readonly string[] PersonSecondary = ["title", "email"];
    private static readonly string[] ContractLabel = ["title"];
    private static readonly string[] ContractSecondary = ["number"];

    private static LookupDefinition Party(string key, string source, string field, string title) =>
        new(key, source, field, title, LookupTargetKind.Record, SharedEntities.Party)
        {
            LabelFields = PartyLabel,
            SecondaryFields = PartySecondary,
            SearchDescription = "Ünvan, vergi numarası, e-posta, telefon ve şehir",
        };

    private static LookupDefinition Person(string key, string source, string field, string title) =>
        new(key, source, field, title, LookupTargetKind.Record, SharedEntities.Person)
        {
            LabelFields = PersonLabel,
            SecondaryFields = PersonSecondary,
            SearchDescription = "Ad, soyad ve e-posta",
            DependsOnField = "partyId",
            DependsOnFilter = "partyId",
            DependsOnHint = "Müşteri seçiliyse kişiler o müşteriyle sınırlanır.",
        };

    public static IReadOnlyList<LookupDefinition> All { get; } =
    [
        Party(ContractParty, ContractsModule.Entities.Contract, "partyId", "Müşteri"),
        Person(ContractPerson, ContractsModule.Entities.Contract, "personId", "İlgili kişi"),
        new(ContractOwner, ContractsModule.Entities.Contract, "ownerId", "Sorumlu", LookupTargetKind.Member)
        {
            SearchDescription = "Üyenin görünen adı",
            PageSize = 20,
        },
        new(ContractPrevious, ContractsModule.Entities.Contract, "previousContractId", "Önceki sözleşme", LookupTargetKind.Record, ContractsModule.Entities.Contract)
        {
            LabelFields = ContractLabel,
            SecondaryFields = ContractSecondary,
            SearchDescription = "Başlık, numara ve açıklama",
            ViewPath = "sozlesmeler/{id}",
        },
        Party(PartyParty, ContractsModule.Entities.ContractParty, "partyId", "Taraf"),
        Person(PartyPerson, ContractsModule.Entities.ContractParty, "personId", "Kişi"),
        new(RuleApprover, ContractsModule.Entities.ContractApprovalRule, "approverId", "Onaylayan", LookupTargetKind.Member)
        {
            SearchDescription = "Üyenin görünen adı",
            PageSize = 20,
        },
        new(DelegationFrom, ContractsModule.Entities.ContractApprovalDelegation, "fromUserId", "Vekâlet veren", LookupTargetKind.Member)
        {
            SearchDescription = "Üyenin görünen adı",
            PageSize = 20,
        },
        new(DelegationTo, ContractsModule.Entities.ContractApprovalDelegation, "toUserId", "Vekil", LookupTargetKind.Member)
        {
            SearchDescription = "Üyenin görünen adı",
            PageSize = 20,
        },
    ];
}
