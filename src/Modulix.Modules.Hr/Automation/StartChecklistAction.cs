using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Hr.Records;

namespace Modulix.Modules.Hr.Automation;

/// <summary>
/// "İşe giriş/çıkış süreci başlat" aksiyonu (<see cref="HrAutomation.StartChecklist"/>, ADR-0042 bölüm 18). Hedef bir çalışan kaydıdır. Süreç
/// <see cref="IChecklistWorkflow"/> ile, yani elle başlatmayla aynı kurallar ve kayıt boru hattıyla açılır (şablon etkin ve adımlı olmalı, ayrılmış
/// çalışana işe giriş başlamaz, aynı türde açık süreç tekildir). Şablon verilmezse türün ada göre ilk etkin şablonu kullanılır; başlangıç günü
/// verilmezse tenant'ın bugünü. Çalışanın aynı türde açık süreci zaten varsa adım atlanır (ikinci süreç açılmaz). Dry-run yazmaz.
/// </summary>
internal sealed class StartChecklistAction(IChecklistWorkflow workflow, IRecordPipeline pipeline, ITenantClock clock) : IAutomationAction
{
    public async Task<AutomationActionResult> ExecuteAsync(AutomationActionRequest request, AutomationCallContext context, CancellationToken cancellationToken)
    {
        if (request.Target is not { EntityKey: HrModule.Entities.Employee } target)
        {
            return Failed("Hedef bir çalışan kaydı değil.");
        }

        var kind = Text(request.Parameters["kind"]);
        if (kind is not (Domain.Checklists.Kinds.Onboarding or Domain.Checklists.Kinds.Offboarding))
        {
            return Failed("Süreç türü işe giriş ya da işten çıkış olmalı.");
        }

        var templateId = Guid.TryParse(Text(request.Parameters["templateId"]), out var given) && given != Guid.Empty ? given : await FirstActiveTemplateAsync(kind, cancellationToken);
        if (templateId is not { } template)
        {
            return Failed(kind == Domain.Checklists.Kinds.Onboarding ? "Etkin işe giriş şablonu yok." : "Etkin işten çıkış şablonu yok.");
        }

        var startDate = DateOnly.TryParse(Text(request.Parameters["startDate"]), CultureInfo.InvariantCulture, out var day)
            ? day
            : (await clock.GetAsync(cancellationToken)).Today;
        // Aynı türde açık süreç varsa ikinci kez başlatılmaz (kural yeniden çalışsa da tek süreç).
        if (await HasOpenChecklistAsync(target.Id, kind, cancellationToken))
        {
            return new AutomationActionResult(AutomationActionStatus.Skipped, "Çalışanın bu türde açık süreci zaten var.");
        }

        if (request.DryRun)
        {
            return new AutomationActionResult(AutomationActionStatus.Planned,
                $"Süreç başlatılacak (başlangıç {startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}).");
        }

        var result = await workflow.StartAsync(target.Id, template, startDate, cancellationToken);
        if (result.IsSuccess)
        {
            return new AutomationActionResult(AutomationActionStatus.Succeeded, "Süreç başlatıldı.")
            {
                Effect = new RecordRef(HrModule.Entities.EmployeeChecklist, result.Value!.Id),
            };
        }

        return Failed(result.Error!.FieldErrors is { Count: > 0 } errors ? string.Join(" ", errors.Select(e => e.Message)) : result.Error.Message);
    }

    private async Task<bool> HasOpenChecklistAsync(Guid employeeId, string kind, CancellationToken cancellationToken)
    {
        var page = await pipeline.QueryAsync(new RecordQuery(HrModule.Entities.EmployeeChecklist, 1, 1, null, new Dictionary<string, string>
        {
            ["employeeId"] = employeeId.ToString(),
            ["kind"] = kind,
            ["status"] = Domain.EmployeeChecklist.Statuses.Open,
        }), cancellationToken);
        return page.IsSuccess && page.Value!.Items.Count > 0;
    }

    private async Task<Guid?> FirstActiveTemplateAsync(string kind, CancellationToken cancellationToken)
    {
        var page = await pipeline.QueryAsync(new RecordQuery(HrModule.Entities.ChecklistTemplate, 1, 1, null, new Dictionary<string, string>
        {
            ["kind"] = kind,
            ["isActive"] = "true",
            ["sort"] = "name",
        }), cancellationToken);
        return page.IsSuccess && page.Value!.Items.Count > 0 ? page.Value.Items[0].Id : null;
    }

    private static AutomationActionResult Failed(string summary) => new(AutomationActionStatus.Failed, summary);

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? (string.IsNullOrWhiteSpace(text) ? null : text) : node?.ToString();
}
