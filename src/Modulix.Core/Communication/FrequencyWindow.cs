using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Communication;

/// <summary>
/// Bir iletişim noktasına bir günde ayrılan pazarlama iletisi sayısı (ADR-0025, dilim 5). Yalnızca <see cref="IFrequencyLimiter"/> yazar (atomik
/// SQL ile: iki kampanya aynı anda sınırı aşamaz); bu sınıf şemayı ve satır güvenliğini tanımlar. Adres normalize biçimindedir.
/// </summary>
public sealed class FrequencyWindow : ITenantEntity, ICompanyShared
{
    public const int AddressMaxLength = CommunicationAddress.MaxLength;

    private FrequencyWindow()
    {
        // EF Core için
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public CommunicationChannel Channel { get; private set; }

    public string Address { get; private set; } = string.Empty;

    /// <summary>Tenant saat dilimindeki gün.</summary>
    public DateOnly Day { get; private set; }

    public int Count { get; private set; }
}
