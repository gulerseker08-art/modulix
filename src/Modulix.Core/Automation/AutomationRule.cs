using System.Security.Cryptography;
using System.Text;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Automation;

public enum AutomationRuleStatus
{
    /// <summary>Henüz yayınlanmış sürümü yok.</summary>
    Draft = 1,

    /// <summary>Yayınlanmış sürümü var ve açık (Dilim 2'den itibaren çalışır).</summary>
    Active = 2,

    /// <summary>Yayınlanmış sürümü var ama kapalı.</summary>
    Inactive = 3,
}

public enum AutomationVersionState
{
    /// <summary>Düzenlenebilir; kural başına en çok bir tane.</summary>
    Draft = 1,

    /// <summary>Yayınlanmış ve değişmez.</summary>
    Published = 2,
}

/// <summary>
/// Kural başlığı (ADR-0027): kimliği (modül, kayıt türü), durumu ve yayınlanmış sürümü. İçerik sürümlerdedir. Tenant verisidir;
/// Row-Level Security ile korunur. <see cref="Name"/> listede göstermek için en son kaydedilen sürümün adıdır.
/// </summary>
public sealed class AutomationRule : ITenantEntity, ICompanyShared
{
    public const int NameMaxLength = 200;

    private AutomationRule()
    {
        // EF Core için
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string ModuleKey { get; private set; } = string.Empty;

    public string EntityKey { get; private set; } = string.Empty;

    public AutomationRuleStatus Status { get; private set; }

    /// <summary>Yayınlanmış sürümün kimliği; yayınlanmamışsa boş.</summary>
    public Guid? PublishedVersionId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid UpdatedBy { get; private set; }

    /// <summary>Eşzamanlılık belirteci: her değişiklikte bir artar.</summary>
    public long Version { get; private set; }

    public static AutomationRule Create(string name, string moduleKey, string entityKey, Guid userId, DateTimeOffset now)
    {
        return new AutomationRule
        {
            Id = Guid.CreateVersion7(),
            Name = NormalizeName(name),
            ModuleKey = moduleKey,
            EntityKey = entityKey,
            Status = AutomationRuleStatus.Draft,
            CreatedAt = now,
            CreatedBy = userId,
            UpdatedAt = now,
            UpdatedBy = userId,
            Version = 1,
        };
    }

    public static string NormalizeName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > NameMaxLength)
        {
            throw new ArgumentException($"Kural adı 1-{NameMaxLength} karakter olmalı.", nameof(name));
        }

        return trimmed;
    }

    public void Touch(string name, Guid userId, DateTimeOffset now)
    {
        Name = NormalizeName(name);
        UpdatedAt = now;
        UpdatedBy = userId;
        Version++;
    }

    /// <summary>Taslağı yayınlar: yayınlanmış sürüm değişir; açılış isteğe bağlıdır (yeni kural varsayılan olarak kapalı yayınlanır).</summary>
    public void Publish(Guid versionId, string name, bool activate, Guid userId, DateTimeOffset now)
    {
        PublishedVersionId = versionId;
        Name = NormalizeName(name);
        Status = activate ? AutomationRuleStatus.Active : AutomationRuleStatus.Inactive;
        UpdatedAt = now;
        UpdatedBy = userId;
        Version++;
    }

    public void SetActive(bool active, Guid userId, DateTimeOffset now)
    {
        if (PublishedVersionId is null)
        {
            throw new InvalidOperationException("Yayınlanmamış kural açılamaz.");
        }

        Status = active ? AutomationRuleStatus.Active : AutomationRuleStatus.Inactive;
        UpdatedAt = now;
        UpdatedBy = userId;
        Version++;
    }
}

/// <summary>
/// Kuralın bir sürümü (ADR-0027). <see cref="State"/> yayınlanmışsa içerik değişmez; çalışan örnekler başlatıldıkları sürüme
/// bağlı kalır. İçerik (ad, öncelik, tarihler, tetikleyici, koşul, aksiyonlar) sürümdedir.
/// </summary>
public sealed class AutomationRuleVersion : ITenantEntity, ICompanyShared
{
    public const int DescriptionMaxLength = 1000;

    public const int MaxPriority = 1000;

    public const int DefaultPriority = 100;

    private AutomationRuleVersion()
    {
        // EF Core için
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RuleId { get; private set; }

    public int VersionNo { get; private set; }

    public AutomationVersionState State { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>Küçük sayı önce çalışır; aynı alanı değiştiren kurallarda sıra belirler.</summary>
    public int Priority { get; private set; }

    /// <summary>Doluysa bu kuraldan sonra aynı olay için diğer kurallar çalıştırılmaz (aynı hedef için).</summary>
    public bool StopProcessing { get; private set; }

    public DateTimeOffset? ActiveFrom { get; private set; }

    public DateTimeOffset? ActiveUntil { get; private set; }

    /// <summary><see cref="AutomationJson"/> biçiminde tetikleyici, koşul ve aksiyonlar.</summary>
    public string DefinitionJson { get; private set; } = string.Empty;

    /// <summary>İçeriğin SHA-256 özeti (ad, öncelik, tarihler ve tanım); aynı içeriğin ikinci kez yayınlanmasını ve sürüm karşılaştırmasını kolaylaştırır.</summary>
    public string DefinitionHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public Guid? PublishedBy { get; private set; }

    public static AutomationRuleVersion CreateDraft(
        Guid ruleId, int versionNo, AutomationVersionContent content, Guid userId, DateTimeOffset now)
    {
        var version = new AutomationRuleVersion
        {
            Id = Guid.CreateVersion7(),
            RuleId = ruleId,
            VersionNo = versionNo,
            State = AutomationVersionState.Draft,
            CreatedAt = now,
            CreatedBy = userId,
        };
        version.Apply(content);
        return version;
    }

    /// <summary>Taslağın içeriğini değiştirir. Yayınlanmış sürüm değişmez.</summary>
    public void Apply(AutomationVersionContent content)
    {
        if (State == AutomationVersionState.Published)
        {
            throw new InvalidOperationException("Yayınlanmış sürüm değiştirilemez; yeni taslak açın.");
        }

        Name = AutomationRule.NormalizeName(content.Name);
        Description = string.IsNullOrWhiteSpace(content.Description) ? null : content.Description.Trim();
        if (Description is { Length: > DescriptionMaxLength })
        {
            throw new ArgumentException($"Açıklama en çok {DescriptionMaxLength} karakter olabilir.", nameof(content));
        }

        if (content.Priority is < 0 or > MaxPriority)
        {
            throw new ArgumentException($"Öncelik 0-{MaxPriority} arasında olmalı.", nameof(content));
        }

        if (content.ActiveFrom is { } from && content.ActiveUntil is { } until && until <= from)
        {
            throw new ArgumentException("Etkinlik bitişi başlangıçtan sonra olmalı.", nameof(content));
        }

        Priority = content.Priority;
        StopProcessing = content.StopProcessing;
        ActiveFrom = content.ActiveFrom;
        ActiveUntil = content.ActiveUntil;
        DefinitionJson = AutomationJson.Serialize(content.Definition);
        DefinitionHash = Hash(this);
    }

    public void MarkPublished(Guid userId, DateTimeOffset now)
    {
        if (State == AutomationVersionState.Published)
        {
            throw new InvalidOperationException("Sürüm zaten yayınlanmış.");
        }

        State = AutomationVersionState.Published;
        PublishedAt = now;
        PublishedBy = userId;
    }

    public AutomationDefinition Definition => AutomationJson.Deserialize(DefinitionJson);

    public AutomationVersionContent Content =>
        new(Name, Description, Priority, StopProcessing, ActiveFrom, ActiveUntil, Definition);

    private static string Hash(AutomationRuleVersion version)
    {
        var text = string.Join(
            "|",
            version.Name,
            version.Description,
            version.Priority,
            version.StopProcessing,
            version.ActiveFrom?.ToUniversalTime().ToString("O"),
            version.ActiveUntil?.ToUniversalTime().ToString("O"),
            version.DefinitionJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>Bir sürümün içeriği (yaratma/düzenleme girdisi ve okuma sonucu).</summary>
public sealed record AutomationVersionContent(
    string Name,
    string? Description,
    int Priority,
    bool StopProcessing,
    DateTimeOffset? ActiveFrom,
    DateTimeOffset? ActiveUntil,
    AutomationDefinition Definition);
