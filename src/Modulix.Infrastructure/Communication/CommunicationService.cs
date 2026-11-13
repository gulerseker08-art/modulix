using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Communication;
using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Communication;

/// <summary>
/// Ortak iletişim tercihi ve engel servisi (ADR-0025). Hem okuma sözleşmesini (<see cref="ICommunicationPreferences"/>, modüller
/// kullanır) hem yönetimi (<see cref="ICommunicationAdministration"/>, API kullanır) uygular. Tenant her çağrıda oturumdan alınır;
/// değişiklik, olay satırı ve denetim kaydı tek <c>SaveChanges</c> ile yazılır. Denetim kaydında adres maskelidir.
/// </summary>
internal sealed class CommunicationService(
    ModulixDbContext db, ITenantContextAccessor tenantAccessor, IAuditWriter audit, TimeProvider time)
    : ICommunicationPreferences, ICommunicationAdministration, ICommunicationTopics
{
    private const int MaxPageSize = 100;

    public async Task<CommunicationDecision> EvaluateAsync(
        CommunicationChannel channel, CommunicationPurpose purpose, string address, CancellationToken cancellationToken)
    {
        var decisions = await EvaluateManyAsync(channel, purpose, [address], cancellationToken);
        return decisions[address];
    }

    public Task<IReadOnlyDictionary<string, CommunicationDecision>> EvaluateManyAsync(
        CommunicationChannel channel, CommunicationPurpose purpose, IReadOnlyCollection<string> addresses, CancellationToken cancellationToken) =>
        EvaluateManyAsync(channel, purpose, addresses, null, cancellationToken);

    public async Task<IReadOnlyDictionary<string, CommunicationDecision>> EvaluateManyAsync(
        CommunicationChannel channel, CommunicationPurpose purpose, IReadOnlyCollection<string> addresses, string? topic, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        RequireTenant();
        if (topic is not null && TopicRules.ValidateKey(topic) is { } topicError)
        {
            throw new ArgumentException(topicError, nameof(topic));
        }

        var now = time.GetUtcNow();
        var result = new Dictionary<string, CommunicationDecision>(addresses.Count, StringComparer.Ordinal);
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in addresses.Distinct(StringComparer.Ordinal))
        {
            if (CommunicationAddress.Normalize(channel, raw) is { } address)
            {
                normalized[raw] = address;
            }
            else
            {
                result[raw] = new CommunicationDecision(CommunicationOutcome.Denied, CommunicationCodes.InvalidAddress, "Adres geçersiz.");
            }
        }

        // Kayıt sayısı büyük olabilir (kampanya alıcıları): sorgular parçalar halinde, adres kümesine indeksle yapılır.
        var statuses = new Dictionary<string, ConsentStatus>(StringComparer.Ordinal);
        var suppressions = new Dictionary<string, List<SuppressionReason>>(StringComparer.Ordinal);
        var topicOptOuts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in normalized.Values.Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            if (topic is not null && purpose == CommunicationPurpose.Marketing)
            {
                topicOptOuts.UnionWith(await db.CommunicationTopicPreferences.AsNoTracking()
                    .Where(p => p.Channel == channel && p.TopicKey == topic && p.OptedOut && chunk.Contains(p.Address))
                    .Select(p => p.Address)
                    .ToListAsync(cancellationToken));
            }

            if (purpose == CommunicationPurpose.Marketing)
            {
                var rows = await db.CommunicationPreferences.AsNoTracking()
                    .Where(p => p.Channel == channel && p.Purpose == CommunicationPurpose.Marketing && chunk.Contains(p.Address))
                    .Select(p => new { p.Address, p.Status })
                    .ToListAsync(cancellationToken);
                foreach (var row in rows)
                {
                    statuses[row.Address] = row.Status;
                }
            }

            var blocked = await db.CommunicationSuppressions.AsNoTracking()
                .Where(s => s.Channel == channel && chunk.Contains(s.Address)
                    && s.LiftedAt == null && s.StartsAt <= now && (s.EndsAt == null || s.EndsAt > now))
                .Select(s => new { s.Address, s.Reason })
                .ToListAsync(cancellationToken);
            foreach (var row in blocked)
            {
                if (!suppressions.TryGetValue(row.Address, out var reasons))
                {
                    suppressions[row.Address] = reasons = [];
                }

                reasons.Add(row.Reason);
            }
        }

        foreach (var (raw, address) in normalized)
        {
            var decision = CommunicationRules.Decide(
                purpose, statuses.GetValueOrDefault(address), suppressions.TryGetValue(address, out var reasons) ? reasons : []);
            result[raw] = TopicRules.ApplyTopic(decision, purpose, topicOptOuts.Contains(address));
        }

        return result;
    }

    public async Task<bool> IsActiveAsync(string key, CancellationToken cancellationToken)
    {
        RequireTenant();
        return TopicRules.ValidateKey(key) is null && await db.CommunicationTopics.AsNoTracking().AnyAsync(t => t.Key == key && t.IsActive, cancellationToken);
    }

    public async Task<CommunicationPage<PreferenceView>> ListPreferencesAsync(PreferenceQuery query, CancellationToken cancellationToken)
    {
        RequireTenant();
        var source = db.CommunicationPreferences.AsNoTracking().AsQueryable();
        if (query.Channel is { } channel)
        {
            source = source.Where(p => p.Channel == channel);
        }

        if (query.Purpose is { } purpose)
        {
            source = source.Where(p => p.Purpose == purpose);
        }

        if (query.Status is { } status)
        {
            source = source.Where(p => p.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Address))
        {
            var term = query.Address.Trim().ToLowerInvariant();
            source = source.Where(p => p.Address.Contains(term));
        }

        var (page, size) = Paging(query.Page, query.PageSize);
        var total = await source.CountAsync(cancellationToken);
        var rows = await source.OrderByDescending(p => p.ChangedAt).ThenBy(p => p.Id).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        return new CommunicationPage<PreferenceView>([.. rows.Select(ToView)], page, size, total);
    }

    public async Task<PreferenceView> RecordConsentAsync(ConsentChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var tenant = RequireTenant();
        var address = CommunicationAddress.Normalize(change.Channel, change.Address)
            ?? throw new ArgumentException("Adres geçersiz.", nameof(change.Address));
        var source = change.Source?.Trim().ToLowerInvariant() ?? string.Empty;
        var evidence = Clean(change.EvidenceRef);
        var reason = Clean(change.Reason);

        for (var attempt = 0; ; attempt++)
        {
            var preference = await db.CommunicationPreferences.SingleOrDefaultAsync(
                p => p.Channel == change.Channel && p.Address == address && p.Purpose == change.Purpose, cancellationToken);
            var current = preference?.Status ?? ConsentStatus.Unknown;
            if (CommunicationRules.ValidateChange(current, change.Status, source, evidence, reason) is { } error)
            {
                throw new ArgumentException(error, nameof(change.Status));
            }

            // Aynı durumun aynı kaynak ve kanıtla yeniden kaydı olay ve denetim üretmez (tekrarlı istek güvenlidir).
            if (preference is not null && preference.Status == change.Status && preference.Source == source && preference.EvidenceRef == evidence)
            {
                return ToView(preference);
            }

            var now = time.GetUtcNow();

            // Oturumsuz tercih merkezinde kullanıcı yoktur (Guid.Empty): değişikliği yapan kişi bilinmez, kaynak ve kanıt alanları yeterlidir.
            var actor = tenant.UserId == Guid.Empty ? (Guid?)null : tenant.UserId;
            var isNew = preference is null;
            preference ??= new CommunicationPreference(change.Channel, address, change.Purpose, now);
            var before = isNew ? null : Snapshot(preference, null);
            preference.Apply(change.Status, source, evidence, change.PartyId, change.PersonId, actor, now);
            if (isNew)
            {
                db.CommunicationPreferences.Add(preference);
            }

            db.ConsentEvents.Add(new ConsentEvent(preference.Id, current, change.Status, source, evidence, reason, actor, now));
            AddAudit(tenant, CoreModule.AuditedEntityKeys.CommunicationPreference, preference.Id, isNew ? AuditAction.Created : AuditAction.Updated, before, Snapshot(preference, reason));

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ToView(preference);
            }
            catch (DbUpdateException) when (isNew && attempt == 0)
            {
                // Aynı iletişim noktası için eşzamanlı ilk kayıt: benzersiz indeksi kaybettik; mevcut satırla yeniden değerlendir.
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<IReadOnlyList<ConsentEventView>?> EventsAsync(Guid preferenceId, CancellationToken cancellationToken)
    {
        RequireTenant();
        if (!await db.CommunicationPreferences.AsNoTracking().AnyAsync(p => p.Id == preferenceId, cancellationToken))
        {
            return null;
        }

        var rows = await db.ConsentEvents.AsNoTracking()
            .Where(e => e.PreferenceId == preferenceId)
            .OrderByDescending(e => e.ChangedAt).ThenByDescending(e => e.Id)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(e => new ConsentEventView(e.Id, e.FromStatus, e.ToStatus, e.Source, e.EvidenceRef, e.Reason, e.ChangedBy, e.ChangedAt))];
    }

    public async Task<CommunicationPage<SuppressionView>> ListSuppressionsAsync(SuppressionQuery query, CancellationToken cancellationToken)
    {
        RequireTenant();
        var now = time.GetUtcNow();
        var source = db.CommunicationSuppressions.AsNoTracking().AsQueryable();
        if (query.Channel is { } channel)
        {
            source = source.Where(s => s.Channel == channel);
        }

        if (!string.IsNullOrWhiteSpace(query.Address))
        {
            var term = query.Address.Trim().ToLowerInvariant();
            source = source.Where(s => s.Address.Contains(term));
        }

        if (query.ActiveOnly is { } activeOnly)
        {
            source = activeOnly
                ? source.Where(s => s.LiftedAt == null && s.StartsAt <= now && (s.EndsAt == null || s.EndsAt > now))
                : source.Where(s => !(s.LiftedAt == null && s.StartsAt <= now && (s.EndsAt == null || s.EndsAt > now)));
        }

        var (page, size) = Paging(query.Page, query.PageSize);
        var total = await source.CountAsync(cancellationToken);
        var rows = await source.OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        return new CommunicationPage<SuppressionView>([.. rows.Select(s => ToView(s, now))], page, size, total);
    }

    public async Task<SuppressionView> SuppressAsync(SuppressionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenant = RequireTenant();
        var address = CommunicationAddress.Normalize(request.Channel, request.Address)
            ?? throw new ArgumentException("Adres geçersiz.", nameof(request.Address));
        var source = request.Source?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!ConsentSources.All.Contains(source))
        {
            throw new ArgumentException("Kaynak geçersiz.", nameof(request.Source));
        }

        var note = Clean(request.Note);
        if (note?.Length > CommunicationSuppression.NoteMaxLength)
        {
            throw new ArgumentException($"Not en fazla {CommunicationSuppression.NoteMaxLength} karakter olabilir.", nameof(request.Note));
        }

        var now = time.GetUtcNow();
        if (request.EndsAt is { } endsAt && endsAt <= now)
        {
            throw new ArgumentException("Bitiş zamanı geçmişte olamaz.", nameof(request.EndsAt));
        }

        // Aynı kanal, adres ve nedenle aktif engel varsa yenisi açılmaz (tekrarlı bounce/şikâyet bildirimi güvenlidir).
        var existing = await db.CommunicationSuppressions.AsNoTracking()
            .Where(s => s.Channel == request.Channel && s.Address == address && s.Reason == request.Reason
                && s.LiftedAt == null && s.StartsAt <= now && (s.EndsAt == null || s.EndsAt > now))
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return ToView(existing, now);
        }

        var suppression = new CommunicationSuppression(request.Channel, address, request.Reason, source, note, now, request.EndsAt, tenant.UserId, now);
        db.CommunicationSuppressions.Add(suppression);
        AddAudit(tenant, CoreModule.AuditedEntityKeys.CommunicationSuppression, suppression.Id, AuditAction.Created, null, Snapshot(suppression));
        await db.SaveChangesAsync(cancellationToken);
        return ToView(suppression, now);
    }

    public async Task<SuppressionView?> LiftAsync(Guid suppressionId, string reason, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var text = Clean(reason);
        if (text is null)
        {
            throw new ArgumentException("Engeli kaldırmak için gerekçe zorunludur.", nameof(reason));
        }

        if (text.Length > CommunicationPreference.ReasonMaxLength)
        {
            throw new ArgumentException($"Gerekçe en fazla {CommunicationPreference.ReasonMaxLength} karakter olabilir.", nameof(reason));
        }

        var suppression = await db.CommunicationSuppressions.SingleOrDefaultAsync(s => s.Id == suppressionId, cancellationToken);
        if (suppression is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (suppression.LiftedAt is null)
        {
            var before = Snapshot(suppression);
            suppression.Lift(text, tenant.UserId, now);
            AddAudit(tenant, CoreModule.AuditedEntityKeys.CommunicationSuppression, suppression.Id, AuditAction.Updated, before, Snapshot(suppression));
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToView(suppression, now);
    }

    public async Task<IReadOnlyList<TopicView>> ListTopicsAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        RequireTenant();
        var source = db.CommunicationTopics.AsNoTracking().AsQueryable();
        if (activeOnly)
        {
            source = source.Where(t => t.IsActive);
        }

        var rows = await source.OrderBy(t => t.Name).ThenBy(t => t.Key).ToListAsync(cancellationToken);
        return [.. rows.Select(ToView)];
    }

    public async Task<TopicView> SaveTopicAsync(string key, string name, bool isActive, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var cleanKey = key?.Trim() ?? string.Empty;
        if (TopicRules.ValidateKey(cleanKey) is { } keyError)
        {
            throw new ArgumentException(keyError, nameof(key));
        }

        if (TopicRules.ValidateName(name) is { } nameError)
        {
            throw new ArgumentException(nameError, nameof(name));
        }

        var cleanName = name.Trim();
        for (var attempt = 0; ; attempt++)
        {
            var now = time.GetUtcNow();
            var topic = await db.CommunicationTopics.SingleOrDefaultAsync(t => t.Key == cleanKey, cancellationToken);
            if (topic is not null && topic.Name == cleanName && topic.IsActive == isActive)
            {
                return ToView(topic);
            }

            var isNew = topic is null;
            var before = isNew ? null : Snapshot(topic!);
            if (isNew)
            {
                topic = new CommunicationTopic(cleanKey, cleanName, now);
                topic.Apply(cleanName, isActive, now);
                db.CommunicationTopics.Add(topic);
            }
            else
            {
                topic!.Apply(cleanName, isActive, now);
            }

            AddAudit(tenant, CoreModule.AuditedEntityKeys.CommunicationTopic, topic.Id, isNew ? AuditAction.Created : AuditAction.Updated, before, Snapshot(topic));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ToView(topic);
            }
            catch (DbUpdateException) when (isNew && attempt == 0)
            {
                // Aynı anahtarla eşzamanlı ilk kayıt: benzersiz indeksi kaybettik; mevcut satırla yeniden değerlendir.
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<CommunicationPage<TopicPreferenceView>> ListTopicPreferencesAsync(TopicPreferenceQuery query, CancellationToken cancellationToken)
    {
        RequireTenant();
        var source = db.CommunicationTopicPreferences.AsNoTracking().AsQueryable();
        if (query.Channel is { } channel)
        {
            source = source.Where(p => p.Channel == channel);
        }

        if (!string.IsNullOrWhiteSpace(query.TopicKey))
        {
            var topic = query.TopicKey.Trim();
            source = source.Where(p => p.TopicKey == topic);
        }

        if (query.OptedOut is { } optedOut)
        {
            source = source.Where(p => p.OptedOut == optedOut);
        }

        if (!string.IsNullOrWhiteSpace(query.Address))
        {
            var term = query.Address.Trim().ToLowerInvariant();
            source = source.Where(p => p.Address.Contains(term));
        }

        var (page, size) = Paging(query.Page, query.PageSize);
        var total = await source.CountAsync(cancellationToken);
        var rows = await source.OrderByDescending(p => p.ChangedAt).ThenBy(p => p.Id).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        return new CommunicationPage<TopicPreferenceView>([.. rows.Select(ToView)], page, size, total);
    }

    public async Task<TopicPreferenceView> RecordTopicPreferenceAsync(TopicPreferenceChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var tenant = RequireTenant();
        var address = CommunicationAddress.Normalize(change.Channel, change.Address)
            ?? throw new ArgumentException("Adres geçersiz.", nameof(change.Address));
        var source = change.Source?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!ConsentSources.All.Contains(source))
        {
            throw new ArgumentException("Kaynak geçersiz.", nameof(change.Source));
        }

        var topicKey = change.TopicKey?.Trim() ?? string.Empty;
        if (TopicRules.ValidateKey(topicKey) is { } keyError)
        {
            throw new ArgumentException(keyError, nameof(change.TopicKey));
        }

        if (!await db.CommunicationTopics.AsNoTracking().AnyAsync(t => t.Key == topicKey, cancellationToken))
        {
            throw new ArgumentException("Konu tanımlı değil.", nameof(change.TopicKey));
        }

        for (var attempt = 0; ; attempt++)
        {
            var preference = await db.CommunicationTopicPreferences.SingleOrDefaultAsync(
                p => p.Channel == change.Channel && p.Address == address && p.TopicKey == topicKey, cancellationToken);
            var currentOptedOut = preference?.OptedOut ?? false;

            // Aynı değerin yeniden kaydı denetim kaydı üretmez (tekrarlı istek güvenlidir); ilk kaydın kaynağı ezilmez.
            if (currentOptedOut == change.OptedOut && (preference is not null || !change.OptedOut))
            {
                return preference is null
                    ? new TopicPreferenceView(Guid.Empty, change.Channel, address, topicKey, false, source, null, time.GetUtcNow())
                    : ToView(preference);
            }

            // Çıkış her kaynakla kaydedilir (iletişimi azaltmak güvenlidir); çıkışı geri açma yalnızca kişinin kendisi ya da yetkili kullanıcıdır.
            if (currentOptedOut && !change.OptedOut && !ConsentSources.CanReGrant.Contains(source))
            {
                throw new ArgumentException("Konudan çıkış içe aktarma, sağlayıcı ya da sistem kaynağıyla geri açılamaz.", nameof(change.Source));
            }

            var now = time.GetUtcNow();
            var actor = tenant.UserId == Guid.Empty ? (Guid?)null : tenant.UserId;
            var isNew = preference is null;
            preference ??= new CommunicationTopicPreference(change.Channel, address, topicKey, now);
            var before = isNew ? null : Snapshot(preference);
            preference.Apply(change.OptedOut, source, actor, now);
            if (isNew)
            {
                db.CommunicationTopicPreferences.Add(preference);
            }

            AddAudit(tenant, CoreModule.AuditedEntityKeys.CommunicationTopicPreference, preference.Id, isNew ? AuditAction.Created : AuditAction.Updated, before, Snapshot(preference));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ToView(preference);
            }
            catch (DbUpdateException) when (isNew && attempt == 0)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan iletişim tercihleri kullanılamaz.");

    private void AddAudit(TenantContext tenant, string entityKey, Guid recordId, AuditAction action, JsonObject? before, JsonObject? after) =>
        audit.Add(new AuditEntry(
            tenant.TenantId, tenant.UserId, entityKey, recordId, action, RecordDiff.Compute(before, after), ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

    private static JsonObject Snapshot(CommunicationPreference p, string? reason) => new()
    {
        ["channel"] = p.Channel.ToString(),
        ["purpose"] = p.Purpose.ToString(),
        ["address"] = CommunicationAddress.Mask(p.Channel, p.Address),
        ["status"] = p.Status.ToString(),
        ["source"] = p.Source,
        ["evidenceRef"] = p.EvidenceRef,
        ["reason"] = reason,
    };

    private static JsonObject Snapshot(CommunicationSuppression s) => new()
    {
        ["channel"] = s.Channel.ToString(),
        ["address"] = CommunicationAddress.Mask(s.Channel, s.Address),
        ["reason"] = s.Reason.ToString(),
        ["source"] = s.Source,
        ["note"] = s.Note,
        ["endsAt"] = s.EndsAt?.ToString("O"),
        ["lifted"] = s.LiftedAt is not null,
        ["liftReason"] = s.LiftReason,
    };

    private static JsonObject Snapshot(CommunicationTopic t) => new()
    {
        ["key"] = t.Key,
        ["name"] = t.Name,
        ["active"] = t.IsActive,
    };

    private static JsonObject Snapshot(CommunicationTopicPreference p) => new()
    {
        ["channel"] = p.Channel.ToString(),
        ["address"] = CommunicationAddress.Mask(p.Channel, p.Address),
        ["topic"] = p.TopicKey,
        ["optedOut"] = p.OptedOut,
        ["source"] = p.Source,
    };

    private static TopicView ToView(CommunicationTopic t) => new(t.Id, t.Key, t.Name, t.IsActive, t.CreatedAt, t.ChangedAt);

    private static TopicPreferenceView ToView(CommunicationTopicPreference p) =>
        new(p.Id, p.Channel, p.Address, p.TopicKey, p.OptedOut, p.Source, p.ChangedBy, p.ChangedAt);

    private static PreferenceView ToView(CommunicationPreference p) =>
        new(p.Id, p.Channel, p.Address, p.Purpose, p.Status, p.Source, p.EvidenceRef, p.PartyId, p.PersonId, p.ChangedBy, p.ChangedAt);

    private static SuppressionView ToView(CommunicationSuppression s, DateTimeOffset now) => new(
        s.Id, s.Channel, s.Address, s.Reason, s.Source, s.Note, s.StartsAt, s.EndsAt, s.IsActiveAt(now), s.CreatedBy, s.CreatedAt,
        s.LiftedAt, s.LiftedBy, s.LiftReason);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (int Page, int Size) Paging(int page, int size) => (Math.Clamp(page, 1, 100_000), Math.Clamp(size, 1, MaxPageSize));
}
