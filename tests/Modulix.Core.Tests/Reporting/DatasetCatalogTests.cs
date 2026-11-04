using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Modules;
using static Modulix.Core.Tests.Reporting.ReportingKit;

namespace Modulix.Core.Tests.Reporting;

public sealed class DatasetCatalogTests
{
    private const ColumnUse All = ColumnUse.Group | ColumnUse.Filter | ColumnUse.Show;

    private sealed class SalesModule(ModuleReporting reporting, IReadOnlyList<CustomizableEntity>? customizable = null) : IModule
    {
        public string Key => "sales";

        public string DisplayName => "Satış";

        public IReadOnlyList<PermissionDefinition> Permissions { get; } = [new("sales.reports.read", "Satış raporları")];

        public IReadOnlyList<CustomizableEntity> CustomizableEntities => customizable ?? [new(Entity, "Anlaşma")];

        public IReadOnlyList<LookupDefinition> Lookups { get; } = [new("sales.deal.owner", Entity, "ownerId", "Sorumlu", LookupTargetKind.Member)];

        public ModuleReporting Reports => reporting;
    }

    private static ModuleCatalog Catalog(ModuleReporting reporting, IReadOnlyList<CustomizableEntity>? customizable = null) =>
        new([new CoreModule(), new SalesModule(reporting, customizable)]);

    private static ModuleReporting With(DatasetDefinition dataset, params ReportLink[] links) => new([dataset], links);

    [Fact]
    public void Dataset_is_found_by_key_and_listed_with_its_module()
    {
        var catalog = Catalog(With(Dataset));

        var (moduleKey, dataset) = catalog.FindDataset("sales.deals")!.Value;

        Assert.Equal("sales", moduleKey);
        Assert.Equal(2, dataset.Version);
        Assert.Single(catalog.Datasets);
        Assert.Null(catalog.FindDataset("sales.nope"));
    }

    [Fact]
    public void Modules_without_reports_contribute_nothing()
    {
        Assert.Empty(new ModuleCatalog([new CoreModule()]).Datasets);
    }

    [Theory]
    [InlineData("crm.deals")]
    [InlineData("deals")]
    public void Dataset_key_must_start_with_the_module_key(string key)
    {
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { Key = key })));
    }

    [Fact]
    public void Dataset_entity_must_belong_to_the_module()
    {
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { EntityKey = "crm.opportunity" })));
    }

    [Fact]
    public void Duplicate_dataset_key_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Catalog(new ModuleReporting([Dataset, Dataset], [])));
    }

    [Fact]
    public void Custom_fields_need_a_customizable_entity()
    {
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset), customizable: []));
    }

    [Fact]
    public void Money_column_needs_a_currency_field()
    {
        var bad = Dataset with { Columns = [new DatasetColumn("amount", "Tutar", DatasetColumnKind.Money, ColumnUse.Measure | ColumnUse.Show)] };

        Assert.Throws<InvalidOperationException>(() => Catalog(With(bad)));
    }

    [Fact]
    public void Only_numbers_and_money_can_be_measures_and_only_non_numeric_columns_grouped()
    {
        var measureText = new DatasetColumn("title", "Başlık", DatasetColumnKind.Text, ColumnUse.Measure | ColumnUse.Show);
        var groupNumber = new DatasetColumn("score", "Puan", DatasetColumnKind.Number, ColumnUse.Group | ColumnUse.Show);

        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { Columns = [measureText] })));
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { Columns = [groupNumber] })));
    }

    [Fact]
    public void Choice_needs_a_list_or_options_and_lookup_needs_a_known_lookup()
    {
        var choice = new DatasetColumn("stage", "Aşama", DatasetColumnKind.Choice, All);
        var lookup = new DatasetColumn("ownerId", "Sorumlu", DatasetColumnKind.Lookup, All) { LookupKey = "sales.unknown" };
        var missingList = new DatasetColumn("stage", "Aşama", DatasetColumnKind.Choice, All) { ChoiceList = "sales.unknown-list" };

        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { Columns = [choice] })));
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { Columns = [lookup] })));
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { Columns = [missingList] })));
    }

    [Fact]
    public void Duplicate_empty_or_unused_columns_are_rejected()
    {
        var title = new DatasetColumn("title", "Başlık", DatasetColumnKind.Text, ColumnUse.Show);

        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { Columns = [title, title] })));
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { Columns = [title with { Label = "" }] })));
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { Columns = [title with { Use = ColumnUse.None }] })));
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { Columns = [title with { Field = "custom.x" }] })));
    }

    [Fact]
    public void Default_columns_must_be_showable_columns_of_the_dataset()
    {
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { DefaultColumns = ["nope"] })));
    }

    [Fact]
    public void A_ready_report_is_validated_against_the_columns_at_startup()
    {
        var good = new ReadyReport("sales.deals.by-stage", "Aşamaya göre", "", Spec(groups: [new("stage")], measures: [new(null, Aggregation.Count)], chart: ChartKind.Bar));
        var bad = new ReadyReport("sales.deals.broken", "Bozuk", "", Spec(groups: [new("nope")]));

        Assert.Single(Catalog(With(Dataset with { ReadyReports = [good] })).FindDataset("sales.deals")!.Value.Dataset.ReadyReports);
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { ReadyReports = [bad] })));
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { ReadyReports = [good, good] })));
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset with { ReadyReports = [good with { Key = "crm.x" }] })));
    }

    [Fact]
    public void A_report_link_needs_a_prefixed_key_and_a_known_permission()
    {
        var link = new ReportLink("sales.reports.overview", "Genel görünüm", "", "sales/ozet", "sales.reports.read");

        Assert.Single(Catalog(With(Dataset, link)).ReportLinks);
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset, link with { Key = "crm.reports.x" })));
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset, link with { Permission = "sales.nope.read" })));
        Assert.Throws<InvalidOperationException>(() => Catalog(With(Dataset, link, link)));
    }

    [Fact]
    public void Hidden_fields_follow_the_sensitive_field_declaration_of_the_entity()
    {
        var module = new SensitiveSalesModule();
        var catalog = new ModuleCatalog([new CoreModule(), module]);

        Assert.Equivalent(new[] { "amount" }, catalog.HiddenFields(Entity, new HashSet<string>()));
        Assert.Empty(catalog.HiddenFields(Entity, new HashSet<string> { "sales.amounts.read" }));
    }

    private sealed class SensitiveSalesModule : IModule
    {
        public string Key => "sales";

        public string DisplayName => "Satış";

        public IReadOnlyList<PermissionDefinition> Permissions { get; } = [new("sales.amounts.read", "Tutarlar")];

        public IReadOnlyList<SensitiveFields> SensitiveFields { get; } = [new(Entity, "sales.amounts.read", ["amount"])];

        public IReadOnlyList<CustomizableEntity> CustomizableEntities => [new(Entity, "Anlaşma")];

        public IReadOnlyList<LookupDefinition> Lookups { get; } = [new("sales.deal.owner", Entity, "ownerId", "Sorumlu", LookupTargetKind.Member)];

        public ModuleReporting Reports => With(Dataset);
    }
}
