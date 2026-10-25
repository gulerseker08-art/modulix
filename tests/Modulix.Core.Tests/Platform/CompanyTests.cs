using Modulix.Core.Organization;

namespace Modulix.Core.Tests.Platform;

public class CompanyTests
{
    [Fact]
    public void Company_trims_the_name_and_keeps_the_default_flag()
    {
        var company = new Company("  Acme Lojistik A.Ş.  ", isDefault: true);

        Assert.Equal("Acme Lojistik A.Ş.", company.Name);
        Assert.True(company.IsDefault);
        Assert.NotEqual(Guid.Empty, company.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Company_requires_a_name(string name)
    {
        Assert.Throws<ArgumentException>(() => new Company(name, isDefault: false));
    }

    [Fact]
    public void Company_name_has_a_length_limit()
    {
        Assert.Throws<ArgumentException>(() => new Company(new string('a', Company.NameMaxLength + 1), isDefault: false));
    }

    [Fact]
    public void Branch_belongs_to_a_company_and_requires_a_name()
    {
        var companyId = Guid.NewGuid();

        var branch = new Branch(companyId, " İstanbul ");

        Assert.Equal(companyId, branch.CompanyId);
        Assert.Equal("İstanbul", branch.Name);
        Assert.Throws<ArgumentException>(() => new Branch(companyId, " "));
    }
}
