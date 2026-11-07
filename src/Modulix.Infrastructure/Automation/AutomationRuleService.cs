using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Automation;

/// <summary>
/// Kural yönetimi (ADR-0027, Dilim 1). Kurallar ve sürümleri tenant verisidir (query filter + RLS); her işlem aktif tenant'ta
/// çalışır ve <c>core.automation-rule</c> kaydı olarak denetime yazılır. Yayınlanmış sürüm değişmez. Bu sınıf kuralı çalıştırmaz.
/// </summary>
internal sealed class AutomationRuleService(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    IAuditWriter audit,
    ModuleCatalog modules,
    IAutomationCatalog catalog,
    AutomationDefinitionValidator validator,
    IOptions<AutomationOptions> automationOptions,
    TimeProvider time) : IAutomationRuleService
{
    public async Task<AutomationRulePage> ListAsync(AutomationRuleQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, AutomationRuleQuery.MaxPageSize);
        var rules = db.AutomationRules.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.ModuleKey))
        {
            rules = rules.Where(r => r.ModuleKey == query.ModuleKey);
        }

        if (query.Status is { } status)
        {
            rules = rules.Where(r => r.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            rules = rules.Where(r => r.Name.Contains(search));
        }

        var total = await rules.CountAsync(cancellationToken);
        var items = await Sorted(rules, query.Sort).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var ids = items.Select(r => r.Id).ToList();
        var versions = await db.AutomationRuleVersions.AsNoTracking()
            .Where(v => ids.Contains(v.RuleId))
            .Select(v => new { v.Id, v.RuleId, v.VersionNo, v.State, v.Priority })
            .ToListAsync(cancellationToken);

        var summaries = items.Select(rule =>
        {
            var own = versions.Where(v => v.RuleId == rule.Id).ToList();
            var published = own.FirstOrDefault(v => v.Id == rule.PublishedVersionId);
            var draft = own.FirstOrDefault(v => v.State == AutomationVersionState.Draft);
            return new AutomationRuleSummary(
                rule.Id, rule.Name, rule.ModuleKey, rule.EntityKey, rule.Status, published?.VersionNo, draft is not null,
                (published ?? draft)?.Priority ?? AutomationRuleVersion.DefaultPriority, rule.UpdatedAt, rule.Version);
        }).ToList();
        return new AutomationRulePage(summaries, page, pageSize, total);
    }

    // Sıralama anahtarı uç noktada doğrulanır; bilinmeyen anahtar varsayılan sıraya düşer (son güncellenen önce).
    private static IOrderedQueryable<AutomationRule> Sorted(IQueryable<AutomationRule> rules, string? sort)
    {
        var descending = sort is { Length: > 1 } && sort[0] == '-';
        return (sort?.TrimStart('-'), descending) switch
        {
            ("name", false) => rules.OrderBy(r => r.Name),
            ("name", true) => rules.OrderByDescending(r => r.Name),
            ("status", false) => rules.OrderBy(r => r.Status),
            ("status", true) => rules.OrderByDescending(r => r.Status),
            ("updatedAt", false) => rules.OrderBy(r => r.UpdatedAt),
            _ => rules.OrderByDescending(r => r.UpdatedAt),
        };
    }

    public async Task<AutomationRuleDetail?> GetAsync(Guid ruleId, CancellationToken cancellationToken)
    {
        var rule = await db.AutomationRules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == ruleId, cancellationToken);
        return rule is null ? null : await DetailAsync(rule, cancellationToken);
    }

    public async Task<AutomationRuleDetail> CreateAsync(
        string moduleKey, string entityKey, AutomationVersionContent content, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        await RequireEntityAsync(moduleKey, entityKey, cancellationToken);
        var now = time.GetUtcNow();
        AutomationRule rule;
        AutomationRuleVersion draft;
        try
        {
            rule = AutomationRule.Create(content.Name, moduleKey, entityKey, tenant.UserId, now);
            draft = AutomationRuleVersion.CreateDraft(rule.Id, 1, content, tenant.UserId, now);
        }
        catch (ArgumentException ex)
        {
            throw new AutomationRuleException(Clean(ex));
        }

        db.AutomationRules.Add(rule);
        db.AutomationRuleVersions.Add(draft);
        Audit(tenant, rule.Id, AuditAction.Created, before: null, after: await SnapshotAsync(rule, draft, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(rule, cancellationToken);
    }

    public async Task<AutomationRuleDetail?> UpdateDraftAsync(
        Guid ruleId, AutomationVersionContent content, long? expectedVersion, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var rule = await db.AutomationRules.SingleOrDefaultAsync(r => r.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return null;
        }

        CheckVersion(rule, expectedVersion);
        var draft = await db.AutomationRuleVersions.SingleOrDefaultAsync(
            v => v.RuleId == ruleId && v.State == AutomationVersionState.Draft, cancellationToken);
        var before = await SnapshotAsync(rule, draft ?? await PublishedAsync(rule, cancellationToken), cancellationToken);
        var now = time.GetUtcNow();
        try
        {
            if (draft is null)
            {
                var next = await db.AutomationRuleVersions.Where(v => v.RuleId == ruleId).MaxAsync(v => v.VersionNo, cancellationToken) + 1;
                draft = AutomationRuleVersion.CreateDraft(ruleId, next, content, tenant.UserId, now);
                db.AutomationRuleVersions.Add(draft);
            }
            else
            {
                draft.Apply(content);
            }

            rule.Touch(draft.Name, tenant.UserId, now);
        }
        catch (ArgumentException ex)
        {
            throw new AutomationRuleException(Clean(ex));
        }

        Audit(tenant, rule.Id, AuditAction.Updated, before, await SnapshotAsync(rule, draft, cancellationToken));
        await SaveAsync(cancellationToken);
        return await DetailAsync(rule, cancellationToken);
    }

    public async Task<AutomationRuleDetail?> PublishAsync(Guid ruleId, bool activate, long? expectedVersion, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var rule = await db.AutomationRules.SingleOrDefaultAsync(r => r.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return null;
        }

        var draft = await db.AutomationRuleVersions.SingleOrDefaultAsync(
            v => v.RuleId == ruleId && v.State == AutomationVersionState.Draft, cancellationToken);
        if (draft is null)
        {
            return null;
        }

        CheckVersion(rule, expectedVersion);
        var published = await PublishedAsync(rule, cancellationToken);
        if (published is not null && published.DefinitionHash == draft.DefinitionHash)
        {
            throw new AutomationRuleConflictException("Taslak yayınlanmış sürümle aynı; yayınlanacak değişiklik yok.");
        }

        var issues = await PublishIssuesAsync(rule, draft.Definition, cancellationToken);
        if (issues.Count > 0)
        {
            throw new AutomationPublishException(issues);
        }

        if (activate && rule.Status != AutomationRuleStatus.Active)
        {
            await EnsureActiveCapacityAsync(cancellationToken);
        }

        var before = await SnapshotAsync(rule, published, cancellationToken);
        var now = time.GetUtcNow();
        draft.MarkPublished(tenant.UserId, now);
        rule.Publish(draft.Id, draft.Name, activate, tenant.UserId, now);
        Audit(tenant, rule.Id, AuditAction.Updated, before, await SnapshotAsync(rule, draft, cancellationToken));
        await SaveAsync(cancellationToken);
        return await DetailAsync(rule, cancellationToken);
    }

    public async Task<AutomationRuleDetail?> SetActiveAsync(Guid ruleId, bool active, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var rule = await db.AutomationRules.SingleOrDefaultAsync(r => r.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return null;
        }

        var published = await PublishedAsync(rule, cancellationToken)
            ?? throw new AutomationRuleConflictException("Yayınlanmamış kural açılamaz; önce yayınlayın.");
        if ((rule.Status == AutomationRuleStatus.Active) == active)
        {
            return await DetailAsync(rule, cancellationToken);
        }

        if (active)
        {
            // Açarken tanım yeniden doğrulanır: bu arada alan, lookup ya da modül kaldırıldıysa kural sessizce açılmaz.
            var issues = await PublishIssuesAsync(rule, published.Definition, cancellationToken);
            if (issues.Count > 0)
            {
                throw new AutomationPublishException(issues);
            }

            await EnsureActiveCapacityAsync(cancellationToken);
        }

        var before = await SnapshotAsync(rule, published, cancellationToken);
        rule.SetActive(active, tenant.UserId, time.GetUtcNow());
        Audit(tenant, rule.Id, AuditAction.Updated, before, await SnapshotAsync(rule, published, cancellationToken));
        await SaveAsync(cancellationToken);
        return await DetailAsync(rule, cancellationToken);
    }

    public async Task<bool> DiscardDraftAsync(Guid ruleId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var rule = await db.AutomationRules.SingleOrDefaultAsync(r => r.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return false;
        }

        var draft = await db.AutomationRuleVersions.SingleOrDefaultAsync(
            v => v.RuleId == ruleId && v.State == AutomationVersionState.Draft, cancellationToken)
            ?? throw new AutomationRuleConflictException("Atılacak taslak yok.");
        var published = await PublishedAsync(rule, cancellationToken);
        var before = await SnapshotAsync(rule, draft, cancellationToken);
        db.AutomationRuleVersions.Remove(draft);
        if (published is null)
        {
            // Hiç yayınlanmamış kural: taslakla birlikte kuralın kendisi de silinir.
            db.AutomationRules.Remove(rule);
            Audit(tenant, rule.Id, AuditAction.Deleted, before, after: null);
        }
        else
        {
            rule.Touch(published.Name, tenant.UserId, time.GetUtcNow());
            Audit(tenant, rule.Id, AuditAction.Updated, before, await SnapshotAsync(rule, published, cancellationToken));
        }

        await SaveAsync(cancellationToken);
        return true;
    }

    // Yayın/açma sorunları: katalogla doğrulama + bu sürümde çalışmayan tetikleyici.
    private async Task<IReadOnlyList<AutomationIssue>> PublishIssuesAsync(
        AutomationRule rule, AutomationDefinition definition, CancellationToken cancellationToken)
    {
        var issues = (await validator.ValidateAsync(rule.ModuleKey, rule.EntityKey, definition, cancellationToken)).ToList();
        if (!AutomationTriggerMatcher.IsExecutable(definition.Trigger))
        {
            issues.Add(new AutomationIssue(
                "trigger", "Bu tetikleyici türü henüz çalışmıyor; kayıt oluşturma, alan değişimi, durum geçişi ya da elle çalıştırma kullanın."));
        }

        return issues;
    }

    // Tenant başına etkin kural sınırı (ADR-0027): her olay etkin kuralları taradığından bir firma ortak kaynağı şişiremesin.
    private async Task EnsureActiveCapacityAsync(CancellationToken cancellationToken)
    {
        var limit = Math.Max(1, automationOptions.Value.MaxActiveRulesPerTenant);
        if (await db.AutomationRules.CountAsync(r => r.Status == AutomationRuleStatus.Active, cancellationToken) >= limit)
        {
            throw new AutomationRuleConflictException($"Etkin kural sayısı sınırına ({limit}) ulaşıldı; önce başka bir kuralı kapatın.");
        }
    }

    private async Task<AutomationRuleDetail> DetailAsync(AutomationRule rule, CancellationToken cancellationToken)
    {
        var versions = await db.AutomationRuleVersions.AsNoTracking().Where(v => v.RuleId == rule.Id).ToListAsync(cancellationToken);
        var draft = versions.FirstOrDefault(v => v.State == AutomationVersionState.Draft);
        var published = versions.FirstOrDefault(v => v.Id == rule.PublishedVersionId);
        var fields = await catalog.GetFieldSetAsync(rule.EntityKey, cancellationToken);
        var summary = new AutomationRuleSummary(
            rule.Id, rule.Name, rule.ModuleKey, rule.EntityKey, rule.Status, published?.VersionNo, draft is not null,
            (published ?? draft)?.Priority ?? AutomationRuleVersion.DefaultPriority, rule.UpdatedAt, rule.Version);
        return new AutomationRuleDetail(
            summary,
            draft is null ? null : Info(draft, fields),
            published is null ? null : Info(published, fields),
            draft is null ? [] : await validator.ValidateAsync(rule.ModuleKey, rule.EntityKey, draft.Definition, cancellationToken),
            published is null ? [] : await validator.ValidateAsync(rule.ModuleKey, rule.EntityKey, published.Definition, cancellationToken));
    }

    private AutomationVersionInfo Info(AutomationRuleVersion version, AutomationFieldSet? fields) => new(
        version.Id, version.VersionNo, version.State, version.Content, version.DefinitionHash,
        fields is null ? null : AutomationSummary.Describe(version.Definition, fields, modules),
        version.PublishedAt, version.PublishedBy);

    private Task<AutomationRuleVersion?> PublishedAsync(AutomationRule rule, CancellationToken cancellationToken) =>
        rule.PublishedVersionId is { } id
            ? db.AutomationRuleVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == id, cancellationToken)
            : Task.FromResult<AutomationRuleVersion?>(null);

    // Kayıt türü modülün kural yazmaya açtığı bir türdür ve çağıran onu görebilir (modül açık, okuma izni var).
    private async Task RequireEntityAsync(string moduleKey, string entityKey, CancellationToken cancellationToken)
    {
        if (modules.FindAutomationEntity(entityKey) is not { } found || found.ModuleKey != moduleKey)
        {
            throw new AutomationRuleException("Bu modülde bu kayıt türü için kural yazılamaz.");
        }

        if (await catalog.GetFieldSetAsync(entityKey, cancellationToken) is null)
        {
            throw new AutomationRuleException("Bu kayıt türü için kural yazma erişiminiz yok ya da modül bu firmada etkin değil.");
        }
    }

    private static void CheckVersion(AutomationRule rule, long? expectedVersion)
    {
        if (expectedVersion is { } expected && expected != rule.Version)
        {
            throw new AutomationRuleConflictException("Kural başkası tarafından değiştirildi; sayfayı yenileyip tekrar deneyin.");
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AutomationRuleConflictException("Kural başkası tarafından değiştirildi; sayfayı yenileyip tekrar deneyin.");
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_AutomationRuleVersions_TenantId_RuleId_Draft", StringComparison.Ordinal) == true)
        {
            throw new AutomationRuleConflictException("Kuralın başka bir taslağı aynı anda açıldı; sayfayı yenileyip tekrar deneyin.");
        }
    }

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan otomasyon kuralları yönetilemez.");

    private void Audit(TenantContext tenant, Guid ruleId, AuditAction action, JsonObject? before, JsonObject? after)
    {
        var changes = RecordDiff.Compute(before, after);
        if (action == AuditAction.Updated && changes.Count == 0)
        {
            return;
        }

        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            CoreModule.AuditedEntityKeys.AutomationRule,
            ruleId,
            action,
            changes,
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));
    }

    // Denetim görünümü: ad, durum, sürüm ve okunabilir özet. Tanımın ham JSON'u yazılmaz (özet yeterli ve okunur).
    private async Task<JsonObject?> SnapshotAsync(AutomationRule rule, AutomationRuleVersion? version, CancellationToken cancellationToken)
    {
        var fields = version is null ? null : await catalog.GetFieldSetAsync(rule.EntityKey, cancellationToken);
        return new JsonObject
        {
            ["name"] = rule.Name,
            ["module"] = rule.ModuleKey,
            ["entity"] = rule.EntityKey,
            ["status"] = rule.Status.ToString(),
            ["priority"] = version?.Priority,
            ["stopProcessing"] = version?.StopProcessing,
            ["activeFrom"] = version?.ActiveFrom?.ToUniversalTime().ToString("O"),
            ["activeUntil"] = version?.ActiveUntil?.ToUniversalTime().ToString("O"),
            ["version"] = version?.VersionNo,
            ["summary"] = version is null || fields is null ? null : AutomationSummary.Describe(version.Definition, fields, modules),
        };
    }

    private static string Clean(ArgumentException ex) =>
        ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty);
}
