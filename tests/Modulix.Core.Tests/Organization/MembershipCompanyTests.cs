using Modulix.Core.Organization;

namespace Modulix.Core.Tests.Organization;

/// <summary>ADR-0008, adım 5: üyelik–şirket görevinin geçici vekâlet penceresi ve varsayılan şube kuralları.</summary>
public class MembershipCompanyTests
{
    private static readonly Guid Membership = Guid.NewGuid();
    private static readonly Guid Company = Guid.NewGuid();

    [Fact]
    public void Open_ended_assignment_is_always_active()
    {
        var assignment = new MembershipCompany(Membership, Company);

        Assert.True(assignment.IsActiveAt(DateTimeOffset.UtcNow));
        Assert.True(assignment.IsActiveAt(DateTimeOffset.UtcNow.AddYears(10)));
    }

    [Fact]
    public void Delegation_is_active_only_within_its_window()
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var assignment = new MembershipCompany(Membership, Company, start, end);

        Assert.False(assignment.IsActiveAt(start.AddDays(-1)));
        Assert.True(assignment.IsActiveAt(start));
        Assert.True(assignment.IsActiveAt(end.AddSeconds(-1)));
        Assert.False(assignment.IsActiveAt(end)); // bitiş dışlayıcı: o an erişim kapanır
    }

    [Fact]
    public void End_before_start_is_rejected()
    {
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => new MembershipCompany(Membership, Company, start, end));
    }

    [Fact]
    public void Empty_branch_is_normalised_to_none()
    {
        var assignment = new MembershipCompany(Membership, Company, branchId: Guid.Empty);

        Assert.Null(assignment.BranchId);
    }
}
