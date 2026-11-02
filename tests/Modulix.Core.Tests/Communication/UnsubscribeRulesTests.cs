using Modulix.Core.Communication;
using Modulix.Core.Contracts.Communication;

namespace Modulix.Core.Tests.Communication;

/// <summary>ADR-0025 dilim 6: bağlantı belirteci (rastgele, tahmin edilemez, yalnızca karması saklanır) ve kişinin kendi çıkışını geri alma kuralı.</summary>
public class UnsubscribeRulesTests
{
    [Fact]
    public void Tokens_are_long_random_url_safe_and_unique()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => LinkTokens.New()).ToList();

        Assert.Equal(200, tokens.Distinct().Count());
        Assert.All(tokens, t =>
        {
            Assert.True(LinkTokens.LooksValid(t), t);
            Assert.True(t.Length >= 43, t);
            Assert.DoesNotContain('+', t);
            Assert.DoesNotContain('/', t);
            Assert.DoesNotContain('=', t);
        });
    }

    [Fact]
    public void The_hash_is_stable_fixed_length_and_does_not_reveal_the_token()
    {
        var token = LinkTokens.New();

        var hash = LinkTokens.Hash(token);

        Assert.Equal(hash, LinkTokens.Hash(token));
        Assert.NotEqual(hash, LinkTokens.Hash(LinkTokens.New()));
        Assert.Equal(CommunicationLinkToken.HashLength, hash.Length);
        Assert.DoesNotContain(token, hash, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kisa")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqr stuvwxyz")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz'; DROP TABLE x;--")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdef")]
    public void Malformed_tokens_are_rejected_before_any_lookup(string? token) => Assert.False(LinkTokens.LooksValid(token));

    [Fact]
    public void A_token_expires_after_the_configured_lifetime()
    {
        var now = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var token = new CommunicationLinkToken(LinkTokens.Hash(LinkTokens.New()), CommunicationChannel.Email, "a@example.com", "ref", now);

        Assert.False(token.IsExpiredAt(now.AddDays(LinkTokens.ValidDays - 1)));
        Assert.True(token.IsExpiredAt(now.AddDays(LinkTokens.ValidDays)));
    }

    [Theory]
    [InlineData(ConsentStatus.Denied, ConsentSources.Unsubscribe, false, true)]
    [InlineData(ConsentStatus.Denied, ConsentSources.PreferenceCenter, false, true)]
    [InlineData(ConsentStatus.Denied, ConsentSources.Manual, false, false)]
    [InlineData(ConsentStatus.Denied, ConsentSources.Import, false, false)]
    [InlineData(ConsentStatus.Denied, ConsentSources.Provider, false, false)]
    [InlineData(ConsentStatus.Denied, ConsentSources.Unsubscribe, true, false)]
    [InlineData(ConsentStatus.Granted, ConsentSources.Unsubscribe, false, false)]
    [InlineData(ConsentStatus.Unknown, ConsentSources.System, false, false)]
    public void A_person_can_only_undo_their_own_unsubscribe_and_never_through_a_suppression(ConsentStatus current, string source, bool suppressed, bool allowed) =>
        Assert.Equal(allowed, CommunicationRules.SelfResubscribeBlock(current, source, suppressed) is null);

    [Fact]
    public void The_block_message_tells_the_person_what_to_do()
    {
        Assert.Contains("firmayla iletişime", CommunicationRules.SelfResubscribeBlock(ConsentStatus.Denied, ConsentSources.Manual, false));
        Assert.Contains("engellenmiş", CommunicationRules.SelfResubscribeBlock(ConsentStatus.Denied, ConsentSources.Unsubscribe, true));
    }
}
