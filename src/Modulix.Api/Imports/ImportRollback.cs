using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Imports;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Api.Imports;

/// <summary>Geri almanın sonucu: silinen, zaten silinmiş olan ve korunan kayıtlar (korunanların nedeniyle).</summary>
public sealed record ImportRollbackOutcome(ImportRun Run, int Removed, int AlreadyRemoved, IReadOnlyList<(Guid RecordId, string Reason)> Kept);

/// <summary>
/// İçe aktarmayı geri alma (ADR-0033 Dilim 4d, Ç7.3). Koşunun oluşturduğu kayıtlar denetim kaydından bulunur: koşunun korelasyon kimliğiyle,
/// içe aktarma kaynağından yazılmış "oluşturuldu" satırları. Her kayıt kayıt boru hattından tek tek silinir (izin, kapsam, audit, olay); bu yüzden
/// geri alan kişinin o kayıt türünde silme yetkisi olmalıdır. Sonradan değiştirilmiş kayıt silinmez (kullanıcının emeği kaybolmasın), başka
/// kayda bağlı ya da yetkinin yetmediği kayıt da silinmez; hepsi nedeniyle raporlanır. İşlem hepsi-ya-da-hiç değildir: silinebilen silinir.
/// </summary>
public sealed class ImportRollback(
    ModulixDbContext db, IRecordPipeline pipeline, ICurrentAccess access, ITenantContextAccessor tenant)
{
    public const int ReasonMaxLength = 300;

    /// <summary>SQL Server parametre sınırının (2.100) altında kalan parça büyüklüğü.</summary>
    private const int Chunk = 500;

    public async Task<RecordResult<ImportRollbackOutcome>> RollBackAsync(Guid runId, string? reason, CancellationToken cancellationToken)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > ReasonMaxLength)
        {
            return RecordResult<ImportRollbackOutcome>.Fail(RecordErrorCode.ValidationFailed, "Geri alma gerekçesi zorunludur.",
                [new RecordFieldError("reason", $"Gerekçe zorunludur (en çok {ReasonMaxLength} karakter).")]);
        }

        // Firmanın bütün koşularını görme izni yoksa yalnızca kendi koşusu geri alınabilir; başkasınınki "bulunamadı" görünür.
        var current = tenant.Current ?? throw new InvalidOperationException("Aktif tenant yok.");
        var seesAll = await access.HasPermissionAsync(CoreModule.Permissions.ReadImports, cancellationToken);
        var run = await db.ImportRuns.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || (!seesAll && run.UserId != current.UserId))
        {
            return RecordResult<ImportRollbackOutcome>.Fail(RecordErrorCode.NotFound, "İçe aktarma bulunamadı.");
        }

        if (!run.CanRollBack || run.CorrelationId is not { } correlation)
        {
            return RecordResult<ImportRollbackOutcome>.Fail(RecordErrorCode.Conflict,
                "Yalnızca kayıt almış ve bitmiş bir içe aktarma geri alınabilir (kuru çalışma, başarısız ya da zaten geri alınmış koşu değil).");
        }

        var created = await db.AuditLog.AsNoTracking()
            .Where(a => a.CorrelationId == correlation && a.EntityKey == run.EntityKey && a.Action == AuditAction.Created && a.Source == ExecutionSource.Import)
            .Select(a => a.RecordId).Distinct().ToListAsync(cancellationToken);

        // Sonradan değişen ya da silinen kayıtlar (koşudan sonraki herhangi bir denetim satırı).
        var later = new Dictionary<Guid, AuditAction>();
        foreach (var part in created.Chunk(Chunk))
        {
            var rows = await db.AuditLog.AsNoTracking()
                .Where(a => a.EntityKey == run.EntityKey && part.Contains(a.RecordId) && a.Action != AuditAction.Created)
                .Select(a => new { a.RecordId, a.Action }).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                // Silinmiş olması değişmiş olmasından önce gelir: kayıt artık yoktur.
                if (row.Action == AuditAction.Deleted || !later.ContainsKey(row.RecordId))
                {
                    later[row.RecordId] = row.Action;
                }
            }
        }

        var removed = 0;
        var alreadyRemoved = 0;
        var kept = new List<(Guid, string)>();
        foreach (var id in created)
        {
            if (later.TryGetValue(id, out var action))
            {
                if (action == AuditAction.Deleted)
                {
                    alreadyRemoved++;
                }
                else
                {
                    kept.Add((id, "İçe aktarmadan sonra değiştirildi."));
                }

                continue;
            }

            var record = await pipeline.GetAsync(new RecordRef(run.EntityKey, id), cancellationToken);
            if (!record.IsSuccess)
            {
                if (record.Error!.Code == RecordErrorCode.NotFound)
                {
                    alreadyRemoved++;
                }
                else
                {
                    kept.Add((id, Reason(record.Error.Code)));
                }

                continue;
            }

            var deleted = await pipeline.DeleteAsync(new RecordRef(run.EntityKey, id), record.Value!.Version, cancellationToken, ExecutionSource.Import);
            if (deleted.IsSuccess)
            {
                removed++;
            }
            else
            {
                kept.Add((id, Reason(deleted.Error!.Code)));
            }
        }

        // Hiçbir şey silinmediyse (hepsi korunuyor) koşu geri alınmış sayılmaz: engel kalkınca yeniden denenebilir.
        if (removed + alreadyRemoved > 0)
        {
            run.RollBack($"Geri alındı ({text}): {removed} kayıt silindi"
                + (alreadyRemoved > 0 ? $", {alreadyRemoved} kayıt zaten silinmişti" : string.Empty)
                + (kept.Count > 0 ? $", {kept.Count} kayıt korundu (değişmiş, bağlı ya da yetki yok)." : "."));
            await db.SaveChangesAsync(cancellationToken);
        }

        return RecordResult<ImportRollbackOutcome>.Ok(new ImportRollbackOutcome(run, removed, alreadyRemoved, kept));
    }

    private static string Reason(RecordErrorCode code) => code switch
    {
        RecordErrorCode.PermissionDenied => "Bu kaydı silme yetkiniz yok.",
        RecordErrorCode.Conflict => "Kayıt başka kayıtlara bağlı ya da bu arada değişti.",
        _ => "Kayıt silinemedi.",
    };
}
