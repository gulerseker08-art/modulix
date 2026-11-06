using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Modulix.Api.Tenancy;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Imports;
using Modulix.Core.Reporting;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;

namespace Modulix.Api.Imports;

/// <summary>Hata raporu: dosya satırları kişisel veri taşıyabildiği için yalnızca bellekte ve kısa süreli tutulur, veritabanına yazılmaz (ADR-0033).</summary>
public sealed class ImportErrorReports(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public void Put(Guid tenantId, Guid runId, string fileName, byte[] content) =>
        cache.Set(Key(tenantId, runId), (fileName, content), Lifetime);

    public (string FileName, byte[] Content)? Find(Guid tenantId, Guid runId) =>
        cache.TryGetValue(Key(tenantId, runId), out (string FileName, byte[] Content) report) ? report : null;

    private static string Key(Guid tenantId, Guid runId) => $"import-report:{tenantId:N}:{runId:N}";
}

/// <summary>
/// İçe aktarma motoru (ADR-0033, Dilim 2). Dosya düzeyi denetim istekte yapılır; satırlar arka planda işlenir, ekran koşuyu sorgulayarak izler.
/// Her satır kayıt boru hattından geçer. Depo varlığı doğrulama bitmeden izlemeye alır, bu yüzden reddedilen satırdan sonra DI kapsamı atılır:
/// aksi halde reddedilen kayıt sonraki satırla yazılırdı. Başarılı satırlar kapsamı paylaşır, yetki sorguları her satırda yinelenmez.
/// Kuru çalışma aynı yoldan geçer, satırın işlemi geri alınır; doğrulama birebir aynıdır. Tenant başına tek koşu oturum kilidiyle sağlanır.
/// Dosya yalnızca koşu boyunca bellektedir, kaldığı yerden devam yoktur.
/// </summary>
public sealed class ImportRunner(
    ITenantScopeFactory tenantScopes,
    ModulixDatabaseOptions database,
    ImportErrorReports reports,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<ImportRunner> logger) : IHostedService
{
    /// <summary>Kapsamın en çok kaç satır paylaşıldığı: izlenen varlıklar birikmesin, yetki değişikliği uzun koşuda da yansısın. İlerleme de bu aralıkla yazılır.</summary>
    private const int RowsPerScope = 50;

    /// <summary>Biten koşunun kilidi durum yazıldıktan hemen sonra bırakılır; bu kısa aralıkta gelen yeni dosya reddedilmesin diye biraz beklenir.</summary>
    private const int LockWaitMilliseconds = 2000;

    private const string LockName = "modulix-imports";
    private const string DryRunMarker = "Kuru çalışma: yazılmadı.";

    private readonly ConcurrentDictionary<Guid, Task> running = new();

    /// <summary>Kilit kaynağı tenant başınadır: bir firmanın koşusu diğerini engellemez.</summary>
    public static string LockResource(Guid tenantId) => $"{LockName}:{tenantId:N}";

    /// <summary>Dosyayı denetler ve satırları arka plana verir. Dosya geçersizse koşu burada <see cref="ImportRunStatus.Failed"/> biter.</summary>
    /// <returns>Koşunun kimliği; tenant'ta başka koşu sürüyorsa <c>null</c>.</returns>
    public async Task<Guid?> StartAsync(
        ImportDefinition definition, ImportRunMode mode, string fileName, byte[] content, TenantContext tenant, CompanyContext? company)
    {
        var stopping = lifetime.ApplicationStopping;

        // Kilit ayrı bağlantıda, koşu boyunca tutulur. Açıkça bırakılmalıdır: kapanan bağlantı havuza döner, SQL oturumu ve kilit yaşamaya devam eder.
        var lockConnection = new SqlConnection(database.AppConnectionString);
        var scope = default(AsyncServiceScope?);
        var handedOver = false;
        try
        {
            await lockConnection.OpenAsync(stopping);
            if (!await TryLockAsync(lockConnection, tenant.TenantId, stopping))
            {
                return null;
            }

            scope = Scope(tenant, company);
            var services = scope.Value.ServiceProvider;
            var db = services.GetRequiredService<ModulixDbContext>();

            // Kilit bizdeyken "çalışıyor" görünen koşu, uygulama çökerken yarıda kalmıştır.
            foreach (var dead in await db.ImportRuns.Where(r => r.Status == ImportRunStatus.Running).ToListAsync(stopping))
            {
                dead.Fail("Uygulama yeniden başladığı için yarıda kaldı; alınan satırları kontrol edip kalanları yükleyin.", time.GetUtcNow());
            }

            var correlation = (Activity.Current?.TraceId ?? ActivityTraceId.CreateRandom()).ToString();
            var run = ImportRun.Create(
                tenant.TenantId, tenant.UserId, definition.EntityKey, fileName, content.LongLength, Convert.ToHexStringLower(SHA256.HashData(content)),
                definition.TemplateVersion, mode, ImportFailurePolicy.SkipInvalidRows, correlation, time.GetUtcNow());
            db.ImportRuns.Add(run);

            var fields = await services.GetRequiredService<ImportSchema>().FieldsAsync(tenant, definition, stopping);
            var file = ImportCsvFile.Read(content);
            var columns = ImportColumns.Bind(file.Headers, fields);
            var issues = new IssueList(tenant.TenantId, run.Id, time);
            foreach (var header in columns.Unknown)
            {
                issues.Add(0, new ImportRowProblem(null, ImportIssueCodes.FileInvalid, ImportIssueSeverity.Warning, $"Tanınmayan sütun yok sayıldı: {header}."));
            }

            run.Start(file.Rows.Count, time.GetUtcNow());
            if (FileProblem(file, columns) is { } problems)
            {
                foreach (var message in problems)
                {
                    issues.Add(0, new ImportRowProblem(null, ImportIssueCodes.FileInvalid, ImportIssueSeverity.Error, message));
                }

                run.Fail(problems[0], time.GetUtcNow());
                await issues.SaveAsync(db);
                return run.Id;
            }

            await db.SaveChangesAsync(stopping);

            var job = new ImportJob(definition, fields, mode, fileName, file, columns, run, issues, db, tenant, company, correlation);
            var owned = scope.Value;
            Task task;

            // İstek bağlamı arka plana taşınmaz; izleme kimliği işin içinde yeniden kurulur.
            using (ExecutionContext.SuppressFlow())
            {
                task = Task.Run(() => RunJobAsync(job, owned, lockConnection));
            }

            handedOver = true;
            running[run.Id] = task;
            _ = task.ContinueWith(_ => running.TryRemove(run.Id, out var _), TaskScheduler.Default);
            return run.Id;
        }
        finally
        {
            if (!handedOver)
            {
                if (scope is { } unused)
                {
                    await unused.DisposeAsync();
                }

                await ReleaseAsync(lockConnection, tenant.TenantId);
            }
        }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Uygulama kapanırken koşular sıradaki satırda durur; sonuçlarını yazmaları beklenir.</summary>
    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.WhenAll(running.Values).WaitAsync(cancellationToken);

    private async Task RunJobAsync(ImportJob job, AsyncServiceScope scope, SqlConnection lockConnection)
    {
        using var activity = new Activity("modulix.import");
        activity.SetParentId(ActivityTraceId.CreateFromString(job.Correlation), ActivitySpanId.CreateRandom());
        activity.Start();
        try
        {
            await ProcessAsync(job);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "İçe aktarma koşusu tamamlanamadı ({RunId}).", job.Run.Id);
            await FailQuietlyAsync(job);
        }
        finally
        {
            await scope.DisposeAsync();
            await ReleaseAsync(lockConnection, job.Tenant.TenantId);
        }
    }

    private async Task ProcessAsync(ImportJob job)
    {
        var stopping = lifetime.ApplicationStopping;
        var (run, issues, db) = (job.Run, job.Issues, job.Db);
        var created = 0;
        var processed = 0;
        var sharedRows = 0;
        var failedRows = new List<(ImportFileRow Row, string Error)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AsyncServiceScope? rowScope = null;
        try
        {
            foreach (var row in job.File.Rows)
            {
                stopping.ThrowIfCancellationRequested();
                var (values, problems) = ImportRows.Build(job.Columns.Columns, row);
                if (problems.Count == 0)
                {
                    rowScope ??= Scope(job.Tenant, job.Company);
                    (problems, var clean) = await ProcessRowAsync(job, rowScope.Value.ServiceProvider, values, seen, stopping);
                    if (!clean || ++sharedRows >= RowsPerScope)
                    {
                        await rowScope.Value.DisposeAsync();
                        rowScope = null;
                        sharedRows = 0;
                    }
                }

                issues.AddRange(row.RowNo, problems);
                var errors = problems.Where(p => p.Severity == ImportIssueSeverity.Error).ToList();
                if (errors.Count == 0)
                {
                    created++;
                }
                else
                {
                    failedRows.Add((row, string.Join(" ", errors.Select(e => e.Message))));
                }

                if (++processed % RowsPerScope == 0)
                {
                    run.ReportProgress(created, failedRows.Count);
                    await db.SaveChangesAsync(stopping);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            run.Stop(created, failedRows.Count, "Uygulama kapanırken durdu. Alınan satırlar kaldı; dosyanın kalanını ayrı yükleyin.", time.GetUtcNow());
            await issues.SaveAsync(db);
            return;
        }
        finally
        {
            if (rowScope is { } open)
            {
                await open.DisposeAsync();
            }
        }

        // Rapor, koşu bitmiş görünmeden hazır olmalı: ekran bitişi görür görmez indirebilir.
        if (failedRows.Count > 0)
        {
            var name = ReportCsv.FileSlug(Path.GetFileNameWithoutExtension(job.FileName)) + "-hatalar.csv";
            reports.Put(job.Tenant.TenantId, run.Id, name, ImportErrorReport.Build(job.File, failedRows));
        }

        run.Complete(created, 0, 0, failedRows.Count, Summary(job.Mode, created, failedRows.Count), time.GetUtcNow());
        await issues.SaveAsync(db);
    }

    // Beklenmedik hata: bağlam bozulmuş olabilir, koşu yeni kapsamda kapatılır.
    private async Task FailQuietlyAsync(ImportJob job)
    {
        try
        {
            await using var scope = Scope(job.Tenant, job.Company);
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            if (await db.ImportRuns.SingleOrDefaultAsync(r => r.Id == job.Run.Id, CancellationToken.None) is { IsTerminal: false } run)
            {
                run.Fail("Beklenmedik bir hata nedeniyle tamamlanamadı; alınan satırları kontrol edin.", time.GetUtcNow());
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is DbUpdateException or SqlException or InvalidOperationException)
        {
            // Sonraki koşu bunu yarıda kalmış sayıp kapatır.
            logger.LogError(exception, "İçe aktarma koşusu kapatılamadı ({RunId}).", job.Run.Id);
        }
    }

    private static string[]? FileProblem(ImportFileContent file, ImportColumnMap columns)
    {
        if (file.Headers.Count == 0)
        {
            return ["Dosya boş."];
        }

        if (file.Headers.Count > ImportLimits.MaxColumns)
        {
            return [$"Dosyada en çok {ImportLimits.MaxColumns} sütun olabilir."];
        }

        if (columns.Errors.Count > 0)
        {
            return [.. columns.Errors];
        }

        if (columns.Columns.Count == 0)
        {
            return ["Dosyada tanınan sütun yok; şablonu indirip başlıkları karşılaştırın."];
        }

        if (file.Rows.Count == 0)
        {
            return ["Dosyada başlık dışında satır yok."];
        }

        return file.Rows.Count > ImportLimits.MaxRows
            ? [$"Bir dosyada en çok {ImportLimits.MaxRows:N0} satır olabilir; dosyayı bölüp yükleyin."]
            : null;
    }

    private static string Summary(ImportRunMode mode, int accepted, int failed) => mode == ImportRunMode.DryRun
        ? $"Kuru çalışma: {accepted} satır alınabilir, {failed} satır hatalı. Hiçbir kayıt yazılmadı."
        : $"{accepted} kayıt oluşturuldu, {failed} satır alınmadı.";

    /// <returns>Satırın sorunları ve kapsamın sonraki satırda kullanılıp kullanılamayacağı.</returns>
    private async Task<(IReadOnlyList<ImportRowProblem> Problems, bool Clean)> ProcessRowAsync(
        ImportJob job, IServiceProvider services, JsonObject values, HashSet<string> seen, CancellationToken cancellationToken)
    {
        try
        {
            var pipeline = services.GetRequiredService<IRecordPipeline>();
            var warnings = await MatchAsync(pipeline, job, values, seen, cancellationToken);
            var command = new RecordCommand(job.Definition.EntityKey, null, values, Source: ExecutionSource.Import);
            var result = job.Mode == ImportRunMode.Apply
                ? await pipeline.CreateAsync(command, cancellationToken)
                : await RehearseAsync(pipeline, command, cancellationToken);
            return result.IsSuccess ? (warnings, true) : (ImportRows.FromRecordError(result.Error!, values), false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Satırın değeri günlüğe yazılmaz; tek satırın beklenmedik hatası koşuyu durdurmaz.
            logger.LogError(exception, "İçe aktarma satırı işlenemedi ({EntityKey}).", job.Definition.EntityKey);
            return ([new ImportRowProblem(null, ImportIssueCodes.SystemError, ImportIssueSeverity.Error, "Satır işlenirken beklenmedik bir hata oluştu.")], false);
        }
    }

    // Boru hattı olduğu gibi çalışır, işlem sonda geri alınır: kayıt, denetim ve olay yazılmaz.
    private static async Task<RecordResult<RecordData>> RehearseAsync(IRecordPipeline pipeline, RecordCommand command, CancellationToken cancellationToken)
    {
        RecordResult<RecordData>? outcome = null;
        await pipeline.RunAtomicAsync<bool>(
            async ct =>
            {
                outcome = await pipeline.CreateAsync(command, ct);
                return RecordResult<bool>.Fail(RecordErrorCode.Conflict, DryRunMarker);
            },
            cancellationToken);
        return outcome!;
    }

    /// <summary>Olası mükerrer uyarıları (ADR-0013): engellemez. Sorgu kullanıcının kapsamında çalışır, göremediği kaydın varlığını sezdirmez.</summary>
    private static async Task<IReadOnlyList<ImportRowProblem>> MatchAsync(
        IRecordPipeline pipeline, ImportJob job, JsonObject values, HashSet<string> seen, CancellationToken cancellationToken)
    {
        var warnings = new List<ImportRowProblem>();
        foreach (var key in job.Definition.MatchFields)
        {
            if (values[key]?.ToString() is not { Length: > 0 } value || job.Fields.FirstOrDefault(f => f.Key == key) is not { } field)
            {
                continue;
            }

            if (!seen.Add(key + "\n" + value.ToLowerInvariant()))
            {
                warnings.Add(Possible(key, $"Aynı {field.Label} dosyada birden çok satırda var."));
                continue;
            }

            var found = await pipeline.QueryAsync(
                new RecordQuery(job.Definition.EntityKey, PageSize: 1, Filters: new Dictionary<string, string> { [key] = value }), cancellationToken);
            if (found.IsSuccess && found.Value!.TotalCount > 0)
            {
                warnings.Add(Possible(key, $"Aynı {field.Label} ile kayıt var; yine de alınır."));
            }
        }

        return warnings;
    }

    private static ImportRowProblem Possible(string key, string message) =>
        new(key, ImportIssueCodes.DuplicatePossible, ImportIssueSeverity.Warning, message);

    private AsyncServiceScope Scope(TenantContext tenant, CompanyContext? company)
    {
        var scope = tenantScopes.Create(tenant);
        scope.ServiceProvider.GetRequiredService<HttpCompanyContextAccessor>().Current = company;
        return scope;
    }

    private static async Task<bool> TryLockAsync(SqlConnection connection, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = @name, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = @wait; SELECT @r;";
        command.Parameters.AddWithValue("@name", LockResource(tenantId));
        command.Parameters.AddWithValue("@wait", LockWaitMilliseconds);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) >= 0;
    }

    private async Task ReleaseAsync(SqlConnection connection, Guid tenantId)
    {
        await using (connection)
        {
            try
            {
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "IF APPLOCK_MODE(N'public', @name, N'Session') <> N'NoLock' EXEC sys.sp_releaseapplock @Resource = @name, @LockOwner = N'Session';";
                    command.Parameters.AddWithValue("@name", LockResource(tenantId));
                    await command.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
            catch (SqlException exception)
            {
                // Bağlantı kopmuşsa oturum kilidi zaten düşmüştür.
                logger.LogWarning(exception, "İçe aktarma kilidi bırakılamadı.");
            }
        }
    }

    private sealed record ImportJob(
        ImportDefinition Definition, IReadOnlyList<ImportField> Fields, ImportRunMode Mode, string FileName, ImportFileContent File, ImportColumnMap Columns,
        ImportRun Run, IssueList Issues, ModulixDbContext Db, TenantContext Tenant, CompanyContext? Company, string Correlation);

    /// <summary>Koşunun sorunları; üst sınırın ötesi saklanmaz (hatalı satır sayısı yine doğrudur).</summary>
    private sealed class IssueList(Guid tenantId, Guid runId, TimeProvider time)
    {
        private readonly List<ImportRowIssue> items = [];

        public void Add(int rowNo, ImportRowProblem problem)
        {
            if (items.Count < ImportRun.MaxStoredIssues)
            {
                items.Add(ImportRowIssue.Create(tenantId, runId, rowNo, problem.ColumnKey, problem.Code, problem.Severity, problem.Message, time.GetUtcNow()));
            }
        }

        public void AddRange(int rowNo, IEnumerable<ImportRowProblem> problems)
        {
            foreach (var problem in problems)
            {
                Add(rowNo, problem);
            }
        }

        // Son yazım uygulama kapanırken de tamamlanmalıdır.
        public async Task SaveAsync(ModulixDbContext db)
        {
            db.ImportRowIssues.AddRange(items);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
