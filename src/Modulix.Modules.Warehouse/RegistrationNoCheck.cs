using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customs;
using Modulix.Modules.Warehouse.Domain;
using Modulix.Modules.Warehouse.Records;

namespace Modulix.Modules.Warehouse;

/// <param name="HasRule">Rejim bir profile bağlı ve profilin tescil no kuralı var.</param>
/// <param name="Enforcement">Etkin düzey: <c>Warn</c> ya da <c>Block</c> (profilin düzeyi, tenant sıkılaştırmasıyla).</param>
/// <param name="Violations">İhlal iletileri (boşsa numara kurala uygun ya da kural yok).</param>
public sealed record RegistrationNoCheckResult(bool HasRule, string Enforcement, IReadOnlyList<string> Violations, string? ProfileName, int? VersionNumber, bool Verified);

/// <summary>
/// Beyannamenin tescil numarasını rejimine bağlı profilin kuralına göre denetler (ADR-0032 K7, K16). Kayıt boru hattı ve arayüz aynı servisi
/// kullanır: <c>Block</c> düzeyinde kayıt reddedilir, <c>Warn</c> düzeyinde yalnızca arayüz uyarır. Rejim bir profile bağlı değilse kural yoktur.
/// </summary>
public interface IRegistrationNoCheck
{
    Task<RegistrationNoCheckResult> CheckAsync(string regimeKey, string registrationNo, CancellationToken cancellationToken);
}

internal sealed class RegistrationNoCheck(WarehouseStoreContext context) : IRegistrationNoCheck
{
    private static readonly RegistrationNoCheckResult NoRule = new(false, nameof(RuleEnforcement.Warn), [], null, null, false);

    public async Task<RegistrationNoCheckResult> CheckAsync(string regimeKey, string registrationNo, CancellationToken cancellationToken)
    {
        var binding = await context.Data.Set<RegimeBinding>().AsNoTracking()
            .Where(b => b.RegimeKey == regimeKey).Select(b => new { b.ProfileId, b.Enforcement }).SingleOrDefaultAsync(cancellationToken);
        if (binding is null)
        {
            return NoRule;
        }

        // Yayını kalkmış ya da pasife alınmış profil kural uygulamaz (bağlama kalır, kayıt engellenmez).
        var profile = await context.RegimeProfiles.GetPublishedAsync(binding.ProfileId, cancellationToken);
        if (profile?.Content.RegistrationNo is not { } rule)
        {
            return NoRule;
        }

        var tenantLevel = binding.Enforcement switch
        {
            RegimeBinding.Warn => RuleEnforcement.Warn,
            RegimeBinding.Block => RuleEnforcement.Block,
            _ => (RuleEnforcement?)null,
        };
        var level = RegimeProfileRules.Tighten(rule.Enforcement, tenantLevel);
        return new RegistrationNoCheckResult(
            true, level.ToString(), RegimeProfileRules.CheckRegistrationNo(rule, registrationNo), profile.Name, profile.VersionNumber, profile.IsVerified);
    }
}
