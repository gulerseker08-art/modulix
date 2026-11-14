using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// Okurun hedef kitle bilgisi: kullanıcı, Core departmanı ve üstleri (<see cref="UnitPath"/>; hedef departman bu yolda ise içerik ona yöneliktir,
/// böylece üst departmana hedeflenen içerik alt departmandakilere de görünür) ve çalışan kartındaki eski İK departmanı (eşlenmemiş eski içerik için).
/// </summary>
internal sealed record HrReader(Guid UserId, Guid[] UnitPath, string? LegacyDepartmentKey);

/// <summary>İçeriğin hedefleri: departmanlar (tek departman kolonundaki eski hedef dahil) ve kullanıcılar. İkisi de boşsa içerik herkese yöneliktir.</summary>
internal sealed record AudienceSet(IReadOnlyList<Guid> Units, IReadOnlyList<Guid> Users)
{
    public static AudienceSet Empty { get; } = new([], []);
}

/// <summary>
/// Duyuru ve anketlerin hedef kitlesi (ADR-0042 bölüm 22 ve 23c). Hedef, birden çok Core departmanı (alt departmanlar dahil) ve/veya belirli
/// kullanıcılardır (<see cref="AudienceTarget"/>); okur hedeflerden birine uyuyorsa içerik ona yöneliktir. Hedefi olmayan içerik herkese yöneliktir.
/// </summary>
internal static class HrAudience
{
    /// <summary>
    /// Okurun departmanı kullanıcının Core departmanıdır (üyeliğinin bağlı olduğu birim); üyelikte departman yoksa etkin çalışan kartındaki departman.
    /// Departmanı olmayan okur herkese açık ve kendisine adıyla hedeflenmiş içeriği görür.
    /// </summary>
    public static async Task<HrReader> ReaderAsync(HrStoreContext context, CancellationToken cancellationToken)
    {
        var me = context.CurrentUserId ?? Guid.Empty;
        var card = await context.Data.Set<Employee>().AsNoTracking()
            .Where(e => e.UserId == me && e.Status != Employee.Statuses.Terminated)
            .Select(e => new { e.BusinessUnitId, e.DepartmentKey })
            .FirstOrDefaultAsync(cancellationToken);
        Guid? unit = (await context.BusinessUnits.UnitsOfUsersAsync([me], cancellationToken)).TryGetValue(me, out var member) ? member : card?.BusinessUnitId;
        Guid[] path = unit is { } id && (await context.BusinessUnits.ListAsync(cancellationToken)).FirstOrDefault(u => u.Id == id) is { } info
            ? [.. info.PathIds]
            : [];
        return new HrReader(me, path, card?.DepartmentKey);
    }

    /// <summary>
    /// Okura yönelik içerik: hiç hedefi yok, ya da hedeflerden biri okura uyuyor (departmanı ya da üst departmanı, kendisi, eşlenmemiş eski İK
    /// departmanı ya da bölüm 22'den kalan tek departman kolonu).
    /// </summary>
    public static IQueryable<T> Visible<T>(IQueryable<T> source, HrReader reader, IQueryable<AudienceTarget> targets)
        where T : class, IAudienceContent
    {
        var path = reader.UnitPath;
        var legacy = reader.LegacyDepartmentKey;
        var me = reader.UserId;
        return source.Where(c =>
            (c.AudienceBusinessUnitId == null && c.AudienceDepartmentKey == null && !targets.Any(t => t.ContentId == c.Id))
            || (c.AudienceDepartmentKey != null && c.AudienceDepartmentKey == legacy)
            || (c.AudienceBusinessUnitId != null && path.Contains(c.AudienceBusinessUnitId.Value))
            || targets.Any(t => t.ContentId == c.Id && ((t.BusinessUnitId != null && path.Contains(t.BusinessUnitId.Value)) || t.UserId == me)));
    }

    /// <summary>İçeriklerin hedefleri (tek departman kolonundaki eski hedef dahil); listede ve okumada göstermek için.</summary>
    public static async Task<Dictionary<Guid, AudienceSet>> LoadAsync<T>(HrStoreContext context, IReadOnlyCollection<T> contents, CancellationToken cancellationToken)
        where T : IAudienceContent
    {
        Guid[] ids = [.. contents.Select(c => c.Id)];
        var rows = await context.Data.Set<AudienceTarget>().AsNoTracking().Where(t => ids.Contains(t.ContentId))
            .Select(t => new { t.ContentId, t.BusinessUnitId, t.UserId }).ToListAsync(cancellationToken);
        var byContent = rows.ToLookup(r => r.ContentId);
        return contents.ToDictionary(c => c.Id, c => new AudienceSet(
            [.. (c.AudienceBusinessUnitId is { } legacyUnit ? [legacyUnit] : Array.Empty<Guid>())
                .Concat(byContent[c.Id].Select(r => r.BusinessUnitId).OfType<Guid>()).Distinct()],
            [.. byContent[c.Id].Select(r => r.UserId).OfType<Guid>().Distinct()]));
    }

    /// <summary>
    /// İstenen hedefleri yazar (içerikle aynı işlemde): verilen tür (departmanlar ve/veya kullanıcılar) tamamen değiştirilir, verilmeyen tür olduğu gibi
    /// kalır. Yeni seçilen departman ağaçta bulunmalı ve etkin olmalı (kapalı departmanda kalan eski hedef korunur); yeni seçilen kullanıcı etkin üye
    /// olmalıdır. <paramref name="previous"/> içeriğin şimdiki hedefleridir (tek departman kolonundaki eski hedef dahil). Yeni hedef kümesi döner.
    /// </summary>
    public static async Task<AudienceSet> ApplyAsync(
        HrStoreContext context, Guid contentId, AudienceSet previous, IReadOnlyList<Guid>? units, string unitField, IReadOnlyList<Guid>? users,
        CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (units is not null)
        {
            if (units.Count > AudienceTarget.MaxUnits)
            {
                errors.Add(new RecordFieldError(unitField, $"En çok {AudienceTarget.MaxUnits} departman seçilir."));
            }

            var tree = (await context.BusinessUnits.ListAsync(cancellationToken)).ToDictionary(u => u.Id);
            foreach (var unit in units.Except(previous.Units))
            {
                if (!tree.TryGetValue(unit, out var info) || !info.IsActive)
                {
                    errors.Add(new RecordFieldError(unitField, info is null ? "Departman bulunamadı." : "Kapalı departman seçilemez."));
                    break;
                }
            }
        }

        if (users is not null)
        {
            if (users.Count > AudienceTarget.MaxUsers)
            {
                errors.Add(new RecordFieldError("audienceUserIds", $"En çok {AudienceTarget.MaxUsers} kişi seçilir."));
            }

            foreach (var user in users.Except(previous.Users))
            {
                if (!await context.Members.IsActiveMemberAsync(user, cancellationToken))
                {
                    errors.Add(new RecordFieldError("audienceUserIds", "Seçilen kullanıcı bu kuruluşun etkin üyesi değil."));
                    break;
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        var set = context.Data.Set<AudienceTarget>();
        var rows = await set.Where(t => t.ContentId == contentId).ToListAsync(cancellationToken);
        if (units is not null)
        {
            set.RemoveRange(rows.Where(r => r.BusinessUnitId is { } unit && !units.Contains(unit)));
            foreach (var unit in units.Except(rows.Select(r => r.BusinessUnitId).OfType<Guid>()))
            {
                set.Add(new AudienceTarget { ContentId = contentId, BusinessUnitId = unit });
            }
        }

        if (users is not null)
        {
            set.RemoveRange(rows.Where(r => r.UserId is { } user && !users.Contains(user)));
            foreach (var user in users.Except(rows.Select(r => r.UserId).OfType<Guid>()))
            {
                set.Add(new AudienceTarget { ContentId = contentId, UserId = user });
            }
        }

        return new AudienceSet(units ?? previous.Units, users ?? previous.Users);
    }

    /// <summary>İçerik silinirken hedefleri de silinir.</summary>
    public static async Task RemoveAsync(HrStoreContext context, Guid contentId, CancellationToken cancellationToken) =>
        context.Data.Set<AudienceTarget>().RemoveRange(
            await context.Data.Set<AudienceTarget>().Where(t => t.ContentId == contentId).ToListAsync(cancellationToken));

    /// <summary>Hedef kümesinin okunur adı: departmanların ağaç yolları ve kişi sayısı ("Merkez › Satış, Finans · 3 kişi"); hedef yoksa boş.</summary>
    public static string? Name(AudienceSet audience, IReadOnlyDictionary<Guid, string> unitNames)
    {
        List<string> parts = [.. audience.Units.Select(u => unitNames.GetValueOrDefault(u, "Bilinmeyen departman"))];
        var units = string.Join(", ", parts);
        return (units.Length, audience.Users.Count) switch
        {
            (0, 0) => null,
            (0, var people) => $"{people} kişi",
            (_, 0) => units,
            (_, var people) => $"{units} · {people} kişi",
        };
    }

    /// <summary>Kimlik listesi JSON'u (okuma için).</summary>
    public static JsonArray Ids(IEnumerable<Guid> ids) => [.. ids.Select(id => (JsonNode?)JsonValue.Create(id.ToString()))];
}

/// <summary>
/// Duyuru ve anket kayıt depolarının ortak hedef alanları (ADR-0042 bölüm 23c):
/// <list type="bullet">
/// <item><c>audienceBusinessUnitIds</c>: departmanlar (alt departmanlar dahil).</item>
/// <item><c>audienceUserIds</c>: kişiler.</item>
/// <item><c>audienceBusinessUnitId</c>: tek departman; bölüm 22 uyumluluğu için tek elemanlı liste gibi yazılır, okunurken hedef tam olarak tek
/// departmansa doludur.</item>
/// <item><c>audienceName</c>: okunur ad.</item>
/// <item><c>audienceDepartmentKey</c>: eski İK departmanı, salt okunur.</item>
/// </list>
/// Depo başına bir örnek; kayıt okunmadan önce <see cref="PrepareAsync"/>, yazıldıktan sonra <see cref="AppliedAsync"/> çağrılır.
/// </summary>
internal sealed class AudienceFields<T>(HrStoreContext context, Action<T> clearUnitColumn, Action<T> clearLegacyKey)
    where T : HrEntity, IAudienceContent
{
    private Dictionary<Guid, AudienceSet> audiences = [];
    private Dictionary<Guid, string> unitNames = [];
    private Guid? single;
    private IReadOnlyList<Guid>? units;
    private IReadOnlyList<Guid>? users;

    public IEnumerable<RecordField<T>> Fields() =>
    [
        Field.Id<T>("audienceBusinessUnitId", e => Of(e) is { Units: [var only], Users.Count: 0 } ? only : null, (_, v) => single = v),
        Field.IdList<T>("audienceBusinessUnitIds", AudienceTarget.MaxUnits, e => HrAudience.Ids(Of(e).Units), (_, v) => units = v),
        Field.IdList<T>("audienceUserIds", AudienceTarget.MaxUsers, e => HrAudience.Ids(Of(e).Users), (_, v) => users = v),
        Field.Computed<T>("audienceName", e => HrAudience.Name(Of(e), unitNames)),
        Field.Computed<T>("audienceDepartmentKey", e => e.AudienceDepartmentKey),
    ];

    public async Task PrepareAsync(IReadOnlyCollection<T> entities, CancellationToken cancellationToken)
    {
        foreach (var (id, audience) in await HrAudience.LoadAsync(context, entities, cancellationToken))
        {
            audiences[id] = audience;
        }

        await LoadNamesAsync(cancellationToken);
    }

    /// <summary>
    /// Hedef alanlarından biri gönderildiyse hedefleri yazar. Departmanlar seçilince eski İK departmanı ve tek departman kolonu temizlenir (kolondaki
    /// eski hedef satıra taşınır).
    /// </summary>
    public async Task AppliedAsync(T entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var listed = values.ContainsKey("audienceBusinessUnitIds");
        IReadOnlyList<Guid>? requestedUnits = listed ? units ?? []
            : values.ContainsKey("audienceBusinessUnitId") ? single is { } one ? [one] : []
            : null;
        IReadOnlyList<Guid>? requestedUsers = values.ContainsKey("audienceUserIds") ? users ?? [] : null;
        (single, units, users) = (null, null, null);
        if (requestedUnits is null && requestedUsers is null)
        {
            return;
        }

        var previous = isNew ? AudienceSet.Empty : Of(entity);

        // Tek departman kolonundaki eski hedef satıra taşınır (kolon temizlenecek).
        requestedUnits ??= entity.AudienceBusinessUnitId is null ? null : previous.Units;
        var audience = await HrAudience.ApplyAsync(context, entity.Id, previous, requestedUnits,
            listed ? "audienceBusinessUnitIds" : "audienceBusinessUnitId", requestedUsers, cancellationToken);
        clearUnitColumn(entity);
        if (requestedUnits is not null)
        {
            clearLegacyKey(entity);
        }

        audiences[entity.Id] = audience;
        await LoadNamesAsync(cancellationToken);
    }

    private AudienceSet Of(T entity) =>
        audiences.TryGetValue(entity.Id, out var audience) ? audience
        : entity.AudienceBusinessUnitId is { } unit ? new AudienceSet([unit], []) : AudienceSet.Empty;

    private async Task LoadNamesAsync(CancellationToken cancellationToken)
    {
        if (audiences.Values.SelectMany(a => a.Units).Any(u => !unitNames.ContainsKey(u)))
        {
            unitNames = await BusinessUnitNames.PathsAsync(context, [.. audiences.Values.SelectMany(a => a.Units).Select(u => (Guid?)u)], cancellationToken);
        }
    }
}
