using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Sectors;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Sektör alanının kataloğa göre çözümü (ADR-0042): gelen değer kod ya da etkin sektör adı olabilir; sonuç katalogdaki kayıttır.
/// Katalogda olmayan değer alan hatasıdır; pasife alınmış sektör yalnızca kayıtta zaten varsa (<paramref name="stored"/>) kalabilir.
/// Firma ve Lead aynı kuralı kullanır.
/// </summary>
internal static class IndustryResolution
{
    public static async Task<Industry?> ResolveAsync(CrmStoreContext context, string written, string? stored, CancellationToken cancellationToken)
    {
        if (context.Industries is not { } catalog)
        {
            return null;
        }

        var industry = await catalog.ResolveAsync(written, cancellationToken)
            ?? throw new RecordValidationException([new RecordFieldError("industry", "Katalogda olmayan sektör.")]);
        return !industry.IsActive && stored != industry.Code
            ? throw new RecordValidationException([new RecordFieldError("industry", "Bu sektör artık kullanılmıyor.")])
            : industry;
    }
}
