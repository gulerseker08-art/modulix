using Modulix.Modules.Orders.Lifecycle;

namespace Modulix.Modules.Orders.Tests;

/// <summary>ADR-0023 dilim 5: teslimat miktar kuralları, gerçekleşme hesabı, geçiş tablosu, hizmet başlangıcı ön koşulları ve kanıt dosyası kuralları (saf kurallar).</summary>
public class FulfillmentRulesTests
{
    private static readonly Guid Device = Guid.NewGuid();
    private static readonly Guid Setup = Guid.NewGuid();

    private static IReadOnlyDictionary<Guid, (string Name, string Kind, decimal Remaining)> Capacity(decimal remaining = 10m) =>
        new Dictionary<Guid, (string, string, decimal)> { [Device] = ("Sunucu", LineKinds.Product, remaining), [Setup] = ("Kurulum", LineKinds.Service, 1m) };

    private static readonly IReadOnlyDictionary<Guid, decimal> None = new Dictionary<Guid, decimal>();

    // ---- Planlama / teslim miktarı

    [Fact]
    public void Partial_plan_within_remaining_is_valid()
    {
        Assert.Empty(DeliveryRules.ValidatePlan([new QuantityRequest(Device, 4m)], Capacity(), None));
    }

    [Fact]
    public void Plan_over_remaining_is_rejected()
    {
        var errors = DeliveryRules.ValidatePlan([new QuantityRequest(Device, 11m)], Capacity(), None);

        Assert.Single(errors);
    }

    [Fact]
    public void Plan_counts_quantity_already_planned_in_other_open_deliveries()
    {
        var elsewhere = new Dictionary<Guid, decimal> { [Device] = 8m };

        Assert.Empty(DeliveryRules.ValidatePlan([new QuantityRequest(Device, 2m)], Capacity(), elsewhere));
        Assert.NotEmpty(DeliveryRules.ValidatePlan([new QuantityRequest(Device, 3m)], Capacity(), elsewhere));
    }

    [Fact]
    public void Plan_rejects_service_lines_zero_and_duplicate_quantities_and_empty_requests()
    {
        Assert.NotEmpty(DeliveryRules.ValidatePlan([new QuantityRequest(Setup, 1m)], Capacity(), None));
        Assert.NotEmpty(DeliveryRules.ValidatePlan([new QuantityRequest(Device, 0m)], Capacity(), None));
        Assert.NotEmpty(DeliveryRules.ValidatePlan([new QuantityRequest(Device, 1m), new QuantityRequest(Device, 1m)], Capacity(), None));
        Assert.NotEmpty(DeliveryRules.ValidatePlan([], Capacity(), None));
        Assert.NotEmpty(DeliveryRules.ValidatePlan([new QuantityRequest(Guid.NewGuid(), 1m)], Capacity(), None));
    }

    [Fact]
    public void Over_delivery_is_blocked_and_under_delivery_is_allowed()
    {
        var remaining = new Dictionary<Guid, (string, decimal)> { [Device] = ("Sunucu", 5m) };

        Assert.NotEmpty(DeliveryRules.ValidateDeliver([new QuantityRequest(Device, 5.0001m)], remaining));
        Assert.Empty(DeliveryRules.ValidateDeliver([new QuantityRequest(Device, 3m)], remaining));
        Assert.Empty(DeliveryRules.ValidateDeliver([new QuantityRequest(Device, 5m)], remaining));
    }

    [Fact]
    public void Delivery_with_no_positive_quantity_or_unknown_line_is_rejected()
    {
        var remaining = new Dictionary<Guid, (string, decimal)> { [Device] = ("Sunucu", 5m) };

        Assert.NotEmpty(DeliveryRules.ValidateDeliver([new QuantityRequest(Device, 0m)], remaining));
        Assert.NotEmpty(DeliveryRules.ValidateDeliver([new QuantityRequest(Guid.NewGuid(), 1m)], remaining));
        Assert.NotEmpty(DeliveryRules.ValidateDeliver([new QuantityRequest(Device, 1m), new QuantityRequest(Device, -1m)], remaining));
    }

    // ---- Gerçekleşme hesabı

    private static FulfillmentLine Product(decimal effective, decimal delivered) => new(Guid.NewGuid(), LineKinds.Product, effective, delivered, null);

    private static FulfillmentLine Service(string? status) => new(Guid.NewGuid(), LineKinds.Service, 1m, 0m, status);

    [Fact]
    public void Fulfillment_is_not_started_without_plan_and_planned_with_a_planned_delivery()
    {
        Assert.Equal(FulfillmentStatus.NotStarted, FulfillmentCalculator.Compute([Product(10m, 0m)], false));
        Assert.Equal(FulfillmentStatus.Planned, FulfillmentCalculator.Compute([Product(10m, 0m)], true));
        Assert.Equal(FulfillmentStatus.Planned, FulfillmentCalculator.Compute([Service(ServiceStatus.Planned)], false));
    }

    [Fact]
    public void Fulfillment_is_partial_after_a_partial_delivery_and_completed_when_all_delivered()
    {
        Assert.Equal(FulfillmentStatus.Partial, FulfillmentCalculator.Compute([Product(10m, 4m)], true));
        Assert.Equal(FulfillmentStatus.Completed, FulfillmentCalculator.Compute([Product(10m, 10m)], false));
    }

    [Fact]
    public void Service_start_is_not_completion()
    {
        Assert.Equal(FulfillmentStatus.Partial, FulfillmentCalculator.Compute([Service(ServiceStatus.Started)], false));
        Assert.Equal(FulfillmentStatus.Completed, FulfillmentCalculator.Compute([Service(ServiceStatus.Completed)], false));
    }

    [Fact]
    public void Mixed_order_needs_both_product_delivery_and_service_completion()
    {
        Assert.Equal(FulfillmentStatus.Partial, FulfillmentCalculator.Compute([Product(2m, 2m), Service(ServiceStatus.Started)], false));
        Assert.Equal(FulfillmentStatus.Partial, FulfillmentCalculator.Compute([Product(2m, 1m), Service(ServiceStatus.Completed)], false));
        Assert.Equal(FulfillmentStatus.Completed, FulfillmentCalculator.Compute([Product(2m, 2m), Service(ServiceStatus.Completed)], false));
    }

    [Fact]
    public void Cancelled_or_fully_reduced_lines_do_not_block_completion()
    {
        Assert.Equal(FulfillmentStatus.Completed, FulfillmentCalculator.Compute([Product(3m, 3m), Product(0m, 0m)], false));
        Assert.Equal(FulfillmentStatus.NotStarted, FulfillmentCalculator.Compute([], false));
    }

    // ---- Geçişler

    [Theory]
    [InlineData(DeliveryAction.Deliver, DeliveryStatus.Planned, true)]
    [InlineData(DeliveryAction.Fail, DeliveryStatus.Planned, true)]
    [InlineData(DeliveryAction.Cancel, DeliveryStatus.Planned, true)]
    [InlineData(DeliveryAction.Deliver, DeliveryStatus.Delivered, false)]
    [InlineData(DeliveryAction.Deliver, DeliveryStatus.Failed, false)]
    [InlineData(DeliveryAction.Cancel, DeliveryStatus.Delivered, false)]
    [InlineData(DeliveryAction.Reschedule, DeliveryStatus.Failed, true)]
    [InlineData(DeliveryAction.Reschedule, DeliveryStatus.Planned, false)]
    [InlineData(DeliveryAction.Reschedule, DeliveryStatus.Delivered, false)]
    public void Delivery_transitions_follow_the_table(string action, string status, bool allowed)
    {
        Assert.Equal(allowed, FulfillmentTransitions.CanDeliveryAct(action, status, OrderStatus.Approved, alreadyRescheduled: false).Allowed);
    }

    [Fact]
    public void Failed_delivery_can_be_rescheduled_only_once_and_only_on_approved_orders()
    {
        Assert.False(FulfillmentTransitions.CanDeliveryAct(DeliveryAction.Reschedule, DeliveryStatus.Failed, OrderStatus.Approved, alreadyRescheduled: true).Allowed);
        Assert.False(FulfillmentTransitions.CanDeliveryAct(DeliveryAction.Deliver, DeliveryStatus.Planned, OrderStatus.Cancelled, false).Allowed);
        Assert.True(FulfillmentTransitions.CanDeliveryAct(DeliveryAction.Cancel, DeliveryStatus.Planned, OrderStatus.Cancelled, false).Allowed);
    }

    [Fact]
    public void Fail_and_cancel_require_a_reason_but_deliver_does_not()
    {
        Assert.True(FulfillmentTransitions.DeliveryRequiresReason(DeliveryAction.Fail));
        Assert.True(FulfillmentTransitions.DeliveryRequiresReason(DeliveryAction.Cancel));
        Assert.False(FulfillmentTransitions.DeliveryRequiresReason(DeliveryAction.Deliver));
    }

    [Theory]
    [InlineData(ServiceAction.Start, ServiceStatus.Planned, true)]
    [InlineData(ServiceAction.Start, ServiceStatus.Started, false)]
    [InlineData(ServiceAction.Confirm, ServiceStatus.Started, true)]
    [InlineData(ServiceAction.Confirm, ServiceStatus.Planned, false)]
    [InlineData(ServiceAction.Complete, ServiceStatus.Started, true)]
    [InlineData(ServiceAction.Complete, ServiceStatus.Planned, false)]
    [InlineData(ServiceAction.Complete, ServiceStatus.Completed, false)]
    public void Service_transitions_follow_the_table(string action, string status, bool allowed)
    {
        Assert.Equal(allowed, FulfillmentTransitions.CanServiceAct(action, status).Allowed);
    }

    [Fact]
    public void Service_start_blockers_list_every_missing_prerequisite()
    {
        Assert.Empty(FulfillmentTransitions.ServiceStartBlockers(OrderStatus.Approved, HandoverStatus.Accepted, [], []));

        var blockers = FulfillmentTransitions.ServiceStartBlockers(OrderStatus.Draft, HandoverStatus.Sent, ["SP-1-IS-1 (Teslimat)"], ["Ekip ve kaynaklar atandı"]);

        Assert.Equal(4, blockers.Count);
        Assert.Contains(blockers, b => b.Contains("SP-1-IS-1"));
        Assert.Contains(blockers, b => b.Contains("Ekip ve kaynaklar"));
    }

    [Fact]
    public void Service_prerequisites_are_the_first_three_service_template_tasks()
    {
        Assert.Equal(3, OperationTemplates.ServicePrerequisites.Count);
        Assert.All(OperationTemplates.ServicePrerequisites, p => Assert.Contains(p, OperationTemplates.Service));
        Assert.DoesNotContain("Hizmet başlatıldı", OperationTemplates.ServicePrerequisites);
    }

    // ---- Kanıt

    private static readonly byte[] Pdf = [.. "%PDF-1.4 test"u8.ToArray()];
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0];

    [Fact]
    public void Valid_pdf_and_png_evidence_is_accepted()
    {
        Assert.Null(EvidenceRules.Validate("tutanak.pdf", "application/pdf", Pdf));
        Assert.Null(EvidenceRules.Validate("foto.png", "image/png; charset=binary", Png));
    }

    [Fact]
    public void Evidence_with_wrong_type_mismatched_content_empty_or_oversize_is_rejected()
    {
        Assert.NotNull(EvidenceRules.Validate("a.exe", "application/x-msdownload", Pdf));
        Assert.NotNull(EvidenceRules.Validate("a.pdf", "application/pdf", Png));
        Assert.NotNull(EvidenceRules.Validate("a.pdf", "application/pdf", []));
        Assert.NotNull(EvidenceRules.Validate("a.pdf", null, Pdf));
        var big = new byte[Modulix.Modules.Orders.Domain.OrderDeliveryEvidence.MaxBytes + 1];
        Pdf.CopyTo(big, 0);
        Assert.NotNull(EvidenceRules.Validate("a.pdf", "application/pdf", big));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x.pdf")]
    [InlineData("a\\b.pdf")]
    public void Evidence_file_names_must_not_contain_paths(string name)
    {
        Assert.NotNull(EvidenceRules.Validate(name, "application/pdf", Pdf));
    }

    [Fact]
    public void Evidence_hash_is_stable_lowercase_hex()
    {
        var hash = EvidenceRules.Sha256(Pdf);

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, EvidenceRules.Sha256(Pdf));
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }
}
