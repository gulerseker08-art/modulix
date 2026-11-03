using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Reports;
using Modulix.Core.Authorization;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Reporting;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0026 dilim 4: dışa aktarma (CSV) ve zamanlanmış rapor. Mutlu yol, hata/doğrulama, tenant izolasyonu ve yetki; indirme anında yetki denetimi, formül
/// enjeksiyonu, süre sonu, zamanlayıcının sahibin yetkisiyle çalışması, yetki kaybında teslimin durması, tekrar/yarışan çalışmada ikinci dosya/e-posta oluşmaması.
/// </summary>
public class ReportDeliveryTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Reports = "/api/reports";
    private const string Opportunities = "crm.opportunities";
    private static readonly string[] DataRead = [CompaniesRead, OpportunitiesRead, OpportunityAmountsRead];

    private static readonly string[] ReportRights =
    [
        CoreModule.Permissions.ReadReports, CoreModule.Permissions.ManageReports, CoreModule.Permissions.ExportReports, CoreModule.Permissions.ScheduleReports,
    ];

    // ---- Dışa aktarma

    [Fact]
    public async Task Export_equals_the_on_screen_result_and_downloads_as_a_safe_csv_attachment()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "Alfa", 100m);
        await OpenAsync(s.Admin, s.CompanyId, "Beta", 200m);
        await OpenAsync(s.Admin, s.CompanyId, "Dolar", 50m, "USD");
        var report = await SaveAsync(s.Admin, "Aşama özeti", OpenByStage());
        var screen = await ReadAsync<ReportResultDto>(await s.Admin.PostAsJsonAsync($"{Reports}/{report.Summary.Id}/run", new RunReportRequest(null, null)));

        var export = await ReadAsync<ReportExportDto>(await s.Admin.PostAsync($"{Reports}/{report.Summary.Id}/exports", null), HttpStatusCode.Created);
        var response = await s.Admin.GetAsync($"{Reports}/exports/{export.Id}/download");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.EndsWith(".csv", response.Content.Headers.ContentDisposition.FileName);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Aşama,Para birimi,Kayıt sayısı,Toplam Tutar", lines[0]);
        // Dosyadaki her satır ekrandaki satırla aynı (grup, para birimi, adet, tutar) ve toplam satırları eklidir.
        foreach (var row in screen.Rows)
        {
            Assert.Contains(lines, l => l == FormattableString.Invariant($"{row.Cells[0].GetString()},{row.Cells[1].GetString()},{row.Cells[2].GetDecimal()},{row.Cells[3].GetDecimal()}"));
        }

        Assert.Equal(screen.Rows.Count + screen.Totals.Count, lines.Length - 1);
        Assert.Equal((screen.Rows.Count, false, "manual"), (export.RowCount, export.Truncated, export.Source));
        Assert.Single(await ListExportsAsync(s.Admin));
    }

    [Fact]
    public async Task Formula_cells_in_the_data_are_escaped_in_the_csv()
    {
        var s = await SetUpAsync();
        foreach (var title in new[] { "=HYPERLINK(\"http://kotu.test\",\"tıkla\")", "+90 555 000", "-1+1", "@SUM(1,1)" })
        {
            await OpenAsync(s.Admin, s.CompanyId, title, 1m);
        }

        var export = await ReadAsync<ReportExportDto>(
            await s.Admin.PostAsJsonAsync($"{Reports}/exports", new ExportReportRequest(new ReportSpecDto { Dataset = Opportunities, Columns = ["title", "amount"] }, "Formüller")),
            HttpStatusCode.Created);

        var text = Encoding.UTF8.GetString(await (await s.Admin.GetAsync($"{Reports}/exports/{export.Id}/download")).Content.ReadAsByteArrayAsync());

        foreach (var dangerous in new[] { "'=HYPERLINK", "'+90 555 000", "'-1+1", "'@SUM" })
        {
            Assert.Contains(dangerous, text);
        }

        Assert.DoesNotContain("\n=HYPERLINK", text);
        Assert.DoesNotContain(",=HYPERLINK", text);
        Assert.DoesNotContain("\n+90", text);
    }

    [Fact]
    public async Task Hidden_amounts_cannot_be_exported_and_a_count_export_contains_no_amount()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "Gizli", 987_654m);
        var viewer = await UserAsync(s, [CompaniesRead, OpportunitiesRead, CoreModule.Permissions.ReadReports, CoreModule.Permissions.ExportReports], Scope.All);

        var blocked = await viewer.PostAsJsonAsync($"{Reports}/exports", new ExportReportRequest(new ReportSpecDto { Dataset = Opportunities, Columns = ["title", "amount"] }, "Tutarlı"));
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.DoesNotContain("987654", await blocked.Content.ReadAsStringAsync());

        var allowed = await ReadAsync<ReportExportDto>(
            await viewer.PostAsJsonAsync($"{Reports}/exports", new ExportReportRequest(new ReportSpecDto { Dataset = Opportunities, Columns = ["title", "stage"] }, "Sayım")),
            HttpStatusCode.Created);
        var csv = Encoding.UTF8.GetString(await (await viewer.GetAsync($"{Reports}/exports/{allowed.Id}/download")).Content.ReadAsByteArrayAsync());
        Assert.Contains("Gizli", csv);
        Assert.DoesNotContain("987654", csv);
        Assert.DoesNotContain("Tutar", csv);
    }

    [Fact]
    public async Task Download_rechecks_permissions_scope_and_expiry_and_only_the_owner_gets_the_file()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "A", 10m);
        var userEntity = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.TenantId, [.. DataRead, .. ReportRights], Scope.All, userEntity);
        var user = await LoginAsync(userEntity);
        var export = await ExportAdHocAsync(user, "Dosya");
        var path = $"{Reports}/exports/{export.Id}/download";
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync(path)).StatusCode);

        // Başkası (yönetici bile) sahibin dosyasını göremez; var olduğu da sızmaz.
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.DeleteAsync($"{Reports}/exports/{export.Id}")).StatusCode);
        Assert.DoesNotContain(await ListExportsAsync(s.Admin), e => e.Id == export.Id);

        // İlgisiz bir izin bile değişirse hazırlanmış dosya verilmez: yeniden hazırlanmalı (erişim parmak izi).
        await factory.CreateRoleAsync(s.TenantId, [.. DataRead, .. ReportRights, LeadsRead], Scope.All, userEntity);
        var changed = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Contains("yeniden dışa aktarın", await changed.Content.ReadAsStringAsync());
        var again = await ExportAdHocAsync(user, "Yeniden");
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync($"{Reports}/exports/{again.Id}/download")).StatusCode);

        // Veri kapsamı Kendi'ye düşer: parmak izi değişir, eski dosya (tüm veri) artık verilmez.
        await factory.CreateRoleAsync(s.TenantId, [.. DataRead, .. ReportRights, LeadsRead], Scope.Own, userEntity);
        Assert.Equal(HttpStatusCode.Conflict, (await user.GetAsync($"{Reports}/exports/{again.Id}/download")).StatusCode);

        // Dışa aktarma izni alınırsa rapor merkezinde olsa bile indirme 403.
        var third = await ExportAdHocAsync(user, "Üçüncü");
        await factory.CreateRoleAsync(s.TenantId, [.. DataRead, CoreModule.Permissions.ReadReports], Scope.Own, userEntity);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"{Reports}/exports/{third.Id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"{Reports}/exports")).StatusCode);
    }

    [Fact]
    public async Task Expired_files_are_gone_and_the_runner_purges_them()
    {
        var s = await SetUpAsync();
        var export = await ExportAdHocAsync(s.Admin, "Eski");
        await ExpireAsync(s.TenantId, export.Id);

        var response = await s.Admin.GetAsync($"{Reports}/exports/{export.Id}/download");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.True((await ListExportsAsync(s.Admin)).Single().Expired);

        var (run, _) = await factory.Services.GetRequiredService<ReportScheduleRunner>().RunTenantAsync(s.TenantId, CancellationToken.None);
        Assert.Equal(1, run!.Purged);
        Assert.Empty(await ListExportsAsync(s.Admin));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.GetAsync($"{Reports}/exports/{export.Id}/download")).StatusCode);
    }

    [Fact]
    public async Task Export_is_limited_per_user_and_validates_the_definition()
    {
        var s = await SetUpAsync();
        for (var i = 0; i < 20; i++)
        {
            await ExportAdHocAsync(s.Admin, $"Dosya {i}");
        }

        var over = await s.Admin.PostAsJsonAsync($"{Reports}/exports", new ExportReportRequest(CountSpec(), "Fazla"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Contains("20", await over.Content.ReadAsStringAsync());

        var other = await SetUpAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await other.Admin.PostAsJsonAsync($"{Reports}/exports", new ExportReportRequest(new ReportSpecDto { Dataset = Opportunities, Columns = ["nope"] }, "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await other.Admin.PostAsJsonAsync($"{Reports}/exports", new ExportReportRequest(null, "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PostAsync($"{Reports}/{Guid.NewGuid()}/exports", null)).StatusCode);
    }

    [Fact]
    public async Task Exports_follow_report_visibility_and_the_exporter_own_data_rights()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "A", 10m);
        var report = await SaveAsync(s.Admin, "Özel", OpenByStage());
        var user = await UserAsync(s, [.. DataRead, .. ReportRights], Scope.All);

        // Özel rapor başkası tarafından dışa aktarılamaz; paylaşılınca kendi yetkisiyle dışa aktarır.
        Assert.Equal(HttpStatusCode.NotFound, (await user.PostAsync($"{Reports}/{report.Summary.Id}/exports", null)).StatusCode);
        await ReadAsync<ReportDetailDto>(await s.Admin.PutAsJsonAsync($"{Reports}/{report.Summary.Id}/sharing", new ShareReportRequest("tenant", null)));
        var export = await ReadAsync<ReportExportDto>(await user.PostAsync($"{Reports}/{report.Summary.Id}/exports", null), HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync($"{Reports}/exports/{export.Id}/download")).StatusCode);
        Assert.Equal(report.Summary.Id, export.ReportId);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_list_or_download_the_files_and_schedules()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        var report = await SaveAsync(a.Admin, "A raporu", OpenByStage());
        var export = await ExportAdHocAsync(a.Admin, "A dosyası");
        var schedule = await ReadAsync<ReportScheduleDto>(
            await a.Admin.PostAsJsonAsync($"{Reports}/{report.Summary.Id}/schedules", new SaveReportScheduleRequest("daily", 9)), HttpStatusCode.Created);

        Assert.Empty(await ListExportsAsync(b.Admin));
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync($"{Reports}/exports/{export.Id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.DeleteAsync($"{Reports}/exports/{export.Id}")).StatusCode);
        Assert.Empty(await ReadAsync<List<ReportScheduleDto>>(await b.Admin.GetAsync($"{Reports}/schedules")));
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PutAsJsonAsync($"{Reports}/schedules/{schedule.Id}", new SaveReportScheduleRequest("daily", 10))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PostAsJsonAsync($"{Reports}/{report.Summary.Id}/schedules", new SaveReportScheduleRequest("daily", 9))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.Admin.GetAsync($"{Reports}/exports/{export.Id}/download")).StatusCode);
    }

    [Fact]
    public async Task Export_and_schedule_endpoints_need_their_own_permissions()
    {
        var s = await SetUpAsync();
        var report = await SaveAsync(s.Admin, "R", OpenByStage());
        var reader = await UserAsync(s, [.. DataRead, CoreModule.Permissions.ReadReports], Scope.All);
        var exporter = await UserAsync(s, [.. DataRead, CoreModule.Permissions.ReadReports, CoreModule.Permissions.ExportReports], Scope.All);

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"{Reports}/exports", new ExportReportRequest(CountSpec(), "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{Reports}/exports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{Reports}/exports/{Guid.NewGuid()}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await exporter.GetAsync($"{Reports}/schedules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await exporter.PostAsJsonAsync($"{Reports}/{report.Summary.Id}/schedules", new SaveReportScheduleRequest("daily", 9))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await exporter.PostAsJsonAsync($"{Reports}/exports", new ExportReportRequest(CountSpec(), "ok"))).StatusCode);
    }

    [Fact]
    public async Task Exports_and_downloads_are_audited()
    {
        var s = await SetUpAsync();
        var export = await ExportAdHocAsync(s.Admin, "Denetim");
        await s.Admin.GetAsync($"{Reports}/exports/{export.Id}/download");

        using var scope = factory.OpenScope(s.TenantId);
        var audit = await scope.ServiceProvider.GetRequiredService<Modulix.Core.Audit.IAuditLog>().QueryAsync(
            new Modulix.Core.Audit.AuditQuery(EntityKey: CoreModule.AuditedEntityKeys.Report, Action: AuditAction.Exported), CancellationToken.None);

        Assert.Equal(2, audit.TotalCount);
    }

    // ---- Zamanlama: tanım

    [Fact]
    public async Task Schedules_are_created_validated_listed_and_managed_by_their_owner_only()
    {
        var s = await SetUpAsync();
        var report = await SaveAsync(s.Admin, "R", OpenByStage());
        var id = report.Summary.Id;

        var daily = await ReadAsync<ReportScheduleDto>(await s.Admin.PostAsJsonAsync($"{Reports}/{id}/schedules", new SaveReportScheduleRequest("daily", 9)), HttpStatusCode.Created);
        var weekly = await ReadAsync<ReportScheduleDto>(await s.Admin.PostAsJsonAsync($"{Reports}/{id}/schedules", new SaveReportScheduleRequest("weekly", 7, "monday")), HttpStatusCode.Created);
        var monthly = await ReadAsync<ReportScheduleDto>(await s.Admin.PostAsJsonAsync($"{Reports}/{id}/schedules", new SaveReportScheduleRequest("monthly", 6, DayOfMonth: 15)), HttpStatusCode.Created);
        Assert.All(new[] { daily, weekly, monthly }, x => Assert.True(x.Enabled && x.NextRunAt > DateTimeOffset.UtcNow));
        Assert.Equal("monday", weekly.DayOfWeek);
        Assert.Equal(3, (await ReadAsync<List<ReportScheduleDto>>(await s.Admin.GetAsync($"{Reports}/schedules?reportId={id}"))).Count);

        foreach (var bad in new[]
        {
            new SaveReportScheduleRequest("daily", 24), new SaveReportScheduleRequest("weekly", 9), new SaveReportScheduleRequest("monthly", 9, DayOfMonth: 31),
            new SaveReportScheduleRequest("hourly", 9), new SaveReportScheduleRequest("weekly", 9, "funday"),
        })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Admin.PostAsJsonAsync($"{Reports}/{id}/schedules", bad)).StatusCode);
        }

        // Güncelle (sürümlü), kapat/aç, sil.
        var updated = await ReadAsync<ReportScheduleDto>(await s.Admin.PutAsJsonAsync($"{Reports}/schedules/{daily.Id}", new SaveReportScheduleRequest("daily", 10, ExpectedVersion: daily.Version)));
        Assert.Equal((10, daily.Version + 1), (updated.Hour, updated.Version));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PutAsJsonAsync($"{Reports}/schedules/{daily.Id}", new SaveReportScheduleRequest("daily", 11, ExpectedVersion: daily.Version))).StatusCode);
        var off = await ReadAsync<ReportScheduleDto>(await s.Admin.PutAsJsonAsync($"{Reports}/schedules/{daily.Id}/enabled", new SetReportScheduleEnabledRequest(false)));
        Assert.False(off.Enabled);
        Assert.True((await ReadAsync<ReportScheduleDto>(await s.Admin.PutAsJsonAsync($"{Reports}/schedules/{daily.Id}/enabled", new SetReportScheduleEnabledRequest(true)))).Enabled);

        // Başkası göremez, değiştiremez, silemez.
        var other = await UserAsync(s, [.. DataRead, .. ReportRights], Scope.All);
        Assert.Empty(await ReadAsync<List<ReportScheduleDto>>(await other.GetAsync($"{Reports}/schedules")));
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{Reports}/schedules/{daily.Id}", new SaveReportScheduleRequest("daily", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{Reports}/schedules/{daily.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Reports}/{id}/schedules", new SaveReportScheduleRequest("daily", 9))).StatusCode);   // özel rapor
        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.DeleteAsync($"{Reports}/schedules/{daily.Id}")).StatusCode);
        Assert.Equal(2, (await ReadAsync<List<ReportScheduleDto>>(await s.Admin.GetAsync($"{Reports}/schedules"))).Count);
    }

    [Fact]
    public async Task A_user_has_a_limited_number_of_schedules_and_deleting_the_report_removes_them()
    {
        var s = await SetUpAsync();
        var report = await SaveAsync(s.Admin, "R", OpenByStage());
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await s.Admin.PostAsJsonAsync($"{Reports}/{report.Summary.Id}/schedules", new SaveReportScheduleRequest("daily", i))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Admin.PostAsJsonAsync($"{Reports}/{report.Summary.Id}/schedules", new SaveReportScheduleRequest("daily", 11))).StatusCode);
        await s.Admin.DeleteAsync($"{Reports}/{report.Summary.Id}");
        Assert.Empty(await ReadAsync<List<ReportScheduleDto>>(await s.Admin.GetAsync($"{Reports}/schedules")));
    }

    // ---- Zamanlama: çalışma

    [Fact]
    public async Task A_due_schedule_exports_as_its_owner_emails_a_link_without_data_and_runs_only_once_per_slot()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "Gizli müşteri", 424_242m);
        var report = await SaveAsync(s.Admin, "Haftalık özet", OpenByStage());
        var schedule = await ScheduleAsync(s.Admin, report.Summary.Id);
        await MakeDueAsync(s.TenantId, schedule.Id);
        var runner = factory.Services.GetRequiredService<ReportScheduleRunner>();

        var (first, skipped) = await runner.RunTenantAsync(s.TenantId, CancellationToken.None);

        Assert.False(skipped);
        Assert.Equal((1, 1, 0, 0), (first!.Due, first.Delivered, first.Stopped, first.Failed));
        var export = Assert.Single(await ListExportsAsync(s.Admin));
        Assert.Equal(("schedule", report.Summary.Id), (export.Source, export.ReportId));
        var csv = Encoding.UTF8.GetString(await (await s.Admin.GetAsync($"{Reports}/exports/{export.Id}/download")).Content.ReadAsByteArrayAsync());
        Assert.Contains("424242", csv);   // dosya sahibine verilir; e-postada ise veri yoktur

        var mail = Assert.Single(await EmailsAsync($"core.report-export:{export.Id}"));
        Assert.Equal(s.AdminEmail, mail.ToAddress);
        Assert.Contains("Haftalık özet", System.Net.WebUtility.HtmlDecode(mail.HtmlBody));
        Assert.DoesNotContain("424242", mail.HtmlBody);
        Assert.DoesNotContain("Gizli müşteri", mail.HtmlBody);

        var after = (await ReadAsync<List<ReportScheduleDto>>(await s.Admin.GetAsync($"{Reports}/schedules"))).Single();
        Assert.Equal(("Delivered", true), (after.LastStatus, after.Enabled));
        Assert.True(after.NextRunAt > DateTimeOffset.UtcNow);

        // İkinci tur: vakti gelen yok → ikinci dosya ya da e-posta yok.
        var (second, _) = await runner.RunTenantAsync(s.TenantId, CancellationToken.None);
        Assert.Equal(0, second!.Due);

        // Tekrar teslim / yarışan çalışma: aynı planlı an yeniden "vakti gelmiş" gösterilse bile (ör. iş yeniden teslim edildi) çalışma yeniden yazılmaz.
        await MakeDueAsync(s.TenantId, schedule.Id, slot: await SlotOfAsync(s.TenantId, schedule.Id));
        var (replay, _) = await runner.RunTenantAsync(s.TenantId, CancellationToken.None);
        Assert.Equal((1, 0, 1), (replay!.Due, replay.Delivered, replay.Skipped));
        Assert.True((await ReadAsync<List<ReportScheduleDto>>(await s.Admin.GetAsync($"{Reports}/schedules"))).Single().NextRunAt > DateTimeOffset.UtcNow);   // bitmiş çalışma bir sonraki ana ilerletildi (tekrar takılmaz)
        Assert.Single(await ListExportsAsync(s.Admin));
        Assert.Single(await EmailsAsync($"core.report-export:{export.Id}"));
    }

    [Fact]
    public async Task Losing_the_export_permission_stops_the_schedule_and_delivers_nothing()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "A", 10m);
        var userEntity = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.TenantId, [.. DataRead, .. ReportRights], Scope.All, userEntity);
        var user = await LoginAsync(userEntity);
        var report = await SaveAsync(user, "Benim", OpenByStage());
        var schedule = await ScheduleAsync(user, report.Summary.Id);
        await factory.CreateRoleAsync(s.TenantId, [.. DataRead, CoreModule.Permissions.ReadReports, CoreModule.Permissions.ManageReports, CoreModule.Permissions.ScheduleReports], Scope.All, userEntity);
        await MakeDueAsync(s.TenantId, schedule.Id);

        var (run, _) = await factory.Services.GetRequiredService<ReportScheduleRunner>().RunTenantAsync(s.TenantId, CancellationToken.None);

        Assert.Equal((1, 0, 1), (run!.Due, run.Delivered, run.Stopped));
        var stopped = (await ReadAsync<List<ReportScheduleDto>>(await user.GetAsync($"{Reports}/schedules"))).Single();
        Assert.False(stopped.Enabled);
        Assert.Equal("Stopped", stopped.LastStatus);
        Assert.Contains("yetkisi kalmadı", stopped.StoppedReason);
        Assert.Equal(0, await ExportCountAsync(s.TenantId));
        Assert.Empty(await EmailsToAsync(userEntity.Email!));

        // Durdurulmuş zamanlama sonraki turlarda yeniden denenmez; kullanıcı yeniden açana kadar bekler.
        var (again, _) = await factory.Services.GetRequiredService<ReportScheduleRunner>().RunTenantAsync(s.TenantId, CancellationToken.None);
        Assert.Equal(0, again!.Due);
    }

    [Fact]
    public async Task A_disabled_membership_a_vanished_report_or_lost_visibility_stops_delivery()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "A", 10m);

        // 1) Üyelik kapandı.
        var member = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.TenantId, [.. DataRead, .. ReportRights], Scope.All, member);
        var memberClient = await LoginAsync(member);
        var own = await SaveAsync(memberClient, "Üye raporu", OpenByStage());
        var memberSchedule = await ScheduleAsync(memberClient, own.Summary.Id);
        using (var scope = factory.OpenScope(s.TenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var membership = await db.Memberships.SingleAsync(m => m.UserId == member.Id && m.TenantId == s.TenantId);
            membership.Disable();
            await db.SaveChangesAsync();
        }

        // 2) Başkasının firmaya açık raporuna kurulmuş zamanlama; rapor sonradan özel yapıldı (sahibi görmez).
        var shared = await SaveAsync(s.Admin, "Ortak", OpenByStage());
        await ReadAsync<ReportDetailDto>(await s.Admin.PutAsJsonAsync($"{Reports}/{shared.Summary.Id}/sharing", new ShareReportRequest("tenant", null)));
        var viewerEntity = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.TenantId, [.. DataRead, .. ReportRights], Scope.All, viewerEntity);
        var viewer = await LoginAsync(viewerEntity);
        var viewerSchedule = await ScheduleAsync(viewer, shared.Summary.Id);
        await ReadAsync<ReportDetailDto>(await s.Admin.PutAsJsonAsync($"{Reports}/{shared.Summary.Id}/sharing", new ShareReportRequest("private", null)));

        foreach (var id in new[] { memberSchedule.Id, viewerSchedule.Id })
        {
            await MakeDueAsync(s.TenantId, id);
        }

        var (run, _) = await factory.Services.GetRequiredService<ReportScheduleRunner>().RunTenantAsync(s.TenantId, CancellationToken.None);

        Assert.Equal((2, 0, 2), (run!.Due, run.Delivered, run.Stopped));
        var viewerList = await ReadAsync<List<ReportScheduleDto>>(await viewer.GetAsync($"{Reports}/schedules"));
        Assert.Contains("göremiyor", viewerList.Single().StoppedReason);
        using var check = factory.OpenScope(s.TenantId);
        var runs = await check.ServiceProvider.GetRequiredService<ModulixDbContext>().ReportSchedules.AsNoTracking().Where(x => !x.Enabled).CountAsync();
        Assert.Equal(2, runs);
        Assert.Empty(await EmailsToAsync(viewerEntity.Email!));
        Assert.Empty(await EmailsToAsync(member.Email!));
    }

    [Fact]
    public async Task One_failing_schedule_does_not_stop_the_others_and_keeps_its_own_next_run()
    {
        var s = await SetUpAsync();
        await OpenAsync(s.Admin, s.CompanyId, "A", 10m);
        var field = await ReadAsync<Modulix.Shared.Customization.CustomFieldDto>(
            await s.Admin.PostAsJsonAsync("/api/customization/fields", new Modulix.Shared.Customization.SaveCustomFieldRequest(
                "crm.opportunity", "Vade", Modulix.Shared.Customization.CustomFieldType.Number)), HttpStatusCode.Created);
        var broken = await SaveAsync(s.Admin, "Vade raporu", new ReportSpecDto
        {
            Dataset = Opportunities,
            Measures = [new() { Field = $"custom.{field.Key}", Aggregation = "sum" }],
        });
        var good = await SaveAsync(s.Admin, "Sağlam", OpenByStage());
        var brokenSchedule = await ScheduleAsync(s.Admin, broken.Summary.Id);
        var goodSchedule = await ScheduleAsync(s.Admin, good.Summary.Id);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/api/customization/fields/{field.Id}/archive", null)).StatusCode);   // rapor artık çalışmaz
        await MakeDueAsync(s.TenantId, brokenSchedule.Id);
        await MakeDueAsync(s.TenantId, goodSchedule.Id);

        var (run, _) = await factory.Services.GetRequiredService<ReportScheduleRunner>().RunTenantAsync(s.TenantId, CancellationToken.None);

        Assert.Equal((2, 1, 0, 1), (run!.Due, run.Delivered, run.Stopped, run.Failed));
        var schedules = await ReadAsync<List<ReportScheduleDto>>(await s.Admin.GetAsync($"{Reports}/schedules"));
        var failed = schedules.Single(x => x.Id == brokenSchedule.Id);
        Assert.Equal(("Failed", true), (failed.LastStatus, failed.Enabled));
        Assert.Contains("arşivlendi", failed.LastMessage);
        Assert.True(failed.NextRunAt > DateTimeOffset.UtcNow);   // bir sonraki planlı anda yeniden dener
        Assert.Equal("Delivered", schedules.Single(x => x.Id == goodSchedule.Id).LastStatus);
    }

    [Fact]
    public async Task Tenants_are_processed_separately_and_a_schedule_of_another_tenant_is_not_touched()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        var reportA = await SaveAsync(a.Admin, "A", OpenByStage());
        var reportB = await SaveAsync(b.Admin, "B", OpenByStage());
        var scheduleA = await ScheduleAsync(a.Admin, reportA.Summary.Id);
        var scheduleB = await ScheduleAsync(b.Admin, reportB.Summary.Id);
        await MakeDueAsync(a.TenantId, scheduleA.Id);
        await MakeDueAsync(b.TenantId, scheduleB.Id);

        var (run, _) = await factory.Services.GetRequiredService<ReportScheduleRunner>().RunTenantAsync(a.TenantId, CancellationToken.None);

        Assert.Equal((1, 1), (run!.Due, run.Delivered));
        Assert.Single(await ListExportsAsync(a.Admin));
        Assert.Empty(await ListExportsAsync(b.Admin));   // B'nin zamanlaması A'nın turunda çalışmadı
        Assert.True((await ReadAsync<List<ReportScheduleDto>>(await b.Admin.GetAsync($"{Reports}/schedules"))).Single().NextRunAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task An_abandoned_started_run_is_taken_over_but_a_fresh_one_is_left_alone()
    {
        var s = await SetUpAsync();
        var report = await SaveAsync(s.Admin, "R", OpenByStage());
        var schedule = await ScheduleAsync(s.Admin, report.Summary.Id);
        await MakeDueAsync(s.TenantId, schedule.Id);
        var slot = await SlotOfAsync(s.TenantId, schedule.Id);
        var runner = factory.Services.GetRequiredService<ReportScheduleRunner>();

        // Başka örnek az önce başlattı ("Started", taze): bu örnek dokunmaz.
        await InsertRunAsync(s.TenantId, schedule.Id, slot, DateTimeOffset.UtcNow);
        var (fresh, _) = await runner.RunTenantAsync(s.TenantId, CancellationToken.None);
        Assert.Equal((1, 0, 1), (fresh!.Due, fresh.Delivered, fresh.Skipped));
        Assert.Empty(await ListExportsAsync(s.Admin));

        // 15 dakikadan eski "Started": yarıda kesilmiş sayılır ve devralınır.
        await AgeRunAsync(s.TenantId, schedule.Id, slot, TimeSpan.FromMinutes(30));
        var (taken, _) = await runner.RunTenantAsync(s.TenantId, CancellationToken.None);
        Assert.Equal((1, 1), (taken!.Due, taken.Delivered));
        Assert.Single(await ListExportsAsync(s.Admin));
    }

    // ---- Yardımcılar

    private record Setup(HttpClient Admin, Tenant Tenant, Guid CompanyId, string AdminEmail)
    {
        public Guid TenantId => Tenant.Id;
    }

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        var client = await LoginAsync(admin);
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        return new Setup(client, tenant, company.Id, admin.Email!);
    }

    private async Task<HttpClient> UserAsync(Setup s, IReadOnlyCollection<string> permissions, Scope scope)
    {
        var user = await factory.CreateUserAsync(s.Tenant);
        await factory.CreateRoleAsync(s.TenantId, permissions, scope, user);
        return await LoginAsync(user);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static ReportSpecDto OpenByStage() => new()
    {
        Dataset = Opportunities,
        Filters = [new() { Field = "status", Operator = "equals", Value = "Open" }],
        Groups = [new() { Field = "stage" }],
        Measures = [new() { Aggregation = "count" }, new() { Field = "amount", Aggregation = "sum" }],
        Sort = new() { Field = "count", Direction = "descending" },
    };

    private static ReportSpecDto CountSpec() => new() { Dataset = Opportunities, Measures = [new() { Aggregation = "count" }] };

    private static async Task<ReportDetailDto> SaveAsync(HttpClient client, string name, ReportSpecDto spec) =>
        await ReadAsync<ReportDetailDto>(await client.PostAsJsonAsync(Reports, new SaveReportRequest(name, null, spec)), HttpStatusCode.Created);

    private static async Task<ReportExportDto> ExportAdHocAsync(HttpClient client, string name) =>
        await ReadAsync<ReportExportDto>(await client.PostAsJsonAsync($"{Reports}/exports", new ExportReportRequest(CountSpec(), name)), HttpStatusCode.Created);

    private static async Task<List<ReportExportDto>> ListExportsAsync(HttpClient client) =>
        await ReadAsync<List<ReportExportDto>>(await client.GetAsync($"{Reports}/exports"));

    private static async Task<ReportScheduleDto> ScheduleAsync(HttpClient client, Guid reportId) =>
        await ReadAsync<ReportScheduleDto>(await client.PostAsJsonAsync($"{Reports}/{reportId}/schedules", new SaveReportScheduleRequest("daily", 9)), HttpStatusCode.Created);

    private async Task<int> ExportCountAsync(Guid tenantId)
    {
        using var scope = factory.OpenScope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().ReportExports.CountAsync();
    }

    private async Task MakeDueAsync(Guid tenantId, Guid scheduleId, DateTimeOffset? slot = null)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var at = slot ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        await db.ReportSchedules.Where(x => x.Id == scheduleId).ExecuteUpdateAsync(set => set.SetProperty(x => x.NextRunAt, at));
    }

    private async Task<DateTimeOffset> SlotOfAsync(Guid tenantId, Guid scheduleId)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var run = await db.ReportScheduleRuns.AsNoTracking().Where(r => r.ScheduleId == scheduleId).OrderBy(r => r.StartedAt).FirstOrDefaultAsync();
        return run?.Slot ?? await db.ReportSchedules.AsNoTracking().Where(x => x.Id == scheduleId).Select(x => x.NextRunAt).SingleAsync();
    }

    private async Task ExpireAsync(Guid tenantId, Guid exportId)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.ReportExports.Where(e => e.Id == exportId).ExecuteUpdateAsync(set => set.SetProperty(e => e.ExpiresAt, past));
    }

    private async Task InsertRunAsync(Guid tenantId, Guid scheduleId, DateTimeOffset slot, DateTimeOffset startedAt)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        db.ReportScheduleRuns.Add(new Modulix.Core.Reporting.ReportScheduleRun(scheduleId, slot, startedAt));
        await db.SaveChangesAsync();
    }

    private async Task AgeRunAsync(Guid tenantId, Guid scheduleId, DateTimeOffset slot, TimeSpan by)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var old = DateTimeOffset.UtcNow - by;
        await db.ReportScheduleRuns.Where(r => r.ScheduleId == scheduleId && r.Slot == slot).ExecuteUpdateAsync(set => set.SetProperty(r => r.StartedAt, old));
    }

    private async Task<List<Modulix.Infrastructure.Email.QueuedEmail>> EmailsAsync(string reference)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().EmailMessages.AsNoTracking().Where(e => e.Reference == reference).ToListAsync();
    }

    private async Task<List<Modulix.Infrastructure.Email.QueuedEmail>> EmailsToAsync(string address)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().EmailMessages.AsNoTracking()
            .Where(e => e.ToAddress == address && e.Reference != null && e.Reference.StartsWith("core.report-export:")).ToListAsync();
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body) =>
        await ReadAsync<T>(await client.PostAsJsonAsync(url, body), HttpStatusCode.Created);

    private static async Task<OpportunityDto> OpenAsync(HttpClient client, Guid companyId, string title, decimal amount, string currency = "TRY") =>
        await PostAsync<OpportunityDto>(client, "/api/crm/opportunities",
            new SaveOpportunityRequest(title, companyId, Amount: amount, Currency: currency, ExpectedCloseDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20)));
}
