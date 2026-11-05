using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Modulix.Api.Tenancy;
using Modulix.Core.Authorization;
using Modulix.Core.Automation;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Modules;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Automation;
using Modulix.Modules.Logistics;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0027 Dilim 1: kural yönetimi (/api/automation). Taslak → yayın → açma/kapama, değişmez sürümler, yayın doğrulaması,
/// tenant izolasyonu (API ve Row-Level Security), izinler, denetim kaydı ve yan etkisiz önizleme. Gerçek uygulamada henüz hiçbir
/// modül aksiyon beyan etmediği için testler, yalnızca bu test sürecinde katalogu genişleten bir <c>autotest</c> modülü kullanır;
/// kural yazılan kayıt türü ise gerçek Lojistik modülünün beyanıdır (CRM'den bağımsız).
/// </summary>
[Collection(AutomationTestCollection.Name)]
public class AutomationRuleTests(AutomationFixture fixture) : IClassFixture<AutomationFixture>
{
    private const string Shipment = LogisticsModule.AutomationEntityKeys.Shipment;
    private const string Logistics = LogisticsModule.ModuleKey;
    private const string NoteAction = AutomationTestModule.NoteAction;

    private static readonly string[] ReadOnlyPermissions = [CoreModule.Permissions.ReadAutomation];
    private static readonly string[] ManagerPermissions =
        [CoreModule.Permissions.ReadAutomation, CoreModule.Permissions.ManageAutomation, LogisticsModule.Permissions.ShipmentsRead];

    private static readonly string[] PublisherPermissions = [.. ManagerPermissions, CoreModule.Permissions.PublishAutomation];

    private static AutomationDefinition Definition(string documentStatus = "missing", string text = "Belge eksik", string action = NoteAction) => new(
        new RecordCreatedTrigger(),
        new ConditionLeaf("logistics.shipment.documentStatus", AutomationOperator.Eq, [documentStatus]),
        [new AutomationActionStep("note", action, new JsonObject { ["text"] = text })]);

    private static JsonElement Json(AutomationDefinition definition) => JsonSerializer.SerializeToElement(definition, AutomationJson.Options);

    private static CreateAutomationRuleRequest Create(string name = "Eksik belge", AutomationDefinition? definition = null, int priority = 100) =>
        new(Logistics, Shipment, name, "Belgesi eksik sevkiyat", priority, false, null, null, Json(definition ?? Definition()));

    private static UpdateAutomationRuleRequest Update(string name, AutomationDefinition definition, long? expected = null, int priority = 100) =>
        new(name, "Belgesi eksik sevkiyat", priority, false, null, null, Json(definition), expected);

    [Fact]
    public async Task Rule_goes_from_draft_to_published_to_active_and_published_versions_never_change()
    {
        var (tenant, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        var created = await CreateRuleAsync(client, Create());
        Assert.Equal("Draft", created.Rule.Status);
        Assert.True(created.Rule.HasDraft);
        Assert.Null(created.Published);
        Assert.Equal(1, created.Draft!.VersionNo);
        Assert.Empty(created.DraftIssues);
        Assert.Contains("Belge durumu = Eksik", created.Draft.Summary);
        Assert.Contains("Kayıt oluşturulunca", created.Draft.Summary);

        // Taslak düzenlenir; sürüm numarası aynı kalır, eşzamanlılık sürümü artar.
        var edited = await ReadAsync<AutomationRuleDetailDto>(await client.PutAsJsonAsync(
            $"/api/automation/rules/{created.Rule.Id}", Update("Eksik belge v2", Definition(text: "Belge eksik!"), created.Rule.Version)));
        Assert.Equal(1, edited.Draft!.VersionNo);
        Assert.Equal("Eksik belge v2", edited.Rule.Name);
        Assert.True(edited.Rule.Version > created.Rule.Version);

        // Yayın: kapalı yayınlanır (yeni kural varsayılan olarak çalışmaz), sonra açılır.
        var published = await ReadAsync<AutomationRuleDetailDto>(await client.PostAsJsonAsync(
            $"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(false, edited.Rule.Version)));
        Assert.Equal("Inactive", published.Rule.Status);
        Assert.Equal(1, published.Rule.PublishedVersionNo);
        Assert.Null(published.Draft);
        Assert.Equal("Published", published.Published!.State);
        var hashV1 = published.Published.Hash;

        var active = await ReadAsync<AutomationRuleDetailDto>(await client.PutAsJsonAsync(
            $"/api/automation/rules/{created.Rule.Id}/active", new SetAutomationRuleActiveRequest(true)));
        Assert.Equal("Active", active.Rule.Status);

        // Yayınlanmış kuralı düzenlemek yeni taslak açar; yayınlanmış sürüm 1 aynen kalır.
        var v2 = await ReadAsync<AutomationRuleDetailDto>(await client.PutAsJsonAsync(
            $"/api/automation/rules/{created.Rule.Id}", Update("Eksik belge v3", Definition(text: "Üçüncü sürüm"), active.Rule.Version, priority: 5)));
        Assert.Equal(2, v2.Draft!.VersionNo);
        Assert.Equal(1, v2.Published!.VersionNo);
        Assert.Equal(hashV1, v2.Published.Hash);
        Assert.Equal(100, v2.Published.Priority);
        Assert.Equal(5, v2.Draft.Priority);
        Assert.Equal("Active", v2.Rule.Status);

        var published2 = await ReadAsync<AutomationRuleDetailDto>(await client.PostAsJsonAsync(
            $"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(true, v2.Rule.Version)));
        Assert.Equal(2, published2.Rule.PublishedVersionNo);
        Assert.Equal("Active", published2.Rule.Status);
        Assert.NotEqual(hashV1, published2.Published!.Hash);

        // Veritabanında iki sürüm de durur, yalnızca sonuncusu yayındadır ve ikisi de yayınlanmış durumdadır.
        using var scope = fixture.Factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var versions = await db.AutomationRuleVersions.AsNoTracking().Where(v => v.RuleId == created.Rule.Id).OrderBy(v => v.VersionNo).ToListAsync();
        Assert.Equal([1, 2], versions.Select(v => v.VersionNo));
        Assert.All(versions, v => Assert.Equal(AutomationVersionState.Published, v.State));
    }

    [Fact]
    public async Task Publishing_an_unchanged_draft_or_with_a_stale_version_is_a_conflict()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var created = await CreateRuleAsync(client, Create());

        var stale = await client.PostAsJsonAsync(
            $"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(false, created.Rule.Version + 10));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var published = await ReadAsync<AutomationRuleDetailDto>(await client.PostAsJsonAsync(
            $"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(false, null)));

        // Yayından sonra taslak yok: yayınlanacak bir şey yoktur.
        var again = await client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(false, null));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        // Aynı içerikle açılan taslak yayınlanamaz.
        var same = await ReadAsync<AutomationRuleDetailDto>(await client.PutAsJsonAsync(
            $"/api/automation/rules/{created.Rule.Id}", Update("Eksik belge", Definition(), published.Rule.Version)));
        var duplicate = await client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(false, same.Rule.Version));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // Eski sürümle düzenleme de çakışır.
        var staleUpdate = await client.PutAsJsonAsync($"/api/automation/rules/{created.Rule.Id}", Update("X", Definition(), expected: 1));
        Assert.Equal(HttpStatusCode.Conflict, staleUpdate.StatusCode);
    }

    [Fact]
    public async Task Publishing_is_blocked_with_every_issue_listed_and_nothing_is_published()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        // Taslak eksik olabilir: bilinmeyen aksiyon ve geçersiz koşul değeriyle kaydedilir.
        var broken = new AutomationDefinition(
            new RecordCreatedTrigger(),
            new ConditionLeaf("logistics.shipment.documentStatus", AutomationOperator.Eq, ["yok-boyle-secenek"]),
            [new AutomationActionStep("note", "autotest.note.yok", new JsonObject())]);
        var created = await CreateRuleAsync(client, Create("Bozuk", broken));
        Assert.Equal(2, created.DraftIssues.Count);

        var response = await client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(false, null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(2, problem["issues"]!.AsArray().Count);
        var detail = await client.GetFromJsonAsync<AutomationRuleDetailDto>($"/api/automation/rules/{created.Rule.Id}");
        Assert.Equal("Draft", detail!.Rule.Status);
        Assert.Null(detail.Published);
    }

    [Fact]
    public async Task Create_validates_the_request_shape_and_the_entity()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        var unknownEntity = Create() with { EntityKey = "logistics.nope" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/automation/rules", unknownEntity)).StatusCode);

        var otherModule = Create() with { ModuleKey = "autotest" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/automation/rules", otherModule)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/automation/rules", Create(name: "  "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/automation/rules", Create(priority: 5000))).StatusCode);

        // Kod/betik düğümü ya da bilinmeyen tetikleyici tipi çözülemez.
        var script = JsonDocument.Parse("""{"trigger":{"type":"manual"},"condition":{"type":"script","code":"1==1"},"actions":[]}""").RootElement.Clone();
        var withScript = Create() with { Definition = script };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/automation/rules", withScript)).StatusCode);

        var list = await client.GetFromJsonAsync<AutomationRulePageDto>("/api/automation/rules");
        Assert.Empty(list!.Items);
    }

    [Fact]
    public async Task Module_that_is_not_open_for_the_tenant_cannot_get_rules_and_existing_rules_show_the_broken_dependency()
    {
        var (tenant, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var created = await CreateRuleAsync(client, Create());
        await client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));

        await fixture.Factory.SetModuleAsync(tenant.Id, Logistics, false);

        // Rule sessizce bozulmaz: bağımlılık sorunu görünür, yeniden açma engellenir, yeni kural yazılamaz.
        var detail = await client.GetFromJsonAsync<AutomationRuleDetailDto>($"/api/automation/rules/{created.Rule.Id}");
        Assert.Contains(detail!.PublishedIssues, i => i.Message.Contains("etkin değil") || i.Message.Contains("kural yazma"));
        Assert.Equal("Active", detail.Rule.Status);

        // Kapatmak her zaman serbesttir; bağımlılık düzelene kadar yeniden açmak engellenir. Yeni kural da yazılamaz.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/active", new SetAutomationRuleActiveRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/active", new SetAutomationRuleActiveRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/automation/rules", Create("Yeni"))).StatusCode);

        var catalog = await client.GetFromJsonAsync<AutomationCatalogDto>("/api/automation/catalog");
        Assert.DoesNotContain(catalog!.Entities, e => e.ModuleKey == Logistics);

        // Modül yeniden açılınca kural yeniden geçerli görünür.
        await fixture.Factory.SetModuleAsync(tenant.Id, Logistics, true);
        detail = await client.GetFromJsonAsync<AutomationRuleDetailDto>($"/api/automation/rules/{created.Rule.Id}");
        Assert.Empty(detail!.PublishedIssues);
    }

    [Fact]
    public async Task Catalog_lists_the_declared_entities_fields_events_and_actions_for_the_tenant()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        var catalog = (await client.GetFromJsonAsync<AutomationCatalogDto>("/api/automation/catalog"))!;

        var shipment = Assert.Single(catalog.Entities, e => e.EntityKey == Shipment);
        var status = Assert.Single(shipment.Fields, f => f.Key == "logistics.shipment.documentStatus");
        Assert.Equal("Choice", status.Type);
        Assert.Contains("Eq", status.Operators);
        Assert.Contains("ChangedTo", status.Operators);
        Assert.DoesNotContain("Gt", status.Operators);
        Assert.Equal(["complete", "missing"], status.Options.Select(o => o.Key).Order());
        Assert.Contains(shipment.Events, e => e.EventType == "RecordCreated");
        var action = Assert.Single(catalog.Actions, a => a.Key == NoteAction);
        Assert.True(action.Available);
        Assert.Equal("Write", action.SideEffect);
        Assert.Contains(action.Parameters, p => p.Name == "text" && p.Required);
    }

    [Fact]
    public async Task Preview_is_free_of_side_effects_and_reports_definition_problems()
    {
        var (tenant, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var before = await CountAsync(tenant.Id);

        var ok = await ReadAsync<AutomationPreviewDto>(await client.PostAsJsonAsync(
            "/api/automation/preview", new PreviewAutomationRequest(Logistics, Shipment, Json(Definition()), null, null)));
        Assert.Empty(ok.Issues);
        Assert.False(ok.Evaluated);
        Assert.Equal("Kayıt oluşturulunca", ok.TriggerText);
        Assert.Equal("Belge durumu = Eksik", ok.ConditionText);
        Assert.Contains(ok.Warnings, w => w.Contains("Örnek kayıt seçilmedi"));

        var invalid = await ReadAsync<AutomationPreviewDto>(await client.PostAsJsonAsync(
            "/api/automation/preview", new PreviewAutomationRequest(Logistics, Shipment, Json(Definition(action: "autotest.note.yok")), null, null)));
        Assert.NotEmpty(invalid.Issues);

        // Örnek kayıt isteği kayıt boru hattına gider; Lojistik'in henüz kayıt deposu yok: yazma yok, yalnızca "bulunamadı".
        var withRecord = await client.PostAsJsonAsync(
            "/api/automation/preview", new PreviewAutomationRequest(Logistics, Shipment, Json(Definition()), Guid.NewGuid(), null));
        Assert.Equal(HttpStatusCode.NotFound, withRecord.StatusCode);

        // Önizleme hiçbir kural, sürüm ya da denetim kaydı bırakmaz.
        Assert.Equal(before, await CountAsync(tenant.Id));
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_change_the_rule_and_the_database_hides_it()
    {
        var (tenantA, adminA) = await SetUpAsync();
        var (tenantB, adminB) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        var rule = await CreateRuleAsync(clientA, Create("Yalnızca A"));
        await CreateRuleAsync(clientB, Create("B kuralı"));

        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/automation/rules/{rule.Rule.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PutAsJsonAsync($"/api/automation/rules/{rule.Rule.Id}", Update("Ele geçirildi", Definition()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PostAsJsonAsync($"/api/automation/rules/{rule.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PutAsJsonAsync($"/api/automation/rules/{rule.Rule.Id}/active", new SetAutomationRuleActiveRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.DeleteAsync($"/api/automation/rules/{rule.Rule.Id}/draft")).StatusCode);

        var listB = await clientB.GetFromJsonAsync<AutomationRulePageDto>("/api/automation/rules");
        Assert.Equal(["B kuralı"], listB!.Items.Select(r => r.Name));

        // A'nın kuralı değişmedi.
        var detailA = await clientA.GetFromJsonAsync<AutomationRuleDetailDto>($"/api/automation/rules/{rule.Rule.Id}");
        Assert.Equal("Yalnızca A", detailA!.Rule.Name);
        Assert.Equal("Draft", detailA.Rule.Status);

        // İkinci katman: Row-Level Security. Oturum tenant'ı olmadan ya da başka tenant'la satır görünmez.
        const string Sql = "SELECT COUNT(*) FROM [core].[AutomationRules] WHERE [Id] = @id";
        const string VersionSql = "SELECT COUNT(*) FROM [core].[AutomationRuleVersions] WHERE [RuleId] = @id";
        var id = ("@id", (object)rule.Rule.Id);
        Assert.Equal(1, await fixture.Factory.ScalarAsync<int>(tenantA.Id, Sql, id));
        Assert.Equal(1, await fixture.Factory.ScalarAsync<int>(tenantA.Id, VersionSql, id));
        Assert.Equal(0, await fixture.Factory.ScalarAsync<int>(tenantB.Id, Sql, id));
        Assert.Equal(0, await fixture.Factory.ScalarAsync<int>(tenantB.Id, VersionSql, id));
        Assert.Equal(0, await fixture.Factory.ScalarAsync<int>(null, Sql, id));
    }

    [Fact]
    public async Task Read_manage_and_publish_are_separate_permissions()
    {
        var (tenant, admin) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        var rule = await CreateRuleAsync(adminClient, Create("Yönetici kuralı"));

        var reader = await fixture.Factory.CreateUserAsync(tenant);
        var manager = await fixture.Factory.CreateUserAsync(tenant);
        var publisher = await fixture.Factory.CreateUserAsync(tenant);
        var nobody = await fixture.Factory.CreateUserAsync(tenant);
        await fixture.Factory.CreateRoleAsync(tenant.Id, ReadOnlyPermissions, reader);
        await fixture.Factory.CreateRoleAsync(tenant.Id, ManagerPermissions, manager);
        await fixture.Factory.CreateRoleAsync(tenant.Id, PublisherPermissions, publisher);

        using var readerClient = await LoginAsync(reader);
        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync("/api/automation/rules")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync($"/api/automation/rules/{rule.Rule.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync("/api/automation/catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync("/api/automation/rules", Create())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PutAsJsonAsync($"/api/automation/rules/{rule.Rule.Id}", Update("X", Definition()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.DeleteAsync($"/api/automation/rules/{rule.Rule.Id}/draft")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync("/api/automation/preview", new PreviewAutomationRequest(Logistics, Shipment, Json(Definition()), null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync($"/api/automation/rules/{rule.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null))).StatusCode);

        // Yönetici taslak yazar ama yayınlayamaz.
        using var managerClient = await LoginAsync(manager);
        var own = await CreateRuleAsync(managerClient, Create("Yönetenin kuralı"));
        Assert.Equal(HttpStatusCode.OK, (await managerClient.PutAsJsonAsync($"/api/automation/rules/{own.Rule.Id}", Update("Düzenlendi", Definition(), own.Rule.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.PostAsJsonAsync($"/api/automation/rules/{own.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.PutAsJsonAsync($"/api/automation/rules/{own.Rule.Id}/active", new SetAutomationRuleActiveRequest(true))).StatusCode);

        // Yayın izni olan yayınlar.
        using var publisherClient = await LoginAsync(publisher);
        Assert.Equal(HttpStatusCode.OK, (await publisherClient.PostAsJsonAsync($"/api/automation/rules/{own.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null))).StatusCode);

        // Hiçbir izni olmayan hiçbir şeye erişemez.
        using var nobodyClient = await LoginAsync(nobody);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobodyClient.GetAsync("/api/automation/rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobodyClient.GetAsync("/api/automation/catalog")).StatusCode);
    }

    [Fact]
    public async Task Author_without_the_entity_read_permission_cannot_write_rules_for_it_and_cannot_see_its_fields()
    {
        var (tenant, admin) = await SetUpAsync();
        var author = await fixture.Factory.CreateUserAsync(tenant);
        await fixture.Factory.CreateRoleAsync(tenant.Id, [CoreModule.Permissions.ReadAutomation, CoreModule.Permissions.ManageAutomation], author);
        using var client = await LoginAsync(author);

        var catalog = await client.GetFromJsonAsync<AutomationCatalogDto>("/api/automation/catalog");
        Assert.DoesNotContain(catalog!.Entities, e => e.EntityKey == Shipment);

        var response = await client.PostAsJsonAsync("/api/automation/rules", Create());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Publisher_cannot_publish_an_action_that_needs_a_permission_they_do_not_hold()
    {
        var (tenant, admin) = await SetUpAsync();
        var publisher = await fixture.Factory.CreateUserAsync(tenant);
        await fixture.Factory.CreateRoleAsync(tenant.Id, PublisherPermissions, publisher);
        using var client = await LoginAsync(publisher);
        var created = await CreateRuleAsync(client, Create("Yetkili aksiyon", Definition(action: AutomationTestModule.SensitiveAction)));

        var response = await client.PostAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(false, null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("gereken izne sahip değilsiniz", await response.Content.ReadAsStringAsync());

        // Yönetici bu izne sahip olduğu için aynı aksiyonu yayınlar: yetki kişiye göredir.
        using var adminClient = await LoginAsync(admin);
        var adminRule = await CreateRuleAsync(adminClient, Create("Yönetici aksiyonu", Definition(action: AutomationTestModule.SensitiveAction)));
        Assert.Equal(HttpStatusCode.OK, (await adminClient.PostAsJsonAsync($"/api/automation/rules/{adminRule.Rule.Id}/publish", new PublishAutomationRuleRequest(false, null))).StatusCode);
    }

    [Fact]
    public async Task Discarding_the_draft_removes_a_never_published_rule_but_keeps_the_published_one()
    {
        var (tenant, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var draftOnly = await CreateRuleAsync(client, Create("Atılacak"));
        var kept = await CreateRuleAsync(client, Create("Kalacak"));
        var published = await ReadAsync<AutomationRuleDetailDto>(await client.PostAsJsonAsync(
            $"/api/automation/rules/{kept.Rule.Id}/publish", new PublishAutomationRuleRequest(false, null)));
        await client.PutAsJsonAsync($"/api/automation/rules/{kept.Rule.Id}", Update("Kalacak v2", Definition(text: "Yeni"), published.Rule.Version));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/automation/rules/{draftOnly.Rule.Id}/draft")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/automation/rules/{draftOnly.Rule.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/automation/rules/{kept.Rule.Id}/draft")).StatusCode);
        var detail = await client.GetFromJsonAsync<AutomationRuleDetailDto>($"/api/automation/rules/{kept.Rule.Id}");
        Assert.Null(detail!.Draft);
        Assert.Equal(1, detail.Published!.VersionNo);
        Assert.Equal("Kalacak", detail.Rule.Name);

        // Taslağı kalmayan kuralda atılacak bir şey yoktur.
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/automation/rules/{kept.Rule.Id}/draft")).StatusCode);

        using var scope = fixture.Factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.False(await db.AutomationRules.AnyAsync(r => r.Id == draftOnly.Rule.Id));
        Assert.False(await db.AutomationRuleVersions.AnyAsync(v => v.RuleId == draftOnly.Rule.Id));
    }

    [Fact]
    public async Task List_filters_by_module_status_and_search_and_pages()
    {
        var (_, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var first = await CreateRuleAsync(client, Create("Alfa kuralı"));
        await CreateRuleAsync(client, Create("Beta kuralı"));
        await client.PostAsJsonAsync($"/api/automation/rules/{first.Rule.Id}/publish", new PublishAutomationRuleRequest(true, null));

        var all = (await client.GetFromJsonAsync<AutomationRulePageDto>("/api/automation/rules"))!;
        Assert.Equal(2, all.TotalCount);
        Assert.Equal(["Active", "Draft"], all.Items.Select(r => r.Status).Order());

        Assert.Equal(["Alfa kuralı"], (await client.GetFromJsonAsync<AutomationRulePageDto>("/api/automation/rules?status=Active"))!.Items.Select(r => r.Name));
        Assert.Equal(["Beta kuralı"], (await client.GetFromJsonAsync<AutomationRulePageDto>("/api/automation/rules?search=beta"))!.Items.Select(r => r.Name));
        Assert.Equal(2, (await client.GetFromJsonAsync<AutomationRulePageDto>($"/api/automation/rules?moduleKey={Logistics}"))!.TotalCount);
        Assert.Equal(0, (await client.GetFromJsonAsync<AutomationRulePageDto>("/api/automation/rules?moduleKey=autotest"))!.TotalCount);
        var page = (await client.GetFromJsonAsync<AutomationRulePageDto>("/api/automation/rules?pageSize=1&page=2"))!;
        Assert.Single(page.Items);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/automation/rules?status=Bilinmeyen")).StatusCode);
    }

    [Fact]
    public async Task Every_management_action_is_written_to_the_audit_log_with_a_readable_summary()
    {
        var (tenant, admin) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var created = await CreateRuleAsync(client, Create("Denetimli kural"));
        var edited = await ReadAsync<AutomationRuleDetailDto>(await client.PutAsJsonAsync(
            $"/api/automation/rules/{created.Rule.Id}", Update("Denetimli kural 2", Definition(text: "Değişti"), created.Rule.Version)));
        var published = await ReadAsync<AutomationRuleDetailDto>(await client.PostAsJsonAsync(
            $"/api/automation/rules/{created.Rule.Id}/publish", new PublishAutomationRuleRequest(false, edited.Rule.Version)));
        await client.PutAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/active", new SetAutomationRuleActiveRequest(true));
        await client.PutAsJsonAsync($"/api/automation/rules/{created.Rule.Id}/active", new SetAutomationRuleActiveRequest(true));
        var discard = await CreateRuleAsync(client, Create("Silinecek"));
        await client.DeleteAsync($"/api/automation/rules/{discard.Rule.Id}/draft");

        using (var scope = fixture.Factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var entries = await db.AuditLog.AsNoTracking()
                .Where(a => a.EntityKey == CoreModule.AuditedEntityKeys.AutomationRule && a.RecordId == created.Rule.Id)
                .OrderBy(a => a.OccurredAt).ToListAsync();

            // Oluşturma, düzenleme, yayın, açma: dört kayıt; aynı duruma ikinci açma değişiklik değildir, kayıt yazılmaz.
            Assert.Equal([AuditAction.Created, AuditAction.Updated, AuditAction.Updated, AuditAction.Updated], entries.Select(e => e.Action));
            Assert.All(entries, e => Assert.Equal(ExecutionSource.Api, e.Source));
            Assert.All(entries, e => Assert.NotEqual(Guid.Empty, e.UserId));
            var json = entries[0].Changes;
            Assert.Contains("Denetimli kural", json);
            Assert.Contains("Belge durumu = Eksik", json);
            Assert.DoesNotContain("DefinitionJson", json);

            var statusChange = entries[2].Changes;
            Assert.Contains("Inactive", statusChange);
            Assert.Contains("Active", entries[3].Changes);

            var deleted = await db.AuditLog.AsNoTracking()
                .Where(a => a.EntityKey == CoreModule.AuditedEntityKeys.AutomationRule && a.RecordId == discard.Rule.Id)
                .Select(a => a.Action).ToListAsync();
            Assert.Contains(AuditAction.Deleted, deleted);
        }

        // Denetim ekranı türü okunabilir adla gösterir.
        var page = await client.GetFromJsonAsync<AuditPageResponse>($"/api/audit?entityKey={CoreModule.AuditedEntityKeys.AutomationRule}");
        Assert.NotEmpty(page!.Items);
        Assert.All(page.Items, i => Assert.Equal("Otomasyon kuralı", i.EntityName));
        Assert.NotNull(published);
    }

    private async Task<(Modulix.Core.Platform.Tenant Tenant, ModulixUser Admin)> SetUpAsync()
    {
        var tenant = await fixture.Factory.CreateTenantAsync();
        var admin = await fixture.Factory.CreateUserAsync(tenant);
        await fixture.Factory.MakeAdministratorAsync(tenant.Id, admin);
        await fixture.Factory.SetModuleAsync(tenant.Id, Logistics, true);
        await fixture.SetTestModuleAsync(tenant.Id);
        return (tenant, admin);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = fixture.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<AutomationRuleDetailDto> CreateRuleAsync(HttpClient client, CreateAutomationRuleRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/automation/rules", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AutomationRuleDetailDto>())!;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(int Rules, int Versions, int Audit)> CountAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        return (
            await db.AutomationRules.CountAsync(),
            await db.AutomationRuleVersions.CountAsync(),
            await db.AuditLog.CountAsync(a => a.EntityKey == CoreModule.AuditedEntityKeys.AutomationRule));
    }
}

/// <summary>
/// Gerçek uygulamada henüz aksiyon beyan eden modül yoktur (CRM aksiyonları ADR-0027 Dilim 3). Bu test modülü yalnızca test
/// sürecinde katalogu genişletir: <see cref="ItemEntity"/> kural yazılan kayıt türü, <see cref="NoteAction"/> işleyicisi
/// <see cref="AutomationProbe"/> üzerinden ne gördüğünü kaydeder ve sonucu testin belirlediği gibi verir.
/// </summary>
public sealed class AutomationTestModule : IModule
{
    public const string ModuleKey = "autotest";
    public const string ItemEntity = "autotest.item";
    public const string NoteAction = "autotest.note.add";
    public const string SensitiveAction = "autotest.note.sensitive";
    public const string WritePermission = "autotest.notes.write";
    public const string ItemsRead = "autotest.items.read";
    public const string MarginRead = "autotest.margin.read";
    public const string ActivitiesRelation = "autotest.item.activities";

    public string Key => ModuleKey;

    public string DisplayName => "Otomasyon testi";

    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(WritePermission, "Not ekler", "Notlar"),
        new(ItemsRead, "Test kayıtlarını görür", "Notlar"),
        new(MarginRead, "Marj alanını görür", "Notlar"),
    ];

    public AutomationCapabilities Automation { get; } = new()
    {
        Entities = [new(ItemEntity, "Test kaydı", ItemsRead) { RecordSource = typeof(Source) }],
        Events =
        [
            new("autotest.item.created", ItemEntity, DomainEventTypes.RecordCreated, "Kayıt oluşturuldu"),
            new("autotest.item.updated", ItemEntity, DomainEventTypes.RecordUpdated, "Kayıt güncellendi"),
            new("autotest.item.escalated", ItemEntity, "autotest.item.escalated", "Kayıt yükseltildi"),
        ],
        Fields =
        [
            new(ItemEntity, "status", "Durum", AutomationFieldType.Choice) { Options = [new("open", "Açık"), new("hold", "Beklemede"), new("closed", "Kapalı")] },
            new(ItemEntity, "title", "Başlık", AutomationFieldType.Text),
            new(ItemEntity, "margin", "Marj", AutomationFieldType.Number) { ReadPermission = MarginRead },
            new(ItemEntity, "due", "Vade", AutomationFieldType.Date),
        ],
        Relations =
        [
            new(ActivitiesRelation, ItemEntity, "autotest.activity", "Aktiviteler", [new("autotest.activity", "kind", "Tür", AutomationFieldType.Text)], typeof(ActivityResolver)),
        ],
        Actions =
        [
            AutomationActionRegistration.For<Handler>(new(
                NoteAction, "Not ekle", "Kayda not ekler.", [new AutomationParameter("text", "Not", AutomationFieldType.Text)],
                [], [], AutomationSideEffect.Write, SupportsDryRun: false)),
            AutomationActionRegistration.For<Handler>(new(
                SensitiveAction, "Yetkili not ekle", "Yazma izni isteyen not.", [new AutomationParameter("text", "Not", AutomationFieldType.Text)],
                [WritePermission], [], AutomationSideEffect.Write, SupportsDryRun: false)),
        ],
    };

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<Handler>();
        services.AddScoped<Source>();
        services.AddScoped<ActivityResolver>();
    }

    /// <summary>Test kayıt kaynağı: kayıtları <see cref="AutomationRecords"/>'tan, aktif tenant ile süzerek verir. Çağrılar <see cref="Source.Calls"/>'ta sayılır.</summary>
    public sealed class Source(ITenantContextAccessor tenantAccessor) : IAutomationRecordSource
    {
        public static int Calls;

        public static Exception? Fail;

        public Task<AutomationScanPage> ScanAsync(AutomationScanQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (Fail is { } failure)
            {
                throw failure;
            }

            var tenant = tenantAccessor.Current!.TenantId;
            var ids = AutomationRecords.Scan(tenant, query.EntityKey, query)
                .Where(id => query.AfterRecordId is not { } after || id.CompareTo(after) > 0)
                .Order().Take(query.PageSize).ToList();
            return Task.FromResult(new AutomationScanPage(ids));
        }
    }

    public sealed class ActivityResolver : IAutomationRelationResolver
    {
        public Task<int> CountAsync(AutomationRelationQuery query, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    public sealed class Handler(IServiceProvider services) : IAutomationAction
    {
        public async Task<AutomationActionResult> ExecuteAsync(AutomationActionRequest request, AutomationCallContext context, CancellationToken cancellationToken)
        {
            var tenant = services.GetRequiredService<ITenantContextAccessor>().Current!.TenantId;
            var access = services.GetRequiredService<ICurrentAccess>();
            var handling = services.GetRequiredService<EventHandlingScope>();
            AutomationProbe.Record(new AutomationCall(
                tenant,
                request,
                context,
                await access.GetPermissionsAsync(cancellationToken),
                await access.GetScopeAsync(cancellationToken),
                handling.Subscription,
                handling.Event?.Id,
                handling.Event?.CorrelationId,
                handling.Event?.Depth));

            if (AutomationProbe.Delay > TimeSpan.Zero)
            {
                await Task.Delay(AutomationProbe.Delay, cancellationToken);
            }

            return AutomationProbe.Behavior?.Invoke(request) ?? new AutomationActionResult(AutomationActionStatus.Succeeded, "Not eklendi.");
        }
    }
}

/// <summary>Bir aksiyon çağrısında işleyicinin gördükleri: istek, bağlam ve çalıştığı kimlik.</summary>
public sealed record AutomationCall(
    Guid Tenant,
    AutomationActionRequest Request,
    AutomationCallContext Context,
    IReadOnlySet<string> Permissions,
    RecordScope Scope,
    string? Subscription,
    Guid? ScopeEventId,
    string? EventCorrelation,
    int? EventDepth);

/// <summary>Test aksiyonunun ortak durumu; çağrılar tenant'a göre ayrılır, davranış testin başında sıfırlanır.</summary>
public static class AutomationProbe
{
    private static readonly ConcurrentQueue<AutomationCall> Calls = new();

    /// <summary>Doluysa işleyici sonucu buradan alır (istisna fırlatabilir); boşsa başarılı döner.</summary>
    public static Func<AutomationActionRequest, AutomationActionResult>? Behavior { get; set; }

    public static TimeSpan Delay { get; set; }

    public static void Record(AutomationCall call) => Calls.Enqueue(call);

    public static IReadOnlyList<AutomationCall> CallsOf(Guid tenantId) => [.. Calls.Where(c => c.Tenant == tenantId)];

    public static int Attempts(Guid tenantId) => Calls.Count(c => c.Tenant == tenantId);

    public static void Reset()
    {
        Behavior = null;
        Delay = TimeSpan.Zero;
    }
}

/// <summary>Test kayıt türünün kayıtları (tenant'a bağlı). Gerçek bir kayıt deposu yoktur; <see cref="FakeRecordReader"/> bunları okur.</summary>
public static class AutomationRecords
{
    private static readonly ConcurrentDictionary<Guid, (Guid Tenant, string Entity, JsonObject Values, DateTimeOffset? LastActivity)> Store = new();

    public static Guid Add(Guid tenantId, string entity, string status, string? title, decimal? margin = null, DateOnly? due = null, DateTimeOffset? lastActivity = null)
    {
        var id = Guid.NewGuid();
        Store[id] = (
            tenantId, entity,
            new JsonObject { ["status"] = status, ["title"] = title, ["margin"] = margin, ["due"] = due?.ToString("yyyy-MM-dd") },
            lastActivity);
        return id;
    }

    /// <summary>Kayıt kaynağının sorgusuna uyan kayıt kimlikleri (aktif tenant ve kayıt türü ile).</summary>
    public static IEnumerable<Guid> Scan(Guid tenantId, string entity, AutomationScanQuery query) =>
        Store.Where(p => p.Value.Tenant == tenantId && p.Value.Entity == entity && query.Kind switch
        {
            AutomationScanKind.Scheduled => true,
            AutomationScanKind.DateRelative => p.Value.Values["due"]?.ToString() == query.TargetDate?.ToString("yyyy-MM-dd"),
            AutomationScanKind.Inactivity => p.Value.LastActivity is null || p.Value.LastActivity < query.InactiveBefore,
            _ => false,
        }).Select(p => p.Key);

    public static RecordData? Find(Guid tenantId, string entity, Guid id) =>
        Store.TryGetValue(id, out var found) && found.Tenant == tenantId && found.Entity == entity
            ? new RecordData(entity, id, 1, (JsonObject)found.Values.DeepClone())
            : null;
}

public sealed class FakeRecordReader(ITenantContextAccessor tenantAccessor) : IAutomationRecordReader
{
    public Task<RecordResult<RecordData>> ReadAsync(RecordRef record, CancellationToken cancellationToken) =>
        Task.FromResult(
            tenantAccessor.Current is { } tenant && AutomationRecords.Find(tenant.TenantId, record.EntityKey, record.Id) is { } found
                ? RecordResult<RecordData>.Ok(found)
                : RecordResult<RecordData>.Fail(RecordErrorCode.NotFound, "Kayıt bulunamadı."));
}

/// <summary>
/// Test süreci için genişletilmiş katalogla çalışan API. Tenant ve kullanıcılar <see cref="Factory"/> ile (aynı veritabanı)
/// kurulur; HTTP istemcisi katalogu <see cref="AutomationTestModule"/> ile genişleten ikinci konaktan gelir. İkinci konakta saat
/// elle ilerler (<see cref="Time"/>), kayıtlar <see cref="FakeRecordReader"/>'dan okunur ve loglar <see cref="Logs"/>'ta toplanır.
/// </summary>
public sealed class AutomationFixture : IDisposable
{
    public AutomationFixture()
    {
        Factory = new ModulixApiFactory();
        Extended = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ModuleCatalog>();
                services.AddSingleton<IModule>(new AutomationTestModule());
                services.AddScoped<AutomationTestModule.Handler>();
                services.AddScoped<AutomationTestModule.Source>();
                services.AddScoped<AutomationTestModule.ActivityResolver>();
                services.AddSingleton(sp => new ModuleCatalog(sp.GetServices<IModule>()));
                services.RemoveAll<IAutomationRecordReader>();
                services.AddScoped<IAutomationRecordReader, FakeRecordReader>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Time);
            });
        });
    }

    public ModulixApiFactory Factory { get; }

    public WebApplicationFactory<Program> Extended { get; }

    public MutableTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public CapturingLoggerProvider Logs { get; } = new();

    public HttpClient CreateBrowserClient() =>
        Extended.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        });

    // Roller test modülünün izinlerini de içerebilsin diye ikinci konakta (genişletilmiş katalog) kurulur.
    public async Task CreateRoleAsync(Guid tenantId, IReadOnlyCollection<string> permissions, params ModulixUser[] assignTo)
    {
        using var scope = Extended.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current = new TenantContext(tenantId, Guid.NewGuid(), Guid.NewGuid());
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var role = await roles.CreateAsync($"Rol {Guid.NewGuid():N}"[..20], permissions, CancellationToken.None, scope: RecordScope.All);
        foreach (var user in assignTo)
        {
            var membershipId = await Factory.GetMembershipIdAsync(user.Id, tenantId);
            Assert.True(await roles.SetMembershipRolesAsync(membershipId, [role.Id], CancellationToken.None));
        }
    }

    public async Task SetTestModuleAsync(Guid tenantId, bool enabled = true)
    {
        using var scope = Extended.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IModuleEntitlements>()
            .SetEnabledAsync(tenantId, AutomationTestModule.ModuleKey, enabled, CancellationToken.None);
    }

    public void Dispose()
    {
        Extended.Dispose();
        Factory.Dispose();
    }
}

/// <summary>
/// Otomasyon sınıfları süreç genelinde ortak durumu (<see cref="AutomationProbe"/> davranışı ve gecikmesi) kullanır; bu yüzden birbirleriyle
/// paralel koşmaz (her sınıfın kendi konağı ve saati yine ayrıdır).
/// </summary>
[CollectionDefinition(Name)]
public sealed class AutomationTestCollection
{
    public const string Name = "Otomasyon kuralları";
}
