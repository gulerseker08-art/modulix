using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

/// <summary>
/// Kayıt türünün etiket alanı (ADR-0027, Dilim 5d): kayıt seçici kaydı bu alanla tanıtır. Alan beyan edilmiş bir metin alanı olmalı;
/// kullanıcı alanı göremiyorsa katalog etiket alanını vermez (alan yetkisi atlanmaz).
/// </summary>
public class AutomationLabelFieldTests
{
    private static readonly AutomationFieldDefinition SecretNote =
        new(Entity, "secretNote", "Gizli not", AutomationFieldType.Text) { ReadPermission = SecretPermission };

    private static AutomationCapabilities With(string? labelField, params AutomationFieldDefinition[] fields) => new()
    {
        Entities = [new(Entity, "Fırsat", "sales.deals.read") { LabelField = labelField }],
        Fields = fields,
    };

    private static IReadOnlyList<AutomationEntityView> Entities(AutomationCapabilities capabilities, params string[] permissions)
    {
        var modules = Catalog(capabilities);
        var access = new FakeAccess(permissions, ["sales", "billing"]);
        return CatalogService(modules, access).GetAsync(CancellationToken.None).GetAwaiter().GetResult().Entities;
    }

    [Fact]
    public void A_declared_text_field_becomes_the_label_the_catalog_exposes()
    {
        var entity = Assert.Single(Entities(With("title", Title, Score), "sales.deals.read"));

        Assert.Equal("title", entity.LabelField);
    }

    [Fact]
    public void An_entity_without_a_label_field_exposes_none()
    {
        Assert.Null(Assert.Single(Entities(With(null, Title), "sales.deals.read")).LabelField);
    }

    [Fact]
    public void The_label_field_must_be_a_declared_text_field_of_the_entity()
    {
        Assert.Throws<InvalidOperationException>(() => Catalog(With("nope", Title)));
        Assert.Throws<InvalidOperationException>(() => Catalog(With("score", Title, Score)));
        Assert.Throws<InvalidOperationException>(() => Catalog(With("title", Score)));
    }

    [Fact]
    public void A_label_field_the_user_may_not_read_is_not_exposed()
    {
        var capabilities = With("secretNote", Title, SecretNote);

        Assert.Equal("secretNote", Assert.Single(Entities(capabilities, "sales.deals.read", SecretPermission)).LabelField);
        Assert.Null(Assert.Single(Entities(capabilities, "sales.deals.read")).LabelField);
    }
}
