using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Orders;
using Modulix.Core.Authorization;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Orders;
using Modulix.Shared.Records;
using Modulix.Shared.Settings;
using static Modulix.Modules.Orders.OrdersModule.Permissions;
using Crm = Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0023 dilim 4: operasyon işi. Devir kabulünde stratejiye göre bir kez üretim, "Atama bekliyor", sunucuda bağımlılık ve görev kuralları, elle iş açma,
/// sipariş iptalinde açık işlerin iptali, tenant izolasyonu ve yetki, atanmamış/geciken iş takibinin tekrarsızlığı.
/// </summary>
public class OperationJobTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    [Fact]
    public async Task Accepting_a_handover_creates_the_delivery_and_service_jobs_once_with_lines_tasks_and_planning_from_the_handover()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        var order = await AcceptedOrderAsync(s, sales, ops);

        var jobs = (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={order.Id}&sort=number"))!.Items;
        Assert.Equal(2, jobs.Count);
        var delivery = jobs.Single(j => j.Kind == "Delivery");
        var service = jobs.Single(j => j.Kind == "Service");
        Assert.StartsWith("OP-", delivery.Number);
        Assert.Equal(("kind:Product", "Planned", "High", s.Ops.Id, "Handover"), (delivery.GroupKey, delivery.Status, delivery.Priority, delivery.ResponsibleUserId, delivery.Source));
        Assert.Equal((new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 15), "Gebze depo"), (delivery.PlannedStartDate, delivery.PlannedEndDate, delivery.Location));
        Assert.Equal("kind:Service", service.GroupKey);
        Assert.Contains(order.Number, delivery.Title);

        var deliveryDetail = await JobDetailAsync(ops, delivery.Id);
        Assert.Equal(["Sunucu"], deliveryDetail.Lines.Select(l => l.Name));
        Assert.Equal(5, deliveryDetail.Tasks.Count);
        Assert.All(deliveryDetail.Tasks, t => Assert.True(t.IsRequired && t.IsDefault && !t.IsDone));
        Assert.Equal(0, deliveryDetail.ProgressPercent);
        Assert.Equal("Generated", Assert.Single(deliveryDetail.History).Kind);
        Assert.Equal(["Kurulum"], (await JobDetailAsync(ops, service.Id)).Lines.Select(l => l.Name));

        // Aynı kalemler başka bir açık işe bağlanamaz: tekrar üretim/ilişkilendirme mükerrer iş açmaz.
        var line = deliveryDetail.Lines.Single();
        var duplicate = await ops.PostAsJsonAsync($"/api/orders/orders/{order.Id}/jobs", new CreateOperationJobRequest([line.OrderLineId], "Mükerrer"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(2, (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={order.Id}"))!.TotalCount);
    }

    [Fact]
    public async Task Job_creation_follows_the_configured_strategy_and_a_dependency_blocks_the_service_job_until_delivery_is_complete()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        using var admin = await LoginAsync(s.Admin);

        await SetSettingAsync(admin, "orders.jobs.strategy", "Single");
        var single = await AcceptedOrderAsync(s, sales, ops);
        var singleJobs = (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={single.Id}"))!.Items;
        Assert.Equal(("Mixed", "all"), (Assert.Single(singleJobs).Kind, singleJobs[0].GroupKey));
        Assert.Equal(2, (await JobDetailAsync(ops, singleJobs[0].Id)).Lines.Count);
        Assert.Equal(10, (await JobDetailAsync(ops, singleJobs[0].Id)).Tasks.Count);

        await SetSettingAsync(admin, "orders.jobs.strategy", "PerLine");
        var perLine = await AcceptedOrderAsync(s, sales, ops);
        Assert.Equal(2, (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={perLine.Id}"))!.TotalCount);

        await SetSettingAsync(admin, "orders.jobs.strategy", "PerKind");
        await SetSettingAsync(admin, "orders.jobs.service-after-delivery", "true");
        var chained = await AcceptedOrderAsync(s, sales, ops);
        var chain = (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={chained.Id}"))!.Items;
        var delivery = chain.Single(j => j.Kind == "Delivery");
        var service = chain.Single(j => j.Kind == "Service");
        var serviceDetail = await JobDetailAsync(ops, service.Id);
        Assert.Equal(delivery.Number, Assert.Single(serviceDetail.DependsOn).Number);
        Assert.Equal(service.Number, Assert.Single((await JobDetailAsync(ops, delivery.Id)).Dependents).Number);

        // Bağımlı iş, bağımlı olduğu iş tamamlanmadan başlayamaz (sunucuda); eylem listesi nedeni söyler.
        var blocked = await PostJobActionAsync(ops, service.Id, "start", service.Version);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("tamamlanmadan başlatılamaz", await blocked.Content.ReadAsStringAsync());
        Assert.Equal("dependency", serviceDetail.Actions.Single(a => a.Action == "start").Code);

        await CompleteJobAsync(ops, delivery.Id);
        var started = await ActAsync(ops, service.Id, "start", (await JobDetailAsync(ops, service.Id)).Job.Version);
        Assert.Equal("InProgress", started.Job.Status);
    }

    [Fact]
    public async Task Job_runs_through_start_tasks_and_completion_with_server_side_rules_permissions_and_history()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        var order = await AcceptedOrderAsync(s, sales, ops);
        var delivery = (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={order.Id}&kind=Delivery"))!.Items.Single();

        // Satış operasyon işini yürütemez; ops yürütür.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostJobActionAsync(sales, delivery.Id, "start", delivery.Version)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostJobActionAsync(ops, delivery.Id, "complete", delivery.Version)).StatusCode);
        var started = await ActAsync(ops, delivery.Id, "start", delivery.Version);
        Assert.Equal(("InProgress", s.Ops.Id), (started.Job.Status, started.Job.ResponsibleUserId));
        Assert.NotNull(started.Job.ActualStartAt);
        Assert.Equal(HttpStatusCode.Conflict, (await PostJobActionAsync(ops, delivery.Id, "start", started.Job.Version)).StatusCode);

        // Zorunlu görevler bitmeden tamamlanmaz; ilerleme görev yüzdesidir.
        var blocked = await PostJobActionAsync(ops, delivery.Id, "complete", started.Job.Version);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("zorunlu görev", await blocked.Content.ReadAsStringAsync());
        var first = started.Tasks[0];
        await ToggleTaskAsync(ops, delivery.Id, first, done: true);
        var detail = await JobDetailAsync(ops, delivery.Id);
        Assert.Equal(20, detail.ProgressPercent);
        Assert.False(detail.Actions.Single(a => a.Action == "complete").Allowed);

        // Şablon görevi: başlığı değişmez, silinmez. Kullanıcı görevi eklenir (zorunlu sayılır) ve silinebilir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ops.PutAsJsonAsync($"/api/orders/operation-tasks/{first.Id}",
            new SaveOperationTaskRequest(delivery.Id, "Başka başlık", true, null, (await JobDetailAsync(ops, delivery.Id)).Tasks[0].Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ops.DeleteAsync($"/api/orders/operation-tasks/{first.Id}?version={detail.Tasks[0].Version}")).StatusCode);
        var extra = (await (await ops.PostAsJsonAsync("/api/orders/operation-tasks", new SaveOperationTaskRequest(delivery.Id, "Ek kontrol: forklift"))).Content.ReadFromJsonAsync<OperationTaskDto>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await ops.DeleteAsync($"/api/orders/operation-tasks/{extra.Id}?version={extra.Version}")).StatusCode);

        // Hepsi bitince tamamlanır; tamamlanan iş ve görevleri değişmez.
        await CompleteJobAsync(ops, delivery.Id);
        var done = await JobDetailAsync(ops, delivery.Id);
        Assert.Equal(("Completed", 100), (done.Job.Status, done.ProgressPercent));
        Assert.NotNull(done.Job.ActualEndAt);
        Assert.All(done.Actions, a => Assert.False(a.Allowed));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ops.PutAsJsonAsync($"/api/orders/operation-tasks/{done.Tasks[0].Id}",
            new SaveOperationTaskRequest(delivery.Id, done.Tasks[0].Title, false, null, done.Tasks[0].Version))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ops.PutAsJsonAsync($"/api/orders/operation-jobs/{delivery.Id}",
            new SaveOperationJobRequest(done.Job.Version, "Kapanmış işi düzenle"))).StatusCode);
        Assert.Equal(["Generated", "Started", "Completed"], done.History.Select(h => h.Kind));
        Assert.Equal(s.Ops.Id, done.History[2].ActorId);
    }

    [Fact]
    public async Task Unassigned_job_waits_visibly_and_only_a_user_with_assign_permission_can_assign_it()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        using var jobsOnly = await LoginAsync(s.JobsOnly);
        var withoutHandover = await ApprovedOrderAsync(s, sales);

        // Devir kabul edilmeden operasyon işi açılamaz.
        var early = await ops.PostAsJsonAsync($"/api/orders/orders/{withoutHandover.Id}/jobs", new CreateOperationJobRequest([Guid.NewGuid()], "Erken"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);

        var order = await AcceptedOrderAsync(s, sales, ops);
        var generated = (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={order.Id}"))!.Items;
        var serviceJob = generated.Single(j => j.Kind == "Service");
        var lineId = (await JobDetailAsync(ops, serviceJob.Id)).Lines.Single().OrderLineId;

        // İşi iptal etmek kalemi serbest bırakır; elle açılan iş atama yapılmadığından "Atama bekliyor" olur ve kuyrukta görünür.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostJobActionAsync(ops, serviceJob.Id, "cancel", serviceJob.Version)).StatusCode);
        var cancelled = await ActAsync(ops, serviceJob.Id, "cancel", serviceJob.Version, reason: "Kapsam değişti");
        Assert.Equal(("Cancelled", "Kapsam değişti"), (cancelled.Job.Status, cancelled.Job.CancelReason));
        var manual = await ManualJobAsync(ops, order.Id, new CreateOperationJobRequest([lineId], "Sonradan açılan hizmet işi"));
        Assert.Equal(("AwaitingAssignment", "Manual", null), (manual.Job.Status, manual.Job.Source, manual.Job.ResponsibleUserId));
        Assert.Equal(5, manual.Tasks.Count);
        var queue = (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>("/api/orders/operation-jobs?unassigned=true"))!.Items;
        Assert.Equal(manual.Job.Id, Assert.Single(queue).Id);

        // Atanmadan başlatılamaz; atama yetkisi olmayan atayamaz (yönetim yetkisi yetmez); boş atama reddedilir.
        Assert.Equal(HttpStatusCode.Conflict, (await PostJobActionAsync(ops, manual.Job.Id, "start", manual.Job.Version)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostJobActionAsync(jobsOnly, manual.Job.Id, "assign", manual.Job.Version, responsible: s.Ops.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostJobActionAsync(ops, manual.Job.Id, "assign", manual.Job.Version)).StatusCode);
        var assigned = await ActAsync(ops, manual.Job.Id, "assign", manual.Job.Version, responsible: s.Ops.Id, team: "Kurulum ekibi");
        Assert.Equal(("Planned", s.Ops.Id, "Kurulum ekibi"), (assigned.Job.Status, assigned.Job.ResponsibleUserId, assigned.Job.TeamName));
        Assert.Empty((await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>("/api/orders/operation-jobs?unassigned=true"))!.Items);
        Assert.Equal(["CreatedManually", "Assigned"], assigned.History.Select(h => h.Kind));

        // Tamamlanmış işe atama yapılamaz.
        await CompleteJobAsync(ops, assigned.Job.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await PostJobActionAsync(ops, assigned.Job.Id, "assign", (await JobDetailAsync(ops, assigned.Job.Id)).Job.Version, responsible: s.Ops.Id)).StatusCode);
    }

    [Fact]
    public async Task Dependencies_reject_cycles_self_links_and_other_orders_and_a_cancelled_predecessor_keeps_blocking()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        var order = await AcceptedOrderAsync(s, sales, ops);
        var other = await AcceptedOrderAsync(s, sales, ops);
        var jobs = (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={order.Id}"))!.Items;
        var delivery = jobs.Single(j => j.Kind == "Delivery");
        var service = jobs.Single(j => j.Kind == "Service");
        var foreign = (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={other.Id}"))!.Items[0];

        // Kendine, başka siparişin işine ve döngü oluşturacak bağ reddedilir; geçerli bağ eklenir.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ops.PostAsJsonAsync($"/api/orders/operation-jobs/{service.Id}/dependencies", new AddJobDependencyRequest(service.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ops.PostAsJsonAsync($"/api/orders/operation-jobs/{service.Id}/dependencies", new AddJobDependencyRequest(foreign.Id))).StatusCode);
        var added = await ops.PostAsJsonAsync($"/api/orders/operation-jobs/{service.Id}/dependencies", new AddJobDependencyRequest(delivery.Id));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ops.PostAsJsonAsync($"/api/orders/operation-jobs/{service.Id}/dependencies", new AddJobDependencyRequest(delivery.Id))).StatusCode);
        var cycle = await ops.PostAsJsonAsync($"/api/orders/operation-jobs/{delivery.Id}/dependencies", new AddJobDependencyRequest(service.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cycle.StatusCode);
        Assert.Contains("döngü", await cycle.Content.ReadAsStringAsync());

        // İptal edilen öncül işi beklemeyi sürdürür; bağımlılık kaldırılınca iş başlayabilir.
        await ActAsync(ops, delivery.Id, "cancel", (await JobDetailAsync(ops, delivery.Id)).Job.Version, reason: "Teslimat iptal");
        var blocked = await PostJobActionAsync(ops, service.Id, "start", (await JobDetailAsync(ops, service.Id)).Job.Version);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var removed = await ops.DeleteAsync($"/api/orders/operation-jobs/{service.Id}/dependencies/{delivery.Id}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ops.DeleteAsync($"/api/orders/operation-jobs/{service.Id}/dependencies/{delivery.Id}")).StatusCode);
        Assert.Equal("InProgress", (await ActAsync(ops, service.Id, "start", (await JobDetailAsync(ops, service.Id)).Job.Version)).Job.Status);
        var history = (await JobDetailAsync(ops, service.Id)).History.Select(h => h.Kind).ToList();
        Assert.Equal(["Generated", "DependencyAdded", "DependencyRemoved", "Started"], history);
    }

    [Fact]
    public async Task Cancelling_the_order_cancels_open_jobs_and_keeps_completed_ones()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        using var approver = await LoginAsync(s.Approver);
        var order = await AcceptedOrderAsync(s, sales, ops);
        var jobs = (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={order.Id}"))!.Items;
        var delivery = jobs.Single(j => j.Kind == "Delivery");
        var service = jobs.Single(j => j.Kind == "Service");
        await ActAsync(ops, delivery.Id, "start", delivery.Version);
        await CompleteJobAsync(ops, delivery.Id);

        var current = (await sales.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{order.Id}"))!;
        var cancel = await approver.PostAsJsonAsync($"/api/orders/orders/{order.Id}/cancel", new OrderTransitionRequest(current.Version, "Müşteri vazgeçti"));
        Assert.True(cancel.StatusCode == HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync());

        var after = (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={order.Id}"))!.Items;
        Assert.Equal("Completed", after.Single(j => j.Id == delivery.Id).Status);
        var cancelled = after.Single(j => j.Id == service.Id);
        Assert.Equal(("Cancelled", "Sipariş iptal edildi."), (cancelled.Status, cancelled.CancelReason));
        Assert.Equal("Cancelled", (await JobDetailAsync(ops, service.Id)).History.Last().Kind);
        Assert.Equal(HttpStatusCode.Conflict, (await PostJobActionAsync(ops, service.Id, "start", cancelled.Version)).StatusCode);
    }

    [Fact]
    public async Task Other_tenants_and_unauthorized_users_cannot_reach_jobs_and_jobs_cannot_be_forged_through_generic_endpoints()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var sales = await LoginAsync(a.Sales);
        using var ops = await LoginAsync(a.Ops);
        using var reader = await LoginAsync(a.Reader);
        using var own = await LoginAsync(a.OwnOnly);
        using var adminB = await LoginAsync(b.Admin);
        var order = await AcceptedOrderAsync(a, sales, ops);
        var delivery = (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={order.Id}&kind=Delivery"))!.Items.Single();

        // Başka tenant: iş, eylem, görev ve bağımlılık uçları 404 ya da boş.
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/orders/operation-jobs/{delivery.Id}/detail")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/orders/operation-jobs/{delivery.Id}/start", new OperationJobActionRequest(delivery.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/orders/operation-jobs/{delivery.Id}/dependencies", new AddJobDependencyRequest(delivery.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/orders/orders/{order.Id}/jobs", new CreateOperationJobRequest([Guid.NewGuid()]))).StatusCode);
        Assert.Empty((await adminB.GetFromJsonAsync<PagedResult<OperationJobDto>>("/api/orders/operation-jobs"))!.Items);
        Assert.Empty((await adminB.GetFromJsonAsync<PagedResult<OperationTaskDto>>($"/api/orders/operation-tasks?jobId={delivery.Id}"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PutAsJsonAsync($"/api/orders/operation-jobs/{delivery.Id}", new SaveOperationJobRequest(delivery.Version, "Ele geçirme"))).StatusCode);

        // Yalnızca okuyucu işi görür ama hiçbir eylemi yoktur ve yazamaz; "kendi" kapsamı başkasının işini göremez.
        var seen = (await reader.GetFromJsonAsync<OperationJobDetailDto>($"/api/orders/operation-jobs/{delivery.Id}/detail"))!;
        Assert.Empty(seen.Actions);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostJobActionAsync(reader, delivery.Id, "start", delivery.Version)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/orders/operation-tasks", new SaveOperationTaskRequest(delivery.Id, "Sızma"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/orders/operation-jobs/{delivery.Id}/dependencies", new AddJobDependencyRequest(delivery.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await own.GetAsync($"/api/orders/operation-jobs/{delivery.Id}/detail")).StatusCode);
        Assert.Empty((await own.GetFromJsonAsync<PagedResult<OperationJobDto>>("/api/orders/operation-jobs"))!.Items);
        Assert.Empty((await own.GetFromJsonAsync<PagedResult<OperationTaskDto>>($"/api/orders/operation-tasks?jobId={delivery.Id}"))!.Items);

        // Genel uçtan iş açılamaz ya da silinemez; durum alanları yazılamaz.
        Assert.NotEqual(HttpStatusCode.Created, (await ops.PostAsJsonAsync("/api/orders/operation-jobs", new SaveOperationJobRequest(0, "Sahte iş"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ops.DeleteAsync($"/api/orders/operation-jobs/{delivery.Id}?version={delivery.Version}")).StatusCode);
        var forged = await ops.PutAsJsonAsync($"/api/records/orders.operation-job/{delivery.Id}", new { version = delivery.Version, values = new Dictionary<string, object> { ["status"] = "Completed", ["responsibleUserId"] = a.Reader.Id } });
        var after = (await ops.GetFromJsonAsync<OperationJobDto>($"/api/orders/operation-jobs/{delivery.Id}"))!;
        Assert.Equal(("Planned", a.Ops.Id), (after.Status, after.ResponsibleUserId));
        _ = forged;
    }

    [Fact]
    public async Task Unassigned_and_overdue_jobs_get_one_follow_up_each_and_the_scheduler_can_be_repeated_safely()
    {
        var s = await SetUpAsync();
        using var sales = await LoginAsync(s.Sales);
        using var ops = await LoginAsync(s.Ops);
        using var admin = await LoginAsync(s.Admin);
        var order = await AcceptedOrderAsync(s, sales, ops);
        var jobs = (await ops.GetFromJsonAsync<PagedResult<OperationJobDto>>($"/api/orders/operation-jobs?orderId={order.Id}"))!.Items;
        var delivery = jobs.Single(j => j.Kind == "Delivery");
        var service = jobs.Single(j => j.Kind == "Service");
        var runner = factory.Services.GetRequiredService<OrderJobsRunner>();

        // Atama bekleyen iş: servis işini iptal edip kalemini atanmamış bir işe taşıyoruz; 48 saat önce açılmış gibi yaşlandırılır.
        var lineId = (await JobDetailAsync(ops, service.Id)).Lines.Single().OrderLineId;
        await ActAsync(ops, service.Id, "cancel", service.Version, reason: "Yeniden açılacak");
        var waiting = await ManualJobAsync(ops, order.Id, new CreateOperationJobRequest([lineId], "Atanmamış hizmet işi"));
        Assert.Equal("AwaitingAssignment", waiting.Job.Status);
        Assert.Equal(0, (await runner.RunTenantAsync(s.Tenant.Id, CancellationToken.None)).Run!.UnassignedJobAlerts);
        await SqlAsync(s.Tenant.Id, "UPDATE orders.OperationJobs SET CreatedAt = {0} WHERE Id = {1}", DateTimeOffset.UtcNow.AddHours(-48), waiting.Job.Id);

        // Planlanan bitişi geçmiş açık iş.
        await SqlAsync(s.Tenant.Id, "UPDATE orders.OperationJobs SET PlannedStartDate = {0}, PlannedEndDate = {1} WHERE Id = {2}", new DateTime(2026, 1, 1), new DateTime(2026, 1, 10), delivery.Id);

        var first = (await runner.RunTenantAsync(s.Tenant.Id, CancellationToken.None)).Run!;
        Assert.Equal((1, 1), (first.UnassignedJobAlerts, first.OverdueJobAlerts));
        var second = (await runner.RunTenantAsync(s.Tenant.Id, CancellationToken.None)).Run!;
        Assert.Equal((0, 0), (second.UnassignedJobAlerts, second.OverdueJobAlerts));
        Assert.Equal((0, 0), ((await runner.RunTenantAsync(s.Tenant.Id, CancellationToken.None)).Run!.UnassignedJobAlerts, 0));

        var activities = (await admin.GetFromJsonAsync<PagedResult<ActivityDto>>("/api/crm/activities?pageSize=200"))!.Items;
        var unassigned = Assert.Single(activities, a => a.Subject.Contains("atama bekliyor"));
        var late = Assert.Single(activities, a => a.Subject.Contains("Operasyon işi gecikti"));
        Assert.Equal(s.Ops.Id, unassigned.OwnerId); // devri kabul eden kullanıcı
        Assert.Equal(s.Ops.Id, late.OwnerId); // işin sorumlusu
        Assert.Contains("UnassignedAlert", (await JobDetailAsync(ops, waiting.Job.Id)).History.Select(h => h.Kind));
        Assert.Single((await JobDetailAsync(ops, delivery.Id)).History, h => h.Kind == "OverdueAlert");

        // Tamamlanan iş gecikme uyarısı almaz; iptal edilen iş de.
        await CompleteJobAsync(ops, delivery.Id);
        await SqlAsync(s.Tenant.Id, "UPDATE orders.OperationJobs SET OverdueAlertAt = NULL WHERE Id = {0}", delivery.Id);
        Assert.Equal(0, (await runner.RunTenantAsync(s.Tenant.Id, CancellationToken.None)).Run!.OverdueJobAlerts);
    }

    // ---- yardımcılar

    private static SaveHandoverRequest Full(long version, Guid responsible) => new(
        version, Scope: "Sunucu cihazlarının kurulumu", Deliverables: "Kurulu cihazlar ve rapor", Location: "Gebze depo", ContactName: "Ali Veli", ContactPhone: "0532 000 00 00",
        TargetStartDate: new DateOnly(2026, 12, 1), TargetEndDate: new DateOnly(2026, 12, 15), Priority: "High", ResponsibleUserId: responsible,
        CompletionCriteria: "Cihazlar çalışır durumda", AcceptanceCriteria: "Müşteri kabul tutanağını imzalar");

    /// <summary>Onaylı sipariş (bir ürün + bir hizmet kalemi) → hazır devir → gönderim → operasyon kabulü; işler devir kabulünde üretilir.</summary>
    private async Task<OrderDto> AcceptedOrderAsync(Setup s, HttpClient sales, HttpClient ops)
    {
        var order = await ApprovedOrderAsync(s, sales);
        await SqlAsync(s.Tenant.Id, "UPDATE orders.OrderLines SET Kind = 'Product' WHERE OrderId = {0} AND Name = 'Sunucu'", order.Id);
        var created = await sales.PostAsJsonAsync($"/api/orders/orders/{order.Id}/handover", new { });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = (await created.Content.ReadFromJsonAsync<HandoverDetailDto>())!;
        var saved = await sales.PutAsJsonAsync($"/api/orders/handovers/{detail.Handover.Id}", Full(detail.Handover.Version, s.Ops.Id));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        detail = (await sales.GetFromJsonAsync<HandoverDetailDto>($"/api/orders/orders/{order.Id}/handover"))!;
        foreach (var item in detail.Items)
        {
            var done = await sales.PutAsJsonAsync($"/api/orders/handover-items/{item.Id}", new SaveHandoverItemRequest(detail.Handover.Id, item.Text, true, null, item.Version));
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        }

        detail = (await sales.GetFromJsonAsync<HandoverDetailDto>($"/api/orders/orders/{order.Id}/handover"))!;
        var sent = await sales.PostAsJsonAsync($"/api/orders/handovers/{detail.Handover.Id}/send", new HandoverActionRequest(detail.Handover.Version));
        Assert.True(sent.StatusCode == HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        var sentDetail = (await sent.Content.ReadFromJsonAsync<HandoverDetailDto>())!;
        var accepted = await ops.PostAsJsonAsync($"/api/orders/handovers/{sentDetail.Handover.Id}/accept", new HandoverActionRequest(sentDetail.Handover.Version, "Kabul"));
        Assert.True(accepted.StatusCode == HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        return order;
    }

    private static async Task<OperationJobDetailDto> JobDetailAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<OperationJobDetailDto>($"/api/orders/operation-jobs/{id}/detail"))!;

    private static Task<HttpResponseMessage> PostJobActionAsync(HttpClient client, Guid id, string action, long version, string? reason = null, Guid? responsible = null, string? team = null) =>
        client.PostAsJsonAsync($"/api/orders/operation-jobs/{id}/{action}", new OperationJobActionRequest(version, reason, responsible, team));

    private static async Task<OperationJobDetailDto> ActAsync(HttpClient client, Guid id, string action, long version, string? reason = null, Guid? responsible = null, string? team = null)
    {
        var response = await PostJobActionAsync(client, id, action, version, reason, responsible, team);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<OperationJobDetailDto>())!;
    }

    private static async Task ToggleTaskAsync(HttpClient client, Guid jobId, OperationTaskDto task, bool done)
    {
        var response = await client.PutAsJsonAsync($"/api/orders/operation-tasks/{task.Id}", new SaveOperationTaskRequest(jobId, task.Title, done, task.Note, task.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Başlamış (ya da başlatılabilir) işin görevlerini bitirir ve işi tamamlar.</summary>
    private static async Task CompleteJobAsync(HttpClient client, Guid id)
    {
        var detail = await JobDetailAsync(client, id);
        if (detail.Job.Status is "Planned" or "AwaitingAssignment")
        {
            detail = await ActAsync(client, id, "start", detail.Job.Version);
        }

        foreach (var task in detail.Tasks.Where(t => !t.IsDone))
        {
            await ToggleTaskAsync(client, id, task, done: true);
        }

        detail = await JobDetailAsync(client, id);
        await ActAsync(client, id, "complete", detail.Job.Version);
    }

    private static async Task<OperationJobDetailDto> ManualJobAsync(HttpClient client, Guid orderId, CreateOperationJobRequest request)
    {
        var response = await client.PostAsJsonAsync($"/api/orders/orders/{orderId}/jobs", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OperationJobDetailDto>())!;
    }

    private async Task SqlAsync(Guid tenantId, string sql, params object[] args)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.True(await db.Database.ExecuteSqlRawAsync(sql, args) >= 1);
    }

    private static async Task SetSettingAsync(HttpClient client, string key, string value)
    {
        var current = (await client.GetFromJsonAsync<SettingsResponse>("/api/settings"))!;
        var version = current.Settings.FirstOrDefault(x => x.Key == key)?.Version ?? 0;
        var response = await client.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { [key] = new(value, version) }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<OrderDto> ApprovedOrderAsync(Setup s, HttpClient sales)
    {
        var accepted = await AcceptedQuoteAsync(sales, s.CompanyId);
        var response = await sales.PostAsJsonAsync($"/api/orders/from-quote/{accepted.Id}", new CreateOrderFromQuoteRequest());
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var order = (await response.Content.ReadFromJsonAsync<OrderDto>())!;
        using var approver = await LoginAsync(s.Approver);
        await TransitionAsync(sales, order.Id, "submit-for-approval");
        return await TransitionAsync(approver, order.Id, "approve");
    }

    private static async Task<OrderDto> TransitionAsync(HttpClient client, Guid id, string action)
    {
        var current = (await client.GetFromJsonAsync<OrderDto>($"/api/orders/orders/{id}"))!;
        var response = await client.PostAsJsonAsync($"/api/orders/orders/{id}/{action}", new OrderTransitionRequest(current.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!;
    }

    private static async Task<QuoteDto> AcceptedQuoteAsync(HttpClient client, Guid companyId)
    {
        var create = await client.PostAsJsonAsync("/api/crm/quotes", new SaveQuoteRequest("Cihaz ve kurulum teklifi", companyId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var quote = (await create.Content.ReadFromJsonAsync<QuoteDto>())!;
        foreach (var (name, quantity, price) in new[] { ("Sunucu", 2m, 1000m), ("Kurulum", 1m, 300m) })
        {
            var added = await client.PostAsJsonAsync("/api/crm/quote-lines", new SaveQuoteLineRequest(quote.Id, name, "Free", null, quantity, price, null, "None", 0m, 20m, null, "Standard"));
            Assert.True(added.StatusCode == HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        }

        await QuoteTransitionAsync(client, quote.Id, "present");
        return await QuoteTransitionAsync(client, quote.Id, "record-accepted");
    }

    private static async Task<QuoteDto> QuoteTransitionAsync(HttpClient client, Guid id, string action)
    {
        var current = (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;
        var response = await client.PostAsJsonAsync($"/api/crm/quotes/{id}/{action}", new QuoteTransitionRequest(current.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<QuoteDto>())!;
    }

    private sealed record Setup(
        Tenant Tenant, ModulixUser Admin, ModulixUser Sales, ModulixUser Approver, ModulixUser Ops, ModulixUser JobsOnly, ModulixUser Reader, ModulixUser OwnOnly, Guid CompanyId);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "orders", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var sales = await factory.CreateUserAsync(tenant);
        var approver = await factory.CreateUserAsync(tenant);
        var ops = await factory.CreateUserAsync(tenant);
        var jobsOnly = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var ownOnly = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        string[] quoteWork = [Crm.CompaniesRead, Crm.QuotesRead, Crm.QuotesWrite, Crm.QuotesPresent, Crm.QuotesRecordDecision, Crm.QuotesManualPrice];
        string[] activityWork = [Crm.ActivitiesRead, Crm.ActivitiesWrite, Crm.ActivitiesAssign];
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, OrdersCreate, OrdersWrite, FinancialsRead, HandoversWrite, HandoversSend, .. quoteWork, .. activityWork], Scope.All, sales);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, FinancialsRead, OrdersApprove, OrdersCancel], Scope.All, approver);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, HandoversAccept, JobsAssign, JobsManage, .. activityWork], Scope.All, ops);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, JobsManage], Scope.All, jobsOnly);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [OrdersRead, Crm.ActivitiesRead], Scope.Own, ownOnly);
        using var client = await LoginAsync(admin);
        var company = await client.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        Assert.Equal(HttpStatusCode.Created, company.StatusCode);
        var companyId = (await company.Content.ReadFromJsonAsync<CompanyDto>())!.Id;
        return new Setup(tenant, admin, sales, approver, ops, jobsOnly, reader, ownOnly, companyId);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
