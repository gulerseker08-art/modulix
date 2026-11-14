using System.Text.Json;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;

namespace Modulix.Core.FieldRules;

/// <summary>Kural listesinin JSON biçimi (paket sürümlerinde ve tenant uyarlamasında saklanır). Koşul ADR-0027'nin çok biçimli JSON'udur.</summary>
public static class FieldRuleJson
{
    public static string Serialize(IReadOnlyList<FieldRule> rules) => JsonSerializer.Serialize(rules, AutomationJson.Options);

    public static string Serialize(IReadOnlyList<string> keys) => JsonSerializer.Serialize(keys, AutomationJson.Options);

    /// <summary>Çözemezse <see cref="FormatException"/>; iletisi kullanıcıya gösterilebilir.</summary>
    public static IReadOnlyList<FieldRule> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<FieldRule>>(json, AutomationJson.Options) ?? throw new FormatException("Kural listesi boş.");
        }
        catch (JsonException)
        {
            throw new FormatException("Kural listesi çözülemedi: biçim geçersiz.");
        }
    }

    /// <summary>
    /// Yayınlanmış paket sürümünün kurallarını çözer. Bozuksa <see cref="CorruptRulePackageException"/> fırlatır: hangi paketin bozuk olduğu hatada ve logda görünür,
    /// kural değerlendirmesi güvenli tarafta (kapalı) kalır.
    /// </summary>
    public static IReadOnlyList<FieldRule> DeserializePackage(string packageCode, int version, string json)
    {
        try
        {
            return Deserialize(json);
        }
        catch (FormatException exception)
        {
            throw new CorruptRulePackageException(packageCode, version, exception);
        }
    }

    public static IReadOnlyList<string> DeserializeKeys(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, AutomationJson.Options) ?? [];
        }
        catch (JsonException)
        {
            throw new FormatException("Kapatılan kural listesi çözülemedi: biçim geçersiz.");
        }
    }
}

/// <summary>Bir kural listesindeki bütün kuralları kendi kayıt türünün alan kümesine karşı doğrular.</summary>
public static class FieldRuleChecker
{
    public const int MaxRulesPerList = 500;

    /// <summary>Alan anahtarının kayıt türü: alan anahtarı "kayıtTürü.ad" olduğundan en uzun eşleşen kayıt türü anahtarıdır; yoksa <c>null</c>.</summary>
    public static string? EntityOf(ModuleCatalog modules, string fieldKey) =>
        modules.AutomationEntities
            .Select(e => e.Entity.EntityKey)
            .Where(key => fieldKey.StartsWith(key + ".", StringComparison.Ordinal))
            .OrderByDescending(key => key.Length)
            .FirstOrDefault();

    /// <param name="fieldSetFor">Kayıt türü için alan kümesi; platformda yalnızca modül alanları, tenant'ta özel alanlar dahil.</param>
    public static async Task<IReadOnlyList<AutomationIssue>> ValidateAsync(
        IReadOnlyList<FieldRule> rules, ModuleCatalog modules, Func<string, Task<AutomationFieldSet?>> fieldSetFor)
    {
        var issues = new List<AutomationIssue>();
        if (rules.Count > MaxRulesPerList)
        {
            issues.Add(new AutomationIssue(string.Empty, $"Bir listede en çok {MaxRulesPerList} kural olabilir."));
            return issues;
        }

        var sets = new Dictionary<string, AutomationFieldSet?>(StringComparer.Ordinal);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var entity = EntityOf(modules, rule.Field);
            AutomationFieldSet? set = null;
            if (entity is not null && !sets.TryGetValue(entity, out set))
            {
                set = await fieldSetFor(entity);
                sets[entity] = set;
            }

            if (set is null)
            {
                issues.Add(new AutomationIssue($"{rule.Key}.field", $"Alan bulunamadı ya da görülemiyor: '{rule.Field}'."));
                continue;
            }

            issues.AddRange(FieldRuleValidator.Validate(rule, set).Select(x => new AutomationIssue($"{rule.Key}.{x.Path}", x.Message)));
        }

        return issues;
    }

    /// <summary>İzin gerektirmeyen, yalnızca modül alanlarını bilen alan kümesi (platform paketi doğrulaması).</summary>
    public static AutomationFieldSet ModuleFieldSet(ModuleCatalog modules, string entityKey) =>
        new(entityKey, modules.AutomationFieldsOf(entityKey), modules.AutomationRelationsOf(entityKey), new HashSet<string>(StringComparer.Ordinal));
}

public enum RulePackageStatus
{
    Draft,
    Published,
    Superseded,
}

/// <summary>
/// Platform kural paketi (ADR-0029 K8). Katman <see cref="RuleLayerKind.ProductStandard"/> (tüm tenant'lar) ya da
/// <see cref="RuleLayerKind.SectorPackage"/> (<see cref="NodeId"/> faaliyetini ya da altını seçen tenant'lar) olur. Platform tablosudur,
/// RLS dışıdır, tenant verisi içermez.
/// </summary>
public sealed class RulePackage
{
    private RulePackage()
    {
        // EF Core için
    }

    public RulePackage(string code, string name, RuleLayerKind layer, Guid? nodeId)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 100 || !code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.'))
        {
            throw new ArgumentException($"Geçersiz paket kodu: '{code}'.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
        {
            throw new ArgumentException("Paket adı boş olamaz ve en çok 100 karakter olabilir.", nameof(name));
        }

        if (layer is not (RuleLayerKind.ProductStandard or RuleLayerKind.SectorPackage))
        {
            throw new ArgumentException("Paket katmanı yalnızca ürün standardı ya da sektör paketi olabilir.", nameof(layer));
        }

        if ((layer == RuleLayerKind.SectorPackage) != (nodeId is not null))
        {
            throw new ArgumentException("Sektör paketi bir faaliyete bağlanır; ürün standardı bağlanmaz.", nameof(nodeId));
        }

        Id = Guid.CreateVersion7();
        Code = code;
        Name = name.Trim();
        Layer = layer;
        NodeId = nodeId;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public RuleLayerKind Layer { get; private set; }

    public Guid? NodeId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Paketin bir sürümü. Her paketin en çok bir taslağı ve en çok bir yayınlanmış sürümü vardır; eski yayınlar <see cref="RulePackageStatus.Superseded"/> olarak kalır (geri alma kaynağı).</summary>
public sealed class RulePackageVersion
{
    private RulePackageVersion()
    {
        // EF Core için
    }

    public RulePackageVersion(Guid packageId, int number, string rulesJson, string? note)
    {
        Id = Guid.CreateVersion7();
        PackageId = packageId;
        Number = number;
        Status = RulePackageStatus.Draft;
        RulesJson = rulesJson;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid PackageId { get; private set; }

    public int Number { get; private set; }

    public RulePackageStatus Status { get; private set; }

    public string RulesJson { get; private set; } = "[]";

    public string? Note { get; private set; }

    /// <summary>Taslağın eşzamanlı düzenlemesini koruyan iyimser kilit.</summary>
    public long Revision { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public Guid? PublishedBy { get; private set; }

    public void ReplaceRules(string rulesJson, string? note)
    {
        if (Status != RulePackageStatus.Draft)
        {
            throw new InvalidOperationException("Yalnızca taslak değiştirilebilir.");
        }

        RulesJson = rulesJson;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Revision++;
    }

    public void Publish(Guid actingUserId)
    {
        if (Status != RulePackageStatus.Draft)
        {
            throw new InvalidOperationException("Yalnızca taslak yayınlanır.");
        }

        Status = RulePackageStatus.Published;
        PublishedAt = DateTimeOffset.UtcNow;
        PublishedBy = actingUserId;
        Revision++;
    }

    public void Supersede()
    {
        if (Status != RulePackageStatus.Published)
        {
            throw new InvalidOperationException("Yalnızca yayınlanmış sürüm geçersiz kılınır.");
        }

        Status = RulePackageStatus.Superseded;
        Revision++;
    }
}

/// <summary>
/// Tenant'ın kendi katmanı (ADR-0029 K8 "tenant uyarlaması"): kendi kuralları ve alttaki katmandan kapattığı kuralların anahtarları.
/// Tenant verisidir; Row-Level Security ile korunur. Tenant başına tek kayıt.
/// </summary>
public sealed class TenantRuleAdaptation : ITenantEntity, ICompanyShared
{
    private TenantRuleAdaptation()
    {
        // EF Core için
    }

    public TenantRuleAdaptation(string rulesJson, string disabledKeysJson)
    {
        Id = Guid.CreateVersion7();
        RulesJson = rulesJson;
        DisabledKeysJson = disabledKeysJson;
        UpdatedAt = DateTimeOffset.UtcNow;
        Revision = 1; // kayıt yokken görünüm 0 döner; ilk kayıt 1 olur
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; set; }

    public string RulesJson { get; private set; } = "[]";

    public string DisabledKeysJson { get; private set; } = "[]";

    public long Revision { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Replace(string rulesJson, string disabledKeysJson)
    {
        RulesJson = rulesJson;
        DisabledKeysJson = disabledKeysJson;
        UpdatedAt = DateTimeOffset.UtcNow;
        Revision++;
    }
}

public sealed record RulePackageVersionView(int Number, RulePackageStatus Status, IReadOnlyList<FieldRule> Rules, string? Note, long Revision, DateTimeOffset? PublishedAt);

public sealed record RulePackageView(
    Guid Id, string Code, string Name, RuleLayerKind Layer, Guid? NodeId, RulePackageVersionView? Published, RulePackageVersionView? Draft);

/// <summary>Yayın önizlemesi: tanım sorunları, birleşim çakışmaları/uyarıları ve yayındaki sürüme göre fark. Önizleme hiçbir şey yazmaz.</summary>
public sealed record RulePublishPreview(
    IReadOnlyList<AutomationIssue> Issues,
    IReadOnlyList<RuleMergeIssue> Conflicts,
    IReadOnlyList<RuleMergeIssue> Warnings,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Changed)
{
    public bool CanPublish => Issues.Count == 0 && Conflicts.Count == 0;
}

/// <summary>
/// Yayınlanmış bir kural paketi sürümünün içeriği okunamıyor. Paket <c>block</c> ya da onay kuralı taşıyor olabileceğinden bozuk paket sessizce atlanmaz:
/// kural değerlendirmesi bu hatayla durur ve etkilenen yazma reddedilir (kapalı kalma). İleti kural içeriği taşımaz, yalnızca paket kodunu ve sürümü söyler.
/// </summary>
public sealed class CorruptRulePackageException(string packageCode, int version, Exception? inner = null)
    : Exception($"Kural paketi '{packageCode}' (sürüm {version}) okunamıyor: yayınlanmış kural listesinin biçimi geçersiz. Güvenlik kuralları atlanmasın diye yazma durduruldu; platform yöneticisi paketi düzeltmelidir.", inner)
{
    public string PackageCode { get; } = packageCode;

    public int Version { get; } = version;
}

public sealed class RulePackageNotFoundException(string message) : Exception(message);

/// <summary>Kod çakışması, eşzamanlı düzenleme ya da yayını engelleyen durum (409).</summary>
public sealed class RulePackageConflictException(string message) : Exception(message);

/// <summary>
/// Platform yöneticisinin kural paketi yönetimi (ADR-0029 K8): taslak → doğrulama → etki önizlemesi → yayın, atomik. Her yazma
/// <c>PlatformAuditLog</c>'a aynı işlemde yazılır. Doğrulama hataları <see cref="ArgumentException"/>.
/// </summary>
public interface IRulePackageService
{
    Task<IReadOnlyList<RulePackageView>> ListAsync(CancellationToken cancellationToken);

    Task<RulePackageView> CreateAsync(Guid actingUserId, string code, string name, RuleLayerKind layer, Guid? nodeId, CancellationToken cancellationToken);

    /// <summary>Taslağı yazar (yoksa açar). Tanım sorunu varsa kaydedilmez. <paramref name="expectedRevision"/> var olan taslak içindir; uyuşmazsa 409.</summary>
    Task<RulePackageView> SaveDraftAsync(
        Guid actingUserId, Guid packageId, IReadOnlyList<FieldRule> rules, string? note, long? expectedRevision, CancellationToken cancellationToken);

    Task<RulePublishPreview> PreviewPublishAsync(Guid packageId, CancellationToken cancellationToken);

    /// <summary>Önizleme temizse taslağı yayınlar, eskisini geçersiz kılar; değilse <see cref="RulePackageConflictException"/>.</summary>
    Task<RulePackageView> PublishAsync(Guid actingUserId, Guid packageId, CancellationToken cancellationToken);

    /// <summary>Açık taslağı siler (yayındaki sürüm değişmez).</summary>
    Task<RulePackageView> DiscardDraftAsync(Guid actingUserId, Guid packageId, CancellationToken cancellationToken);

    /// <summary>Eski bir sürümün kurallarını yeni sürüm olarak yayınlar. Geri alma ayar geri alımıdır; veri migrationı değildir.</summary>
    Task<RulePackageView> RollbackAsync(Guid actingUserId, Guid packageId, int toNumber, CancellationToken cancellationToken);
}

/// <summary>Tenant uyarlaması görünümü.</summary>
public sealed record TenantRuleAdaptationView(IReadOnlyList<FieldRule> Rules, IReadOnlyList<string> DisabledRuleKeys, long Revision);

/// <summary>
/// Tenant bağlamında etkin kurallar ve tenant uyarlaması (ADR-0029 K8). Etkin kural = ürün standardı + tenant'ın seçtiği faaliyetlerin
/// (ve üst düğümlerinin) yayınlanmış sektör paketleri + tenant uyarlaması, <see cref="RuleLayerMerger"/> ile birleşir.
/// </summary>
public interface ITenantFieldRules
{
    Task<RuleMergeResult> GetEffectiveAsync(CancellationToken cancellationToken);

    Task<TenantRuleAdaptationView> GetAdaptationAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Uyarlamayı değiştirir. Tanım sorunu ya da birleşim çakışması (ör. kilitli kuralı geçersiz kılma) varsa kaydedilmez
    /// (<see cref="ArgumentException"/>). <paramref name="expectedRevision"/> uyuşmazsa <see cref="RulePackageConflictException"/>.
    /// </summary>
    Task<TenantRuleAdaptationView> SaveAdaptationAsync(
        IReadOnlyList<FieldRule> rules, IReadOnlyList<string> disabledRuleKeys, long? expectedRevision, CancellationToken cancellationToken);
}

/// <summary>
/// Kural katmanlarını yazmadan sınamak için (ADR-0041 K4): firmanın etkin kural kümesi, <b>alt profil katmanı verilen kurallarla</b> değiştirilerek birleştirilir.
/// Alt profil etkinleştirmeden ya da kuralı değiştirmeden önce çakışma var mı diye bakmak içindir; kaydı değiştirmez.
/// </summary>
public interface ITenantRuleSimulation
{
    /// <param name="subProfileRules">Alt profil katmanına girecek kuralların tamamı (diğer etkin alt profillerinki dahil); kayıtlı alt profil kuralları yok sayılır.</param>
    Task<RuleMergeResult> GetEffectiveWithSubProfileRulesAsync(IReadOnlyList<FieldRule> subProfileRules, CancellationToken cancellationToken);
}
