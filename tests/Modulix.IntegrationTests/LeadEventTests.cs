using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Tenancy;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Records;
using Modulix.Infrastructure.Events;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// Lead ilk temas aboneliği (<c>crm.lead-first-contact</c>): yalnızca gerçekleşen temaslar Lead'in ilk temasını doldurur; planlanan
/// arama, görev ve not doldurmaz; olay tekrar teslim edilse ikinci değişiklik, audit ya da olay oluşmaz. Olay işleyicisi tüm
/// veritabanındaki olayları işlediği için bu sınıf diğer testlerden ayrı ve sırayla çalışır.
/// </summary>
[Collection(EventTestCollection.Name)]
public sealed class LeadEventTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] Permissions =
    [
        CompaniesRead, CompaniesWrite, ContactsRead, ContactsWrite, OpportunitiesRead, OpportunitiesWrite,
        ActivitiesRead, ActivitiesWrite, LeadsRead, LeadsCreate, LeadsUpdate, LeadsQualify,
    ];

    [Fact]
    public async Task Only_a_completed_real_contact_sets_first_contact_and_replays_do_not_duplicate()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, Permissions, user);
        using var client = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password))).StatusCode);

        var lead = (await (await client.PostAsJsonAsync("/api/crm/leads", new SaveLeadRequest("Temas", LastName: "Kaya"))).Content.ReadFromJsonAsync<LeadDto>())!;
        var done = DateTimeOffset.UtcNow.AddMinutes(-5);

        // Planlanmış arama, tamamlanmış ama ulaşılamayan arama, görev ve not: temas değil.
        await PostActivityAsync(client, new SaveActivityRequest("Planlı arama", ActivityType.Call, DateTimeOffset.UtcNow.AddHours(1), LeadId: lead.Id));
        await PostActivityAsync(client, new SaveActivityRequest("Açılmadı", ActivityType.Call, done, done, LeadId: lead.Id, Outcome: ActivityOutcome.NoAnswer));
        await PostActivityAsync(client, new SaveActivityRequest("Görev", ActivityType.Task, done, done, LeadId: lead.Id, Outcome: ActivityOutcome.Reached));
        await PostActivityAsync(client, new SaveActivityRequest("Not", ActivityType.Note, done, done, LeadId: lead.Id));
        await DrainAsync(tenant.Id);
        var untouched = (await client.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{lead.Id}"))!;
        Assert.Null(untouched.FirstContactAt);
        Assert.Equal(LeadStatus.New, untouched.Status);

        // Ulaşılan arama gerçekleşen temastır: ilk temas dolar, Lead "İletişim kuruluyor"a geçer.
        await PostActivityAsync(client, new SaveActivityRequest("Görüşüldü", ActivityType.Call, done, done, LeadId: lead.Id, Outcome: ActivityOutcome.Reached));
        await DrainAsync(tenant.Id);
        var contacted = (await client.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{lead.Id}"))!;
        Assert.Equal(done, contacted.FirstContactAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(LeadStatus.Contacting, contacted.Status);

        // Daha sonraki bir temas ilk temayı değiştirmez; olay yeniden işlense de ikinci değişiklik olmaz.
        await PostActivityAsync(client, new SaveActivityRequest("İkinci görüşme", ActivityType.Meeting, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, LeadId: lead.Id));
        await DrainAsync(tenant.Id);
        await ReplayAllDeliveriesAsync(tenant.Id);
        await DrainAsync(tenant.Id);
        var after = (await client.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{lead.Id}"))!;
        Assert.Equal(contacted.FirstContactAt, after.FirstContactAt);
        Assert.Equal(contacted.Version, after.Version);

        using var scope = factory.OpenScope(tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var updates = await db.AuditLog.Where(a => a.RecordId == lead.Id && a.Action == AuditAction.Updated).ToListAsync();
        var change = Assert.Single(updates);
        Assert.Equal("crm.lead-first-contact", change.Subscription);
        Assert.Equal("Contacting", JsonNode.Parse(change.Changes)!["status"]!["new"]!.GetValue<string>());
    }

    [Fact]
    public async Task Completing_tasks_never_qualifies_or_converts_the_lead()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, Permissions, user);
        using var client = factory.CreateBrowserClient();
        await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        var lead = (await (await client.PostAsJsonAsync("/api/crm/leads",
            new SaveLeadRequest("Görev", LastName: "Kaya", Email: "k@k.test", NeedDescription: "ihtiyaç"))).Content.ReadFromJsonAsync<LeadDto>())!;

        await PostActivityAsync(client, new SaveActivityRequest("Teklif hazırla", ActivityType.Task, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, LeadId: lead.Id));
        await DrainAsync(tenant.Id);

        var current = (await client.GetFromJsonAsync<LeadDto>($"/api/crm/leads/{lead.Id}"))!;
        Assert.Equal(LeadStatus.New, current.Status);
        Assert.Null(current.QualifiedAt);
    }

    private static async Task PostActivityAsync(HttpClient client, SaveActivityRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/crm/activities", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task DrainAsync(Guid tenantId)
    {
        for (var i = 0; i < 20; i++)
        {
            using var scope = TenantScope(tenantId);
            var round = await scope.ServiceProvider.GetRequiredService<EventDispatcher>().RunOnceAsync(CancellationToken.None);
            if (round.Dispatched == 0 && round.Delivered == 0)
            {
                return;
            }
        }
    }

    /// <summary>Tüm teslimleri yeniden "bekliyor" yapar: en az bir kez teslim garantisinin tekrarını taklit eder.</summary>
    private async Task ReplayAllDeliveriesAsync(Guid tenantId)
    {
        using var scope = TenantScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM platform.EventDeliveries; UPDATE platform.OutboxMessages SET DispatchedAt = NULL");
    }

    private IServiceScope TenantScope(Guid tenantId)
    {
        var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current = new TenantContext(tenantId, Guid.Empty, Guid.Empty);
        return scope;
    }
}
