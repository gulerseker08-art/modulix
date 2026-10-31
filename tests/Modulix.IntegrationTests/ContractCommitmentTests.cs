using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Contracts;
using Modulix.Shared.Crm;
using Modulix.Shared.Records;
using static Modulix.Modules.Contracts.ContractsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0022 dilim 7: taahhütler. Mutlu yol (tek seferlik ve dönemsel gerçekleşme, kanıt, ölçüm), doğrulama (takvim, ölçüm, bağımlılık, kilit), ihlal ve takip aktivitesi
/// (tolerans, eskalasyon, idempotent değerlendirme, CRM kapalıyken kullanılamıyor), tenant izolasyonu ve yetki (tanımlama, tamamlama, muafiyet, indirme ayrı).
/// </summary>
public class ContractCommitmentTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Parties = "/api/records/" + SharedEntities.Party;
    private const string Contracts = "/api/contracts/contracts";
    private const string Commitments = "/api/contracts/commitments";
    private static readonly byte[] Evidence = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\n%%EOF");

    private sealed record Setup(Tenant Tenant, ModulixUser Admin, ModulixUser Manager, ModulixUser Completer, ModulixUser Waiver, ModulixUser Reader, ModulixUser Escalation, ContractDto Contract);

    [Fact]
    public async Task One_time_and_periodic_commitments_are_tracked_per_period_and_recorded_periods_are_never_overwritten()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var completer = await LoginAsync(s.Completer);
        using var admin = await LoginAsync(s.Admin);

        var oneTime = await CreateAsync(manager, s, "Kurulum teslimi", dueIn: 10, category: "Installation", responsible: s.Completer.Id);
        var oneOccurrence = Assert.Single(await OccurrencesAsync(completer, s));
        Assert.Equal(("Open", false, 1), (oneOccurrence.Status, oneOccurrence.IsOverdue, oneOccurrence.Sequence));
        Assert.Equal(oneTime.Id, oneOccurrence.CommitmentId);
        Assert.True(oneOccurrence.CanComplete);

        // Kanıt dosyalı tamamlama: durum, taahhüt, tarihçe ve indirme.
        var done = await ReadAsync<CommitmentOccurrenceDto>(await CompleteRawAsync(completer, oneTime.Id, oneOccurrence.Id, Today(), file: Evidence, note: "Teslim tutanağı"));
        Assert.Equal(("Fulfilled", Today(), s.Completer.Id), (done.Status, done.CompletedOn, done.CompletedBy));
        Assert.NotNull(done.EvidenceFileId);
        Assert.Equal("Completed", (await admin.GetFromJsonAsync<ContractCommitmentDto>($"{Commitments}/{oneTime.Id}"))!.Status);
        Assert.Contains(await HistoryAsync(admin, s), k => k == "CommitmentCompleted");
        Assert.Equal(HttpStatusCode.Conflict, (await CompleteRawAsync(completer, oneTime.Id, oneOccurrence.Id, Today())).StatusCode);

        // Dönemsel aylık taahhüt: üç ay için üç ayrı satır; ilk dönem tamamlanır, diğerleri açık kalır ve ilk kayıt değişmez.
        var periodic = await CreateAsync(manager, s, "Aylık rapor", dueIn: 5, kind: "Periodic", recurrence: "Monthly", recurrenceEnd: Today().AddDays(5).AddMonths(2), category: "Reporting");
        var periods = (await OccurrencesAsync(completer, s)).Where(o => o.CommitmentId == periodic.Id).OrderBy(o => o.Sequence).ToList();
        Assert.Equal([1, 2, 3], periods.Select(p => p.Sequence));
        Assert.Equal(periods.Select(p => p.DueDate), periods.Select(p => p.DueDate).OrderBy(d => d));
        var first = await ReadAsync<CommitmentOccurrenceDto>(await CompleteRawAsync(completer, periodic.Id, periods[0].Id, Today()));
        Assert.Equal("Fulfilled", first.Status);
        Assert.Equal("Active", (await admin.GetFromJsonAsync<ContractCommitmentDto>($"{Commitments}/{periodic.Id}"))!.Status);
        var after = (await OccurrencesAsync(completer, s, "?status=Open")).Where(o => o.CommitmentId == periodic.Id).ToList();
        Assert.Equal([2, 3], after.Select(p => p.Sequence).Order());
        Assert.Equal(first.CompletedOn, (await OccurrencesAsync(completer, s, "?status=Fulfilled")).Single(o => o.CommitmentId == periodic.Id).CompletedOn);
        Assert.Equal(Today().AddDays(5), periods[0].DueDate);

        // Tanım alanı (başlık) düzenlenebilir; gerçekleşmesi kayıtlı taahhüdün takvimi ve ölçümü kilitlidir; silinemez.
        var current = (await admin.GetFromJsonAsync<ContractCommitmentDto>($"{Commitments}/{periodic.Id}"))!;
        var rename = await admin.PutAsJsonAsync($"{Commitments}/{periodic.Id}", Save(current) with { Title = "Aylık faaliyet raporu" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        current = (await admin.GetFromJsonAsync<ContractCommitmentDto>($"{Commitments}/{periodic.Id}"))!;
        var locked = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Commitments}/{periodic.Id}", Save(current) with { DueDate = current.DueDate.AddDays(3) }));
        Assert.Contains("dueDate", locked.Errors.Keys);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Commitments}/{periodic.Id}?version={current.Version}")).StatusCode);

        // Hiç kaydı olmayan taahhüt silinebilir.
        var spare = await CreateAsync(manager, s, "Gereksiz", dueIn: 20);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"{Commitments}/{spare.Id}?version={spare.Version}")).StatusCode);
        Assert.DoesNotContain(await OccurrencesAsync(completer, s), o => o.CommitmentId == spare.Id);
    }

    [Fact]
    public async Task Measured_commitment_needs_a_value_and_a_missed_target_is_a_breach_with_a_follow_up_activity()
    {
        var s = await SetUpAsync(crm: true);
        using var admin = await LoginAsync(s.Admin);
        using var completer = await LoginAsync(s.Completer);
        var sla = await CreateAsync(admin, s, "Aylık erişilebilirlik", dueIn: 3, kind: "Periodic", recurrence: "Monthly", recurrenceEnd: Today().AddDays(3).AddMonths(1), category: "ServiceLevel",
            metric: "Erişilebilirlik", unit: "%", direction: "AtLeast", target: 99.5m, tolerance: 0.1m, responsible: s.Completer.Id);
        var occurrence = (await OccurrencesAsync(admin, s)).First(o => o.CommitmentId == sla.Id && o.Sequence == 1);

        // Doğrulama: ölçüm değeri, gelecek tarih, bozuk dosya.
        Assert.Contains("actualValue", (await ValidationProblemAsync(await CompleteRawAsync(completer, sla.Id, occurrence.Id, Today()))).Errors.Keys);
        Assert.Contains("completedOn", (await ValidationProblemAsync(await CompleteRawAsync(completer, sla.Id, occurrence.Id, Today().AddDays(2), actual: 99.9m))).Errors.Keys);
        Assert.Contains("file", (await ValidationProblemAsync(await CompleteRawAsync(completer, sla.Id, occurrence.Id, Today(), actual: 99.9m, file: Encoding.UTF8.GetBytes("pdf değil")))).Errors.Keys);

        // Tolerans içinde (99,45 ≥ 99,5 − 0,1) karşılanmış; ikinci dönemde 98,0 hedefi tutturmaz: ihlal ve takip aktivitesi.
        var met = await ReadAsync<CommitmentOccurrenceDto>(await CompleteRawAsync(completer, sla.Id, occurrence.Id, Today(), actual: 99.45m));
        Assert.Equal(("Fulfilled", true), (met.Status, met.MetTarget));
        var second = (await OccurrencesAsync(admin, s)).First(o => o.CommitmentId == sla.Id && o.Sequence == 2);
        var missed = await ReadAsync<CommitmentOccurrenceDto>(await CompleteRawAsync(completer, sla.Id, second.Id, Today(), actual: 98.0m));
        Assert.Equal(("Breached", false), (missed.Status, missed.MetTarget));
        Assert.NotNull(missed.BreachedAt);
        Assert.Contains("Hedef karşılanmadı", missed.BreachReason);
        Assert.Equal(98.0m, missed.ActualValue);

        // Tamamlayanın aktivite (CRM) yazma yetkisi yoksa takip aktivitesi "başarısız" işaretlenir (ihlal yine kayıtlıdır); yetkili bir değerlendirme turu aktiviteyi açar.
        var failed = (await admin.GetFromJsonAsync<List<CommitmentOccurrenceDto>>($"{Commitments}/occurrences?contractId={s.Contract.Id}&status=Breached"))!.Single();
        Assert.Equal(("Failed", null), (failed.FollowUpStatus, failed.FollowUpActivityId));
        var evaluation = await ReadAsync<CommitmentEvaluationDto>(await admin.PostAsJsonAsync($"{Commitments}/evaluate", new CommitmentEvaluateRequest(s.Contract.Id)));
        Assert.Equal((0, 1), (evaluation.Breached, evaluation.FollowUpsCreated));
        var detail = (await admin.GetFromJsonAsync<List<CommitmentOccurrenceDto>>($"{Commitments}/occurrences?contractId={s.Contract.Id}&status=Breached"))!.Single();
        Assert.Equal("Created", detail.FollowUpStatus);
        var activity = (await admin.GetFromJsonAsync<PagedResult<ActivityDto>>($"/api/crm/activities?pageSize=100"))!.Items.Single(a => a.Id == detail.FollowUpActivityId);
        Assert.Contains("Taahhüt ihlali", activity.Subject);
        Assert.Equal(s.Completer.Id, activity.OwnerId);
        Assert.Equal(HttpStatusCode.Conflict, (await CompleteRawAsync(completer, sla.Id, second.Id, Today(), actual: 100m)).StatusCode);
        Assert.Contains(await HistoryAsync(admin, s), k => k == "CommitmentBreached");
    }

    [Fact]
    public async Task Evaluation_breaches_only_after_the_grace_period_creates_one_follow_up_escalates_and_is_idempotent()
    {
        var s = await SetUpAsync(crm: true);
        using var admin = await LoginAsync(s.Admin);
        var late = await CreateAsync(admin, s, "Eğitim", dueIn: -10, start: -40, grace: 0, category: "Training", responsible: s.Completer.Id, escalation: s.Escalation.Id, escalateAfterDays: 3);
        var tolerated = await CreateAsync(admin, s, "Belge sağlama", dueIn: -2, start: -40, grace: 5, category: "Documents");
        var future = await CreateAsync(admin, s, "Gelecek teslim", dueIn: 20, start: -5);

        // Tolerans içindeki gecikme "gecikmiş" görünür ama ihlal değildir.
        var overdue = await OccurrencesAsync(admin, s, "?status=Overdue");
        Assert.Equal(2, overdue.Count);
        Assert.Equal(10, overdue.Single(o => o.CommitmentId == late.Id).DaysOverdue);

        var first = await ReadAsync<CommitmentEvaluationDto>(await admin.PostAsJsonAsync($"{Commitments}/evaluate", new CommitmentEvaluateRequest(s.Contract.Id)));
        Assert.Equal((1, 0, 1), (first.Breached, first.Escalated, first.FollowUpsCreated));
        var breached = (await OccurrencesAsync(admin, s, "?status=Breached")).Single();
        Assert.Equal(late.Id, breached.CommitmentId);
        Assert.Equal("Created", breached.FollowUpStatus);
        Assert.NotNull(breached.BreachedAt);
        Assert.Equal("Open", (await OccurrencesAsync(admin, s)).Single(o => o.CommitmentId == tolerated.Id).Status);
        Assert.Equal("Open", (await OccurrencesAsync(admin, s)).Single(o => o.CommitmentId == future.Id).Status);

        // Aynı tur tekrar çalışırsa yeni ihlal, aktivite ya da eskalasyon üretmez.
        var again = await ReadAsync<CommitmentEvaluationDto>(await admin.PostAsJsonAsync($"{Commitments}/evaluate", new CommitmentEvaluateRequest(s.Contract.Id)));
        Assert.Equal((0, 0, 0), (again.Breached, again.Escalated, again.FollowUpsCreated));
        var activities = (await admin.GetFromJsonAsync<PagedResult<ActivityDto>>("/api/crm/activities?pageSize=100"))!.Items.Where(a => a.Subject.StartsWith("Taahhüt ihlali")).ToList();
        Assert.Single(activities);

        // Eskalasyon: ihlalden bu yana yeterli gün geçince eskalasyon kullanıcısına ikinci aktivite açılır (bir kez).
        using (var scope = factory.OpenScope(s.Tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE contracts.ContractCommitmentOccurrences SET BreachedAt = DATEADD(day, -4, SYSDATETIMEOFFSET()) WHERE Id = {0}", breached.Id);
        }

        var escalate = await ReadAsync<CommitmentEvaluationDto>(await admin.PostAsJsonAsync($"{Commitments}/evaluate", new CommitmentEvaluateRequest(null)));
        Assert.Equal((0, 1, 1), (escalate.Breached, escalate.Escalated, escalate.FollowUpsCreated));
        var escalated = (await OccurrencesAsync(admin, s, "?status=Breached")).Single();
        Assert.NotNull(escalated.EscalatedAt);
        var escalationActivity = (await admin.GetFromJsonAsync<PagedResult<ActivityDto>>("/api/crm/activities?pageSize=100"))!.Items.Single(a => a.Id == escalated.EscalationActivityId);
        Assert.Equal(s.Escalation.Id, escalationActivity.OwnerId);
        Assert.Equal((0, 0), ((await ReadAsync<CommitmentEvaluationDto>(await admin.PostAsJsonAsync($"{Commitments}/evaluate", new CommitmentEvaluateRequest(null)))) is { } last ? (last.Escalated, last.FollowUpsCreated) : (1, 1)));

        // Geç tamamlama: ihlal kaydı korunur (geç tamamlandı), tamamlanma tarihi yazılır.
        using var completer = await LoginAsync(s.Completer);
        var lateDone = await ReadAsync<CommitmentOccurrenceDto>(await CompleteRawAsync(completer, late.Id, escalated.Id, Today()));
        Assert.Equal(("Breached", Today()), (lateDone.Status, lateDone.CompletedOn));
        Assert.Equal(escalated.BreachedAt, lateDone.BreachedAt);
        Assert.Equal("Completed", (await admin.GetFromJsonAsync<ContractCommitmentDto>($"{Commitments}/{late.Id}"))!.Status);
    }

    [Fact]
    public async Task Without_the_activity_module_the_breach_is_recorded_and_the_follow_up_is_marked_unavailable_and_open_ended_periods_extend_a_rolling_horizon()
    {
        var s = await SetUpAsync(crm: false, indefinite: true);
        using var admin = await LoginAsync(s.Admin);
        var late = await CreateAsync(admin, s, "Rapor", dueIn: -3, start: -20, grace: 0, category: "Reporting");

        var run = await ReadAsync<CommitmentEvaluationDto>(await admin.PostAsJsonAsync($"{Commitments}/evaluate", new CommitmentEvaluateRequest(s.Contract.Id)));
        Assert.Equal((1, 0, 1), (run.Breached, run.FollowUpsCreated, run.FollowUpsUnavailable));
        var breached = (await OccurrencesAsync(admin, s, "?status=Breached")).Single();
        Assert.Equal("Unavailable", breached.FollowUpStatus);
        Assert.Null(breached.FollowUpActivityId);

        // Belirsiz süreli sözleşmede bitişsiz dönemsel taahhüt kayan ufukla (12 ay) üretilir; yeniden değerlendirme satır çoğaltmaz.
        var open = await CreateAsync(admin, s, "Aylık gözden geçirme", dueIn: 5, kind: "Periodic", recurrence: "Monthly");
        var count = (await OccurrencesAsync(admin, s)).Count(o => o.CommitmentId == open.Id);
        Assert.InRange(count, 12, 13);
        await ReadAsync<CommitmentEvaluationDto>(await admin.PostAsJsonAsync($"{Commitments}/evaluate", new CommitmentEvaluateRequest(null)));
        Assert.Equal(count, (await OccurrencesAsync(admin, s)).Count(o => o.CommitmentId == open.Id));
        Assert.Equal(late.Id, breached.CommitmentId);
    }

    [Fact]
    public async Task Definition_rules_dependencies_and_waivers_are_enforced()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var completer = await LoginAsync(s.Completer);
        using var waiver = await LoginAsync(s.Waiver);

        async Task<ValidationProblemDetails> ErrorsAsync(SaveContractCommitmentRequest r) => await ValidationProblemAsync(await admin.PostAsJsonAsync(Commitments, r));
        var baseRequest = Request(s, "Hatalı", dueIn: 10);
        Assert.Contains("recurrence", (await ErrorsAsync(baseRequest with { Kind = "Periodic" })).Errors.Keys);
        Assert.Contains("recurrence", (await ErrorsAsync(baseRequest with { Recurrence = "Monthly" })).Errors.Keys);
        Assert.Contains("dueDate", (await ErrorsAsync(baseRequest with { DueDate = baseRequest.StartDate.AddDays(-1) })).Errors.Keys);
        Assert.Contains("recurrenceEndDate", (await ErrorsAsync(baseRequest with { Kind = "Periodic", Recurrence = "Monthly", RecurrenceEndDate = baseRequest.DueDate.AddDays(-1) })).Errors.Keys);
        Assert.Contains("direction", (await ErrorsAsync(baseRequest with { MetricName = "Süre", TargetValue = 4m })).Errors.Keys);
        Assert.Contains("targetValue", (await ErrorsAsync(baseRequest with { MetricName = "Süre", Direction = "AtMost" })).Errors.Keys);
        Assert.Contains("metricName", (await ErrorsAsync(baseRequest with { TargetValue = 4m })).Errors.Keys);
        Assert.Contains("responsibleUserId", (await ErrorsAsync(baseRequest with { ResponsibleUserId = Guid.NewGuid() })).Errors.Keys);
        Assert.Contains("contractId", (await ErrorsAsync(baseRequest with { ContractId = Guid.NewGuid() })).Errors.Keys);
        Assert.Contains("category", (await ErrorsAsync(baseRequest with { Category = "Yok" })).Errors.Keys);

        // Bağımlılık: B, A'ya bağlı; A tamamlanmadan B kaydedilemez; kendine/döngüye/başka sözleşmeye bağ reddedilir.
        var a = await CreateAsync(admin, s, "A: ön hazırlık", dueIn: 5);
        var b = await CreateAsync(admin, s, "B: kurulum", dueIn: 8, dependsOn: a.Id);
        var aOccurrence = (await OccurrencesAsync(admin, s)).Single(o => o.CommitmentId == a.Id);
        var bOccurrence = (await OccurrencesAsync(admin, s)).Single(o => o.CommitmentId == b.Id);
        Assert.False(bOccurrence.CanComplete);
        Assert.Equal(aOccurrence.DueDate, bOccurrence.BlockedByDue);
        Assert.Equal(HttpStatusCode.Conflict, (await CompleteRawAsync(completer, b.Id, bOccurrence.Id, Today())).StatusCode);
        Assert.Contains("dependsOnCommitmentId", (await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Commitments}/{a.Id}", Save(a) with { DependsOnCommitmentId = b.Id }))).Errors.Keys);
        Assert.Contains("dependsOnCommitmentId", (await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Commitments}/{a.Id}", Save(a) with { DependsOnCommitmentId = a.Id }))).Errors.Keys);
        var other = await NewContractAsync(admin, s.Contract.PartyId);
        Assert.Contains("dependsOnCommitmentId", (await ErrorsAsync(Request(s, "Başka", dueIn: 4) with { ContractId = other.Id, DependsOnCommitmentId = a.Id })).Errors.Keys);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Commitments}/{a.Id}?version={a.Version}")).StatusCode); // B ona bağlı

        await ReadAsync<CommitmentOccurrenceDto>(await CompleteRawAsync(completer, a.Id, aOccurrence.Id, Today()));
        Assert.Equal("Fulfilled", (await ReadAsync<CommitmentOccurrenceDto>(await CompleteRawAsync(completer, b.Id, bOccurrence.Id, Today()))).Status);

        // Muafiyet: ayrı izin, gerekçe zorunlu; muaf gerçekleşme ihlal sayılmaz ve yeniden yazılamaz.
        var c = await CreateAsync(admin, s, "C: geciken", dueIn: -9, start: -30, grace: 0);
        var cOccurrence = (await OccurrencesAsync(admin, s)).Single(o => o.CommitmentId == c.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await completer.PostAsJsonAsync($"{Commitments}/{c.Id}/occurrences/{cOccurrence.Id}/waive", new SignatureReasonRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await waiver.PostAsJsonAsync($"{Commitments}/{c.Id}/occurrences/{cOccurrence.Id}/waive", new SignatureReasonRequest(" "))).StatusCode);
        var waived = await ReadAsync<CommitmentOccurrenceDto>(await waiver.PostAsJsonAsync($"{Commitments}/{c.Id}/occurrences/{cOccurrence.Id}/waive", new SignatureReasonRequest("Müşteri erteledi")));
        Assert.Equal(("Waived", s.Waiver.Id, "Müşteri erteledi"), (waived.Status, waived.WaivedBy, waived.WaiveReason));
        Assert.Equal(0, (await ReadAsync<CommitmentEvaluationDto>(await admin.PostAsJsonAsync($"{Commitments}/evaluate", new CommitmentEvaluateRequest(s.Contract.Id)))).Breached);
        Assert.Equal(HttpStatusCode.Conflict, (await CompleteRawAsync(completer, c.Id, cOccurrence.Id, Today())).StatusCode);

        // İptal: gerekçe ister; açık gerçekleşmeler iptal olur, tamamlanmış kayıtlar korunur.
        var d = await CreateAsync(admin, s, "D: iptal edilecek", dueIn: 30, kind: "Periodic", recurrence: "Monthly", recurrenceEnd: Today().AddDays(30).AddMonths(2));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync($"{Commitments}/{d.Id}/cancel", new SignatureReasonRequest(""))).StatusCode);
        Assert.Equal("Cancelled", (await ReadAsync<ContractCommitmentDto>(await admin.PostAsJsonAsync($"{Commitments}/{d.Id}/cancel", new SignatureReasonRequest("Kapsamdan çıktı")))).Status);
        Assert.All((await OccurrencesAsync(admin, s, "?includeClosed=true")).Where(o => o.CommitmentId == d.Id), o => Assert.Equal("Cancelled", o.Status));
    }

    [Fact]
    public async Task Tenants_cannot_see_complete_or_evaluate_each_others_commitments()
    {
        var a = await SetUpAsync(crm: true);
        var b = await SetUpAsync(crm: true);
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        using var completerB = await LoginAsync(b.Completer);
        var commitment = await CreateAsync(adminA, a, "A taahhüdü", dueIn: -5, start: -20, grace: 0, category: "Delivery");
        var occurrence = (await OccurrencesAsync(adminA, a)).Single();

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Commitments}/{commitment.Id}")).StatusCode);
        Assert.Empty((await adminB.GetFromJsonAsync<PagedResult<ContractCommitmentDto>>(Commitments))!.Items);
        Assert.Empty((await adminB.GetFromJsonAsync<List<CommitmentOccurrenceDto>>($"{Commitments}/occurrences?includeClosed=true"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await CompleteRawAsync(completerB, commitment.Id, occurrence.Id, Today())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"{Commitments}/{commitment.Id}/cancel", new SignatureReasonRequest("sızıntı"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await adminB.PostAsJsonAsync(Commitments, Request(b, "Sızıntı", dueIn: 3) with { ContractId = a.Contract.Id })).StatusCode);

        // B'nin değerlendirmesi A'nın taahhüdünü ihlal etmez; A'nınki yalnızca A'da ihlal eder.
        Assert.Equal(0, (await ReadAsync<CommitmentEvaluationDto>(await adminB.PostAsJsonAsync($"{Commitments}/evaluate", new CommitmentEvaluateRequest(null)))).Breached);
        Assert.Equal("Open", (await OccurrencesAsync(adminA, a)).Single().Status);
        Assert.Equal(1, (await ReadAsync<CommitmentEvaluationDto>(await adminA.PostAsJsonAsync($"{Commitments}/evaluate", new CommitmentEvaluateRequest(null)))).Breached);
    }

    [Fact]
    public async Task Defining_completing_waiving_and_downloading_evidence_are_separate_permissions()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var manager = await LoginAsync(s.Manager);
        using var completer = await LoginAsync(s.Completer);
        using var reader = await LoginAsync(s.Reader);

        // Tanımlama yalnızca yazma iznindedir; tamamlayıcı ve okuyucu tanımlayamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await completer.PostAsJsonAsync(Commitments, Request(s, "Yetkisiz", dueIn: 5))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Commitments, Request(s, "Yetkisiz", dueIn: 5))).StatusCode);
        var commitment = await CreateAsync(manager, s, "Teslimat", dueIn: 5);
        var occurrence = (await OccurrencesAsync(reader, s)).Single();

        // Tamamlama yalnızca tamamlama izninde: tanımlayan yönetici ve okuyucu kaydedemez; muafiyet ayrı yetki; değerlendirme yazma izni ister.
        Assert.Equal(HttpStatusCode.Forbidden, (await CompleteRawAsync(manager, commitment.Id, occurrence.Id, Today())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CompleteRawAsync(reader, commitment.Id, occurrence.Id, Today())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"{Commitments}/{commitment.Id}/occurrences/{occurrence.Id}/waive", new SignatureReasonRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await completer.PostAsJsonAsync($"{Commitments}/evaluate", new CommitmentEvaluateRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await completer.PostAsJsonAsync($"{Commitments}/{commitment.Id}/cancel", new SignatureReasonRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Commitments}/{commitment.Id}")).StatusCode);

        // Kanıt indirme ayrı izin: tamamlayan indiremez (indirme izni yok), yönetici-admin indirir; dosya ek olarak ve koklanmadan sunulur.
        var done = await ReadAsync<CommitmentOccurrenceDto>(await CompleteRawAsync(completer, commitment.Id, occurrence.Id, Today(), file: Evidence));
        var url = $"{Commitments}/{commitment.Id}/occurrences/{occurrence.Id}/files/{done.EvidenceFileId}";
        Assert.Equal(HttpStatusCode.Forbidden, (await completer.GetAsync(url)).StatusCode);
        var file = await admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(Evidence, await file.Content.ReadAsByteArrayAsync());
        Assert.NotNull(file.Content.Headers.ContentDisposition?.FileName);
    }

    // ---- yardımcılar

    private async Task<Setup> SetUpAsync(bool crm = false, bool indefinite = false)
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "contracts", enabled: true);
        if (crm) { await factory.SetModuleAsync(tenant.Id, "crm", enabled: true); }
        var admin = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        var completer = await factory.CreateUserAsync(tenant);
        var waiver = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var escalation = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, CommitmentsWrite], Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, CommitmentsComplete], Scope.All, completer, escalation);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, CommitmentsWaive], Scope.All, waiver);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead], Scope.All, reader);
        using var client = await LoginAsync(admin);
        var party = await PostRecordAsync(client, Parties, new JsonObject { ["name"] = "Anka Lojistik A.Ş." });
        var contract = await NewContractAsync(client, party.Id, indefinite);
        return new Setup(tenant, admin, manager, completer, waiver, reader, escalation, contract);
    }

    private static async Task<ContractDto> NewContractAsync(HttpClient client, Guid partyId, bool indefinite = false)
    {
        var start = Today().AddDays(-60);
        return await PostAsync<ContractDto>(client, Contracts, indefinite
            ? new SaveContractRequest("Bakım sözleşmesi", partyId, StartDate: start, IsIndefinite: true)
            : new SaveContractRequest("Bakım sözleşmesi", partyId, StartDate: start, EndDate: start.AddYears(2)));
    }

    private static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul")).DateTime);

    private static SaveContractCommitmentRequest Request(
        Setup s, string title, int dueIn, int start = -1, string kind = "OneTime", string? recurrence = null, DateOnly? recurrenceEnd = null, string? category = null, Guid? responsible = null,
        Guid? escalation = null, int grace = 0, int escalateAfterDays = 0, string? metric = null, string? unit = null, string? direction = null, decimal? target = null, decimal? tolerance = null,
        Guid? dependsOn = null) =>
        new(s.Contract.Id, title, Today().AddDays(start), Today().AddDays(dueIn), Category: category, ResponsibleUserId: responsible, EscalationUserId: escalation, Kind: kind,
            Recurrence: recurrence, RecurrenceEndDate: recurrenceEnd, GraceDays: grace, EscalateAfterDays: escalateAfterDays, MetricName: metric, Unit: unit, Direction: direction,
            TargetValue: target, Tolerance: tolerance, DependsOnCommitmentId: dependsOn);

    private static async Task<ContractCommitmentDto> CreateAsync(
        HttpClient client, Setup s, string title, int dueIn, int start = -1, string kind = "OneTime", string? recurrence = null, DateOnly? recurrenceEnd = null, string? category = null,
        Guid? responsible = null, Guid? escalation = null, int grace = 0, int escalateAfterDays = 0, string? metric = null, string? unit = null, string? direction = null, decimal? target = null,
        decimal? tolerance = null, Guid? dependsOn = null) =>
        await PostAsync<ContractCommitmentDto>(client, Commitments, Request(s, title, dueIn, start, kind, recurrence, recurrenceEnd, category, responsible, escalation, grace, escalateAfterDays, metric, unit, direction, target, tolerance, dependsOn));

    private static SaveContractCommitmentRequest Save(ContractCommitmentDto c) => new(
        c.ContractId, c.Title, c.StartDate, c.DueDate, c.Description, c.Category, c.Obligor, c.ResponsibleUserId, c.EscalationUserId, c.Kind, c.Recurrence, c.RecurrenceEndDate, c.GraceDays,
        c.EscalateAfterDays, c.MetricName, c.Unit, c.Direction, c.TargetValue, c.Tolerance, c.DependsOnCommitmentId, c.ConsequenceNote, c.Version);

    private static async Task<List<CommitmentOccurrenceDto>> OccurrencesAsync(HttpClient client, Setup s, string query = "")
    {
        var separator = query.Length == 0 ? "?" : query + "&";
        return (await client.GetFromJsonAsync<List<CommitmentOccurrenceDto>>($"{Commitments}/occurrences{separator}contractId={s.Contract.Id}"))!;
    }

    private static Task<HttpResponseMessage> CompleteRawAsync(HttpClient client, Guid commitmentId, Guid occurrenceId, DateOnly completedOn, decimal? actual = null, string? note = null, byte[]? file = null) =>
        client.PostAsJsonAsync($"{Commitments}/{commitmentId}/occurrences/{occurrenceId}/complete",
            new CompleteOccurrenceRequest(completedOn, actual, note, file is null ? null : "kanit.pdf", file is null ? null : Convert.ToBase64String(file)));

    private static async Task<List<string>> HistoryAsync(HttpClient client, Setup s) =>
        (await client.GetFromJsonAsync<List<ContractHistoryDto>>($"{Contracts}/{s.Contract.Id}/history"))!.Select(h => h.Kind).ToList();

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<RecordResponse> PostRecordAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
