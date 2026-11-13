using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Imports;
using Modulix.Core.Modules;

namespace Modulix.Core.Tests.Imports;

/// <summary>ADR-0033 Dilim 3: tasarım kuralları ve içe aktarma tanımının açılış doğrulaması.</summary>
public class ImportDesignRulesTests
{
    private static readonly ImportField Name = new("name", "Ad", Required: true);
    private static readonly ImportField City = new("city", "Şehir");
    private static readonly ImportField Notes = new("notes", "Notlar");

    [Fact]
    public void Default_design_keeps_every_column_as_defined() =>
        Assert.Equal([Name, City, Notes], ImportDesignRules.Apply([Name, City, Notes], ImportDesignInput.Default));

    [Fact]
    public void Design_removes_columns_and_adds_requirement()
    {
        var fields = ImportDesignRules.Apply([Name, City, Notes], new ImportDesignInput(true, false, ["notes"], ["city"]));

        Assert.Equal(["name", "city"], fields.Select(f => f.Key));
        Assert.True(fields[1].Required);
    }

    [Fact]
    public void A_code_required_column_survives_even_a_stored_exclusion() =>
        Assert.Contains(ImportDesignRules.Apply([Name], new ImportDesignInput(true, false, ["name"], [])), f => f.Key == "name");

    // ADR-0033 Dilim 4c: kayıtlı sütun eşlemesi.
    [Fact]
    public void Saved_aliases_bind_file_headers_to_their_column_but_never_steal_another_columns_name()
    {
        var aliases = new Dictionary<string, IReadOnlyList<string>> { ["city"] = ["İL", "Bulunduğu şehir"] };
        var fields = ImportDesignRules.Apply([Name, City, Notes], new ImportDesignInput(true, false, [], [], aliases));

        Assert.Equal(["İL", "Bulunduğu şehir"], fields[1].Aliases);
        var map = ImportColumns.Bind(["ad", "il", "Notlar"], fields);
        Assert.Equal(["name", "city", "notes"], map.Columns.Select(c => c.Field.Key));
        Assert.Empty(map.Unknown);
    }

    [Fact]
    public void Alias_validation_rejects_unknown_columns_taken_names_shared_aliases_and_limits()
    {
        IReadOnlyList<Modulix.Core.Contracts.Records.RecordFieldError> Errors(Dictionary<string, IReadOnlyList<string>> aliases) =>
            ImportDesignRules.Validate([Name, City, Notes], new ImportDesignInput(true, false, [], [], aliases));

        Assert.Empty(Errors(new() { ["city"] = ["İl", "Şehir"] })); // kendi adı tekrar yazılabilir
        Assert.Equal("yok", Assert.Single(Errors(new() { ["yok"] = ["X"] })).Field);
        Assert.Contains("başka bir sütunun adı", Assert.Single(Errors(new() { ["city"] = ["notlar"] })).Message);
        Assert.Contains("hata raporunun", Assert.Single(Errors(new() { ["city"] = ["Hata"] })).Message);
        Assert.Contains("birden çok sütuna", Assert.Single(Errors(new() { ["city"] = ["Bölge"], ["notes"] = ["BÖLGE"] })).Message);
        Assert.Single(Errors(new() { ["city"] = [.. Enumerable.Range(1, ImportDesignRules.MaxAliasesPerField + 1).Select(i => $"Ad {i}")] }));
        Assert.Single(Errors(new() { ["city"] = [new string('x', ImportDesignRules.MaxAliasLength + 1)] }));
    }

    [Fact]
    public void Aliases_are_trimmed_deduplicated_and_empty_entries_dropped()
    {
        var normalized = ImportDesignRules.NormalizeAliases(new Dictionary<string, IReadOnlyList<string>>
        {
            ["city"] = ["  İl ", "il", "", "Şehir adı"],
            ["notes"] = ["  "],
        });

        Assert.Equal(["İl", "Şehir adı"], Assert.Single(normalized!).Value);
        Assert.Null(ImportDesignRules.NormalizeAliases(new Dictionary<string, IReadOnlyList<string>> { ["notes"] = [""] }));
    }

    [Fact]
    public void Validation_rejects_unknown_locked_and_contradictory_columns()
    {
        Assert.Empty(ImportDesignRules.Validate([Name, City], new ImportDesignInput(true, true, ["city"], [])));
        Assert.Equal("yok", Assert.Single(ImportDesignRules.Validate([Name], new ImportDesignInput(true, false, ["yok"], []))).Field);
        Assert.Equal("name", Assert.Single(ImportDesignRules.Validate([Name], new ImportDesignInput(true, false, ["name"], []))).Field);
        Assert.Equal("city", Assert.Single(ImportDesignRules.Validate([City], new ImportDesignInput(true, false, ["city"], ["city"]))).Field);
    }

    [Theory]
    [InlineData("bilinmeyen")]
    [InlineData("baglanti")]
    public void Catalog_rejects_invalid_import_definitions(string kind)
    {
        ImportDefinition definition = kind == "bilinmeyen"
            ? new("demo.kayit", "Kayıt", "demo.kayit.create", [new("name", "Ad")]) { MatchRules = [new("yok", "yok")] }
            : new("demo.kayit", "Kayıt", "demo.kayit.create", [new("firma", "Firma", ImportFieldType.Reference)]);

        Assert.Throws<InvalidOperationException>(() => new ModuleCatalog([new CoreModule(), new DemoModule(definition)]));
    }

    private sealed class DemoModule(ImportDefinition definition) : IModule
    {
        public string Key => "demo";

        public string DisplayName => "Demo";

        public IReadOnlyList<PermissionDefinition> Permissions { get; } = [new("demo.kayit.create", "Kayıt oluşturur")];

        public IReadOnlyList<ImportDefinition> Imports { get; } = [definition];

        public void ConfigureServices(IServiceCollection services)
        {
        }
    }
}
