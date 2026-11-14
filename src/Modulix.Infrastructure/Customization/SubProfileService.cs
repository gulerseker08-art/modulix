using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Sectors;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Customization;
using Modulix.Core.FieldRules;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Customization;

/// <summary>
/// Firma alt profilleri (ADR-0041). Alanlar ve liste değerleri <see cref="ICustomFieldService"/> ve <see cref="IChoiceListService"/> ile eklenir
/// (doğrulama ve denetim kaydı onlarındır; yalnızca ekler, aynı adlı atlanır); alt profilin kendisi <c>core.sub-profile</c> olarak denetime yazılır.
/// Uygulama tek işlemdir: bir alan eklenemezse hiçbiri eklenmez.
/// </summary>
internal sealed class SubProfileService(
    ModulixDbContext db,
    ITenantContextAccessor tenantAccessor,
    ModuleCatalog catalog,
    IModuleEntitlements entitlements,
    ICustomFieldService fields,
    IChoiceListService lists,
    IChoiceLists choiceLists,
    IActivityCodeCatalog activities,
    SubProfileStarterCatalog starters,
    CustomizationLimits limits,
    IAutomationCatalog automation,
    ITenantRuleSimulation simulation,
    IAuditWriter audit) : ISubProfileService
{
    public async Task<IReadOnlyList<SubProfileView>> ListAsync(CancellationToken cancellationToken)
    {
        RequireTenant();
        var rows = await db.SubProfiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken);
        var counts = (await db.SubProfileOptions.AsNoTracking()
                .GroupBy(o => o.SubProfileId)
                .Select(g => new { Id = g.Key, Total = g.Count(), Pending = g.Count(o => o.AppliedAt == null) })
                .ToListAsync(cancellationToken))
            .ToDictionary(c => c.Id);
        var features = (await db.SubProfileFeatures.AsNoTracking().OrderBy(f => f.SortOrder).ToListAsync(cancellationToken)).ToLookup(f => f.SubProfileId);
        var rules = (await db.SubProfileRules.AsNoTracking().OrderBy(r => r.SortOrder).ToListAsync(cancellationToken)).ToLookup(r => r.SubProfileId);
        var names = await ActivityNamesAsync(rows.Select(r => r.ActivityCode), cancellationToken);
        return [.. rows.Select(r => ToView(r, [], counts.GetValueOrDefault(r.Id)?.Total ?? 0, counts.GetValueOrDefault(r.Id)?.Pending ?? 0, names, [.. features[r.Id].Select(ToView)], [.. rules[r.Id].Select(x => ToView(x, r.Code))]))];
    }

    public async Task<SubProfileView?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireTenant();
        var row = await db.SubProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        return row is null ? null : await LoadViewAsync(row, cancellationToken);
    }

    public async Task<SubProfileView> CreateAsync(SubProfileInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tenant = RequireTenant();
        var existing = await db.SubProfiles.Select(p => new { p.Code, p.Name }).ToListAsync(cancellationToken);
        if (limits.MaxSubProfiles is { } max && existing.Count >= max)
        {
            throw new ArgumentException($"En fazla {max} alt profil olabilir.", "name");
        }

        EnsureNameFree(input.Name, existing.Select(e => e.Name));
        await EnsureActivityAsync(input.ActivityCode, cancellationToken);
        var code = string.IsNullOrWhiteSpace(input.Code)
            ? CustomFieldRules.KeyFrom(input.Name, existing.Select(e => e.Code).ToHashSet(StringComparer.Ordinal), SubProfile.CodeMaxLength)
            : input.Code.Trim();
        if (existing.Any(e => string.Equals(e.Code, code, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Bu kod zaten var: '{code}'.", "code");
        }

        var row = new SubProfile(code, input.Name, input.Description, input.ActivityCode);
        db.SubProfiles.Add(row);
        Audit(tenant, row.Id, AuditAction.Created, null, Snapshot(row, []));
        await SaveAsync(cancellationToken);
        return (await GetAsync(row.Id, cancellationToken))!;
    }

    public async Task<SubProfileView?> UpdateAsync(Guid id, SubProfileInput input, long revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tenant = RequireTenant();
        var row = await db.SubProfiles.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        RequireRevision(row, revision);
        var options = await db.SubProfileOptions.AsNoTracking().Where(o => o.SubProfileId == id).ToListAsync(cancellationToken);
        EnsureNameFree(input.Name, await db.SubProfiles.Where(p => p.Id != id).Select(p => p.Name).ToListAsync(cancellationToken));
        if (!string.Equals(input.ActivityCode?.Trim(), row.ActivityCode, StringComparison.Ordinal))
        {
            await EnsureActivityAsync(input.ActivityCode, cancellationToken);
        }

        var before = Snapshot(row, options);
        row.Edit(input.Name, input.Description, input.ActivityCode);
        var after = Snapshot(row, options);
        if (!JsonNode.DeepEquals(before, after))
        {
            Audit(tenant, row.Id, AuditAction.Updated, before, after);
        }

        await SaveAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<SubProfileView?> SetStatusAsync(Guid id, SubProfileStatus status, long revision, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var row = await db.SubProfiles.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        RequireRevision(row, revision);
        if (row.Status == status)
        {
            return await GetAsync(id, cancellationToken);
        }

        // Taslak yalnızca etkinleştirilir; etkin alt profilin alanları firmaya eklendiğinden taslağa dönemez.
        var allowed = (row.Status, status) is (SubProfileStatus.Draft, SubProfileStatus.Active)
            or (SubProfileStatus.Active, SubProfileStatus.Inactive)
            or (SubProfileStatus.Inactive, SubProfileStatus.Active);
        if (!allowed)
        {
            throw new SubProfileConflictException("Bu durum değişikliği yapılamaz.");
        }

        // Etkinleştirme, kuralların etkin kural kümesinde yeni çakışma doğurmadığını doğrular (ADR-0041 K4); çakışırsa durum değişmez.
        if (status == SubProfileStatus.Active)
        {
            var mine = (await db.SubProfileRules.AsNoTracking().Where(r => r.SubProfileId == id).OrderBy(r => r.SortOrder).ToListAsync(cancellationToken))
                .Select(r => r.ToRule(row.Code)).ToList();
            await EnsureNoNewConflictsAsync(row, [], mine, cancellationToken);
        }

        var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var options = await db.SubProfileOptions.Where(o => o.SubProfileId == id).ToListAsync(cancellationToken);
            var before = Snapshot(row, options);
            row.SetStatus(status);
            if (status == SubProfileStatus.Active)
            {
                await ApplyAsync(row, options, cancellationToken);
            }

            Audit(tenant, row.Id, AuditAction.Updated, before, Snapshot(row, options));
            await SaveAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        return await GetAsync(id, cancellationToken);
    }

    public async Task<ProfilePlan?> PreviewApplyAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireTenant();
        var row = await db.SubProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var options = await db.SubProfileOptions.AsNoTracking().Where(o => o.SubProfileId == id).ToListAsync(cancellationToken);
        return await PlanAsync(row, options, cancellationToken);
    }

    public async Task<SubProfileView?> ApplyPendingAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var row = await db.SubProfiles.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        if (row.Status != SubProfileStatus.Active)
        {
            throw new SubProfileConflictException("Yalnızca etkin alt profil uygulanır.");
        }

        await ApplyWithTransactionAsync(tenant, row, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<SubProfileView?> AddOptionAsync(Guid id, SubProfileOptionInput input, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var row = await db.SubProfiles.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var normalized = SubProfileRules.Normalize(input, catalog);
        await EnsureEntityEnabledAsync(normalized, cancellationToken);
        var options = await db.SubProfileOptions.Where(o => o.SubProfileId == id).ToListAsync(cancellationToken);
        if (limits.MaxOptionsPerSubProfile is { } max && options.Count >= max)
        {
            throw new ArgumentException($"Bir alt profilde en fazla {max} seçenek olabilir.", "label");
        }

        if (SubProfileRules.IsDuplicate(normalized, options))
        {
            throw new ArgumentException($"Bu seçenek zaten var: '{normalized.Label}'.", "label");
        }

        var before = Snapshot(row, options);
        var option = new SubProfileOption(id, options.Count == 0 ? 0 : options.Max(o => o.SortOrder) + 1);
        Fill(option, normalized);
        db.SubProfileOptions.Add(option);
        options.Add(option);
        row.Touch();
        Audit(tenant, row.Id, AuditAction.Updated, before, Snapshot(row, options));
        await SaveAsync(cancellationToken);

        // Etkin alt profilde yeni seçenek hemen uygulanır; pasifte ve taslakta bekler.
        if (row.Status == SubProfileStatus.Active)
        {
            await ApplyWithTransactionAsync(tenant, row, cancellationToken);
        }

        return await GetAsync(id, cancellationToken);
    }

    public async Task<SubProfileView?> UpdateOptionAsync(Guid id, Guid optionId, SubProfileOptionInput input, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var row = await db.SubProfiles.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        var options = row is null ? [] : await db.SubProfileOptions.Where(o => o.SubProfileId == id).ToListAsync(cancellationToken);
        if (row is null || options.SingleOrDefault(o => o.Id == optionId) is not { } option)
        {
            return null;
        }

        if (option.IsApplied)
        {
            throw new SubProfileConflictException("Uygulanmış seçenek değiştirilemez; Özel alanlar ve Listeler sayfasından düzenleyin.");
        }

        var normalized = SubProfileRules.Normalize(input, catalog);
        await EnsureEntityEnabledAsync(normalized, cancellationToken);
        if (SubProfileRules.IsDuplicate(normalized, options.Where(o => o.Id != optionId)))
        {
            throw new ArgumentException($"Bu seçenek zaten var: '{normalized.Label}'.", "label");
        }

        var before = Snapshot(row, options);
        Fill(option, normalized);
        row.Touch();
        Audit(tenant, row.Id, AuditAction.Updated, before, Snapshot(row, options));
        await SaveAsync(cancellationToken);
        if (row.Status == SubProfileStatus.Active)
        {
            await ApplyWithTransactionAsync(tenant, row, cancellationToken);
        }

        return await GetAsync(id, cancellationToken);
    }

    public async Task<SubProfileView?> RemoveOptionAsync(Guid id, Guid optionId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var row = await db.SubProfiles.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        var options = row is null ? [] : await db.SubProfileOptions.Where(o => o.SubProfileId == id).ToListAsync(cancellationToken);
        if (row is null || options.SingleOrDefault(o => o.Id == optionId) is not { } option)
        {
            return null;
        }

        if (option.IsApplied)
        {
            throw new SubProfileConflictException("Uygulanmış seçenek kaldırılamaz; alan artık firmaya aittir, Özel alanlar ve Listeler sayfasından düzenleyin.");
        }

        var before = Snapshot(row, options);
        db.SubProfileOptions.Remove(option);
        options.Remove(option);
        row.Touch();
        Audit(tenant, row.Id, AuditAction.Updated, before, Snapshot(row, options));
        await SaveAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<SubProfileView?> AddFeatureAsync(Guid id, SubProfileFeatureInput input, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var row = await db.SubProfiles.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var normalized = SubProfileRules.NormalizeFeature(input);
        var features = await db.SubProfileFeatures.Where(f => f.SubProfileId == id).ToListAsync(cancellationToken);
        if (SubProfileRules.IsDuplicateFeature(normalized, features))
        {
            throw new ArgumentException($"Bu özellik zaten var: '{normalized.Group}: {normalized.Value}'.", "value");
        }

        var before = FeatureSnapshot(row, features);
        var feature = new SubProfileFeature(id, normalized.Group, normalized.Value, features.Count == 0 ? 0 : features.Max(f => f.SortOrder) + 1);
        db.SubProfileFeatures.Add(feature);
        features.Add(feature);
        row.Touch();
        Audit(tenant, row.Id, AuditAction.Updated, before, FeatureSnapshot(row, features));
        await SaveAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<SubProfileView?> RemoveFeatureAsync(Guid id, Guid featureId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var row = await db.SubProfiles.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        var features = row is null ? [] : await db.SubProfileFeatures.Where(f => f.SubProfileId == id).ToListAsync(cancellationToken);
        if (row is null || features.SingleOrDefault(f => f.Id == featureId) is not { } feature)
        {
            return null;
        }

        var before = FeatureSnapshot(row, features);
        db.SubProfileFeatures.Remove(feature);
        features.Remove(feature);
        row.Touch();
        Audit(tenant, row.Id, AuditAction.Updated, before, FeatureSnapshot(row, features));
        await SaveAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<SubProfileView?> AddRuleAsync(Guid id, SubProfileRuleInput input, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var row = await db.SubProfiles.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var normalized = SubProfileRules.NormalizeRule(input);
        var rules = await db.SubProfileRules.Where(r => r.SubProfileId == id).ToListAsync(cancellationToken);
        if (limits.MaxOptionsPerSubProfile is { } max && rules.Count + await db.SubProfileOptions.CountAsync(o => o.SubProfileId == id, cancellationToken) >= max)
        {
            throw new ArgumentException($"Bir alt profilde en fazla {max} seçenek olabilir.", "name");
        }

        EnsureRuleNameFree(normalized.Name, rules);
        var taken = (await db.SubProfileRules.Select(r => r.Key).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var rule = await ValidatedRuleAsync(normalized, SubProfileRules.RuleKey(row.Code, normalized.Name, taken), cancellationToken);
        var stored = rules.Select(r => r.ToRule(row.Code)).ToList();
        if (row.Status == SubProfileStatus.Active)
        {
            await EnsureNoNewConflictsAsync(row, stored, [.. stored, rule], cancellationToken);
        }

        var before = RuleSnapshot(row, rules);
        var entity = new SubProfileRule(id, normalized.Name, rule, rules.Count == 0 ? 0 : rules.Max(r => r.SortOrder) + 1);
        db.SubProfileRules.Add(entity);
        rules.Add(entity);
        row.Touch();
        Audit(tenant, row.Id, AuditAction.Updated, before, RuleSnapshot(row, rules));
        await SaveAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<SubProfileView?> UpdateRuleAsync(Guid id, Guid ruleId, SubProfileRuleInput input, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var row = await db.SubProfiles.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        var rules = row is null ? [] : await db.SubProfileRules.Where(r => r.SubProfileId == id).ToListAsync(cancellationToken);
        if (row is null || rules.SingleOrDefault(r => r.Id == ruleId) is not { } entity)
        {
            return null;
        }

        var normalized = SubProfileRules.NormalizeRule(input);
        EnsureRuleNameFree(normalized.Name, rules.Where(r => r.Id != ruleId));
        var rule = await ValidatedRuleAsync(normalized, entity.Key, cancellationToken);
        var stored = rules.Select(r => r.ToRule(row.Code)).ToList();
        if (row.Status == SubProfileStatus.Active)
        {
            await EnsureNoNewConflictsAsync(row, stored, [.. stored.Where(r => r.Key != entity.Key), rule], cancellationToken);
        }

        var before = RuleSnapshot(row, rules);
        entity.Replace(normalized.Name, rule);
        row.Touch();
        Audit(tenant, row.Id, AuditAction.Updated, before, RuleSnapshot(row, rules));
        await SaveAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<SubProfileView?> RemoveRuleAsync(Guid id, Guid ruleId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var row = await db.SubProfiles.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        var rules = row is null ? [] : await db.SubProfileRules.Where(r => r.SubProfileId == id).ToListAsync(cancellationToken);
        if (row is null || rules.SingleOrDefault(r => r.Id == ruleId) is not { } entity)
        {
            return null;
        }

        var before = RuleSnapshot(row, rules);
        db.SubProfileRules.Remove(entity);
        rules.Remove(entity);
        row.Touch();
        Audit(tenant, row.Id, AuditAction.Updated, before, RuleSnapshot(row, rules));
        await SaveAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<SubProfileRulePreview?> PreviewRulesAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireTenant();
        var row = await db.SubProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var mine = (await db.SubProfileRules.AsNoTracking().Where(r => r.SubProfileId == id).OrderBy(r => r.SortOrder).ToListAsync(cancellationToken))
            .Select(r => r.ToRule(row.Code)).ToList();
        var (conflicts, warnings) = await SimulateAsync(row, row.Status == SubProfileStatus.Active ? mine : [], mine, cancellationToken);
        return new SubProfileRulePreview(mine, conflicts, warnings);
    }

    public async Task<IReadOnlyList<SubProfileStarterView>> ListStartersAsync(CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var codes = (await db.SubProfiles.AsNoTracking().Select(p => p.Code).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var enabled = await entitlements.GetEnabledAsync(tenant.TenantId, cancellationToken);
        var names = await ActivityNamesAsync(starters.Starters.Select(s => s.ActivityCode), cancellationToken);
        return [.. starters.Starters.Select(s => new SubProfileStarterView(
            s.Key,
            s.Name,
            s.Description,
            s.ActivityCode is { } code ? names.GetValueOrDefault(code) : null,
            [.. s.Options.Select(o => o.Label)],
            SubProfileRules.GroupLabels(s.Features ?? []),
            MissingModules(s, enabled),
            codes.Contains(s.Key)))];
    }

    public async Task<SubProfileView?> CreateFromStarterAsync(string key, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        if (starters.Find(key) is not { } starter)
        {
            return null;
        }

        if (await db.SubProfiles.AnyAsync(p => p.Code == starter.Key, cancellationToken))
        {
            throw new SubProfileConflictException("Bu hazır alt profil zaten eklenmiş.");
        }

        var missing = MissingModules(starter, await entitlements.GetEnabledAsync(tenant.TenantId, cancellationToken));
        if (missing.Count > 0)
        {
            throw new ArgumentException($"Bu alt profil için şu modüller açık olmalı: {string.Join(", ", missing)}.", "key");
        }

        // Katalogda bulunmayan ya da pasif faaliyet alt profilin eklenmesini engellemez; bağlanmadan eklenir.
        var activityCode = starter.ActivityCode is { } code
            && (await activities.FindAsync([code], cancellationToken)).SingleOrDefault() is { IsActive: true, Kind: ActivityCodeKinds.Activity }
                ? code
                : null;

        var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var created = await CreateAsync(new SubProfileInput(starter.Name, starter.Description, activityCode, starter.Key), cancellationToken);
            foreach (var option in starter.Options)
            {
                _ = await AddOptionAsync(created.Id, option, cancellationToken);
            }

            foreach (var feature in starter.Features ?? [])
            {
                _ = await AddFeatureAsync(created.Id, feature, cancellationToken);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return await GetAsync(created.Id, cancellationToken);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private List<string> MissingModules(SubProfileStarter starter, IEnumerable<string> enabled)
    {
        var open = enabled.ToHashSet(StringComparer.Ordinal);
        return [.. starters.ModulesOf(starter).Where(m => !catalog.IsEnabled(m, open)).Select(m => catalog.Find(m)?.DisplayName ?? m)];
    }

    private async Task ApplyWithTransactionAsync(TenantContext tenant, SubProfile row, CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var options = await db.SubProfileOptions.Where(o => o.SubProfileId == row.Id).ToListAsync(cancellationToken);
            var before = Snapshot(row, options);
            var applied = await ApplyAsync(row, options, cancellationToken);
            if (applied > 0)
            {
                row.Touch();
                Audit(tenant, row.Id, AuditAction.Updated, before, Snapshot(row, options));
            }

            await SaveAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>Uygulanmamış seçenekleri firmaya ekler ve uygulandı diye işaretler; kapalı modülün seçenekleri beklemeye devam eder. İşaretlenen sayıyı verir.</summary>
    private async Task<int> ApplyAsync(SubProfile row, IReadOnlyList<SubProfileOption> options, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(row, options, cancellationToken);
        foreach (var field in plan.Fields)
        {
            var choices = field.Options?.Select(o => new CustomFieldOptionInput(null, o)).ToList();
            await fields.CreateAsync(field.EntityKey, new CustomFieldInput(field.Label, field.Type, field.Required, field.ShowInList, choices), cancellationToken);
        }

        if (plan.ListValues.Count > 0)
        {
            var current = await lists.ListAsync(cancellationToken);
            foreach (var group in plan.ListValues.GroupBy(v => v.ListKey))
            {
                var list = current.Single(l => l.Definition.Key == group.Key).List;
                _ = await lists.SaveAsync(group.Key, SectorProfileRules.Extend(list, group), cancellationToken)
                    ?? throw new InvalidOperationException($"Liste kaydedilemedi: '{group.Key}'.");
            }
        }

        // Planın saydığı modülü kapalı olanlar hariç hepsi (eklenen de, aynı adla zaten var olan da) uygulanmış sayılır.
        var unavailable = plan.UnavailableModules.ToHashSet(StringComparer.Ordinal);
        var marked = 0;
        foreach (var option in options.Where(o => !o.IsApplied))
        {
            var module = option.Kind == SubProfileOptionKind.Field
                ? catalog.FindCustomizable(option.TargetKey)?.ModuleKey
                : catalog.FindChoiceList(option.TargetKey)?.ModuleKey;
            if (module is not null && !unavailable.Contains(module))
            {
                option.MarkApplied();
                marked++;
            }
        }

        return marked;
    }

    private async Task<ProfilePlan> PlanAsync(SubProfile row, IReadOnlyList<SubProfileOption> options, CancellationToken cancellationToken)
    {
        var profile = SubProfileRules.ToProfile(row, options, catalog);
        var enabled = await entitlements.GetEnabledAsync(RequireTenant().TenantId, cancellationToken);
        var labels = (await fields.ListAsync(null, includeArchived: true, cancellationToken))
            .GroupBy(f => f.EntityKey)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(f => f.Label)]);
        var current = new Dictionary<string, ChoiceList>();
        foreach (var key in profile.ListValues.Select(v => v.ListKey).Distinct())
        {
            current[key] = await choiceLists.GetAsync(key, cancellationToken);
        }

        return SectorProfileRules.Plan(profile, catalog, enabled, canEnableModules: false, labels, current);
    }

    private async Task<SubProfileView> LoadViewAsync(SubProfile row, CancellationToken cancellationToken)
    {
        var options = await db.SubProfileOptions.AsNoTracking().Where(o => o.SubProfileId == row.Id).OrderBy(o => o.SortOrder).ToListAsync(cancellationToken);
        var features = await db.SubProfileFeatures.AsNoTracking().Where(f => f.SubProfileId == row.Id).OrderBy(f => f.SortOrder).ToListAsync(cancellationToken);
        var rules = await db.SubProfileRules.AsNoTracking().Where(r => r.SubProfileId == row.Id).OrderBy(r => r.SortOrder).ToListAsync(cancellationToken);
        var names = await ActivityNamesAsync([row.ActivityCode], cancellationToken);
        return ToView(row, [.. options.Select(ToView)], options.Count, options.Count(o => !o.IsApplied), names, [.. features.Select(ToView)], [.. rules.Select(r => ToView(r, row.Code))]);
    }

    private static SubProfileView ToView(
        SubProfile row, IReadOnlyList<SubProfileOptionView> options, int total, int pending, IReadOnlyDictionary<string, string> names,
        IReadOnlyList<SubProfileFeatureView> features, IReadOnlyList<SubProfileRuleView> rules) => new(
        row.Id, row.Code, row.Name, row.Description, row.ActivityCode, row.ActivityCode is { } code ? names.GetValueOrDefault(code) : null,
        row.Status, row.Revision, total, pending, row.CreatedAt, row.ActivatedAt, options, features, rules);

    private static SubProfileFeatureView ToView(SubProfileFeature f) => new(f.Id, f.Group, f.Value);

    private static SubProfileRuleView ToView(SubProfileRule r, string subProfileCode) => new(r.Id, r.Name, r.ToRule(subProfileCode));

    private static SubProfileOptionView ToView(SubProfileOption o) => new(
        o.Id, o.Kind, o.TargetKey, o.Label, o.FieldType, o.Required, o.ShowInList, o.Choices, o.Category, o.AppliedAt, o.SortOrder);

    private async Task<IReadOnlyDictionary<string, string>> ActivityNamesAsync(IEnumerable<string?> codes, CancellationToken cancellationToken)
    {
        var distinct = codes.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 0
            ? new Dictionary<string, string>()
            : (await activities.FindAsync(distinct, cancellationToken)).ToDictionary(a => a.Code, a => a.Name, StringComparer.Ordinal);
    }

    private async Task EnsureActivityAsync(string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        if (!ActivityCodeList.IsValidCode(code.Trim())
            || (await activities.FindAsync([code.Trim()], cancellationToken)).SingleOrDefault() is not { IsActive: true, Kind: ActivityCodeKinds.Activity })
        {
            throw new ArgumentException("Faaliyet katalogda bulunamadı ya da seçilebilir bir faaliyet değil.", "activityCode");
        }
    }

    // Seçenek yalnızca modülü bu firmada açık olan kayıt türü ve listeye yazılır (özel alan ekranındaki kuralla aynı).
    private async Task EnsureEntityEnabledAsync(SubProfileOptionInput input, CancellationToken cancellationToken)
    {
        var enabled = await entitlements.GetEnabledAsync(RequireTenant().TenantId, cancellationToken);
        var module = input.Kind == SubProfileOptionKind.Field
            ? catalog.FindCustomizable(input.TargetKey)?.ModuleKey
            : catalog.FindChoiceList(input.TargetKey)?.ModuleKey;
        if (module is null || !catalog.IsEnabled(module, enabled))
        {
            throw new ArgumentException("Bu kayıt türünün ya da listenin modülü firmada açık değil.", "targetKey");
        }
    }

    private static void EnsureNameFree(string name, IEnumerable<string> others)
    {
        if (others.Any(o => string.Equals(o.Trim(), name?.Trim(), StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new ArgumentException($"Bu adda bir alt profil zaten var: '{name?.Trim()}'.", "name");
        }
    }

    private static void RequireRevision(SubProfile row, long revision)
    {
        if (row.Revision != revision)
        {
            throw new SubProfileConflictException("Alt profil siz düzenlerken değişti; yenileyip tekrar deneyin.");
        }
    }

    private static void Fill(SubProfileOption option, SubProfileOptionInput input)
    {
        option.Kind = input.Kind;
        option.TargetKey = input.TargetKey;
        option.Label = input.Label;
        option.FieldType = input.FieldType;
        option.Required = input.Required;
        option.ShowInList = input.ShowInList;
        option.Choices = input.Choices ?? [];
        option.Category = input.Category;
    }

    // Eşzamanlı yazma EF'in eşzamanlılık belirteciyle (Revision) yakalanır ve kullanıcıya çakışma olarak döner.
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new SubProfileConflictException("Alt profil siz düzenlerken değişti; yenileyip tekrar deneyin.");
        }
    }

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan alt profil yönetilemez.");

    private void Audit(TenantContext tenant, Guid id, AuditAction action, JsonObject? before, JsonObject? after) =>
        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            CoreModule.AuditedEntityKeys.SubProfile,
            id,
            action,
            RecordDiff.Compute(before, after),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

    // Kuralı firmanın alan kümesine (özel alanlar dahil) karşı doğrular; anahtarı sunucu verir, alt profil kuralı kilitli olamaz.
    private async Task<FieldRule> ValidatedRuleAsync(SubProfileRuleInput input, string key, CancellationToken cancellationToken)
    {
        var rule = input.Rule with { Key = key, Locked = false };
        var issues = await FieldRuleChecker.ValidateAsync([rule], catalog, entityKey => automation.GetFieldSetAsync(entityKey, cancellationToken));
        if (issues.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", issues.Select(i => $"{i.Path}: {i.Message}")), "rule");
        }

        return rule;
    }

    private static void EnsureRuleNameFree(string name, IEnumerable<SubProfileRule> others)
    {
        if (others.Any(r => string.Equals(r.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new ArgumentException($"Bu kural zaten var: '{name}'.", "name");
        }
    }

    // Diğer etkin alt profillerin kuralları (bu alt profil hariç): katmanın geri kalanı.
    private async Task<List<FieldRule>> OtherActiveRulesAsync(Guid subProfileId, CancellationToken cancellationToken)
    {
        var rows = await (from r in db.SubProfileRules.AsNoTracking()
                          join p in db.SubProfiles.AsNoTracking() on r.SubProfileId equals p.Id
                          where p.Status == SubProfileStatus.Active && p.Id != subProfileId
                          orderby p.Code, r.SortOrder
                          select new { p.Code, Rule = r }).ToListAsync(cancellationToken);
        return [.. rows.Select(x => x.Rule.ToRule(x.Code))];
    }

    // Değişiklikten önce ve sonra katman birleşimini karşılaştırır: yalnızca değişikliğin doğurduğu çakışmalar döner (başka katmandan gelen eski
    // çakışma bu alt profilin düzenlenmesini engellemesin).
    private async Task<(IReadOnlyList<RuleMergeIssue> Conflicts, IReadOnlyList<RuleMergeIssue> Warnings)> SimulateAsync(
        SubProfile row, IReadOnlyList<FieldRule> beforeMine, IReadOnlyList<FieldRule> afterMine, CancellationToken cancellationToken)
    {
        var others = await OtherActiveRulesAsync(row.Id, cancellationToken);
        var before = await simulation.GetEffectiveWithSubProfileRulesAsync([.. others, .. beforeMine], cancellationToken);
        var after = await simulation.GetEffectiveWithSubProfileRulesAsync([.. others, .. afterMine], cancellationToken);
        var known = before.Conflicts.Select(c => c.Message).ToHashSet(StringComparer.Ordinal);
        return ([.. after.Conflicts.Where(c => !known.Contains(c.Message))], after.Warnings);
    }

    private async Task EnsureNoNewConflictsAsync(SubProfile row, IReadOnlyList<FieldRule> beforeMine, IReadOnlyList<FieldRule> afterMine, CancellationToken cancellationToken)
    {
        var (conflicts, _) = await SimulateAsync(row, beforeMine, afterMine, cancellationToken);
        if (conflicts.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", conflicts.Select(c => c.Message)), "rule");
        }
    }

    // Kural değişiklikleri kendi kaydıyla denetlenir: yalnızca ad ve kurallar.
    private static JsonObject RuleSnapshot(SubProfile row, IEnumerable<SubProfileRule> rules) => new()
    {
        ["name"] = row.Name,
        ["rules"] = new JsonArray([.. rules.OrderBy(r => r.SortOrder).Select(r => JsonValue.Create($"{r.Name} ({r.Key})"))]),
    };

    // Özellik değişiklikleri kendi kaydıyla denetlenir: yalnızca ad ve özellikler (seçenek ve durum farkı gürültü olmasın).
    private static JsonObject FeatureSnapshot(SubProfile row, IEnumerable<SubProfileFeature> features) => new()
    {
        ["name"] = row.Name,
        ["features"] = new JsonArray([.. features.OrderBy(f => f.SortOrder).Select(f => JsonValue.Create($"{f.Group}: {f.Value}"))]),
    };

    private static JsonObject Snapshot(SubProfile row, IEnumerable<SubProfileOption> options) => new()
    {
        ["name"] = row.Name,
        ["code"] = row.Code,
        ["description"] = row.Description,
        ["activity"] = row.ActivityCode,
        ["status"] = row.Status.ToString(),
        ["options"] = new JsonArray([.. options.OrderBy(o => o.SortOrder).Select(o => JsonValue.Create(
            $"{(o.Kind == SubProfileOptionKind.Field ? "Alan" : "Liste değeri")}: {o.TargetKey} / {o.Label}{(o.IsApplied ? " (uygulandı)" : string.Empty)}"))]),
    };
}
