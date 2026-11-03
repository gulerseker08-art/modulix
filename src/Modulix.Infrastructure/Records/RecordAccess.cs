using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Records;

namespace Modulix.Infrastructure.Records;

/// <summary>
/// Modüllere verilen erişim bilgisi (ADR-0010); boru hattının kullandığı izin ve veri kapsamı servislerini sarar.
/// İzin servisi kayıt deposu listesine, o da modül depolarına (bu servisi kullanan) bağlı olduğu için tembel çözülür.
/// </summary>
internal sealed class RecordAccess(
    ITenantContextAccessor tenant, IDataScopeService dataScope, IServiceProvider services) : IRecordAccess
{
    private static readonly IReadOnlySet<Guid> NoOwners = new HashSet<Guid>();

    public async Task<IReadOnlySet<Guid>?> GetOwnersAsync(CancellationToken cancellationToken) =>
        tenant.Current is { } current ? await dataScope.GetOwnersAsync(current, cancellationToken) : NoOwners;

    public async Task<bool> HidesOutOfScopeSharedRecordsAsync(CancellationToken cancellationToken) =>
        tenant.Current is { } current && await dataScope.HidesOutOfScopeSharedAsync(current, cancellationToken);

    public async Task<bool> CanAsync(string entityKey, RecordOperation operation, CancellationToken cancellationToken) =>
        tenant.Current is { } current
        && await services.GetRequiredService<IPermissionService>().HasPermissionAsync(current, entityKey, operation, cancellationToken);
}
