using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Customization;
using Modulix.Shared.Sectors;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0041 (dilim 1-4): firma alt profilleri. Firma yöneticisi alt profil açar, alan ve liste değeri seçenekleri ekler; etkinleştirince seçenekler
/// firmaya yalnızca eklenir. Pasife alınca eklenenler kalır; uygulanmış seçenek değişmez; başka firma görmez; yetkisiz kullanıcı yazamaz.
/// </summary>
public class SubProfileTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Url = "/api/customization/sub-profiles";

    [Fact]
    public async Task Admin_builds_a_sub_profile_previews_activates_it_and_the_options_reach_the_tenant_without_duplicates()
    {
        var (tenant, admin, _) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        // Firmanın "Güzergâh" alanı zaten var: seçenek aynı adlı olduğundan ikinci alan açılmaz.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/customization/fields", new SaveCustomFieldRequest(
            CrmEntityKeys.Opportunity, "güzergâh", CustomFieldType.Text))).StatusCode);

        var created = await CreateAsync(client, "Kara taşımacılığı");
        Assert.Equal("kara_tasimaciligi", created.Code);
        Assert.Equal(SubProfileStatuses.Draft, created.Status);

        await AddFieldAsync(client, created.Id, "Güzergâh", CustomFieldType.Text);
        await AddFieldAsync(client, created.Id, "Araç tipi", CustomFieldType.Choice, required: true, choices: ["Tır", "Kamyon"]);
        var withOptions = await AddListValueAsync(client, created.Id, "Ön görüşme");
        Assert.Equal(3, withOptions.Options.Count);
        Assert.All(withOptions.Options, o => Assert.Null(o.AppliedAt));

        // Önizleme yazmaz: yalnızca yeni alan ve değer eklenecek görünür.
        var preview = (await client.GetFromJsonAsync<ProfilePlanDto>($"{Url}/{created.Id}/apply/preview"))!;
        Assert.Equal(["Araç tipi"], preview.Fields.Select(f => f.Label));
        Assert.Equal(["Ön görüşme"], preview.ListValues.Select(v => v.Label));
        Assert.Equal(["güzergâh"], (await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!.Select(f => f.Label));

        var active = await SetStatusAsync(client, created.Id, SubProfileStatuses.Active, withOptions.Revision);
        Assert.Equal(SubProfileStatuses.Active, active.Status);
        Assert.NotNull(active.ActivatedAt);
        Assert.All(active.Options, o => Assert.NotNull(o.AppliedAt));

        var fields = (await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!;
        Assert.Equal(["güzergâh", "Araç tipi"], fields.Select(f => f.Label));
        var vehicleType = fields.Single(f => f.Label == "Araç tipi");
        Assert.True(vehicleType.Required);
        Assert.Equal(["Tır", "Kamyon"], vehicleType.Options.Select(o => o.Label));
        var stages = (await client.GetFromJsonAsync<List<ChoiceListDto>>("/api/choice-lists"))!.Single(l => l.Key == CrmLists.OpportunityStage);
        Assert.Contains(stages.Values, v => v.Label == "Ön görüşme" && v.Category == CrmLists.StageCategories.Open);

        // Etkin alt profile eklenen seçenek hemen uygulanır.
        var later = await AddFieldAsync(client, created.Id, "Yük tipi", CustomFieldType.Text);
        Assert.Equal(0, later.PendingCount);
        Assert.Contains(
            (await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!, f => f.Label == "Yük tipi");

        // Pasife alma eklenenleri silmez; yeni seçenek bekler, yeniden etkinleştirince uygulanır.
        var inactive = await SetStatusAsync(client, created.Id, SubProfileStatuses.Inactive, later.Revision);
        Assert.Equal(3, (await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!.Count);
        Assert.Equal(SubProfileStatuses.Inactive, inactive.Status);
        var pending = await AddFieldAsync(client, created.Id, "Teslim notu", CustomFieldType.Text);
        Assert.Equal(1, pending.PendingCount);
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!, f => f.Label == "Teslim notu");
        var reactivated = await SetStatusAsync(client, created.Id, SubProfileStatuses.Active, pending.Revision);
        Assert.Equal(0, reactivated.PendingCount);
        Assert.Contains((await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!, f => f.Label == "Teslim notu");
        Assert.Equal(inactive.Id, reactivated.Id);

        var list = (await client.GetFromJsonAsync<List<SubProfileDto>>(Url))!;
        Assert.Equal(created.Id, Assert.Single(list).Id);
        Assert.Equal(5, list[0].OptionCount);
        Assert.Empty(list[0].Options);

        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityKey == "core.sub-profile").OrderBy(a => a.OccurredAt).ToListAsync();
        Assert.Equal(AuditAction.Created, audit[0].Action);
        Assert.True(audit.Count >= 6);
        Assert.All(audit, a => Assert.Equal(admin.Id, a.UserId));
    }

    [Fact]
    public async Task Invalid_input_conflicts_and_unknown_ids_are_rejected_without_changing_anything()
    {
        var (_, admin, _) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var created = await CreateAsync(client, "Gümrüklü antrepo");

        // Ad zorunlu ve tekil; bilinmeyen faaliyet reddedilir.
        Assert.Contains("name", await ProblemAsync(await client.PostAsJsonAsync(Url, new SaveSubProfileRequest(" ", null, null))));
        Assert.Contains("name", await ProblemAsync(await client.PostAsJsonAsync(Url, new SaveSubProfileRequest("gümrüklü ANTREPO", null, null))));
        Assert.Contains("activityCode", await ProblemAsync(await client.PostAsJsonAsync(Url, new SaveSubProfileRequest("Deniz", null, "yok-boyle-faaliyet"))));

        // Seçenek doğrulaması: bilinmeyen kayıt türü, boş ad, seçim tipli alanda seçenek yok.
        Assert.Contains("targetKey", await ProblemAsync(await PostOptionAsync(client, created.Id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, "yok.entity", "Alan", CustomFieldType.Text))));
        Assert.Contains("label", await ProblemAsync(await PostOptionAsync(client, created.Id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, " ", CustomFieldType.Text))));
        Assert.Contains("choices", await ProblemAsync(await PostOptionAsync(client, created.Id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, "Tip", CustomFieldType.Choice))));
        Assert.Contains("category", await ProblemAsync(await PostOptionAsync(client, created.Id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.ListValue, CrmLists.OpportunityStage, "Değer", Category: "yok"))));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostOptionAsync(client, created.Id, new SaveSubProfileOptionRequest("Bilinmeyen", "x", "y"))).StatusCode);

        // Aynı seçenek iki kez eklenmez.
        var first = await AddFieldAsync(client, created.Id, "Antrepo kodu", CustomFieldType.Text);
        Assert.Contains("label", await ProblemAsync(await PostOptionAsync(client, created.Id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, "antrepo KODU", CustomFieldType.Text))));
        Assert.Single((await GetAsync(client, created.Id)).Options);

        // Eski sürümle yazma 409; taslak doğrudan pasife alınamaz; geçersiz durum 400.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{Url}/{created.Id}", new SaveSubProfileRequest("Yeni ad", null, null, created.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{Url}/{created.Id}/status", new SetSubProfileStatusRequest(SubProfileStatuses.Inactive, first.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Url}/{created.Id}/status", new SetSubProfileStatusRequest("Uçtu", first.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"{Url}/{created.Id}/apply", null)).StatusCode);

        // Doğru sürümle düzenleme olur; ad değişir, kod kalır.
        var renamed = await client.PutAsJsonAsync($"{Url}/{created.Id}", new SaveSubProfileRequest("Gümrüklü antrepo 2", "Açıklama", null, first.Revision));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var renamedView = (await renamed.Content.ReadFromJsonAsync<SubProfileDto>())!;
        Assert.Equal(("Gümrüklü antrepo 2", created.Code), (renamedView.Name, renamedView.Code));

        // Uygulanmış seçenek değişmez ve kaldırılmaz; uygulanmamış olan kaldırılır.
        var active = await SetStatusAsync(client, created.Id, SubProfileStatuses.Active, renamedView.Revision);
        var applied = active.Options.Single();
        Assert.NotNull(applied.AppliedAt);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{Url}/{created.Id}/options/{applied.Id}", new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, "Başka ad", CustomFieldType.Text))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{Url}/{created.Id}/options/{applied.Id}")).StatusCode);
        var inactive = await SetStatusAsync(client, created.Id, SubProfileStatuses.Inactive, active.Revision);
        var waiting = await AddFieldAsync(client, created.Id, "Bekleyen", CustomFieldType.Text);
        var waitingOption = waiting.Options.Single(o => o.Label == "Bekleyen");
        var edited = await client.PutAsJsonAsync($"{Url}/{created.Id}/options/{waitingOption.Id}", new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, "Bekleyen 2", CustomFieldType.Number));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(CustomFieldType.Number, (await edited.Content.ReadFromJsonAsync<SubProfileDto>())!.Options.Single(o => o.Id == waitingOption.Id).FieldType);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"{Url}/{created.Id}/options/{waitingOption.Id}")).StatusCode);
        Assert.Single((await GetAsync(client, created.Id)).Options);
        Assert.NotEqual(inactive.Revision, (await GetAsync(client, created.Id)).Revision);

        // Bilinmeyen kimlikler 404.
        var missing = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Url}/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Url}/{missing}/apply/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Url}/{created.Id}/options/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"{Url}/{missing}/options", new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, "Alan", CustomFieldType.Text))).StatusCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_change_a_sub_profile()
    {
        var (_, adminA, _) = await SetUpAsync();
        var (_, adminB, _) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        var created = await CreateAsync(clientA, "Yalnız A'nın");
        await AddFieldAsync(clientA, created.Id, "Gizli alan", CustomFieldType.Text);

        Assert.Empty((await clientB.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"{Url}/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PutAsJsonAsync($"{Url}/{created.Id}", new SaveSubProfileRequest("Ele geçirildi", null, null, created.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PostAsJsonAsync($"{Url}/{created.Id}/status", new SetSubProfileStatusRequest(SubProfileStatuses.Active, created.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"{Url}/{created.Id}/apply/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostOptionAsync(clientB, created.Id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, "Sızdı", CustomFieldType.Text))).StatusCode);

        // B aynı adı ve aynı kodu bağımsız kullanabilir; A'nın verisi değişmez.
        var own = await CreateAsync(clientB, "Yalnız A'nın");
        Assert.Equal(created.Code, own.Code);
        Assert.Single((await GetAsync(clientA, created.Id)).Options);
        Assert.Empty((await clientB.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!);
    }

    [Fact]
    public async Task Users_without_the_customization_permission_cannot_read_or_write_sub_profiles()
    {
        var (_, admin, rep) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        using var repClient = await LoginAsync(rep);
        var created = await CreateAsync(adminClient, "Yetkili işi");

        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync($"{Url}/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.PostAsJsonAsync(Url, new SaveSubProfileRequest("İzinsiz", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.PutAsJsonAsync($"{Url}/{created.Id}", new SaveSubProfileRequest("İzinsiz", null, null, created.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.PostAsJsonAsync($"{Url}/{created.Id}/status", new SetSubProfileStatusRequest(SubProfileStatuses.Active, created.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync($"{Url}/{created.Id}/apply/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostOptionAsync(repClient, created.Id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, "İzinsiz", CustomFieldType.Text))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.DeleteAsync($"{Url}/{created.Id}/options/{Guid.NewGuid()}")).StatusCode);

        // Hiçbir şey değişmedi.
        Assert.Single((await adminClient.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
        Assert.Empty((await GetAsync(adminClient, created.Id)).Options);
    }

    [Fact]
    public async Task Admin_adds_a_starter_as_a_draft_and_its_options_reach_the_tenant_only_when_activated()
    {
        var (_, admin, _) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        var starters = (await client.GetFromJsonAsync<List<SubProfileStarterDto>>($"{Url}/starters"))!;
        Assert.Equal(["kara-tasimaciligi", "gumruklu-antrepo", "deniz-tasimaciligi"], starters.Select(s => s.Key));
        Assert.All(starters, s =>
        {
            Assert.False(s.Added);
            Assert.Empty(s.MissingModules);
            Assert.NotEmpty(s.Options);
        });
        Assert.Equal(["Güzergâh", "Araç tipi", "Yük tipi"], starters[0].Options);

        var response = await client.PostAsync($"{Url}/starters/kara-tasimaciligi", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<SubProfileDto>())!;
        Assert.Equal(("kara-tasimaciligi", "Kara taşımacılığı", SubProfileStatuses.Draft), (created.Code, created.Name, created.Status));
        Assert.Equal(["Güzergâh", "Araç tipi", "Yük tipi"], created.Options.Select(o => o.Label));
        Assert.All(created.Options, o => Assert.Null(o.AppliedAt));
        Assert.Equal(["Tır", "Kamyon", "Kamyonet", "Frigorifik"], created.Options.Single(o => o.Label == "Araç tipi").Choices);

        // Eklemek hiçbir şeyi uygulamaz; kart "Eklendi" olur, diğer hazır alt profiller etkilenmez.
        Assert.Empty((await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!);
        var after = (await client.GetFromJsonAsync<List<SubProfileStarterDto>>($"{Url}/starters"))!;
        Assert.Equal([true, false, false], after.Select(s => s.Added));

        var active = await SetStatusAsync(client, created.Id, SubProfileStatuses.Active, created.Revision);
        Assert.All(active.Options, o => Assert.NotNull(o.AppliedAt));
        Assert.Equal(["Güzergâh", "Araç tipi", "Yük tipi"], (await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!.Select(f => f.Label));
    }

    [Fact]
    public async Task Starter_is_rejected_when_unknown_already_added_named_like_another_or_its_module_is_closed()
    {
        var (_, admin, _) = await SetUpAsync();
        using var client = await LoginAsync(admin);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{Url}/starters/yok", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync($"{Url}/starters/gumruklu-antrepo", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"{Url}/starters/gumruklu-antrepo", null)).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<List<SubProfileDto>>(Url))!);

        // Başka koddan aynı adlı alt profil varsa hazır alt profil eklenmez ve yarım kayıt kalmaz.
        _ = await CreateAsync(client, "Deniz taşımacılığı");
        Assert.Contains("name", await ProblemAsync(await client.PostAsync($"{Url}/starters/deniz-tasimaciligi", null)));
        var names = (await client.GetFromJsonAsync<List<SubProfileDto>>(Url))!.Select(p => p.Name).Order().ToList();
        Assert.Equal(["Deniz taşımacılığı", "Gümrüklü antrepo"], names);
        var deniz = (await client.GetFromJsonAsync<List<SubProfileDto>>(Url))!.Single(p => p.Name == "Deniz taşımacılığı");
        Assert.Equal(0, deniz.OptionCount);

        // CRM kapalı bir firmada hazır alt profiller listelenir ama eklenemez; eksik modül adı söylenir.
        var tenant = await factory.CreateTenantAsync();
        var closedAdmin = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(closedAdmin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        using var closedClient = await LoginAsync(closedAdmin);
        var listed = (await closedClient.GetFromJsonAsync<List<SubProfileStarterDto>>($"{Url}/starters"))!;
        Assert.All(listed, s => Assert.Equal(["CRM"], s.MissingModules));
        Assert.Contains("key", await ProblemAsync(await closedClient.PostAsync($"{Url}/starters/kara-tasimaciligi", null)));
        Assert.Empty((await closedClient.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
    }

    [Fact]
    public async Task A_starter_added_by_one_tenant_is_invisible_to_another_which_can_add_its_own()
    {
        var (_, adminA, _) = await SetUpAsync();
        var (_, adminB, _) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);

        var a = await clientA.PostAsync($"{Url}/starters/deniz-tasimaciligi", null);
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        var createdA = (await a.Content.ReadFromJsonAsync<SubProfileDto>())!;

        Assert.Empty((await clientB.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
        Assert.All((await clientB.GetFromJsonAsync<List<SubProfileStarterDto>>($"{Url}/starters"))!, s => Assert.False(s.Added));
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"{Url}/{createdA.Id}")).StatusCode);

        // B kendi kopyasını ekleyince A'nınkine dokunmaz.
        var b = await clientB.PostAsync($"{Url}/starters/deniz-tasimaciligi", null);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.NotEqual(createdA.Id, (await b.Content.ReadFromJsonAsync<SubProfileDto>())!.Id);
        Assert.Single((await clientA.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
    }

    [Fact]
    public async Task Users_without_the_customization_permission_cannot_list_or_add_starters()
    {
        var (_, admin, rep) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        using var repClient = await LoginAsync(rep);

        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync($"{Url}/starters")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.PostAsync($"{Url}/starters/kara-tasimaciligi", null)).StatusCode);

        Assert.Empty((await adminClient.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
    }

    [Fact]
    public async Task Features_are_added_listed_filtered_by_the_ui_data_and_removed_in_any_status_without_applying_anything()
    {
        var (tenant, admin, _) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var created = await CreateAsync(client, "Kara taşımacılığı");

        var first = await AddFeatureAsync(client, created.Id, "Taşıma türü", "Komple");
        await AddFeatureAsync(client, created.Id, "Taşıma türü", "Parsiyel");
        var detail = await AddFeatureAsync(client, created.Id, "Kapsam", "Yurt içi");
        Assert.Equal(["Taşıma türü: Komple", "Taşıma türü: Parsiyel", "Kapsam: Yurt içi"], detail.Features.Select(f => $"{f.Group}: {f.Value}"));
        Assert.True(detail.Revision > created.Revision);

        // Liste özellikleri de taşır (seçenekler boş kalır); özellik hiçbir şey uygulamaz.
        var listed = Assert.Single((await client.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
        Assert.Equal(3, listed.Features.Count);
        Assert.Empty(listed.Options);
        Assert.Empty((await client.GetFromJsonAsync<List<CustomFieldDto>>("/api/custom-fields"))!);
        Assert.Equal(SubProfileStatuses.Draft, listed.Status);

        // Etkin ve pasif alt profilde de eklenip kaldırılır.
        _ = await AddFieldAsync(client, created.Id, "Güzergâh", CustomFieldType.Text);
        var active = await SetStatusAsync(client, created.Id, SubProfileStatuses.Active, (await GetAsync(client, created.Id)).Revision);
        var whileActive = await AddFeatureAsync(client, created.Id, "Sıcaklık", "Kontrollü");
        Assert.Equal(4, whileActive.Features.Count);
        var inactive = await SetStatusAsync(client, created.Id, SubProfileStatuses.Inactive, whileActive.Revision);
        var removed = await client.DeleteAsync($"{Url}/{created.Id}/features/{first.Features[0].Id}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var after = (await removed.Content.ReadFromJsonAsync<SubProfileDto>())!;
        Assert.Equal(["Taşıma türü: Parsiyel", "Kapsam: Yurt içi", "Sıcaklık: Kontrollü"], after.Features.Select(f => $"{f.Group}: {f.Value}"));
        Assert.Equal((SubProfileStatuses.Inactive, active.Id), (after.Status, inactive.Id));

        // Hazır alt profil özellikleriyle gelir ve galeride özetlenir.
        var starters = (await client.GetFromJsonAsync<List<SubProfileStarterDto>>($"{Url}/starters"))!;
        Assert.Equal(["Taşıma türü: Komple, Parsiyel", "Kapsam: Yurt içi, Uluslararası"], starters.Single(s => s.Key == "kara-tasimaciligi").Features);
        Assert.Empty(starters.Single(s => s.Key == "gumruklu-antrepo").Features);
        var deniz = (await (await client.PostAsync($"{Url}/starters/deniz-tasimaciligi", null)).Content.ReadFromJsonAsync<SubProfileDto>())!;
        Assert.Equal(["Taşıma türü: Tam konteyner", "Taşıma türü: Parsiyel konteyner", "Taşıma türü: Dökme", "Kapsam: Uluslararası"], deniz.Features.Select(f => $"{f.Group}: {f.Value}"));

        // Özellik değişiklikleri denetim kaydındadır.
        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityKey == "core.sub-profile" && a.RecordId == created.Id).ToListAsync();
        Assert.True(audit.Count(a => a.Changes.Contains("features")) >= 5);
    }

    [Fact]
    public async Task Invalid_duplicate_and_unknown_features_are_rejected_without_changing_anything()
    {
        var (_, admin, _) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var created = await CreateAsync(client, "Deniz taşımacılığı");
        await AddFeatureAsync(client, created.Id, "Taşıma türü", "Komple");

        Assert.Contains("group", await ProblemAsync(await PostFeatureAsync(client, created.Id, " ", "Komple")));
        Assert.Contains("group", await ProblemAsync(await PostFeatureAsync(client, created.Id, new string('a', 61), "Komple")));
        Assert.Contains("value", await ProblemAsync(await PostFeatureAsync(client, created.Id, "Kapsam", "")));
        Assert.Contains("value", await ProblemAsync(await PostFeatureAsync(client, created.Id, "Kapsam", new string('a', 101))));
        Assert.Contains("value", await ProblemAsync(await PostFeatureAsync(client, created.Id, "taşıma TÜRÜ", "komple")));
        Assert.Single((await GetAsync(client, created.Id)).Features);

        // Aynı değer başka özellik altında geçerlidir.
        Assert.Equal(HttpStatusCode.OK, (await PostFeatureAsync(client, created.Id, "Kapsam", "Komple")).StatusCode);

        // Bilinmeyen alt profil ve özellik 404.
        var missing = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await PostFeatureAsync(client, missing, "Kapsam", "Yurt içi")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Url}/{created.Id}/features/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Url}/{missing}/features/{missing}")).StatusCode);
        Assert.Equal(2, (await GetAsync(client, created.Id)).Features.Count);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_change_the_features_of_a_sub_profile()
    {
        var (_, adminA, _) = await SetUpAsync();
        var (_, adminB, _) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        var created = await CreateAsync(clientA, "Yalnız A'nın");
        var withFeature = await AddFeatureAsync(clientA, created.Id, "Kapsam", "Yurt içi");

        Assert.Empty((await clientB.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
        Assert.Equal(HttpStatusCode.NotFound, (await PostFeatureAsync(clientB, created.Id, "Kapsam", "Sızdı")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.DeleteAsync($"{Url}/{created.Id}/features/{withFeature.Features[0].Id}")).StatusCode);

        // B aynı adda kendi alt profilini ve aynı özelliği bağımsız ekleyebilir; A'nınki değişmez.
        var own = await CreateAsync(clientB, "Yalnız A'nın");
        Assert.Single((await AddFeatureAsync(clientB, own.Id, "Kapsam", "Yurt içi")).Features);
        Assert.Equal(["Kapsam: Yurt içi"], (await GetAsync(clientA, created.Id)).Features.Select(f => $"{f.Group}: {f.Value}"));
    }

    [Fact]
    public async Task Users_without_the_customization_permission_cannot_add_or_remove_features()
    {
        var (_, admin, rep) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        using var repClient = await LoginAsync(rep);
        var created = await CreateAsync(adminClient, "Yetkili işi");
        var withFeature = await AddFeatureAsync(adminClient, created.Id, "Kapsam", "Yurt içi");

        Assert.Equal(HttpStatusCode.Forbidden, (await PostFeatureAsync(repClient, created.Id, "Kapsam", "İzinsiz")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.DeleteAsync($"{Url}/{created.Id}/features/{withFeature.Features[0].Id}")).StatusCode);

        Assert.Equal(["Kapsam: Yurt içi"], (await GetAsync(adminClient, created.Id)).Features.Select(f => $"{f.Group}: {f.Value}"));
    }

    [Fact]
    public async Task Rules_enter_the_effective_rules_only_while_the_sub_profile_is_active_and_the_tenant_can_override_them()
    {
        var (_, admin, _) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var created = await CreateAsync(client, "Deniz taşımacılığı");

        // Taslakta kural eklenir ama etkin kurallara girmez.
        var withRule = await AddRuleAsync(client, created.Id, "Başlık uyarısı", RuleTitle, "Warn", "Başlığı kontrol edin.");
        var rule = Assert.Single(withRule.Rules);
        Assert.Equal($"sp.{created.Code.Replace('_', '-')}.baslik-uyarisi", rule.Rule.Key);
        Assert.False(rule.Rule.Locked);
        Assert.DoesNotContain(await EffectiveKeysAsync(client), key => key == rule.Rule.Key);

        // Önizleme: çakışma yok.
        var preview = (await client.GetFromJsonAsync<SubProfileRulePreviewDto>($"{Url}/{created.Id}/rules/preview"))!;
        Assert.True(preview.CanActivate);
        Assert.Equal([rule.Rule.Key], preview.Rules.Select(r => r.Key));

        // Etkinleştirince katmana girer; etkinken eklenen kural hemen girer.
        var active = await SetStatusAsync(client, created.Id, "Active", withRule.Revision);
        var withSecond = await AddRuleAsync(client, active.Id, "Öncelik uyarısı", RulePriority, "Warn", "Önceliği kontrol edin.");
        var keys = await EffectiveKeysAsync(client);
        Assert.Contains(rule.Rule.Key, keys);
        Assert.Contains(withSecond.Rules[1].Rule.Key, keys);

        // Düzenleme anahtarı korur; kural değişir.
        var update = await client.PutAsJsonAsync($"{Url}/{created.Id}/rules/{rule.Id}",
            new SaveSubProfileRuleRequest("Başlık uyarısı (yeni)", Rule("başka", RuleTitle, "Warn", "Yeni ileti.")));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<SubProfileDto>())!.Rules.Single(r => r.Id == rule.Id);
        Assert.Equal(rule.Rule.Key, updated.Rule.Key);
        Assert.Equal("Yeni ileti.", updated.Rule.Message);
        Assert.Equal("Başlık uyarısı (yeni)", updated.Name);

        // Firma uyarlaması alt profil kuralını kapatabilir (alt profil katmanı uyarlamanın altındadır).
        var adaptation = (await client.GetFromJsonAsync<TenantRuleAdaptationDto>("/api/rules/adaptation"))!;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/rules/adaptation",
            new SaveRuleAdaptationRequest([], [rule.Rule.Key], adaptation.Revision))).StatusCode);
        keys = await EffectiveKeysAsync(client);
        Assert.DoesNotContain(rule.Rule.Key, keys);
        Assert.Contains(withSecond.Rules[1].Rule.Key, keys);

        // Pasife alınınca kurallar katmandan çıkar; kural ve alt profil kalır.
        var current = await GetAsync(client, created.Id);
        var inactive = await SetStatusAsync(client, created.Id, "Inactive", current.Revision);
        Assert.Equal(2, inactive.Rules.Count);
        Assert.DoesNotContain(withSecond.Rules[1].Rule.Key, await EffectiveKeysAsync(client));

        // Kural her durumda kaldırılabilir.
        var removed = await client.DeleteAsync($"{Url}/{created.Id}/rules/{rule.Id}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Single((await GetAsync(client, created.Id)).Rules);
    }

    [Fact]
    public async Task Invalid_unknown_and_conflicting_rules_are_rejected_without_changing_anything()
    {
        var (tenant, admin, _) = await SetUpAsync();
        using var client = await LoginAsync(admin);
        var first = await CreateAsync(client, "Kara taşımacılığı");
        var second = await CreateAsync(client, "Deniz taşımacılığı");

        // Doğrulama: bilinmeyen alan, eksik ileti, boş ad, tekrar eden ad.
        Assert.Contains("rule", await ProblemAsync(await PostRuleAsync(client, first.Id, "Yok", Rule("x", "crm.opportunity.yok", "Hide"))));
        Assert.Contains("message", await ProblemAsync(await PostRuleAsync(client, first.Id, "İletisiz", Rule("x", RuleTitle, "Warn"))));
        Assert.Contains("name", await ProblemAsync(await PostRuleAsync(client, first.Id, " ", Rule("x", RuleTitle, "Hide"))));
        Assert.Contains("effect", await ProblemAsync(await PostRuleAsync(client, first.Id, "Etkisiz", Rule("x", RuleTitle, "Uçur"))));
        Assert.Empty((await GetAsync(client, first.Id)).Rules);

        // Anahtar ve kilit istemciden gelmez: sunucu anahtar üretir, alt profil kuralı kilitli olmaz.
        var hidden = await PostRuleAsync(client, first.Id, "Başlık gizli", Rule("hack", RuleTitle, "Hide", locked: true));
        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        var hiddenRule = (await hidden.Content.ReadFromJsonAsync<SubProfileDto>())!.Rules.Single();
        Assert.StartsWith("sp.", hiddenRule.Rule.Key, StringComparison.Ordinal);
        Assert.False(hiddenRule.Rule.Locked);
        Assert.Contains("name", await ProblemAsync(await PostRuleAsync(client, first.Id, "başlık gizli", Rule("x", RuleTitle, "Hide"))));

        // İkinci alt profil aynı alanı koşulsuz zorunlu yapar: ilki etkinken ikincisi etkinleştirilemez.
        var requiredSecond = await AddRuleAsync(client, second.Id, "Başlık zorunlu", RuleTitle, "Required");
        var firstActive = await SetStatusAsync(client, first.Id, "Active", (await GetAsync(client, first.Id)).Revision);
        var blocked = await client.PostAsJsonAsync($"{Url}/{second.Id}/status", new SetSubProfileStatusRequest("Active", requiredSecond.Revision));
        Assert.Contains("gizli", await ProblemAsync(blocked));
        Assert.Equal("Draft", (await GetAsync(client, second.Id)).Status);
        var preview = (await client.GetFromJsonAsync<SubProfileRulePreviewDto>($"{Url}/{second.Id}/rules/preview"))!;
        Assert.False(preview.CanActivate);
        Assert.NotEmpty(preview.Conflicts);

        // Etkin alt profile çakışan kural da eklenemez; kural kaydedilmez.
        var third = await CreateAsync(client, "Hava taşımacılığı");
        var thirdActive = await SetStatusAsync(client, third.Id, "Active", third.Revision);
        Assert.Contains("gizli", await ProblemAsync(await PostRuleAsync(client, thirdActive.Id, "Başlık zorunlu", Rule("x", RuleTitle, "Required"))));
        Assert.Empty((await GetAsync(client, third.Id)).Rules);

        // Çakışmayı doğuran alt profil pasife alınınca ikincisi etkinleşir.
        await SetStatusAsync(client, first.Id, "Inactive", firstActive.Revision);
        Assert.Equal("Active", (await SetStatusAsync(client, second.Id, "Active", (await GetAsync(client, second.Id)).Revision)).Status);

        // Bilinmeyen alt profil ve kural 404.
        var missing = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await PostRuleAsync(client, missing, "Yok", Rule("x", RuleTitle, "Hide"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Url}/{first.Id}/rules/{missing}", new SaveSubProfileRuleRequest("Yok", Rule("x", RuleTitle, "Hide")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Url}/{first.Id}/rules/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Url}/{missing}/rules/preview")).StatusCode);

        // Kural değişiklikleri denetim kaydındadır.
        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityKey == "core.sub-profile" && a.RecordId == first.Id).ToListAsync();
        Assert.Contains(audit, a => a.Changes.Contains("rules"));
    }

    [Fact]
    public async Task Another_tenant_cannot_see_change_or_inherit_the_rules_of_a_sub_profile()
    {
        var (_, adminA, _) = await SetUpAsync();
        var (_, adminB, _) = await SetUpAsync();
        using var clientA = await LoginAsync(adminA);
        using var clientB = await LoginAsync(adminB);
        var created = await CreateAsync(clientA, "Yalnız A'nın");
        var withRule = await AddRuleAsync(clientA, created.Id, "Başlık uyarısı", RuleTitle, "Warn", "Yalnız A.");
        var ruleKey = withRule.Rules.Single().Rule.Key;
        await SetStatusAsync(clientA, created.Id, "Active", withRule.Revision);
        Assert.Contains(ruleKey, await EffectiveKeysAsync(clientA));

        // B alt profili göremez, değiştiremez; kuralı etkin kurallarında görmez.
        Assert.Empty((await clientB.GetFromJsonAsync<List<SubProfileDto>>(Url))!);
        Assert.Equal(HttpStatusCode.NotFound, (await PostRuleAsync(clientB, created.Id, "Sızdı", Rule("x", RuleTitle, "Hide"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.PutAsJsonAsync($"{Url}/{created.Id}/rules/{withRule.Rules[0].Id}", new SaveSubProfileRuleRequest("Sızdı", Rule("x", RuleTitle, "Hide")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.DeleteAsync($"{Url}/{created.Id}/rules/{withRule.Rules[0].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"{Url}/{created.Id}/rules/preview")).StatusCode);
        Assert.DoesNotContain(ruleKey, await EffectiveKeysAsync(clientB));

        // B aynı adda kendi alt profilini ve kuralını bağımsız ekleyip etkinleştirir; A'nın kuralı değişmez.
        var own = await CreateAsync(clientB, "Yalnız A'nın");
        var ownRule = await AddRuleAsync(clientB, own.Id, "Başlık uyarısı", RuleTitle, "Warn", "B'nin");
        await SetStatusAsync(clientB, own.Id, "Active", ownRule.Revision);
        Assert.Equal("Yalnız A.", (await GetAsync(clientA, created.Id)).Rules.Single().Rule.Message);
        Assert.Equal("B'nin", (await GetAsync(clientB, own.Id)).Rules.Single().Rule.Message);
    }

    [Fact]
    public async Task Users_without_the_customization_permission_cannot_read_or_write_sub_profile_rules()
    {
        var (_, admin, rep) = await SetUpAsync();
        using var adminClient = await LoginAsync(admin);
        using var repClient = await LoginAsync(rep);
        var created = await CreateAsync(adminClient, "Yetkili işi");
        var withRule = await AddRuleAsync(adminClient, created.Id, "Başlık uyarısı", RuleTitle, "Warn", "Yalnız yetkili.");
        var ruleId = withRule.Rules.Single().Id;

        Assert.Equal(HttpStatusCode.Forbidden, (await PostRuleAsync(repClient, created.Id, "İzinsiz", Rule("x", RuleTitle, "Hide"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.PutAsJsonAsync($"{Url}/{created.Id}/rules/{ruleId}", new SaveSubProfileRuleRequest("İzinsiz", Rule("x", RuleTitle, "Hide")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.DeleteAsync($"{Url}/{created.Id}/rules/{ruleId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync($"{Url}/{created.Id}/rules/preview")).StatusCode);

        var unchanged = (await GetAsync(adminClient, created.Id)).Rules.Single();
        Assert.Equal("Yalnız yetkili.", unchanged.Rule.Message);
    }

    private const string RuleTitle = "crm.opportunity.title";
    private const string RulePriority = "crm.opportunity.priority";

    private static FieldRuleDto Rule(string key, string field, string effect, string? message = null, bool locked = false) =>
        new(key, field, effect, null, message, null, locked);

    private static Task<HttpResponseMessage> PostRuleAsync(HttpClient client, Guid id, string name, FieldRuleDto rule) =>
        client.PostAsJsonAsync($"{Url}/{id}/rules", new SaveSubProfileRuleRequest(name, rule));

    private static async Task<SubProfileDto> AddRuleAsync(HttpClient client, Guid id, string name, string field, string effect, string? message = null)
    {
        var response = await PostRuleAsync(client, id, name, Rule("istemci-anahtari", field, effect, message));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubProfileDto>())!;
    }

    private static async Task<List<string>> EffectiveKeysAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<EffectiveRulesDto>("/api/rules/effective"))!.Rules.Select(r => r.Key)];

    private static Task<HttpResponseMessage> PostFeatureAsync(HttpClient client, Guid id, string group, string value) =>
        client.PostAsJsonAsync($"{Url}/{id}/features", new SaveSubProfileFeatureRequest(group, value));

    private static async Task<SubProfileDto> AddFeatureAsync(HttpClient client, Guid id, string group, string value)
    {
        var response = await PostFeatureAsync(client, id, group, value);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubProfileDto>())!;
    }

    private static async Task<SubProfileDto> CreateAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(Url, new SaveSubProfileRequest(name, null, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubProfileDto>())!;
    }

    private static async Task<SubProfileDto> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<SubProfileDto>($"{Url}/{id}"))!;

    private static Task<HttpResponseMessage> PostOptionAsync(HttpClient client, Guid id, SaveSubProfileOptionRequest request) =>
        client.PostAsJsonAsync($"{Url}/{id}/options", request);

    private static async Task<SubProfileDto> AddFieldAsync(
        HttpClient client, Guid id, string label, CustomFieldType type, bool required = false, string[]? choices = null)
    {
        var response = await PostOptionAsync(client, id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.Field, CrmEntityKeys.Opportunity, label, type, required, Choices: choices));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubProfileDto>())!;
    }

    private static async Task<SubProfileDto> AddListValueAsync(HttpClient client, Guid id, string label)
    {
        var response = await PostOptionAsync(client, id, new SaveSubProfileOptionRequest(
            SubProfileOptionKinds.ListValue, CrmLists.OpportunityStage, label, Category: CrmLists.StageCategories.Open));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubProfileDto>())!;
    }

    private static async Task<SubProfileDto> SetStatusAsync(HttpClient client, Guid id, string status, long revision)
    {
        var response = await client.PostAsJsonAsync($"{Url}/{id}/status", new SetSubProfileStatusRequest(status, revision));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubProfileDto>())!;
    }

    private static async Task<string> ProblemAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity, $"Beklenmeyen durum: {response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<(Core.Platform.Tenant Tenant, ModulixUser Admin, ModulixUser Rep)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var rep = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [CompaniesRead, OpportunitiesRead], rep);
        return (tenant, admin, rep);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
