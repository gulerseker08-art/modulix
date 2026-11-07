using System.Text.RegularExpressions;

namespace Modulix.Core.Platform;

public enum BillingStatus
{
    Pending = 1,
    Paid = 2,
    Cancelled = 3,
}

/// <summary>
/// Firmanın bir dönem için ödeme kaydı (ADR-0028 Dilim 7c). Ödeme sağlayıcısından bağımsızdır: tahsilat ya da fatura bu kayda
/// elle (Finans rolü) işlenir; ileride bir sağlayıcı bağdaştırıcısı aynı kaydı <c>MarkPaid</c> ile kapatır. Fatura kesmez, tahsilat
/// yapmaz, <b>erişimi kendiliğinden değiştirmez</b> (lisans bitişi plan/abonelikte ayrıca ve açıkça yönetilir). Hiçbir kayıt silinmez:
/// iptal edilen kayıt gerekçesiyle kalır.
/// </summary>
public sealed partial class BillingRecord
{
    public const int ReferenceMaxLength = 100;
    public const int NoteMaxLength = 500;
    public const int ReasonMaxLength = 500;
    public const decimal MaxAmount = 1_000_000_000_000m;

    private BillingRecord()
    {
        // EF Core için
    }

    public BillingRecord(
        Guid tenantId, DateOnly periodStart, DateOnly periodEnd, decimal amount, string? currency, DateOnly? dueDate,
        string? reference, string? note, Guid createdByUserId, DateTimeOffset now)
    {
        if (periodEnd < periodStart)
        {
            throw new ArgumentException("Dönem bitişi başlangıçtan önce olamaz.", "periodEnd");
        }

        if (amount <= 0 || amount > MaxAmount)
        {
            throw new ArgumentException("Tutar sıfırdan büyük olmalı.", "amount");
        }

        var code = string.IsNullOrWhiteSpace(currency) ? "TRY" : currency.Trim().ToUpperInvariant();
        if (!CurrencyPattern().IsMatch(code))
        {
            throw new ArgumentException("Para birimi 3 harfli koddur (ör. TRY, EUR).", "currency");
        }

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        Amount = decimal.Round(amount, 2);
        Currency = code;
        DueDate = dueDate;
        Reference = Optional(reference, ReferenceMaxLength, "reference", "Referans");
        Note = Optional(note, NoteMaxLength, "note", "Not");
        Status = BillingStatus.Pending;
        CreatedByUserId = createdByUserId;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public DateOnly PeriodStart { get; private set; }

    public DateOnly PeriodEnd { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "TRY";

    public DateOnly? DueDate { get; private set; }

    /// <summary>Fatura numarası ya da havale/EFT açıklaması gibi dış referans.</summary>
    public string? Reference { get; private set; }

    public string? Note { get; private set; }

    public BillingStatus Status { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public string? CancelReason { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public Guid? ClosedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Bekleyen kaydı ödendi yapar; <paramref name="reference"/> verilirse dış referansı günceller. Bekleyen değilse çakışma (çağıran denetler).</summary>
    public void MarkPaid(DateTimeOffset at, string? reference, Guid byUserId)
    {
        Status = BillingStatus.Paid;
        PaidAt = at;
        ClosedByUserId = byUserId;
        if (!string.IsNullOrWhiteSpace(reference))
        {
            Reference = Optional(reference, ReferenceMaxLength, "reference", "Referans");
        }
    }

    public void Cancel(DateTimeOffset at, string reason, Guid byUserId)
    {
        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > ReasonMaxLength)
        {
            throw new ArgumentException($"İptal gerekçesi zorunludur (en çok {ReasonMaxLength} karakter).", "reason");
        }

        Status = BillingStatus.Cancelled;
        CancelledAt = at;
        CancelReason = trimmed;
        ClosedByUserId = byUserId;
    }

    /// <summary>Vadesi geçmiş bekleyen kayıt: bilgi amaçlıdır, erişimi değiştirmez.</summary>
    public bool IsOverdueAt(DateOnly today) => Status == BillingStatus.Pending && DueDate is { } due && due < today;

    private static string? Optional(string? value, int max, string field, string label)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= max ? trimmed : throw new ArgumentException($"{label} en çok {max} karakter olabilir.", field);
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();
}

public sealed record BillingInput(
    DateOnly PeriodStart, DateOnly PeriodEnd, decimal Amount, string? Currency, DateOnly? DueDate, string? Reference, string? Note);

/// <summary><see cref="TenantName"/>: güncel firma adı.</summary>
public sealed record BillingInfo(
    Guid Id,
    Guid TenantId,
    string TenantName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal Amount,
    string Currency,
    DateOnly? DueDate,
    string? Reference,
    string? Note,
    BillingStatus Status,
    DateTimeOffset? PaidAt,
    string? CancelReason,
    DateTimeOffset CreatedAt,
    bool IsOverdue);

/// <summary>
/// Ödeme kayıtları (ADR-0028 Dilim 7c). Sağlayıcıdan bağımsız defter: oluştur, ödendi işle, iptal et, listele. Hiçbir kayıt silinmez.
/// Hatalar: <see cref="UserAdministrationException"/> (bulunamadı, çakışma), geçersiz alan için <see cref="ArgumentException"/>.
/// Her işlem platform denetim kaydına yazılır.
/// </summary>
public interface IBillingLedger
{
    Task<BillingInfo> CreateAsync(Guid tenantId, BillingInput input, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary><paramref name="paidAt"/> boşsa şimdi. Bekleyen değilse çakışma.</summary>
    Task<BillingInfo> MarkPaidAsync(Guid id, DateTimeOffset? paidAt, string? reference, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>Gerekçe zorunlu. Bekleyen değilse çakışma. Kayıt silinmez.</summary>
    Task<BillingInfo> CancelAsync(Guid id, string reason, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>En yeni üstte, en çok 200. <paramref name="tenantId"/> boşsa bütün firmalar.</summary>
    Task<IReadOnlyList<BillingInfo>> ListAsync(Guid? tenantId, BillingStatus? status, CancellationToken cancellationToken);
}
