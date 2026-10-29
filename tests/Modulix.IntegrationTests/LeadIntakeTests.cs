using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using Modulix.Shared.Records;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Web formu / entegrasyon ile Lead toplama (herkese açık uç): tenant URL'den sunucuda çözülür, gövdeye güvenilmez; istemci
/// durum/sorumlu/öncelik atayamaz; aynı gönderim tek Lead üretir; yanıt kayıtlı kişi/e-postayı belli etmez; sır tarayıcıya gitmez.
/// </summary>
public class LeadIntakeTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Site = "https://site.test";

    private static readonly string[] AdminPermissions =
    [
        CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite, LeadsRead, LeadsCreate, LeadsUpdate, LeadsAssign, LeadFormsManage,
    ];

    [Fact]
    public async Task Browser_submission_creates_a_lead_in_the_url_tenant_with_server_assigned_fields()
    {
        var setup = await SetUpAsync();
        var stranger = await SetUpAsync();

        var response = await PostAsync(setup, Submission("sub-0000001"), Site);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var leads = (await setup.Admin.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads"))!.Items;
        var lead = Assert.Single(leads);
        Assert.Equal("Web formu: Kaya", lead.Subject.Replace("Mehmet ", string.Empty));
        Assert.Equal(LeadStatus.New, lead.Status);
        Assert.Equal(LeadPriority.Normal, lead.Priority);
        Assert.Equal("WebForm", lead.Source);
        Assert.Equal($"web-form:{setup.Form.FormKey}", lead.ExternalSource);
        Assert.Equal("sub-0000001", lead.ExternalSubmissionId);
        Assert.Equal(setup.DefaultOwner.Id, lead.OwnerId);
        Assert.NotNull(lead.AssignedAt);
        Assert.Equal("kaya@musteri.test", lead.Email);

        // Başka tenant bu Lead'i göremez.
        Assert.Empty((await stranger.Admin.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads"))!.Items);
    }

    [Fact]
    public async Task Tenant_comes_from_the_url_and_client_supplied_tenant_or_privileged_fields_are_rejected()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();

        // Gövdede tenant, sorumlu, durum, öncelik, iş birimi, rol: izinli alan listesinde olmadıkları için reddedilir.
        foreach (var (field, value) in new (string, object)[]
        {
            ("tenantId", b.Tenant.Id), ("ownerId", b.DefaultOwner.Id), ("status", "Converted"), ("priority", "Urgent"),
            ("businessUnitId", Guid.NewGuid()), ("role", "admin"), ("source", "Manual"), ("convertedOpportunityId", Guid.NewGuid()),
        })
        {
            var body = Submission($"sub-{field}-1");
            body[field] = JsonSerializer.SerializeToNode(value);
            var bad = await PostAsync(a, body, Site);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains(field, (await bad.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);
        }

        Assert.Empty((await a.Admin.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads"))!.Items);

        // Başka tenant'ın form kimliği bu tenant'ın adresinde bulunamaz; başlıkla tenant seçilemez.
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/public/lead-forms/{a.Tenant.Slug}/{b.Form.FormKey}")
        {
            Content = JsonContent.Create(Submission("sub-cross-01")),
        };
        request.Headers.Add("Origin", Site);
        request.Headers.Add("X-Tenant-Id", b.Tenant.Id.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await a.Anonymous.SendAsync(request)).StatusCode);
        Assert.Empty((await b.Admin.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads"))!.Items);
    }

    [Fact]
    public async Task Same_submission_is_idempotent_but_the_same_person_may_send_different_requests()
    {
        var setup = await SetUpAsync();

        var first = await PostAsync(setup, Submission("sub-dup-0001"), Site);
        var again = await PostAsync(setup, Submission("sub-dup-0001"), Site);
        var racing = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostAsync(setup, Submission("sub-race-001"), Site)));
        var other = await PostAsync(setup, Submission("sub-dup-0002", message: "Farklı bir ihtiyaç"), Site);

        Assert.All(new[] { first, again, other }.Concat(racing), r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var leads = (await setup.Admin.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads"))!.Items;
        Assert.Equal(3, leads.Count);
        Assert.Equal(["sub-dup-0001", "sub-dup-0002", "sub-race-001"], leads.Select(l => l.ExternalSubmissionId).Order());
    }

    [Fact]
    public async Task Response_never_reveals_whether_the_email_or_customer_exists()
    {
        var setup = await SetUpAsync();
        await setup.Admin.PostAsJsonAsync("/api/crm/companies", new SaveCompanyRequest("Var Olan A.Ş.", Email: "kaya@musteri.test"));
        await setup.Admin.PostAsJsonAsync("/api/crm/leads", new SaveLeadRequest("Eski talep", LastName: "Kaya", Email: "kaya@musteri.test"));

        var known = await PostAsync(setup, Submission("sub-known-001"), Site);
        var unknown = await PostAsync(setup, Submission("sub-unknown-01", email: "kimse@yeni.test"), Site);

        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Origin_is_enforced_for_browsers_and_preflight_only_allows_listed_origins()
    {
        var setup = await SetUpAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(setup, Submission("sub-origin-01"), "https://evil.test")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(setup, Submission("sub-origin-02"), null)).StatusCode);

        var ok = await PostAsync(setup, Submission("sub-origin-03"), Site);
        Assert.Equal(Site, ok.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False((await PostAsync(setup, Submission("sub-origin-04"), "https://evil.test")).Headers.Contains("Access-Control-Allow-Origin"));

        var allowed = await Preflight(setup, Site);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal(Site, allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("POST", allowed.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.False((await Preflight(setup, "https://evil.test")).Headers.Contains("Access-Control-Allow-Origin"));

        // Yalnızca izinli kaynaktan gelen tek Lead kaydedildi.
        Assert.Single((await setup.Admin.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads"))!.Items);
    }

    [Fact]
    public async Task Integration_secret_is_shown_once_stored_hashed_and_authenticates_server_calls()
    {
        var setup = await SetUpAsync(serverOnly: true);

        // Sunucu-only form tarayıcıdan gönderimi reddeder.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(setup, Submission("sub-srv-00001"), Site)).StatusCode);

        var rotated = await setup.Admin.PostAsJsonAsync($"/api/crm/lead-forms/{setup.Form.Id}/secret", new LeadVersionRequest(setup.Form.Version));
        var secret = (await rotated.Content.ReadFromJsonAsync<LeadFormSecretDto>())!;
        Assert.StartsWith("lfs_", secret.Secret);
        Assert.NotNull(secret.Form.SecretRotatedAt);

        // Sır hiçbir okuma yolunda dönmez: tipli uç, genel kayıt ucu ve audit.
        var typed = await setup.Admin.GetStringAsync($"/api/crm/lead-forms/{setup.Form.Id}");
        var generic = await setup.Admin.GetStringAsync($"/api/records/{CrmModule.Entities.LeadForm}/{setup.Form.Id}");
        Assert.DoesNotContain(secret.Secret, typed + generic);
        Assert.DoesNotContain("secretHash", typed + generic, StringComparison.OrdinalIgnoreCase);
        using (var scope = factory.OpenScope(setup.Tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var audit = string.Join(' ', await db.AuditLog.Where(a => a.EntityKey == CrmModule.Entities.LeadForm).Select(a => a.Changes).ToListAsync());
            Assert.DoesNotContain("secretHash", audit, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret.Secret, audit);
            Assert.Contains("secretRotatedAt", audit);
        }

        // Sunucudan sunucuya çağrı: sırla, Origin olmadan kabul.
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(setup, Submission("sub-srv-00002"), null, secret.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(setup, Submission("sub-srv-00003"), null, "lfs_yanlis")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(setup, Submission("sub-srv-00004"), Site, "lfs_yanlis")).StatusCode);

        // Yenileme eski sırrı hemen geçersiz kılar.
        var current = (await setup.Admin.GetFromJsonAsync<LeadFormDto>($"/api/crm/lead-forms/{setup.Form.Id}"))!;
        var next = (await (await setup.Admin.PostAsJsonAsync($"/api/crm/lead-forms/{setup.Form.Id}/secret", new LeadVersionRequest(current.Version)))
            .Content.ReadFromJsonAsync<LeadFormSecretDto>())!;
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(setup, Submission("sub-srv-00005"), null, secret.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(setup, Submission("sub-srv-00006"), null, next.Secret)).StatusCode);
        Assert.Equal(2, (await setup.Admin.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads"))!.Items.Count);
    }

    [Fact]
    public async Task Honeypot_inactive_forms_and_invalid_input_are_handled_safely()
    {
        var setup = await SetUpAsync();

        // Bal tuzağı dolu: kabul edilmiş gibi yanıtlanır, kaydedilmez.
        var bot = Submission("sub-bot-00001");
        bot["website2"] = "http://spam.test";
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(setup, bot, Site)).StatusCode);
        Assert.Empty((await setup.Admin.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads"))!.Items);

        var badEmail = await PostAsync(setup, Submission("sub-bad-00001", email: "gecersiz"), Site);
        Assert.Equal(HttpStatusCode.BadRequest, badEmail.StatusCode);
        Assert.Contains("email", (await badEmail.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);

        var tooLong = Submission("sub-bad-00002");
        tooLong["message"] = new string('x', 2001);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(setup, tooLong, Site)).StatusCode);

        var shortId = Submission("kisa");
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(setup, shortId, Site)).StatusCode);

        var empty = new JsonObject { ["submissionId"] = "sub-empty-0001", ["message"] = "merhaba" };
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(setup, empty, Site)).StatusCode);

        var notJson = new HttpRequestMessage(HttpMethod.Post, Url(setup)) { Content = new StringContent("{bozuk", Encoding.UTF8, "application/json") };
        notJson.Headers.Add("Origin", Site);
        Assert.Equal(HttpStatusCode.BadRequest, (await setup.Anonymous.SendAsync(notJson)).StatusCode);

        // Pasif form bulunamaz.
        var inactive = await setup.Admin.PutAsJsonAsync($"/api/crm/lead-forms/{setup.Form.Id}",
            new SaveLeadFormRequest(setup.Form.Name, IsActive: false, AllowedOrigins: Site, Version: setup.Form.Version));
        Assert.Equal(HttpStatusCode.OK, inactive.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(setup, Submission("sub-inactive-1"), Site)).StatusCode);
        Assert.Empty((await setup.Admin.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads"))!.Items);
    }

    [Fact]
    public async Task Inactive_default_owner_leaves_the_lead_unassigned_instead_of_picking_someone()
    {
        var setup = await SetUpAsync();
        await factory.DisableMembershipAsync(setup.DefaultOwner.Id, setup.Tenant.Id);

        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(setup, Submission("sub-owner-001"), Site)).StatusCode);

        var lead = Assert.Single((await setup.Admin.GetFromJsonAsync<CrmPage<LeadDto>>("/api/crm/leads?unassigned=true"))!.Items);
        Assert.Null(lead.OwnerId);
        Assert.Null(lead.AssignedAt);
    }

    [Fact]
    public async Task Form_management_needs_its_own_permission()
    {
        var setup = await SetUpAsync();
        var reader = await factory.CreateUserAsync(setup.Tenant);
        await factory.CreateRoleAsync(setup.Tenant.Id, [LeadsRead, LeadsCreate], reader);
        using var readerClient = await LoginAsync(reader.Email!);

        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.GetAsync("/api/crm/lead-forms")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync("/api/crm/lead-forms", new SaveLeadFormRequest("Yeni"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync($"/api/crm/lead-forms/{setup.Form.Id}/secret", new LeadVersionRequest(setup.Form.Version))).StatusCode);

        var badOrigin = await setup.Admin.PostAsJsonAsync("/api/crm/lead-forms", new SaveLeadFormRequest("Kötü", AllowedOrigins: "https://*.site.test/yol"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badOrigin.StatusCode);
    }

    [Fact]
    public async Task Public_endpoint_is_rate_limited()
    {
        using var limited = factory.WithWebHostBuilder(builder => builder.UseSetting("LeadIntake:RateLimitPerMinute", "3"));
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var client = limited.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/public/lead-forms/{tenant.Slug}/yokformanahtari01")
            {
                Content = JsonContent.Create(Submission($"sub-rate-{i:000}")),
            };
            statuses.Add((await client.SendAsync(request)).StatusCode);
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.NotFound));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    // ---- yardımcılar

    private sealed record Setup(Tenant Tenant, HttpClient Admin, HttpClient Anonymous, LeadFormDto Form, ModulixUser DefaultOwner);

    private async Task<Setup> SetUpAsync(bool serverOnly = false)
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, AdminPermissions, admin);
        var owner = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [LeadsRead], owner);
        var adminClient = await LoginAsync(admin.Email!);

        var created = await adminClient.PostAsJsonAsync("/api/crm/lead-forms",
            new SaveLeadFormRequest("İletişim formu", DefaultOwnerId: owner.Id, AllowedOrigins: Site, ServerOnly: serverOnly));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var form = (await created.Content.ReadFromJsonAsync<LeadFormDto>())!;
        Assert.Equal(20, form.FormKey.Length);
        return new Setup(tenant, adminClient, factory.CreateBrowserClient(), form, owner);
    }

    private async Task<HttpClient> LoginAsync(string email)
    {
        var client = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, ModulixApiFactory.Password))).StatusCode);
        return client;
    }

    private static string Url(Setup setup) => $"/api/public/lead-forms/{setup.Tenant.Slug}/{setup.Form.FormKey}";

    private static JsonObject Submission(string id, string email = "kaya@musteri.test", string message = "10 araçlık filo için bakım teklifi istiyoruz.") => new()
    {
        ["submissionId"] = id,
        ["firstName"] = "Mehmet",
        ["lastName"] = "Kaya",
        ["email"] = email,
        ["phone"] = "0532 111 22 33",
        ["message"] = message,
        ["utmSource"] = "google",
    };

    private static async Task<HttpResponseMessage> PostAsync(Setup setup, JsonObject body, string? origin, string? secret = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(setup)) { Content = JsonContent.Create(body) };
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (secret is not null)
        {
            request.Headers.Add("X-Lead-Form-Secret", secret);
        }

        return await setup.Anonymous.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Preflight(Setup setup, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, Url(setup));
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        return await setup.Anonymous.SendAsync(request);
    }
}
