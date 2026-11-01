using Modulix.Modules.Orders.Lifecycle;

namespace Modulix.Modules.Orders.Tests;

/// <summary>ADR-0023 dilim 3: satış devri hazırlık denetimi, düzenlenebilirlik ve eylem geçişleri (saf kurallar).</summary>
public class OrderHandoverRulesTests
{
    private static readonly HandoverSnapshot Complete = new(
        "Sunucu kurulumu", "Kurulu cihazlar ve rapor", "Gebze depo", "Ali Veli", "0532 000 00 00", null, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 15),
        "Cihazlar çalışır durumda", "Müşteri kabul tutanağı imzalar", Guid.NewGuid(), null);

    private static IReadOnlyList<HandoverChecklistState> Done() => [new("Kapsam netleşti", true, true), new("İsteğe bağlı not", false, false)];

    [Fact]
    public void A_complete_handover_has_nothing_missing_and_optional_items_do_not_block()
    {
        Assert.Empty(HandoverRules.Missing(Complete, Done()));
    }

    [Fact]
    public void Every_required_field_is_reported_when_the_handover_is_empty()
    {
        var empty = new HandoverSnapshot(null, null, null, null, null, null, null, null, null, null, null, null);

        var missing = HandoverRules.Missing(empty, [new("Kapsam netleşti", true, false)]);

        Assert.Equal(10, missing.Count);
        Assert.Contains(missing, m => m.Contains("kapsam"));
        Assert.Contains(missing, m => m.Contains("çıktılar"));
        Assert.Contains(missing, m => m.Contains("Lokasyon"));
        Assert.Contains(missing, m => m.Contains("telefon ya da e-posta"));
        Assert.Contains(missing, m => m.Contains("Hedef başlangıç"));
        Assert.Contains(missing, m => m.Contains("kabul kriterleri"));
        Assert.Contains(missing, m => m.Contains("Sorumlu operasyon"));
        Assert.Contains(missing, m => m.Contains("Kapsam netleşti"));
    }

    [Theory]
    [InlineData("0532 111", null, true)]
    [InlineData(null, "ali@example.com", true)]
    [InlineData(" ", " ", false)]
    public void Either_a_phone_or_an_email_satisfies_the_contact_requirement(string? phone, string? email, bool satisfied)
    {
        var missing = HandoverRules.Missing(Complete with { ContactPhone = phone, ContactEmail = email }, Done());

        Assert.Equal(satisfied, missing.Count == 0);
    }

    [Fact]
    public void Responsible_team_alone_satisfies_the_responsibility_and_an_inverted_period_is_reported()
    {
        Assert.Empty(HandoverRules.Missing(Complete with { ResponsibleUserId = null, ResponsibleTeam = "Kurulum ekibi" }, Done()));
        Assert.Contains(HandoverRules.Missing(Complete with { TargetEndDate = new DateOnly(2026, 11, 1) }, Done()), m => m.Contains("bitiş"));
    }

    [Fact]
    public void Editable_status_follows_the_missing_list_and_info_requested_stays_until_resubmitted()
    {
        Assert.Equal(HandoverStatus.Ready, HandoverRules.EditableStatus(HandoverStatus.Preparing, []));
        Assert.Equal(HandoverStatus.Preparing, HandoverRules.EditableStatus(HandoverStatus.Ready, ["eksik"]));
        Assert.Equal(HandoverStatus.InfoRequested, HandoverRules.EditableStatus(HandoverStatus.InfoRequested, []));
        Assert.Equal(HandoverStatus.InfoRequested, HandoverRules.EditableStatus(HandoverStatus.InfoRequested, ["eksik"]));
    }

    [Theory]
    [InlineData(HandoverStatus.Preparing, true)]
    [InlineData(HandoverStatus.Ready, true)]
    [InlineData(HandoverStatus.InfoRequested, true)]
    [InlineData(HandoverStatus.Sent, false)]
    [InlineData(HandoverStatus.Accepted, false)]
    public void Only_preparing_ready_and_info_requested_handovers_can_be_edited(string status, bool editable)
    {
        Assert.Equal(editable, HandoverRules.CanEdit(status));
    }

    [Fact]
    public void Sending_needs_an_approved_order_a_complete_handover_and_a_sendable_status()
    {
        Assert.Equal("order-not-approved", HandoverRules.Can(HandoverAction.Send, HandoverStatus.Ready, OrderStatus.Draft, []).Code);
        Assert.Equal("missing-info", HandoverRules.Can(HandoverAction.Send, HandoverStatus.Preparing, OrderStatus.Approved, ["Lokasyon yazılmadı.", "Başka"]).Code);
        Assert.Contains("+1", HandoverRules.Can(HandoverAction.Send, HandoverStatus.Preparing, OrderStatus.Approved, ["a", "b"]).Message);
        Assert.Equal("already-sent", HandoverRules.Can(HandoverAction.Send, HandoverStatus.Sent, OrderStatus.Approved, []).Code);
        Assert.Equal("accepted", HandoverRules.Can(HandoverAction.Send, HandoverStatus.Accepted, OrderStatus.Approved, []).Code);
        Assert.True(HandoverRules.Can(HandoverAction.Send, HandoverStatus.Ready, OrderStatus.Approved, []).Allowed);
        Assert.True(HandoverRules.Can(HandoverAction.Send, HandoverStatus.InfoRequested, OrderStatus.Approved, []).Allowed);
    }

    [Theory]
    [InlineData(HandoverAction.RequestInfo, HandoverStatus.Sent, true)]
    [InlineData(HandoverAction.Accept, HandoverStatus.Sent, true)]
    [InlineData(HandoverAction.Accept, HandoverStatus.Ready, false)]
    [InlineData(HandoverAction.RequestInfo, HandoverStatus.InfoRequested, false)]
    [InlineData(HandoverAction.Accept, HandoverStatus.Accepted, false)]
    public void Operations_can_decide_only_on_a_sent_handover(string action, string status, bool allowed)
    {
        Assert.Equal(allowed, HandoverRules.Can(action, status, OrderStatus.Approved, []).Allowed);
    }

    [Fact]
    public void A_note_is_required_for_info_requests_and_for_resubmission_after_one()
    {
        Assert.True(HandoverRules.RequiresNote(HandoverAction.RequestInfo, HandoverStatus.Sent));
        Assert.True(HandoverRules.RequiresNote(HandoverAction.Send, HandoverStatus.InfoRequested));
        Assert.False(HandoverRules.RequiresNote(HandoverAction.Send, HandoverStatus.Ready));
        Assert.False(HandoverRules.RequiresNote(HandoverAction.Accept, HandoverStatus.Sent));
        Assert.Equal(8, HandoverRules.DefaultChecklist.Count);
    }
}
