using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.FieldRules;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.FieldRules;

/// <summary>
/// Yayınlanmış bir kural paketinin içeriği bozulursa (elle düzenleme, ileride biçim değişikliği) kural değerlendirmesi kapalı kalır:
/// paket <c>block</c> ya da onay kuralı taşıyor olabilir, bu yüzden sessizce atlanmaz. Hata paketin kodunu ve sürümünü söyler, kural içeriğini değil.
/// </summary>
public class CorruptRulePackageTests
{
    private sealed class BrokenRules(CorruptRulePackageException failure) : ITenantFieldRules
    {
        public Task<RuleMergeResult> GetEffectiveAsync(CancellationToken cancellationToken) => throw failure;

        public Task<TenantRuleAdaptationView> GetAdaptationAsync(CancellationToken cancellationToken) => throw failure;

        public Task<TenantRuleAdaptationView> SaveAdaptationAsync(
            IReadOnlyList<FieldRule> rules, IReadOnlyList<string> disabledRuleKeys, long? expectedRevision, CancellationToken cancellationToken) =>
            throw failure;
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("\"metin\"")]
    [InlineData("[{\"yanlis\":")]
    public void An_unreadable_published_list_names_the_package_and_version_and_never_the_content(string json)
    {
        var failure = Assert.Throws<CorruptRulePackageException>(() => FieldRuleJson.DeserializePackage("sektor-lojistik", 3, json));

        Assert.Equal("sektor-lojistik", failure.PackageCode);
        Assert.Equal(3, failure.Version);
        Assert.Contains("'sektor-lojistik'", failure.Message);
        Assert.Contains("sürüm 3", failure.Message);
        Assert.DoesNotContain("metin", failure.Message, StringComparison.Ordinal);
        Assert.IsType<FormatException>(failure.InnerException);
    }

    [Fact]
    public void A_readable_published_list_still_deserializes_including_the_empty_list()
    {
        Assert.Empty(FieldRuleJson.DeserializePackage("bos", 1, "[]"));

        var rules = FieldRuleJson.DeserializePackage("tam", 2, FieldRuleJson.Serialize([new FieldRule("anahtar", Title.Key, FieldRuleEffect.Required, null)]));
        Assert.Equal("anahtar", Assert.Single(rules).Key);
    }

    [Fact]
    public async Task The_write_gate_rejects_instead_of_skipping_when_a_published_package_is_corrupt()
    {
        var services = new ServiceCollection()
            .AddSingleton<ITenantFieldRules>(new BrokenRules(new CorruptRulePackageException("sektor-lojistik", 3)))
            .BuildServiceProvider();
        var gate = new FieldRuleGate(Catalog(), services);

        var errors = await gate.CheckAsync(Entity, null, null, new JsonObject { ["stage"] = "new" }, CancellationToken.None);

        var error = Assert.Single(errors);
        Assert.Equal(FieldRuleGate.CorruptRulesField, error.Field);
        Assert.Contains("sektor-lojistik", error.Message);
    }

    [Fact]
    public async Task A_record_type_without_rules_support_never_reaches_the_rule_service()
    {
        var services = new ServiceCollection()
            .AddSingleton<ITenantFieldRules>(new BrokenRules(new CorruptRulePackageException("sektor-lojistik", 3)))
            .BuildServiceProvider();
        var gate = new FieldRuleGate(Catalog(), services);

        Assert.Empty(await gate.CheckAsync("sales.olmayan-tur", null, null, new JsonObject(), CancellationToken.None));
    }
}
