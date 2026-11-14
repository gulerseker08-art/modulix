using System.Text.Json.Nodes;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using static Modulix.Core.Tests.Automation.AutomationKit;

namespace Modulix.Core.Tests.Automation;

/// <summary>
/// Şablonun firmaya bıraktığı parametre (ADR-0027 Dilim 3b eki): yalnızca beyan edilen eksik parametre şablonu kullanılabilir bırakır; başka
/// eksik, izin ya da modül sorunu şablonu yine "kullanılamıyor" yapar (yetki yükseltme yok).
/// </summary>
public class AutomationTemplateServiceTests
{
    private const string Key = "sales.deal-follow-up";

    private static AutomationTemplateDefinition Template(JsonObject parameters, params string[] blanks) =>
        new(Key, "Takip", "Açıklama", Entity, new AutomationDefinition(new ManualTrigger(), null, [new AutomationActionStep("a", FollowUp.Key, parameters)]))
        {
            ToComplete = [.. blanks.Select(b => new AutomationTemplateBlank("a", b))],
        };

    private static async Task<AutomationTemplateView?> ViewAsync(AutomationTemplateDefinition template, string[]? permissions = null, string[]? modules = null)
    {
        var catalog = Catalog(new AutomationCapabilities
        {
            Entities = Capabilities.Entities,
            Fields = Capabilities.Fields,
            Actions = Capabilities.Actions,
            Templates = [template],
        });
        var access = new FakeAccess(permissions ?? ["sales.deals.read", WritePermission], modules ?? ["sales"]);

        // Listeleme kural servisini kullanmaz.
        var service = new AutomationTemplateService(catalog, access, Validator(catalog, access), null!);
        return (await service.ListAsync(CancellationToken.None)).SingleOrDefault();
    }

    [Fact]
    public async Task A_declared_blank_keeps_the_template_available()
    {
        var view = await ViewAsync(Template(new JsonObject { ["dueInDays"] = 1 }, "subject"));

        Assert.True(view!.Available);
        Assert.Empty(view.Issues);
    }

    [Fact]
    public async Task An_undeclared_missing_parameter_still_makes_the_template_unavailable()
    {
        var undeclared = await ViewAsync(Template(new JsonObject { ["dueInDays"] = 1 }));
        Assert.False(undeclared!.Available);
        Assert.Equal(["actions[0].parameters.subject"], undeclared.Issues.Select(i => i.Path));

        // Beyan edilen boşluk yalnızca kendi yolunu kapatır; yanındaki eksik parametre görünür kalır.
        var partly = await ViewAsync(Template(new JsonObject(), "subject"));
        Assert.False(partly!.Available);
        Assert.Equal(["actions[0].parameters.dueInDays"], partly.Issues.Select(i => i.Path));
    }

    [Fact]
    public async Task A_blank_does_not_hide_missing_permissions_or_a_disabled_module()
    {
        var template = Template(new JsonObject { ["dueInDays"] = 1 }, "subject");

        // Aksiyonun yazma izni yok: şablon kullanılamaz.
        var noWrite = await ViewAsync(template, permissions: ["sales.deals.read"]);
        Assert.False(noWrite!.Available);
        Assert.NotEmpty(noWrite.Issues);

        // Kayıt türünü okuma izni yok: şablon kullanılamaz.
        var noRead = await ViewAsync(template, permissions: [WritePermission]);
        Assert.False(noRead!.Available);

        // Modül kapalı: şablon hiç listelenmez.
        Assert.Null(await ViewAsync(template, modules: []));
    }

    [Fact]
    public void Blank_paths_follow_the_validators_parameter_path()
    {
        var template = Template(new JsonObject { ["dueInDays"] = 1 }, "subject");

        Assert.Equal(["actions[0].parameters.subject"], AutomationTemplateService.BlankPaths(template));
    }
}
