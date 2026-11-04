using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Marketing;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Modules.Marketing.Domain;
using Modulix.Modules.Marketing.Segments;

namespace Modulix.Modules.Marketing.Records;

/// <summary>
/// Segment motoru. Alan kaynaklarından her modül kendi tablolarını okur; burada yalnızca sonuçlar kimlik kümeleri olarak birleştirilir
/// (AND = kesişim, OR = birleşim), segmentin dahil/hariç üyeleri eklenir/çıkarılır: <c>(koşul ∪ dahil) ∖ hariç</c>. Sonuç bir kimlik
/// kümesi olduğundan ilişki geçişi üyeleri mükerrerleştirmez. Alan görme yetkisi olmayan kişi o alanla filtreleyemez, sayı alamaz.
/// </summary>
internal sealed class SegmentEngine(
    IModuleData data, IEnumerable<ISegmentFieldSource> sources, IRecordAccess access, ITenantClock clock) : ISegmentEngine
{
    /// <summary>Tek hesaplamanın süre sınırı; aşılırsa iptal edilir ve kullanıcıya bildirilir.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private const string UnavailableReason = "Alanın modülü bu firma için kapalı ya da okuma yetkiniz yok.";

    public async Task<SegmentCatalog> CatalogAsync(SegmentTarget target, CancellationToken cancellationToken)
    {
        var available = new List<SegmentFieldDefinition>();
        var unavailable = new List<UnavailableField>();
        var allowed = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var field in sources.SelectMany(s => s.Fields).Where(f => f.Target == target).OrderBy(f => f.Group).ThenBy(f => f.Label))
        {
            if (!allowed.TryGetValue(field.ReadEntityKey, out var can))
            {
                allowed[field.ReadEntityKey] = can = await access.CanAsync(field.ReadEntityKey, RecordOperation.Read, cancellationToken);
            }

            if (can)
            {
                available.Add(field);
            }
            else
            {
                unavailable.Add(new UnavailableField(field.Key, field.Label, UnavailableReason));
            }
        }

        return new SegmentCatalog(available, unavailable);
    }

    public async Task<SegmentEvaluation> EvaluateAsync(SegmentEvaluationRequest request, CancellationToken cancellationToken)
    {
        var query = await PrepareAsync(request, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Limit);
        var watch = Stopwatch.StartNew();
        try
        {
            var count = await query.CountAsync(timeout.Token);
            var rows = new List<SegmentRow>();
            var hidden = false;
            if (request.IncludeRows)
            {
                if (await CanSeeRowsAsync(request.Target, cancellationToken))
                {
                    var page = Math.Max(request.Page, 1);
                    var size = Math.Clamp(request.PageSize, 1, 200);
                    var ids = await query.OrderBy(id => id).Skip((page - 1) * size).Take(size).ToListAsync(timeout.Token);
                    rows.AddRange(await LabelsAsync(request.Target, ids, timeout.Token));
                }
                else
                {
                    hidden = true;
                }
            }

            watch.Stop();
            return new SegmentEvaluation(count, rows, hidden, (int)watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SegmentTimeoutException(Limit);
        }
    }

    public async Task<IReadOnlyList<Guid>> ResolveIdsAsync(SegmentEvaluationRequest request, int limit, CancellationToken cancellationToken)
    {
        var query = await PrepareAsync(request, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Limit);
        try
        {
            var count = await query.CountAsync(timeout.Token);
            if (count > limit)
            {
                throw new SegmentTooLargeException(count, limit);
            }

            return await query.OrderBy(id => id).ToListAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SegmentTimeoutException(Limit);
        }
    }

    /// <summary>Koşulu doğrular, çağıranın alan yetkisi ve veri kapsamıyla kimlik sorgusunu kurar (çalıştırmaz).</summary>
    private async Task<IQueryable<Guid>> PrepareAsync(SegmentEvaluationRequest request, CancellationToken cancellationToken)
    {
        if (request.Condition is null && request.SegmentId is null)
        {
            throw new SegmentConditionException([new ConditionError("", "Koşul ya da segment gerekli.")]);
        }

        var catalog = await CatalogAsync(request.Target, cancellationToken);
        var byKey = catalog.Fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        if (request.Condition is not null)
        {
            var errors = SegmentConditions.Validate(request.Condition, byKey, request.Target);
            if (errors.Count > 0)
            {
                throw new SegmentConditionException(errors);
            }
        }

        var day = await clock.GetAsync(cancellationToken);

        // Her koşul kendi kayıt türünün kapsamıyla değerlendirilir (ADR-0030): kaynakların bildirdiği her tür için kapsam önceden çözülür.
        var scopes = new Dictionary<string, IReadOnlySet<Guid>?>(StringComparer.Ordinal);
        foreach (var entityKey in sources.SelectMany(s => s.ScopeEntityKeys).Distinct(StringComparer.Ordinal))
        {
            scopes[entityKey] = await access.GetOwnersAsync(entityKey, cancellationToken);
        }

        var context = new SegmentEvaluationContext(
            entityKey => scopes.TryGetValue(entityKey, out var owners)
                ? owners
                : throw new InvalidOperationException($"'{entityKey}' kayıt türü kaynakların ScopeEntityKeys listesinde bildirilmemiş."),
            day.Today,
            day.StartOfDay);
        return Build(request, byKey, context);
    }

    private IQueryable<Guid> Build(SegmentEvaluationRequest request, Dictionary<string, SegmentFieldDefinition> byKey, SegmentEvaluationContext context)
    {
        IQueryable<Guid>? result = request.Condition is null ? null : Compose(request.Condition, byKey, context);
        if (request.SegmentId is { } segmentId && request.IncludeMembers)
        {
            var members = data.Set<SegmentMember>().AsNoTracking().Where(m => m.SegmentId == segmentId);
            var includes = members.Where(m => m.Kind == MemberKinds.Include).Select(m => m.TargetId);
            var excludes = members.Where(m => m.Kind == MemberKinds.Exclude).Select(m => m.TargetId);
            result = (result is null ? includes : result.Union(includes)).Except(excludes);
        }

        return result ?? throw new SegmentConditionException([new ConditionError("", "Koşul gerekli.")]);
    }

    private IQueryable<Guid> Compose(SegmentCondition node, Dictionary<string, SegmentFieldDefinition> byKey, SegmentEvaluationContext context)
    {
        if (node.Leaf is { } leaf)
        {
            var field = byKey[leaf.Field];
            var source = sources.First(s => s.Fields.Any(f => f.Key == field.Key));
            return source.Match(field, leaf, context);
        }

        var parts = node.Items.Select(i => Compose(i, byKey, context)).ToList();
        return node.Group == SegmentCondition.And
            ? parts.Aggregate((a, b) => a.Intersect(b))
            : parts.Aggregate((a, b) => a.Union(b));
    }

    /// <summary>Satır (ad) gösterimi alıcı görüntüleme yetkisine bağlıdır: ortak kaydı (ya da lead'i) okuyamayan satır görmez.</summary>
    private Task<bool> CanSeeRowsAsync(SegmentTarget target, CancellationToken cancellationToken) => access.CanAsync(
        target switch { SegmentTarget.Person => SharedEntities.Person, SegmentTarget.Lead => "crm.lead", _ => SharedEntities.Party },
        RecordOperation.Read,
        cancellationToken);

    private async Task<IReadOnlyList<SegmentRow>> LabelsAsync(SegmentTarget target, List<Guid> ids, CancellationToken cancellationToken)
    {
        switch (target)
        {
            case SegmentTarget.Company:
            {
                var names = await data.Set<Party>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Name }).ToListAsync(cancellationToken);
                return [.. ids.Select(id => new SegmentRow(id, names.FirstOrDefault(n => n.Id == id)?.Name))];
            }

            case SegmentTarget.Person:
            {
                var names = await data.Set<Person>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.FirstName, p.LastName }).ToListAsync(cancellationToken);
                return [.. ids.Select(id => new SegmentRow(id, names.FirstOrDefault(n => n.Id == id) is { } n ? $"{n.FirstName} {n.LastName}".Trim() : null))];
            }

            default:
                return [.. ids.Select(id => new SegmentRow(id, null))];
        }
    }
}
