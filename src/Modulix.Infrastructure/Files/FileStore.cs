using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modulix.Core.Contracts.Files;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Files;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Files;

/// <summary>
/// Kayıtlara bağlı dosyalar (ADR-0040). Erişim bağlı kaydın erişimidir ve her istekte yeniden sorulur: kaydı <see cref="IRecordPipeline.GetAsync"/> ile
/// okuyamayan için dosya yoktur (bulunamadı); yazma ayrıca kayıt türünde güncelleme izni ve yazılabilir modül ister. Dosya kimliğini bilmek yetki vermez.
/// Yükleme ve silme <c>core.file</c> anahtarıyla denetim kaydına yazılır. Önce içerik, sonra üst veri yazılır; üst veri kaydedilemezse içerik silinir.
/// </summary>
internal sealed class FileStore(
    ModulixDbContext db,
    IBlobStorage blobs,
    IRecordPipeline pipeline,
    IRecordAccess access,
    IRecordStoreRegistry stores,
    IModuleLicense license,
    ITenantContextAccessor tenantAccessor,
    IAuditWriter audit,
    IOptions<FileStorageOptions> options,
    ILogger<FileStore> logger) : IFileStore, IFileLimits
{
    public long MaxFileBytes => options.Value.MaxFileBytes;

    public async Task<RecordResult<IReadOnlyList<StoredFileInfo>>> ListAsync(RecordRef record, CancellationToken cancellationToken)
    {
        if (await CheckReadAsync(record, cancellationToken) is { } error)
        {
            return RecordResult<IReadOnlyList<StoredFileInfo>>.Fail(error.Code, error.Message, error.FieldErrors);
        }

        var rows = await db.StoredFiles.AsNoTracking()
            .Where(f => f.EntityKey == record.EntityKey && f.RecordId == record.Id)
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
            .ToListAsync(cancellationToken);
        return RecordResult<IReadOnlyList<StoredFileInfo>>.Ok([.. rows.Select(ToInfo)]);
    }

    public async Task<RecordResult<StoredFileInfo>> AddAsync(RecordRef record, string? fileName, Stream content, CancellationToken cancellationToken)
    {
        var tenant = tenantAccessor.Current;
        if (tenant is null)
        {
            return RecordResult<StoredFileInfo>.Fail(RecordErrorCode.NoActiveTenant, "Aktif tenant yok.");
        }

        if (await CheckWriteAsync(tenant, record, cancellationToken) is { } error)
        {
            return RecordResult<StoredFileInfo>.Fail(error.Code, error.Message, error.FieldErrors);
        }

        // Sınır + 1 bayt okunur: fazlası gövdenin tamamı okunmadan reddedilir.
        var max = MaxFileBytes;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max)
            {
                break;
            }
        }

        var data = buffer.GetBuffer();
        var (name, contentType, validation) = FileRules.Validate(fileName, data.AsSpan(0, (int)Math.Min(buffer.Length, 4096)), buffer.Length, max);
        if (validation is not null)
        {
            return Invalid(validation);
        }

        var perRecord = db.StoredFiles.Where(f => f.EntityKey == record.EntityKey && f.RecordId == record.Id);
        if (await perRecord.CountAsync(cancellationToken) >= FileRules.MaxPerRecord)
        {
            return Invalid($"Bir kayda en fazla {FileRules.MaxPerRecord} dosya eklenebilir.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(data.AsSpan(0, (int)buffer.Length)));
        if (await perRecord.AnyAsync(f => f.Sha256 == hash, cancellationToken))
        {
            return RecordResult<StoredFileInfo>.Fail(RecordErrorCode.Conflict, "Bu dosya kayda zaten eklenmiş.", [new RecordFieldError("file", "Aynı içerik zaten ekli.")]);
        }

        var file = new StoredFile(record.EntityKey, record.Id, name!, contentType!, buffer.Length, hash, tenant.UserId);
        buffer.Position = 0;
        await blobs.PutAsync(tenant.TenantId, file.StorageKey, buffer, cancellationToken);
        try
        {
            db.StoredFiles.Add(file);
            audit.Add(AuditFor(tenant, file, AuditAction.Created, RecordDiff.Compute(null, Snapshot(file, record))));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Üst veri yazılamadıysa içerik yetim kalmasın.
            await blobs.DeleteAsync(tenant.TenantId, file.StorageKey, CancellationToken.None);
            throw;
        }

        return RecordResult<StoredFileInfo>.Ok(ToInfo(file));
    }

    public async Task<RecordResult<StoredFileContent>> OpenAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var tenant = tenantAccessor.Current;
        if (tenant is null)
        {
            return RecordResult<StoredFileContent>.Fail(RecordErrorCode.NoActiveTenant, "Aktif tenant yok.");
        }

        var file = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);
        if (file is null || await CheckReadAsync(new RecordRef(file.EntityKey, file.RecordId), cancellationToken) is not null)
        {
            return RecordResult<StoredFileContent>.Fail(RecordErrorCode.NotFound, "Dosya bulunamadı.");
        }

        if (!FileClassification.IsDownloadable(file.Classification) || file.ScanStatus == FileScanStatus.Infected)
        {
            return RecordResult<StoredFileContent>.Fail(RecordErrorCode.PermissionDenied, "Bu dosya indirilemez.");
        }

        if (!FileScanStatus.IsDownloadable(file.ScanStatus))
        {
            return RecordResult<StoredFileContent>.Fail(RecordErrorCode.Conflict, "Dosya taranıyor; tarama bitince indirilebilir.");
        }

        var stream = await blobs.OpenReadAsync(tenant.TenantId, file.StorageKey, cancellationToken);
        if (stream is null)
        {
            logger.LogError("Dosya {FileId} için içerik bulunamadı (tenant {TenantId}).", file.Id, tenant.TenantId);
            return RecordResult<StoredFileContent>.Fail(RecordErrorCode.NotFound, "Dosya içeriği bulunamadı.");
        }

        return RecordResult<StoredFileContent>.Ok(new StoredFileContent(ToInfo(file), stream));
    }

    public async Task<RecordResult<bool>> DeleteAsync(Guid fileId, long version, CancellationToken cancellationToken)
    {
        var tenant = tenantAccessor.Current;
        if (tenant is null)
        {
            return RecordResult<bool>.Fail(RecordErrorCode.NoActiveTenant, "Aktif tenant yok.");
        }

        var file = await db.StoredFiles.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);
        var record = file is null ? null : new RecordRef(file.EntityKey, file.RecordId);
        if (file is null || await CheckReadAsync(record!, cancellationToken) is not null)
        {
            return RecordResult<bool>.Fail(RecordErrorCode.NotFound, "Dosya bulunamadı.");
        }

        if (await CheckWriteAsync(tenant, record!, cancellationToken) is { } error)
        {
            return RecordResult<bool>.Fail(error.Code, error.Message, error.FieldErrors);
        }

        if (file.Version != version)
        {
            return RecordResult<bool>.Fail(RecordErrorCode.Conflict, "Dosya başkası tarafından değiştirilmiş.");
        }

        db.StoredFiles.Remove(file);
        audit.Add(AuditFor(tenant, file, AuditAction.Deleted, RecordDiff.Compute(Snapshot(file, record!), null)));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RecordResult<bool>.Fail(RecordErrorCode.Conflict, "Dosya başkası tarafından değiştirilmiş.");
        }

        // Üst veri kesinleştikten sonra içerik silinir; silinemezse yetim kalır (temizliği dilim 3) ama kullanıcıya hata gösterilmez.
        try
        {
            await blobs.DeleteAsync(tenant.TenantId, file.StorageKey, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Dosya {FileId} içeriği silinemedi; yetim içerik kaldı (tenant {TenantId}).", file.Id, tenant.TenantId);
        }

        return RecordResult<bool>.Ok(true);
    }

    /// <summary>Kayıt türü bilinmiyorsa ya da kayıt okunamıyorsa hata; izin eksikliği de "bulunamadı"dır (dosyanın varlığı sızmaz).</summary>
    private async Task<RecordError?> CheckReadAsync(RecordRef record, CancellationToken cancellationToken)
    {
        if (stores.Find(record.EntityKey) is null)
        {
            return new RecordError(RecordErrorCode.UnknownEntity, "Bu kayıt türüne dosya eklenemez.");
        }

        var read = await pipeline.GetAsync(record, cancellationToken);
        if (read.IsSuccess)
        {
            return null;
        }

        return read.Error!.Code is RecordErrorCode.PermissionDenied or RecordErrorCode.NotFound
            ? new RecordError(RecordErrorCode.NotFound, "Kayıt bulunamadı.")
            : read.Error;
    }

    private async Task<RecordError?> CheckWriteAsync(TenantContext tenant, RecordRef record, CancellationToken cancellationToken)
    {
        if (await CheckReadAsync(record, cancellationToken) is { } readError)
        {
            return readError;
        }

        if (!await access.CanAsync(record.EntityKey, RecordOperation.Update, cancellationToken))
        {
            return new RecordError(RecordErrorCode.PermissionDenied, "Bu kayda dosya eklemek ya da silmek için güncelleme izni gerekir.");
        }

        // Kayıt düzeyinde yazma kuralı olan türler (ör. süreli belge: sahip kaydın güncelleme izni) ayrıca sorulur.
        if (stores.Find(record.EntityKey) is IRecordWriteGuard guard && await guard.WriteDeniedAsync(record.Id, cancellationToken) is { } denied)
        {
            return new RecordError(RecordErrorCode.PermissionDenied, denied);
        }

        var moduleKey = record.EntityKey[..record.EntityKey.IndexOf('.', StringComparison.Ordinal)];
        if (!await license.IsWritableAsync(tenant.TenantId, moduleKey, cancellationToken))
        {
            return new RecordError(RecordErrorCode.PermissionDenied, "Modül lisansı salt okunur evrede; dosya eklenemez ya da silinemez.");
        }

        return null;
    }

    private static AuditEntry AuditFor(TenantContext tenant, StoredFile file, AuditAction action, JsonObject changes) =>
        new(tenant.TenantId, tenant.UserId, CoreModule.AuditedEntityKeys.File, file.Id, action, changes, ExecutionSource.Api, Activity.Current?.TraceId.ToString());

    private static JsonObject Snapshot(StoredFile file, RecordRef record) => new()
    {
        ["fileName"] = file.FileName,
        ["contentType"] = file.ContentType,
        ["sizeBytes"] = file.SizeBytes,
        ["sha256"] = file.Sha256,
        ["entityKey"] = record.EntityKey,
        ["recordId"] = record.Id.ToString(),
    };

    private static StoredFileInfo ToInfo(StoredFile f) =>
        new(f.Id, f.Version, f.EntityKey, f.RecordId, f.FileName, f.ContentType, f.SizeBytes, f.Sha256, f.Classification, f.ScanStatus, f.UploadedByUserId, f.CreatedAt);

    private static RecordResult<StoredFileInfo> Invalid(string message) =>
        RecordResult<StoredFileInfo>.Fail(RecordErrorCode.ValidationFailed, message, [new RecordFieldError("file", message)]);
}
