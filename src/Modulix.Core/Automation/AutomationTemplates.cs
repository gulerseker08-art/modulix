using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Modules;

namespace Modulix.Core.Automation;

/// <summary>
/// Firmaya sunulan başlangıç şablonu. <see cref="Issues"/> şablonun bu firmada ve bu kullanıcı için neden kullanılamadığını söyler
/// (modül kapalı, kayıt türü ya da aksiyon izni yok...); boşsa <see cref="Available"/> doğrudur.
/// </summary>
public sealed record AutomationTemplateView(
    string Key,
    string ModuleKey,
    string EntityKey,
    string DisplayName,
    string Description,
    bool Available,
    IReadOnlyList<AutomationIssue> Issues);

/// <summary>
/// Modüllerin beyan ettiği başlangıç şablonları (ADR-0027, Dilim 3b). Şablon kullanmak <b>taslak</b> kural açar; taslak hiçbir
/// koşulda çalışmaz, firma gözden geçirip kendisi yayınlar. Listeleme <c>core.automation.read</c>, kullanma <c>core.automation.manage</c>
/// ister (API denetler); kural oluşturma yolu <see cref="IAutomationRuleService.CreateAsync"/> olduğundan denetim kaydı orada yazılır.
/// </summary>
public interface IAutomationTemplates
{
    /// <summary>Modülü bu firmada açık olan şablonlar; kullanılamayanlar nedeniyle birlikte döner.</summary>
    Task<IReadOnlyList<AutomationTemplateView>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Şablondan yeni taslak kural açar. Şablon bilinmiyorsa ya da modülü kapalıysa <c>null</c>; kullanıcı için kullanılamıyorsa
    /// (<see cref="AutomationTemplateView.Issues"/>) <see cref="AutomationRuleException"/>.
    /// </summary>
    Task<AutomationRuleDetail?> UseAsync(string templateKey, CancellationToken cancellationToken);
}

public sealed class AutomationTemplateService(
    ModuleCatalog modules,
    ICurrentAccess access,
    AutomationDefinitionValidator validator,
    IAutomationRuleService rules) : IAutomationTemplates
{
    public async Task<IReadOnlyList<AutomationTemplateView>> ListAsync(CancellationToken cancellationToken)
    {
        var views = new List<AutomationTemplateView>();
        foreach (var (moduleKey, template) in modules.AutomationTemplates)
        {
            if (await ViewAsync(moduleKey, template, cancellationToken) is { } view)
            {
                views.Add(view);
            }
        }

        return views;
    }

    public async Task<AutomationRuleDetail?> UseAsync(string templateKey, CancellationToken cancellationToken)
    {
        if (modules.FindAutomationTemplate(templateKey) is not { } found
            || await ViewAsync(found.ModuleKey, found.Template, cancellationToken) is not { } view)
        {
            return null;
        }

        if (!view.Available)
        {
            throw new AutomationRuleException($"Şablon kullanılamıyor: {view.Issues[0].Message}");
        }

        var template = found.Template;
        var content = new AutomationVersionContent(
            template.DisplayName,
            template.Description,
            AutomationRuleVersion.DefaultPriority,
            StopProcessing: false,
            ActiveFrom: null,
            ActiveUntil: null,
            Clone(template.Definition));
        return await rules.CreateAsync(found.ModuleKey, template.EntityKey, content, cancellationToken);
    }

    private async Task<AutomationTemplateView?> ViewAsync(string moduleKey, AutomationTemplateDefinition template, CancellationToken cancellationToken)
    {
        if (!await access.IsModuleEnabledAsync(moduleKey, cancellationToken))
        {
            return null;
        }

        var issues = await validator.ValidateAsync(moduleKey, template.EntityKey, template.Definition, cancellationToken);

        // Şablonun firmaya bıraktığı zorunlu parametreler (ör. bildirimin alıcısı) kullanmaya engel değildir: taslakta sorun olarak görünür
        // ve doldurulmadan yayınlanamaz. Katalog bu parametrelerin şablonda hiç verilmediğini açılışta doğrular; o yolda başka sorun çıkmaz.
        if (template.ToComplete.Count > 0)
        {
            var open = BlankPaths(template);
            issues = [.. issues.Where(i => !open.Contains(i.Path))];
        }

        return new AutomationTemplateView(
            template.Key, moduleKey, template.EntityKey, template.DisplayName, template.Description, issues.Count == 0, issues);
    }

    /// <summary>Firmaya bırakılan parametrelerin doğrulama yolları (<c>actions[i].parameters.ad</c>; doğrulayıcının yol biçimi).</summary>
    public static IReadOnlySet<string> BlankPaths(AutomationTemplateDefinition template)
    {
        var steps = template.Definition.Actions;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var blank in template.ToComplete)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i].Id == blank.StepId)
                {
                    paths.Add($"actions[{i}].parameters.{blank.Parameter}");
                }
            }
        }

        return paths;
    }

    // Beyandaki JsonObject düğümleri paylaşılan sabittir; kurala kopyalanırken yeni ağaç kurulur.
    private static AutomationDefinition Clone(AutomationDefinition definition) =>
        AutomationJson.Deserialize(AutomationJson.Serialize(definition));
}
