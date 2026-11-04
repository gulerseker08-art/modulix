namespace Modulix.Core.Platform;

/// <summary>Satışa konu olan sayısal limitler (ADR-0028 Dilim 4). Yeni limit buraya eklenir ve kullanımı ölçülür.</summary>
public static class LimitKeys
{
    /// <summary>Kullanıcı koltuğu: etkin ve davet bekleyen üyelikler. Aşılınca yeni davet ve yeniden etkinleştirme durur; mevcut kullanıcılar etkilenmez.</summary>
    public const string Seats = "seats";

    public static readonly IReadOnlyList<LimitDefinition> All = [new(Seats, "Kullanıcı koltuğu")];

    public static bool IsKnown(string key) => All.Any(l => l.Key == key);

    public static string NameOf(string key) => All.FirstOrDefault(l => l.Key == key)?.Name ?? key;
}

public sealed record LimitDefinition(string Key, string Name);

/// <summary>
/// Satış paketi (ADR-0028): modül seti ve limitler. Platform geneli kayıttır; silinmez, <see cref="IsActive"/> kapatılarak
/// yeni aboneliklere kapatılır. Plan değişikliği mevcut abonelerin modül haklarını kendiliğinden değiştirmez (plan yeniden
/// atanınca geçerli olur); limitler ise her okumada güncel plandan çözülür.
/// </summary>
public sealed class Plan
{
    public const int KeyMaxLength = 40;
    public const int NameMaxLength = 100;

    private Plan()
    {
        // EF Core için
    }

    public Plan(string key, string name, IEnumerable<string> modules, IReadOnlyDictionary<string, int>? limits)
    {
        Id = Guid.CreateVersion7();
        CreatedAt = DateTimeOffset.UtcNow;
        Key = ValidateKey(key);
        IsActive = true;
        Update(name, modules, limits, isActive: true);
    }

    public Guid Id { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public List<PlanModule> Modules { get; private set; } = [];

    public List<PlanLimit> Limits { get; private set; } = [];

    /// <summary>Ad, modüller, limitler ve etkinlik birlikte değişir; kayıt silinmez. Limit yoksa sınırsızdır.</summary>
    public void Update(string name, IEnumerable<string> modules, IReadOnlyDictionary<string, int>? limits, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > NameMaxLength)
        {
            throw new ArgumentException($"Plan adı 1-{NameMaxLength} karakter olmalı.", "name");
        }

        var moduleKeys = modules.Select(m => m.Trim()).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        foreach (var limit in limits ?? new Dictionary<string, int>())
        {
            if (!LimitKeys.IsKnown(limit.Key))
            {
                throw new ArgumentException($"Bilinmeyen limit: '{limit.Key}'.", "limits");
            }

            if (limit.Value < 0)
            {
                throw new ArgumentException("Limit eksi olamaz.", "limits");
            }
        }

        Name = trimmed;
        IsActive = isActive;
        UpdatedAt = DateTimeOffset.UtcNow;

        // Alt kayıtlar plan düzeyinde yeniden kurulur (bunlar plan tanımıdır, firma verisi değildir).
        Modules.RemoveAll(m => !moduleKeys.Contains(m.ModuleKey));
        foreach (var key in moduleKeys.Where(k => Modules.All(m => m.ModuleKey != k)))
        {
            Modules.Add(new PlanModule(Id, key));
        }

        var wanted = limits ?? new Dictionary<string, int>();
        Limits.RemoveAll(l => !wanted.ContainsKey(l.LimitKey));
        foreach (var (key, value) in wanted)
        {
            if (Limits.FirstOrDefault(l => l.LimitKey == key) is { } existing)
            {
                existing.Value = value;
            }
            else
            {
                Limits.Add(new PlanLimit(Id, key, value));
            }
        }
    }

    private static string ValidateKey(string key)
    {
        var trimmed = (key ?? string.Empty).Trim().ToLowerInvariant();
        if (trimmed.Length is 0 or > KeyMaxLength || trimmed.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')))
        {
            throw new ArgumentException($"Plan anahtarı küçük harf, rakam ve '-' içeren 1-{KeyMaxLength} karakter olmalı.", "key");
        }

        return trimmed;
    }
}

public sealed class PlanModule(Guid planId, string moduleKey)
{
    public Guid PlanId { get; private set; } = planId;

    public string ModuleKey { get; private set; } = moduleKey;
}

public sealed class PlanLimit(Guid planId, string limitKey, int value)
{
    public Guid PlanId { get; private set; } = planId;

    public string LimitKey { get; private set; } = limitKey;

    public int Value { get; set; } = value;
}

/// <summary>
/// Firmanın bir plana aboneliği (ADR-0028). Firma başına en çok bir geçerli (<see cref="EndedAt"/> boş) kayıt vardır; plan
/// değişince eski kayıt kapatılır, silinmez (geçmiş korunur). <see cref="ValidUntil"/> plana bağlı modül haklarının bitişidir.
/// </summary>
public sealed class TenantSubscription
{
    private TenantSubscription()
    {
        // EF Core için
    }

    public TenantSubscription(Guid tenantId, Guid planId, DateTimeOffset startsAt, DateTimeOffset? validUntil, string? reason)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        PlanId = planId;
        StartsAt = startsAt;
        ValidUntil = validUntil;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid PlanId { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset? ValidUntil { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public string? Reason { get; private set; }

    public void End(DateTimeOffset at) => EndedAt ??= at;
}

/// <summary>
/// Plan limitinin geçici istisnası (ADR-0028): gerekçe ve bitiş zorunludur. Silinmez; geri alınınca
/// <see cref="RevokedAt"/> dolar. Aynı limitte en yeni geçerli istisna plan değerinin yerine geçer.
/// </summary>
public sealed class LimitOverride
{
    public const int ReasonMaxLength = 500;

    private LimitOverride()
    {
        // EF Core için
    }

    public LimitOverride(Guid tenantId, string limitKey, int value, DateTimeOffset validUntil, string reason, DateTimeOffset now)
    {
        if (!LimitKeys.IsKnown(limitKey))
        {
            throw new ArgumentException($"Bilinmeyen limit: '{limitKey}'.", "limitKey");
        }

        if (value < 0)
        {
            throw new ArgumentException("Limit eksi olamaz.", "value");
        }

        if (validUntil <= now)
        {
            throw new ArgumentException("Bitiş tarihi gelecekte olmalı.", "validUntil");
        }

        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("İstisna için gerekçe zorunludur.", "reason");
        }

        if (trimmed.Length > ReasonMaxLength)
        {
            throw new ArgumentException($"Gerekçe en çok {ReasonMaxLength} karakter olabilir.", "reason");
        }

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        LimitKey = limitKey;
        Value = value;
        ValidUntil = validUntil;
        Reason = trimmed;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string LimitKey { get; private set; } = string.Empty;

    public int Value { get; private set; }

    public DateTimeOffset ValidUntil { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActiveAt(DateTimeOffset now) => RevokedAt is null && ValidUntil > now;

    public void Revoke(DateTimeOffset at) => RevokedAt ??= at;
}

public enum LimitSource
{
    /// <summary>Plan yok ya da planda bu limit tanımlı değil: sınırsız.</summary>
    None = 0,

    Plan = 1,

    Override = 2,
}

/// <summary>Bir limitin çözülmüş değeri; <see cref="Value"/> boşsa sınırsızdır. <see cref="OverrideUntil"/> yalnızca istisnada dolar.</summary>
public sealed record ResolvedLimit(string Key, int? Value, LimitSource Source, DateTimeOffset? OverrideUntil)
{
    /// <summary><paramref name="usage"/> bir tane daha eklenebilir mi? Sınırsızsa her zaman.</summary>
    public bool Allows(int usage, int extra = 1) => Value is not { } limit || usage + extra <= limit;
}

public static class LimitResolution
{
    /// <summary>Geçerli en yeni istisna, yoksa plan değeri, yoksa sınırsız.</summary>
    public static ResolvedLimit Resolve(string key, int? planValue, IEnumerable<LimitOverride> overrides, DateTimeOffset now)
    {
        var active = overrides.Where(o => o.LimitKey == key && o.IsActiveAt(now)).OrderByDescending(o => o.CreatedAt).FirstOrDefault();
        if (active is not null)
        {
            return new ResolvedLimit(key, active.Value, LimitSource.Override, active.ValidUntil);
        }

        return planValue is { } value
            ? new ResolvedLimit(key, value, LimitSource.Plan, null)
            : new ResolvedLimit(key, null, LimitSource.None, null);
    }
}

/// <summary>
/// Etkin limitleri çözer (ADR-0028). Modül hakları <c>ModuleEntitlements</c> tablosuna yazıldığı için (plan atanınca kaynağı
/// <c>Plan</c> olarak) tek kaynak <see cref="Modules.IModuleEntitlements"/> olarak kalır; bu arayüz limitler içindir.
/// </summary>
public interface IEntitlementResolver
{
    Task<IReadOnlyList<ResolvedLimit>> GetLimitsAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<ResolvedLimit> GetLimitAsync(Guid tenantId, string limitKey, CancellationToken cancellationToken);

    /// <summary>Limitin güncel kullanımı; bilinmeyen limit için <see cref="ArgumentException"/>.</summary>
    Task<int> GetUsageAsync(Guid tenantId, string limitKey, CancellationToken cancellationToken);
}
