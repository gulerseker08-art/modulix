using System.Text.Json;
using System.Text.Json.Serialization;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Approvals;

/// <summary>
/// Onay sürecinin bir adımı. Aynı <see cref="Order"/> değerine sahip adımlar paralel çalışır; küçükten büyüğe sırayla ilerlenir.
/// Onaycı ya kişidir (<see cref="ApproverUserId"/>) ya roldür (<see cref="ApproverRoleId"/>; rolün doğrudan üyelerinden biri yeter).
/// <see cref="MinAmount"/> doluysa adım yalnızca talep tutarı bu değere ulaşınca talebe eklenir. Süre aşılınca eskalasyon kişisi ya da rolü
/// de karar verebilir hale gelir (ADR-0027 Dilim 4b); ikisi de boşsa onaycılara yalnızca hatırlatma gider.
/// </summary>
public sealed record ApprovalStepDefinition(
    string Name,
    int Order,
    Guid? ApproverUserId,
    Guid? ApproverRoleId,
    decimal? MinAmount,
    int? DueHours,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? EscalateToUserId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? EscalateToRoleId = null);

/// <summary>
/// Onay süreci tanımı (ADR-0027 Dilim 4a, Ç6a.1). Firma kendi süreçlerini tanımlar (satın alma, masraf, iskonto istisnası…); kodda
/// sektöre ya da süreç adına bağlı dal yoktur. Değişiklik <see cref="Revision"/> artırır; açık talepler başlatıldıkları andaki adım
/// kopyasıyla sürer, tanım değişikliğinden etkilenmez. Talep bağlandığı için tanım silinmez, kapatılır.
/// </summary>
public sealed class ApprovalDefinition : ITenantEntity, ICompanyShared
{
    public const int NameMaxLength = 150;
    public const int DescriptionMaxLength = 500;
    public const int StepNameMaxLength = 100;
    public const int MaxSteps = 10;
    public const int MaxDueHours = 8760;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApprovalDefinition()
    {
        // EF Core için
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>Doluysa talepte tutar zorunludur ve bu para biriminde olmalıdır; adım eşikleri bu para birimindedir (kur çevrilmez).</summary>
    public string? Currency { get; private set; }

    public bool IsActive { get; private set; }

    public string StepsJson { get; private set; } = "[]";

    /// <summary>İçerik sürümü: adım ya da para birimi değişince bir artar ve talebe yazılır.</summary>
    public int Revision { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid UpdatedBy { get; private set; }

    /// <summary>Eşzamanlılık belirteci: her değişiklikte bir artar.</summary>
    public long Version { get; private set; }

    public IReadOnlyList<ApprovalStepDefinition> Steps =>
        JsonSerializer.Deserialize<List<ApprovalStepDefinition>>(StepsJson, Json) ?? [];

    public static ApprovalDefinition Create(
        string name, string? description, string? currency, IReadOnlyList<ApprovalStepDefinition> steps, Guid userId, DateTimeOffset now)
    {
        ApprovalDefinition definition = new()
        {
            Id = Guid.CreateVersion7(),
            IsActive = true,
            Revision = 1,
            CreatedAt = now,
            CreatedBy = userId,
            Version = 1,
        };
        definition.Assign(name, description, currency, steps, userId, now);
        return definition;
    }

    public void Update(string name, string? description, string? currency, IReadOnlyList<ApprovalStepDefinition> steps, Guid userId, DateTimeOffset now)
    {
        (string StepsJson, string? Currency) before = (StepsJson, Currency);
        Assign(name, description, currency, steps, userId, now);
        if (before != (StepsJson, Currency))
        {
            Revision++;
        }

        Version++;
    }

    public void SetActive(bool active, Guid userId, DateTimeOffset now)
    {
        IsActive = active;
        UpdatedAt = now;
        UpdatedBy = userId;
        Version++;
    }

    private void Assign(string name, string? description, string? currency, IReadOnlyList<ApprovalStepDefinition> steps, Guid userId, DateTimeOffset now)
    {
        string trimmedName = (name ?? string.Empty).Trim();
        if (trimmedName.Length is 0 or > NameMaxLength)
        {
            throw new ArgumentException($"Süreç adı 1-{NameMaxLength} karakter olmalı.");
        }

        string? trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmedDescription?.Length > DescriptionMaxLength)
        {
            throw new ArgumentException($"Açıklama en çok {DescriptionMaxLength} karakter olabilir.");
        }

        string? code = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();
        if (code is not null && (code.Length != 3 || !code.All(char.IsAsciiLetter)))
        {
            throw new ArgumentException("Para birimi üç harfli kod olmalı (ör. TRY).");
        }

        Name = trimmedName;
        Description = trimmedDescription;
        Currency = code;
        StepsJson = JsonSerializer.Serialize(Normalize(steps, code), Json);
        UpdatedAt = now;
        UpdatedBy = userId;
    }

    private static List<ApprovalStepDefinition> Normalize(IReadOnlyList<ApprovalStepDefinition> steps, string? currency)
    {
        if (steps is null || steps.Count is 0 or > MaxSteps)
        {
            throw new ArgumentException($"Süreçte 1-{MaxSteps} adım olmalı.");
        }

        List<ApprovalStepDefinition> normalized = new(steps.Count);
        foreach (ApprovalStepDefinition step in steps.OrderBy(s => s.Order))
        {
            string stepName = (step.Name ?? string.Empty).Trim();
            if (stepName.Length is 0 or > StepNameMaxLength)
            {
                throw new ArgumentException($"Adım adı 1-{StepNameMaxLength} karakter olmalı.");
            }

            if (step.Order < 1)
            {
                throw new ArgumentException($"\"{stepName}\" adımının sırası 1 ya da daha büyük olmalı.");
            }

            if (step.ApproverUserId is null == step.ApproverRoleId is null)
            {
                throw new ArgumentException($"\"{stepName}\" adımı için bir kişi ya da bir rol seçin.");
            }

            if (step.MinAmount is { } min && (min < 0 || currency is null))
            {
                throw new ArgumentException(currency is null
                    ? $"\"{stepName}\" adımında tutar eşiği için süreçte para birimi seçin."
                    : $"\"{stepName}\" adımının tutar eşiği negatif olamaz.");
            }

            if (step.DueHours is { } hours && hours is < 1 or > MaxDueHours)
            {
                throw new ArgumentException($"\"{stepName}\" adımının süresi 1-{MaxDueHours} saat olmalı.");
            }

            if (step.EscalateToUserId is not null && step.EscalateToRoleId is not null)
            {
                throw new ArgumentException($"\"{stepName}\" adımında eskalasyon için tek bir kişi ya da rol seçin.");
            }

            if ((step.EscalateToUserId ?? step.EscalateToRoleId) is not null && step.DueHours is null)
            {
                throw new ArgumentException($"\"{stepName}\" adımında eskalasyon için süre girin.");
            }

            normalized.Add(step with { Name = stepName });
        }

        return normalized;
    }
}
