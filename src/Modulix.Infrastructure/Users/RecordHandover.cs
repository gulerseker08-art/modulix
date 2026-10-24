using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Modules;
using Modulix.Core.Platform;

namespace Modulix.Infrastructure.Users;

/// <summary>
/// Kayıt devri (ADR-0004). Çekirdek modüllerin tablolarını bilmez: modüller kişiye atanan kayıt türlerini bildirir
/// (<see cref="IModule.OwnedRecords"/>), devir kayıt boru hattıyla, sorumlu alanını güncelleyerek yapılır. Böylece her
/// kayıt için yetki, doğrulama (yeni sorumlu etkin üye mi), denetim kaydı ve olay normal düzenlemedeki gibi uygulanır.
/// Yalnızca tenant için açık modüllere bakılır. Çağıran işlemi açar; bir kayıt başarısız olursa hata fırlatılır ve
/// işlem geri alınmalıdır.
/// </summary>
internal sealed class RecordHandover(ModuleCatalog catalog, ICurrentAccess access, IRecordPipeline pipeline)
{
    private const int BatchSize = RecordQuery.MaxPageSize;

    public async Task<IReadOnlyList<OwnedRecordSummary>> CountAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        var counts = new List<OwnedRecordSummary>();
        foreach (var type in await EnabledTypesAsync(cancellationToken))
        {
            var page = await pipeline.QueryAsync(Query(type, ownerId, pageSize: 1), cancellationToken);

            // Bu kayıtları göremeyen kullanıcıya sayı verilmez; devir de bu kişi için yapılamaz.
            counts.Add(new OwnedRecordSummary(type.EntityKey, type.Label, page.IsSuccess ? page.Value!.TotalCount : null));
        }

        return counts;
    }

    /// <summary>Devredilen kayıt sayısı.</summary>
    public async Task<int> TransferAsync(Guid fromUserId, Guid toUserId, CancellationToken cancellationToken)
    {
        var moved = 0;
        var values = new JsonObject { [OwnedRecordType.OwnerField] = toUserId.ToString() };

        foreach (var type in await EnabledTypesAsync(cancellationToken))
        {
            // Devredilen kayıt filtreden çıkar; ilk sayfa boşalana kadar okunur. Aynı kayıt tekrar gelirse (güncelleme
            // sorumluyu değiştirmediyse) döngü durdurulur.
            var seen = new HashSet<Guid>();
            while (true)
            {
                var page = await pipeline.QueryAsync(Query(type, fromUserId, BatchSize), cancellationToken);
                if (!page.IsSuccess)
                {
                    throw Failure(type, page.Error!);
                }

                if (page.Value!.Items.Count == 0)
                {
                    break;
                }

                foreach (var record in page.Value.Items)
                {
                    if (!seen.Add(record.Id))
                    {
                        throw new InvalidOperationException($"{type.EntityKey} kaydı {record.Id} devredilemedi: sorumlu değişmedi.");
                    }

                    var updated = await pipeline.UpdateAsync(
                        new RecordCommand(type.EntityKey, record.Id, values.DeepClone().AsObject()), cancellationToken);
                    if (!updated.IsSuccess)
                    {
                        throw Failure(type, updated.Error!);
                    }

                    moved++;
                }
            }
        }

        return moved;
    }

    private async Task<IReadOnlyList<OwnedRecordType>> EnabledTypesAsync(CancellationToken cancellationToken)
    {
        var types = new List<OwnedRecordType>();
        foreach (var module in catalog.Modules)
        {
            if (module.OwnedRecords.Count > 0 && await access.IsModuleEnabledAsync(module.Key, cancellationToken))
            {
                types.AddRange(module.OwnedRecords);
            }
        }

        return types;
    }

    private static RecordQuery Query(OwnedRecordType type, Guid ownerId, int pageSize)
    {
        var filters = new Dictionary<string, string>(type.Filters ?? new Dictionary<string, string>())
        {
            [OwnedRecordType.OwnerField] = ownerId.ToString(),
        };
        return new RecordQuery(type.EntityKey, Page: 1, PageSize: pageSize, Filters: filters);
    }

    private static UserAdministrationException Failure(OwnedRecordType type, RecordError error) =>
        error.Code == RecordErrorCode.PermissionDenied
            ? new UserAdministrationException(
                UserAdministrationError.PermissionDenied,
                $"Kayıtları devretmek için {type.Label} kayıtlarını düzenleme izniniz olmalı. Hiçbir kayıt devredilmedi.")
            : new UserAdministrationException(
                UserAdministrationError.Conflict,
                $"{type.Label} kayıtları devredilemedi: {Describe(error)} Hiçbir kayıt devredilmedi.");

    private static string Describe(RecordError error) =>
        error.FieldErrors is { Count: > 0 } fields ? string.Join(" ", fields.Select(f => f.Message)) : error.Message;
}
