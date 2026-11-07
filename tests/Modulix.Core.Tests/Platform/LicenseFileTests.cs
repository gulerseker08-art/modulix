using System.Text;
using System.Text.Json.Nodes;
using Modulix.Core.Platform;

namespace Modulix.Core.Tests.Platform;

/// <summary>ADR-0028 Dilim 7b: imzalı lisans dosyası biçimi, imza ve içerik kuralları.</summary>
public class LicenseFileTests
{
    private static readonly DateTimeOffset Issued = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static LicensePayload Payload(string tenant = "acme", string[]? modules = null, DateTimeOffset? validUntil = null) => new(
        "LIC-001", tenant, "Acme A.Ş.", modules ?? ["crm", "support"], new Dictionary<string, int> { ["seats"] = 25 }, Issued, validUntil ?? Issued.AddYears(1));

    [Fact]
    public void A_signed_license_verifies_and_returns_its_content()
    {
        var (privateKey, publicKey) = LicenseFile.GenerateKeyPair();

        var payload = LicenseFile.Verify(LicenseFile.Sign(Payload(), privateKey), publicKey);

        Assert.Equal("LIC-001", payload.LicenseId);
        Assert.Equal("acme", payload.TenantSlug);
        Assert.Equal(["crm", "support"], payload.Modules);
        Assert.Equal(25, payload.Limits!["seats"]);
        Assert.Equal(Issued.AddYears(1), payload.ValidUntil);
    }

    [Fact]
    public void A_license_signed_with_another_key_is_rejected()
    {
        var (otherPrivateKey, _) = LicenseFile.GenerateKeyPair();
        var (_, publicKey) = LicenseFile.GenerateKeyPair();

        var ex = Assert.Throws<LicenseException>(() => LicenseFile.Verify(LicenseFile.Sign(Payload(), otherPrivateKey), publicKey));

        Assert.Contains("imza", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Editing_the_content_after_signing_breaks_the_signature()
    {
        var (privateKey, publicKey) = LicenseFile.GenerateKeyPair();
        var file = JsonNode.Parse(LicenseFile.Sign(Payload(), privateKey))!.AsObject();

        // Müşteri bitişi uzatmaya çalışır: içerik değişir, imza aynı kalır.
        var edited = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(file["payload"]!.GetValue<string>())))!.AsObject();
        edited["validUntil"] = Issued.AddYears(10).ToString("O");
        file["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(edited.ToJsonString()));

        Assert.Throws<LicenseException>(() => LicenseFile.Verify(file.ToJsonString(), publicKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bu bir json degil")]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"payload\":\"%%%\",\"signature\":\"%%%\"}")]
    public void A_malformed_file_is_rejected(string text)
    {
        var (_, publicKey) = LicenseFile.GenerateKeyPair();

        Assert.Throws<LicenseException>(() => LicenseFile.Verify(text, publicKey));
    }

    [Fact]
    public void An_unsupported_version_is_rejected()
    {
        var (privateKey, publicKey) = LicenseFile.GenerateKeyPair();
        var file = JsonNode.Parse(LicenseFile.Sign(Payload(), privateKey))!.AsObject();
        file["version"] = 99;

        var ex = Assert.Throws<LicenseException>(() => LicenseFile.Verify(file.ToJsonString(), publicKey));

        Assert.Contains("sürüm", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("anahtar-degil")]
    public void A_missing_or_invalid_public_key_is_rejected(string publicKey)
    {
        var (privateKey, _) = LicenseFile.GenerateKeyPair();

        Assert.Throws<LicenseException>(() => LicenseFile.Verify(LicenseFile.Sign(Payload(), privateKey), publicKey));
    }

    [Fact]
    public void Content_rules_are_checked_when_signing()
    {
        var (privateKey, _) = LicenseFile.GenerateKeyPair();

        Assert.Throws<LicenseException>(() => LicenseFile.Sign(Payload(tenant: " "), privateKey));
        Assert.Throws<LicenseException>(() => LicenseFile.Sign(Payload(modules: []), privateKey));
        Assert.Throws<LicenseException>(() => LicenseFile.Sign(Payload(validUntil: Issued.AddDays(-1)), privateKey));
        Assert.Throws<LicenseException>(() => LicenseFile.Sign(Payload(), "özel anahtar değil"));
    }

    [Theory]
    [InlineData("acme", "lisans-acme")]
    [InlineData("ACME ", "lisans-acme")]
    public void The_plan_key_follows_the_tenant_slug(string slug, string expected) => Assert.Equal(expected, LicenseFile.PlanKeyFor(slug));

    [Fact]
    public void A_long_slug_gets_a_valid_distinct_plan_key()
    {
        var first = LicenseFile.PlanKeyFor(new string('a', 49) + "1");
        var second = LicenseFile.PlanKeyFor(new string('a', 49) + "2");

        Assert.Equal(Plan.KeyMaxLength, first.Length);
        Assert.NotEqual(first, second);
        Assert.All(first, c => Assert.True(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'));
    }
}
