using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Reporting;

public enum ReportVisibility
{
    /// <summary>Yalnızca sahibi görür.</summary>
    Private = 1,

    /// <summary>Sahibi ve seçilen kullanıcılar (<see cref="ReportShare"/>) görür.</summary>
    Users = 2,

    /// <summary>Firmadaki rapor merkezini görebilen herkes görür.</summary>
    Tenant = 3,
}

/// <summary>
/// Kayıtlı rapor (ADR-0035, dilim 3): veri kümesi, sınırlı oluşturucunun ürettiği tanım (<see cref="SpecJson"/>) ve paylaşım ayarı. Tanım
/// <b>veri erişimi vermez</b>: paylaşılan raporu açan kişi raporu kendi yetkisiyle çalıştırır, göremediği kayıt ve alan sonuca girmez.
/// Tenant verisidir; Row-Level Security ile korunur. Sütunlar kalıcı alan adlarıyla saklanır, bu yüzden görünen ad değişince rapor bozulmaz.
/// </summary>
public sealed class ReportDefinition : ITenantEntity, ICompanyShared
{
    public const int NameMaxLength = 120;
    public const int DescriptionMaxLength = 500;
    public const int SpecMaxLength = 20000;

    /// <summary>Bir kullanıcının sahip olabileceği en fazla rapor.</summary>
    public const int MaxPerUser = 200;

    private ReportDefinition()
    {
        // EF Core için
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid OwnerUserId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string DatasetKey { get; private set; } = string.Empty;

    /// <summary>Kaydedilirken veri kümesinin sürümü; eskiyse çalıştırma uyarı taşır.</summary>
    public int DatasetVersion { get; private set; }

    public string SpecJson { get; private set; } = string.Empty;

    public ReportVisibility Visibility { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Eşzamanlılık belirteci: her değişiklikte bir artar.</summary>
    public long Version { get; private set; }

    public static ReportDefinition Create(Guid ownerUserId, string name, string? description, string datasetKey, int datasetVersion, string specJson, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = ownerUserId,
            Name = NormalizeName(name),
            Description = NormalizeDescription(description),
            DatasetKey = datasetKey,
            DatasetVersion = datasetVersion,
            SpecJson = specJson,
            Visibility = ReportVisibility.Private,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };

    public void Update(string name, string? description, string datasetKey, int datasetVersion, string specJson, DateTimeOffset now)
    {
        Name = NormalizeName(name);
        Description = NormalizeDescription(description);
        DatasetKey = datasetKey;
        DatasetVersion = datasetVersion;
        SpecJson = specJson;
        Touch(now);
    }

    public void SetVisibility(ReportVisibility visibility, DateTimeOffset now)
    {
        Visibility = visibility;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }

    public static string NormalizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > NameMaxLength)
        {
            throw new ArgumentException($"Rapor adı 1-{NameMaxLength} karakter olmalı.", nameof(name));
        }

        return trimmed;
    }

    public static string? NormalizeDescription(string? description)
    {
        var trimmed = description?.Trim();
        if (trimmed is { Length: > DescriptionMaxLength })
        {
            throw new ArgumentException($"Açıklama en fazla {DescriptionMaxLength} karakter olabilir.", nameof(description));
        }

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>Raporun belirli bir kullanıcıyla paylaşımı (<see cref="ReportVisibility.Users"/>). Paylaşım yalnızca raporu görme hakkıdır, veri erişimi vermez.</summary>
public sealed class ReportShare : ITenantEntity, ICompanyShared
{
    private ReportShare()
    {
        // EF Core için
    }

    public ReportShare(Guid reportId, Guid userId, Guid createdBy, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        ReportId = reportId;
        UserId = userId;
        CreatedBy = createdBy;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ReportId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Kullanıcının favori işaretlediği rapor (kişiye özel).</summary>
public sealed class ReportFavorite : ITenantEntity, ICompanyShared
{
    private ReportFavorite()
    {
        // EF Core için
    }

    public ReportFavorite(Guid reportId, Guid userId, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        ReportId = reportId;
        UserId = userId;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ReportId { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
