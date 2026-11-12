using Modulix.Core.Contracts.Credentials;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Credentials;

/// <summary>
/// Süreli belge (ADR-0038 K3): araç muayenesi, sigorta, ehliyet, SRC... Sahibi bir modül kaydıdır (<see cref="OwnerEntityKey"/> +
/// <see cref="OwnerId"/>); sahip türü modül bildirir (<c>IModule.CredentialOwners</c>). Core yalnızca saklar ve sorgular; belgenin
/// zorunluluğunu sahip modül kendi kuralıyla söyler (ör. sefer atamasında geçerli SRC). Dosyası Core dosya deposundadır (ADR-0040) ve
/// bu kayda bağlanır. Tenant verisidir; Row-Level Security ile korunur. Şirketler arasında ortaktır: kapsamı sahip kaydın kapsamıdır.
/// </summary>
public sealed class Credential : ITenantEntity, ICompanyShared
{
    public const int OwnerEntityKeyMaxLength = 100;
    public const int TypeKeyMaxLength = 50;
    public const int DocumentNoMaxLength = 50;
    public const int IssuedByMaxLength = 200;
    public const int NotesMaxLength = 2000;

    public Credential()
    {
        Id = Guid.CreateVersion7();
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        Version = 1;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Sahip kaydın türü (ör. <c>fleet.vehicle</c>); oluşturulduktan sonra değişmez.</summary>
    public string OwnerEntityKey { get; set; } = string.Empty;

    /// <summary>Sahip kaydın kimliği; oluşturulduktan sonra değişmez.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>Belge türü (<see cref="CredentialEntities.TypeList"/> değeri); kategorisi sahip türün kategorisiyle aynı olmalıdır.</summary>
    public string TypeKey { get; set; } = string.Empty;

    /// <summary>Belge numarası; verilirse aynı sahipte aynı türde tekildir.</summary>
    public string? DocumentNo { get; set; }

    /// <summary>Belgeyi veren kurum (ör. TÜVTÜRK, Emniyet).</summary>
    public string? IssuedBy { get; set; }

    public DateOnly? ValidFrom { get; set; }

    /// <summary>Bitiş tarihi; boşsa süresiz. Yaklaşan bitiş uyarısı bu alana bakar.</summary>
    public DateOnly? ValidTo { get; set; }

    public string Status { get; set; } = CredentialStatuses.Active;

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Her değişiklikte artar; eşzamanlı güncellemeleri yakalamak için kullanılır (409).</summary>
    public long Version { get; private set; }

    public void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}
