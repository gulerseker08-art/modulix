using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customs;
using Modulix.Core.Customs;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Customs;

/// <summary>
/// Rejim profili yönetimi (ADR-0032 K16). Tablolar <c>platform</c> şemasındadır, RLS dışıdır ve tenant verisi içermez. Profil başına en çok bir
/// taslak ve bir yayın vardır (filtreli benzersiz dizin); yayın, önceki yayını "yerini bıraktı" yapar ve geçmiş olarak kalır. Her yazma
/// değiştiren yönetici ile birlikte <c>PlatformAuditLog</c>'a aynı işlemde yazılır.
/// </summary>
internal sealed class RegimeProfileService(ModulixDbContext db) : IRegimeProfileAdmin, IRegimeProfileCatalog
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // ---- Okuma (modüller): yalnızca etkin profilin yayındaki sürümü ----

    public async Task<IReadOnlyList<PublishedRegimeProfile>> ListPublishedAsync(CancellationToken cancellationToken)
    {
        // Birkaç profil vardır; sıralama bellekte yapılır (EF kayıt projeksiyonundan sonraki sıralamayı çeviremez).
        var rows = await PublishedQuery(null).ToListAsync(cancellationToken);
        return [.. rows.OrderBy(r => r.Profile.Name, StringComparer.CurrentCultureIgnoreCase).Select(ToPublished)];
    }

    public async Task<PublishedRegimeProfile?> GetPublishedAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var row = await PublishedQuery(profileId).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToPublished(row);
    }

    // Süzgeç projeksiyondan önce uygulanır (EF kayıt tipine projeksiyondan sonraki koşulu çeviremez).
    private IQueryable<PublishedRow> PublishedQuery(Guid? profileId) =>
        from profile in db.RegimeProfiles.AsNoTracking()
        join version in db.RegimeProfileVersions.AsNoTracking() on profile.Id equals version.ProfileId
        where profile.IsActive && version.State == RegimeProfileVersionState.Published && (profileId == null || profile.Id == profileId)
        select new PublishedRow(profile, version);

    private static PublishedRegimeProfile ToPublished(PublishedRow row)
    {
        var view = ToView(row.Version);
        return new PublishedRegimeProfile(row.Profile.Id, row.Profile.Code, row.Profile.Name, row.Version.Id, row.Version.Number, row.Version.IsVerified, row.Version.VerificationNote, view.Content);
    }

    private sealed record PublishedRow(RegimeProfile Profile, RegimeProfileVersion Version);

    // ---- Yönetim (platform) ----

    public async Task<IReadOnlyList<RegimeProfileSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var profiles = await db.RegimeProfiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken);
        var versions = await db.RegimeProfileVersions.AsNoTracking()
            .Where(v => v.State != RegimeProfileVersionState.Superseded)
            .Select(v => new { v.ProfileId, v.Number, v.State, v.IsVerified }).ToListAsync(cancellationToken);
        return [.. profiles.Select(p => Summarize(p, versions.Where(v => v.ProfileId == p.Id)
            .Select(v => (v.Number, v.State, v.IsVerified))))];
    }

    public async Task<RegimeProfileDetail> GetAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var profile = await FindAsync(profileId, cancellationToken);
        var versions = await db.RegimeProfileVersions.AsNoTracking().Where(v => v.ProfileId == profileId)
            .OrderByDescending(v => v.Number).ToListAsync(cancellationToken);
        return new RegimeProfileDetail(
            Summarize(profile, versions.Where(v => v.State != RegimeProfileVersionState.Superseded).Select(v => (v.Number, v.State, v.IsVerified))),
            [.. versions.Select(ToView)]);
    }

    public async Task<RegimeProfileSummary> CreateAsync(Guid actingUserId, string code, string name, CancellationToken cancellationToken)
    {
        var profile = new RegimeProfile(code, name);
        if (await db.RegimeProfiles.AnyAsync(p => p.Code == profile.Code, cancellationToken))
        {
            throw new RegimeProfileConflictException($"Bu kodla bir rejim profili zaten var: '{profile.Code}'.");
        }

        db.RegimeProfiles.Add(profile);
        Audit(actingUserId, "regime-profile.created", new JsonObject { ["code"] = profile.Code, ["name"] = profile.Name });
        await db.SaveChangesAsync(cancellationToken);
        return Summarize(profile, []);
    }

    public async Task<RegimeProfileVersionView> SaveDraftAsync(Guid actingUserId, Guid profileId, RegimeProfileContent content, CancellationToken cancellationToken)
    {
        var profile = await FindAsync(profileId, cancellationToken);
        if (RegimeProfileRules.Validate(content) is { Count: > 0 } errors)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(content));
        }

        var json = JsonSerializer.Serialize(content, Json);
        var draft = await db.RegimeProfileVersions.SingleOrDefaultAsync(v => v.ProfileId == profileId && v.State == RegimeProfileVersionState.Draft, cancellationToken);
        if (draft is null)
        {
            var next = (await db.RegimeProfileVersions.Where(v => v.ProfileId == profileId).MaxAsync(v => (int?)v.Number, cancellationToken) ?? 0) + 1;
            draft = new RegimeProfileVersion(profileId, next, json, actingUserId);
            db.RegimeProfileVersions.Add(draft);
        }
        else
        {
            draft.ReplaceContent(json);
        }

        Audit(actingUserId, "regime-profile.draft-saved", new JsonObject { ["code"] = profile.Code, ["version"] = draft.Number });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Aynı anda ikinci bir taslak açıldı (profil başına tek taslak).
            throw new RegimeProfileConflictException("Taslak başka bir yönetici tarafından açıldı; sayfayı yenileyip tekrar deneyin.");
        }

        return ToView(draft);
    }

    public async Task DiscardDraftAsync(Guid actingUserId, Guid profileId, CancellationToken cancellationToken)
    {
        var profile = await FindAsync(profileId, cancellationToken);
        var draft = await db.RegimeProfileVersions.SingleOrDefaultAsync(v => v.ProfileId == profileId && v.State == RegimeProfileVersionState.Draft, cancellationToken)
            ?? throw new RegimeProfileNotFoundException("Silinecek taslak yok.");
        db.RegimeProfileVersions.Remove(draft);
        Audit(actingUserId, "regime-profile.draft-discarded", new JsonObject { ["code"] = profile.Code, ["version"] = draft.Number });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<RegimeProfileVersionView> PublishAsync(Guid actingUserId, Guid profileId, bool verified, string? note, CancellationToken cancellationToken)
    {
        var profile = await FindAsync(profileId, cancellationToken);
        if (note is { Length: > RegimeProfileVersion.NoteMaxLength })
        {
            throw new ArgumentException($"Doğrulama notu en çok {RegimeProfileVersion.NoteMaxLength} karakter olabilir.", nameof(note));
        }

        if (verified && string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException("Doğrulanmış olarak yayınlarken kimin/neye göre doğruladığını not olarak yazın.", nameof(note));
        }

        var draft = await db.RegimeProfileVersions.SingleOrDefaultAsync(v => v.ProfileId == profileId && v.State == RegimeProfileVersionState.Draft, cancellationToken)
            ?? throw new RegimeProfileConflictException("Yayınlanacak taslak yok; önce içeriği taslak olarak kaydedin.");

        // Taslağın içeriği yayın anında yeniden doğrulanır (kural paketi bir kez yazılıp sonra bozulmuş olamaz).
        var content = JsonSerializer.Deserialize<RegimeProfileContent>(draft.ContentJson, Json);
        if (RegimeProfileRules.Validate(content) is { Count: > 0 } errors)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(profileId));
        }

        // Önce eski yayın yerini bırakır, sonra taslak yayına çıkar: "profil başına tek yayın" dizini iki adımda da korunur.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var previous = await db.RegimeProfileVersions.SingleOrDefaultAsync(v => v.ProfileId == profileId && v.State == RegimeProfileVersionState.Published, cancellationToken);
        if (previous is not null)
        {
            previous.Supersede();
            await db.SaveChangesAsync(cancellationToken);
        }

        draft.Publish(verified, note, actingUserId);
        Audit(actingUserId, "regime-profile.published", new JsonObject
        {
            ["code"] = profile.Code, ["version"] = draft.Number, ["verified"] = verified, ["note"] = draft.VerificationNote,
            ["previousVersion"] = previous?.Number,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToView(draft);
    }

    public async Task<RegimeProfileSummary> SetActiveAsync(Guid actingUserId, Guid profileId, bool active, CancellationToken cancellationToken)
    {
        var profile = await FindAsync(profileId, cancellationToken, track: true);
        if (profile.IsActive != active)
        {
            profile.SetActive(active);
            Audit(actingUserId, "regime-profile.active", new JsonObject { ["code"] = profile.Code, ["active"] = active });
            await db.SaveChangesAsync(cancellationToken);
        }

        return (await GetAsync(profileId, cancellationToken)).Summary;
    }

    private async Task<RegimeProfile> FindAsync(Guid profileId, CancellationToken cancellationToken, bool track = false)
    {
        var query = track ? db.RegimeProfiles : db.RegimeProfiles.AsNoTracking();
        return await query.SingleOrDefaultAsync(p => p.Id == profileId, cancellationToken)
            ?? throw new RegimeProfileNotFoundException("Rejim profili bulunamadı.");
    }

    private static RegimeProfileSummary Summarize(RegimeProfile profile, IEnumerable<(int Number, RegimeProfileVersionState State, bool IsVerified)> openVersions)
    {
        var list = openVersions.ToList();
        var published = list.FirstOrDefault(v => v.State == RegimeProfileVersionState.Published);
        return new RegimeProfileSummary(
            profile.Id, profile.Code, profile.Name, profile.IsActive,
            list.Any(v => v.State == RegimeProfileVersionState.Published) ? published.Number : null,
            published.IsVerified, list.Any(v => v.State == RegimeProfileVersionState.Draft));
    }

    internal static RegimeProfileVersionView ToView(RegimeProfileVersion v) =>
        new(v.Id, v.Number, v.State, v.IsVerified, v.VerificationNote, v.CreatedAt, v.PublishedAt,
            JsonSerializer.Deserialize<RegimeProfileContent>(v.ContentJson, Json) ?? new RegimeProfileContent(RegimeProfileContent.CurrentSchemaVersion, null, null));

    private void Audit(Guid actingUserId, string action, JsonObject details) =>
        db.PlatformAuditLog.Add(new PlatformAuditEntry(actingUserId, action, null, null, details.ToJsonString(), Activity.Current?.TraceId.ToString()));
}
