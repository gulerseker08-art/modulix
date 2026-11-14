using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Automation;
using Modulix.Modules.Logistics.Domain;

namespace Modulix.Modules.Logistics.Automation;

/// <summary>
/// Lojistiğin başlangıç kuralı şablonları (ADR-0027 Dilim 3b; ADR-0038 bölüm 23). Hepsi yalnızca uygulama içi bildirim yazar; hiçbiri kayıt
/// değiştirmez, durum ilerletmez ya da dışarıya ileti göndermez. Lojistik kayıtlarında sorumlu kullanıcı alanı olmadığı için bildirimin alıcısı
/// şablonda boş bırakılır (<see cref="AutomationTemplateDefinition.ToComplete"/>): firma "kullan" deyince taslak açılır, alıcıyı seçmeden kural
/// yayınlanamaz. Alıcı kaydı kendi yetkisiyle göremiyorsa Core bildirimi göndermez.
/// </summary>
public static class LogisticsAutomationTemplates
{
    /// <summary>Core'un "Bildirim gönder" aksiyonu (<c>NotifyAutomation.NotifyAction</c>); modül Core'a başvuramadığı için anahtar burada yazılır.</summary>
    public const string NotifyAction = "core.notify";

    /// <summary>Bildirim adımının kimliği ve firmaya bırakılan parametresi.</summary>
    public const string NotifyStep = "bildir";

    public const string RecipientParameter = "recipientId";

    public const string RequestCreated = "logistics.request-created-notify";

    public const string RequestHazardous = "logistics.request-hazardous-notify";

    public const string RequestPendingBeforePickup = "logistics.request-pending-reminder";

    public const string ShipmentUnplannedBeforePickup = "logistics.shipment-unplanned-reminder";

    public const string ShipmentOverdue = "logistics.shipment-overdue-notify";

    public const string ShipmentDelivered = "logistics.shipment-delivered-notify";

    public const string ShipmentDeliveryFailed = "logistics.shipment-delivery-failed-notify";

    public const string TripNotReady = "logistics.trip-not-ready-reminder";

    public const string TripOverdue = "logistics.trip-overdue-notify";

    public const string ExpensePending = "logistics.expense-pending-reminder";

    private const string Request = LogisticsModule.Entities.TransportRequest;
    private const string Shipment = LogisticsModule.Entities.Shipment;
    private const string Trip = LogisticsModule.Entities.Trip;
    private const string Expense = LogisticsModule.Entities.TripExpense;

    private const string ChooseRecipient = " Kullandıktan sonra bildirimin kime gideceğini seçin.";

    private static string Var(string entity, string field) => "{{" + entity + "." + field + "}}";

    private static string Route(string entity) => $"{Var(entity, "pickupCity")} → {Var(entity, "deliveryCity")}";

    public static IReadOnlyList<AutomationTemplateDefinition> All { get; } =
    [
        Template(
            RequestCreated,
            "Yeni taşıma talebi açılınca bildir",
            "Yeni bir taşıma talebi açıldığında seçtiğiniz kişinin bildirim kutusuna yazar.",
            Request,
            new RecordCreatedTrigger(),
            new ConditionLeaf($"{Request}.status", AutomationOperator.Eq, [TransportRequest.Statuses.New]),
            $"Yeni taşıma talebi: {Var(Request, "cargoDescription")} ({Route(Request)})"),
        Template(
            RequestHazardous,
            "Tehlikeli madde (ADR) talebi açılınca bildir",
            "Tehlikeli madde işaretli bir taşıma talebi açıldığında seçtiğiniz kişiye bildirir.",
            Request,
            new RecordCreatedTrigger(),
            new ConditionLeaf($"{Request}.hazardous", AutomationOperator.Eq, ["true"]),
            $"Tehlikeli madde (ADR) talebi: {Var(Request, "cargoDescription")} ({Route(Request)})"),
        Template(
            RequestPendingBeforePickup,
            "Yükleme gününe 1 gün kala onaylanmamış talebi hatırlat",
            "Yükleme gününden bir gün önce hâlâ onay bekleyen taşıma talebini seçtiğiniz kişiye hatırlatır.",
            Request,
            new DateRelativeTrigger($"{Request}.pickupDate", -1),
            new ConditionLeaf($"{Request}.status", AutomationOperator.Eq, [TransportRequest.Statuses.New]),
            $"Yükleme günü yarın, talep hâlâ onay bekliyor: {Var(Request, "cargoDescription")}"),
        Template(
            ShipmentUnplannedBeforePickup,
            "Yükleme gününe 1 gün kala planlanmamış sevkiyatı hatırlat",
            "Yükleme gününden bir gün önce hâlâ sefere atanmamış sevkiyatı seçtiğiniz kişiye hatırlatır.",
            Shipment,
            new DateRelativeTrigger($"{Shipment}.pickupDate", -1),
            new ConditionLeaf($"{Shipment}.status", AutomationOperator.Eq, [Domain.Shipment.Statuses.Pending]),
            $"Yükleme günü yarın, sevkiyat planlanmadı: {Var(Shipment, "cargoDescription")} ({Route(Shipment)})"),
        Template(
            ShipmentOverdue,
            "Teslim tarihi geçen sevkiyatı bildir",
            "En geç teslim gününün ertesi günü hâlâ teslim edilmemiş sevkiyatı seçtiğiniz kişiye bildirir.",
            Shipment,
            new DateRelativeTrigger($"{Shipment}.deliveryBy", 1),
            new ConditionLeaf(
                $"{Shipment}.status",
                AutomationOperator.In,
                [Domain.Shipment.Statuses.Pending, Domain.Shipment.Statuses.Planned, Domain.Shipment.Statuses.Loading, Domain.Shipment.Statuses.InTransit]),
            $"Teslim tarihi geçti ({Var(Shipment, "deliveryBy")}): {Var(Shipment, "cargoDescription")}"),
        Template(
            ShipmentDelivered,
            "Sevkiyat teslim edilince bildir",
            "Sevkiyat teslim edildiğinde (kısmi teslim dahil) seçtiğiniz kişiye bildirir.",
            Shipment,
            new DomainEventTrigger(LogisticsModule.Events.ShipmentDelivered),
            null,
            $"Sevkiyat teslim edildi: {Var(Shipment, "cargoDescription")} ({Var(Shipment, "deliveryCity")})"),
        Template(
            ShipmentDeliveryFailed,
            "Sevkiyat teslim edilemeyince bildir",
            "Teslim başarısız kaydedildiğinde seçtiğiniz kişiye bildirir.",
            Shipment,
            new DomainEventTrigger(LogisticsModule.Events.ShipmentDeliveryFailed),
            null,
            $"Sevkiyat teslim edilemedi: {Var(Shipment, "cargoDescription")} ({Var(Shipment, "deliveryCity")})"),
        Template(
            TripNotReady,
            "Başlangıcına 1 gün kala hazır olmayan seferi hatırlat",
            "Planlanan başlangıçtan bir gün önce hâlâ planlama aşamasındaki seferi seçtiğiniz kişiye hatırlatır.",
            Trip,
            new DateRelativeTrigger($"{Trip}.plannedStart", -1),
            new ConditionLeaf($"{Trip}.status", AutomationOperator.Eq, [Domain.Trip.Statuses.Planning]),
            $"Yarın başlayacak sefer hâlâ hazır değil: {Var(Trip, "number")}"),
        Template(
            TripOverdue,
            "Planlanan bitişi geçen seferi bildir",
            "Planlanan bitiş gününün ertesi günü hâlâ kapanmamış seferi seçtiğiniz kişiye bildirir.",
            Trip,
            new DateRelativeTrigger($"{Trip}.plannedEnd", 1),
            new ConditionLeaf(
                $"{Trip}.status", AutomationOperator.In, [Domain.Trip.Statuses.Planning, Domain.Trip.Statuses.Ready, Domain.Trip.Statuses.InProgress]),
            $"Planlanan bitişi geçti ({Var(Trip, "plannedEnd")}), sefer kapanmadı: {Var(Trip, "number")}"),
        Template(
            ExpensePending,
            "3 gündür onay bekleyen masrafı hatırlat",
            "Masraf tarihinden üç gün sonra hâlâ beklemede olan sefer masrafını seçtiğiniz kişiye hatırlatır.",
            Expense,
            new DateRelativeTrigger($"{Expense}.expenseDate", 3),
            new ConditionLeaf($"{Expense}.status", AutomationOperator.Eq, [TripExpense.Statuses.Pending]),
            $"Onay bekleyen masraf: {Var(Expense, "description")} ({Var(Expense, "amount")} {Var(Expense, "currency")})"),
    ];

    private static AutomationTemplateDefinition Template(
        string key, string name, string description, string entity, AutomationTrigger trigger, ConditionNode? condition, string title) =>
        new(
            key,
            name,
            description + ChooseRecipient,
            entity,
            new AutomationDefinition(trigger, condition, [new AutomationActionStep(NotifyStep, NotifyAction, new JsonObject { ["title"] = title })]))
        {
            ToComplete = [new AutomationTemplateBlank(NotifyStep, RecipientParameter)],
        };
}
