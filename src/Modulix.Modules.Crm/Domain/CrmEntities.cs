using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Modules.Crm.Domain;

// CRM entity'leri. Alanlar kayıt depolarından (Records) doğrulanarak yazılır; uzunluk sınırları
// hem doğrulamada hem veritabanı kolonlarında aynı sabitlerden gelir.

/// <summary>Tüm CRM kayıtlarının ortak alanları. TenantId kaydederken aktif tenant'tan atanır.</summary>
public abstract class CrmEntity : ITenantEntity
{
    protected CrmEntity()
    {
        Id = Guid.CreateVersion7();
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        Version = 1;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Her değişiklikte artar; eşzamanlı güncellemeleri yakalamak için kullanılır.</summary>
    public long Version { get; private set; }

    /// <summary>Firmanın tanımladığı özel alanların değerleri (Y9a), JSON nesnesi; boşsa null. Boru hattı doğrular.</summary>
    public string? CustomFields { get; internal set; }

    internal void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}

/// <summary>Bir kullanıcıya atanabilen kayıt. Sorumlu, aktif tenant'ın etkin üyesi olmalıdır.</summary>
public interface IOwnedEntity
{
    /// <summary>Sorumlu kullanıcı (platform kullanıcı kimliği). Verilmezse kaydı oluşturan kullanıcı atanır.</summary>
    Guid? OwnerId { get; }
}

// Seçim alanları (Y9b) firmanın düzenleyebildiği listelerden değer anahtarı taşır (CrmModule.ChoiceLists); hazır
// değerlerin anahtarları eski enum adlarıdır (ör. "Proposal"). Varsayılanlar boru hattında listenin ilk etkin değeridir.

public sealed class Company : CrmEntity, IOwnedEntity
{
    public const int NameMaxLength = 200;
    public const int TaxNumberMaxLength = 20;
    public const int PhoneMaxLength = 30;
    public const int EmailMaxLength = 254;
    public const int WebsiteMaxLength = 200;
    public const int CityMaxLength = 100;
    public const int NotesMaxLength = 4000;
    public const int IndustryMaxLength = 100;
    public const int AddressMaxLength = 500;
    public const int TagsMaxLength = 200;

    public string Name { get; internal set; } = string.Empty;

    public string? TaxNumber { get; internal set; }

    public string? Phone { get; internal set; }

    public string? Email { get; internal set; }

    public string? Website { get; internal set; }

    public string? City { get; internal set; }

    public string? Address { get; internal set; }

    public string? Industry { get; internal set; }

    public string Status { get; internal set; } = string.Empty;

    /// <summary>Müşterinin bize nereden geldiği.</summary>
    public string? Source { get; internal set; }

    /// <summary>Virgülle ayrılmış etiketler (ör. "öncelikli, ihracat").</summary>
    public string? Tags { get; internal set; }

    public Guid? OwnerId { get; internal set; }

    public string? Notes { get; internal set; }
}

public sealed class Contact : CrmEntity
{
    public const int NameMaxLength = 100;
    public const int TitleMaxLength = 100;

    public string FirstName { get; internal set; } = string.Empty;

    public string LastName { get; internal set; } = string.Empty;

    /// <summary>Kişinin çalıştığı firma; aynı tenant'ta olmak zorundadır.</summary>
    public Guid? CompanyId { get; internal set; }

    public string? Title { get; internal set; }

    public string? Email { get; internal set; }

    public string? Phone { get; internal set; }

    public string? Notes { get; internal set; }
}

public sealed class Opportunity : CrmEntity, IOwnedEntity
{
    public const int TitleMaxLength = 200;
    public const int LostReasonMaxLength = 500;
    public const int CurrencyLength = 3;
    public const string DefaultCurrency = "TRY";

    public string Title { get; internal set; } = string.Empty;

    public Guid CompanyId { get; internal set; }

    public Guid? ContactId { get; internal set; }

    public string Stage { get; internal set; } = string.Empty;

    public decimal? Amount { get; internal set; }

    /// <summary>ISO 4217 kodu (ör. "TRY").</summary>
    public string Currency { get; internal set; } = DefaultCurrency;

    public DateOnly? ExpectedCloseDate { get; internal set; }

    /// <summary>Kazanma olasılığı (yüzde, 0-100).</summary>
    public int? Probability { get; internal set; }

    /// <summary>Fırsat kaybedildiyse nedeni.</summary>
    public string? LostReason { get; internal set; }

    public Guid? OwnerId { get; internal set; }

    public string? Notes { get; internal set; }
}

public sealed class Activity : CrmEntity, IOwnedEntity
{
    public const int SubjectMaxLength = 200;

    public string Type { get; internal set; } = string.Empty;

    public string Subject { get; internal set; } = string.Empty;

    public DateTimeOffset? DueAt { get; internal set; }

    public DateTimeOffset? CompletedAt { get; internal set; }

    public Guid? CompanyId { get; internal set; }

    public Guid? ContactId { get; internal set; }

    public Guid? OpportunityId { get; internal set; }

    /// <summary>Aktiviteyi yapacak kişi.</summary>
    public Guid? OwnerId { get; internal set; }

    public string? Notes { get; internal set; }
}
