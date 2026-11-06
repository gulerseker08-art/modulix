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
