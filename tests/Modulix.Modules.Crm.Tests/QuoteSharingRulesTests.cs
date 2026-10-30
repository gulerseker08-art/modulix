using Modulix.Modules.Crm.Quotes;

namespace Modulix.Modules.Crm.Tests;

/// <summary>Paylaşım kuralı ve belirteç yardımcıları (veritabanı gerektirmez).</summary>
public class QuoteSharingRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(QuoteStatus.Presented, true)]
    [InlineData(QuoteStatus.Draft, false)]
    [InlineData(QuoteStatus.Accepted, false)]
    [InlineData(QuoteStatus.Rejected, false)]
    [InlineData(QuoteStatus.Cancelled, false)]
    [InlineData(QuoteStatus.Superseded, false)]
    [InlineData(QuoteStatus.Expired, false)]
    public void Only_a_presented_quote_can_be_shared(string status, bool allowed) =>
        Assert.Equal(allowed, QuoteRules.Can(QuoteAction.Share, status, QuoteApprovalStatus.NotRequired, Now.AddDays(5), Now, true).Allowed);

    [Fact]
    public void A_presented_quote_past_its_validity_date_cannot_be_shared()
    {
        var result = QuoteRules.Can(QuoteAction.Share, QuoteStatus.Presented, QuoteApprovalStatus.NotRequired, Now.AddSeconds(-1), Now, true);
        Assert.False(result.Allowed);
        Assert.Equal("expired", result.Code);
    }

    [Fact]
    public void Tokens_are_long_unique_url_safe_and_only_their_hash_is_comparable()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => QuoteTokens.New()).ToList();
        Assert.Equal(200, tokens.Distinct().Count());
        Assert.All(tokens, t =>
        {
            Assert.Equal(43, t.Length);
            Assert.True(QuoteTokens.LooksValid(t));
            Assert.DoesNotContain(t, QuoteTokens.Hash(t));
            Assert.Equal(64, QuoteTokens.Hash(t).Length);
        });
        Assert.Equal(QuoteTokens.Hash(tokens[0]), QuoteTokens.Hash(tokens[0]));
        Assert.NotEqual(QuoteTokens.Hash(tokens[0]), QuoteTokens.Hash(tokens[1]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kisa")]
    [InlineData("abc def abc def abc def abc def abc def abc def")]
    [InlineData("../../../../etc/passwd/../../../../etc/passwd")]
    public void Malformed_tokens_are_rejected_before_any_lookup(string? token) => Assert.False(QuoteTokens.LooksValid(token));
}
