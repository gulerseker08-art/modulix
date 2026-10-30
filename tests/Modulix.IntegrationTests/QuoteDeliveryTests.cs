using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Email;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>ADR-0019 dilim 5: müşteri bağlantı e-postası, kuyruk tabanlı gönderim durumu ve dürüst durum adlandırması.</summary>
public class QuoteDeliveryTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] RepPermissions = [QuotesRead, QuotesWrite, CompaniesRead];

    [Fact]
    public async Task Sending_needs_a_recipient_and_the_share_permission()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(factory, s.Admin);
        using var rep = await LoginAsync(factory, s.Rep);
        var quote = await PresentedQuoteAsync(admin, s.CompanyId);

        var noAddress = await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/share-links", new CreateQuoteShareLinkRequest(SendEmail: true));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noAddress.StatusCode);
        var bad = await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/share-links", new CreateQuoteShareLinkRequest(RecipientEmail: "gecersiz", SendEmail: true));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<List<QuoteShareLinkDto>>($"/api/crm/quotes/{quote.Id}/share-links"))!);

        var denied = await rep.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/share-links", new CreateQuoteShareLinkRequest(RecipientEmail: "ali@musteri.test", SendEmail: true));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task The_email_is_queued_pending_then_provider_accepted_and_the_quote_status_follows()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(factory, s.Admin);
        var quote = await PresentedQuoteAsync(admin, s.CompanyId);
        var address = $"musteri-{Guid.NewGuid():N}@test.modulix";

        var response = await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/share-links",
            new CreateQuoteShareLinkRequest(RecipientName: "Ali <b>Veli</b>", RecipientEmail: address, SendEmail: true));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var created = (await response.Content.ReadFromJsonAsync<QuoteShareLinkCreatedDto>())!;

        // Kuyruğa yazıldı, henüz gönderilmedi: dürüst durum "Pending"; teklif başlığı da aynı.
        Assert.Equal("Pending", created.Link.DeliveryStatus);
        Assert.Equal("Pending", (await GetQuoteAsync(admin, quote.Id)).DeliveryStatus);
        var queued = await QueuedAsync(address);
        Assert.Equal(EmailStatus.Pending, queued.Status);
        Assert.Equal($"crm.quote-link:{created.Link.Id:N}", queued.Reference);
        Assert.Equal(s.TenantId, queued.TenantId);
        Assert.DoesNotContain('\n', queued.Subject);

        // Müşteri bağlantısı gövdede ve düz metinde var; kullanıcı girdisi HTML'e kodlanmış.
        Assert.Contains($"/{created.Path}", queued.TextBody);
        Assert.Contains("#t=" + created.Token, queued.HtmlBody);
        Assert.DoesNotContain("<b>Veli</b>", queued.HtmlBody);
        Assert.Contains("&lt;b&gt;Veli&lt;/b&gt;", queued.HtmlBody);
        // İç not ve maliyet e-postada yok.
        Assert.DoesNotContain("GIZLI", queued.HtmlBody + queued.TextBody);

        // Gönderici çalışınca (yerel Mailpit) "sağlayıcı kabul etti"; listeleme teklif başlığını da eşitler.
        await DrainAsync(factory.Services);
        var links = (await admin.GetFromJsonAsync<List<QuoteShareLinkDto>>($"/api/crm/quotes/{quote.Id}/share-links"))!;
        var link = Assert.Single(links);
        Assert.Equal("Succeeded", link.DeliveryStatus);
        Assert.NotNull(link.ProviderAcceptedAt);
        Assert.Equal("Succeeded", (await GetQuoteAsync(admin, quote.Id)).DeliveryStatus);
        var history = (await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{quote.Id}/history"))!;
        Assert.Contains(history, h => h.Kind == "DeliveryUpdated");

        // Müşteri e-postadaki bağlantıyla teklifi görür.
        using var anonymous = factory.CreateClient();
        var view = await anonymous.PostAsJsonAsync($"/api/public/quotes/{s.Slug}/view", new PublicQuoteTokenRequest(created.Token));
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
    }

    [Fact]
    public async Task A_link_without_email_stays_not_sent_and_a_permanent_failure_is_reported_as_failed()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(factory, s.Admin);
        var quote = await PresentedQuoteAsync(admin, s.CompanyId);

        // E-postasız bağlantı: durum "NotSent", teklif başlığı değişmez.
        var plain = await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/share-links", new CreateQuoteShareLinkRequest());
        Assert.Equal("NotSent", (await plain.Content.ReadFromJsonAsync<QuoteShareLinkCreatedDto>())!.Link.DeliveryStatus);
        Assert.Equal("NotSent", (await GetQuoteAsync(admin, quote.Id)).DeliveryStatus);

        // Alıcı reddedilirse kuyruk kalıcı hata verir; durum "Failed", hata kısa gösterilir, bağlantı yine de çalışır.
        var address = $"red-{Guid.NewGuid():N}@test.modulix";
        var created = (await (await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/share-links",
            new CreateQuoteShareLinkRequest(RecipientEmail: address, SendEmail: true))).Content.ReadFromJsonAsync<QuoteShareLinkCreatedDto>())!;
        await FailPermanentlyAsync(address);

        var link = (await admin.GetFromJsonAsync<List<QuoteShareLinkDto>>($"/api/crm/quotes/{quote.Id}/share-links"))!.Single(l => l.Id == created.Link.Id);
        Assert.Equal("Failed", link.DeliveryStatus);
        Assert.Contains("reddetti", link.DeliveryError);
        Assert.Null(link.ProviderAcceptedAt);
        Assert.Equal("Failed", (await GetQuoteAsync(admin, quote.Id)).DeliveryStatus);

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync($"/api/public/quotes/{s.Slug}/view", new PublicQuoteTokenRequest(created.Token))).StatusCode);
    }

    // ---- yardımcılar

    /// <summary>Alıcının sunucuca reddedilmesini (kalıcı hata) kuyruk satırında oluşturur; ortak kuyruğu başka testlerden çalmamak için gönderici çalıştırılmaz.</summary>
    private async Task FailPermanentlyAsync(string address)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var email = await db.EmailMessages.SingleAsync(e => e.ToAddress == address);
        email.MarkAttemptFailed("Sunucu alıcıyı reddetti.", permanent: true, DateTimeOffset.UtcNow, maxAttempts: 5);
        await db.SaveChangesAsync();
    }

    private async Task<QueuedEmail> QueuedAsync(string address)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().EmailMessages.AsNoTracking().SingleAsync(e => e.ToAddress == address);
    }

    private static async Task DrainAsync(IServiceProvider services)
    {
        for (var i = 0; i < 50; i++)
        {
            using var scope = services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().DispatchPendingAsync(CancellationToken.None) == 0)
            {
                return;
            }
        }
    }

    private static async Task<QuoteDto> PresentedQuoteAsync(HttpClient client, Guid companyId)
    {
        var quote = await PostAsync<QuoteDto>(client, "/api/crm/quotes",
            new SaveQuoteRequest("Teklif", companyId, ValidUntil: DateTimeOffset.UtcNow.AddDays(30), InternalNotes: "GIZLI iç not"));
        await PostAsync<QuoteLineDto>(client, "/api/crm/quote-lines",
            new SaveQuoteLineRequest(quote.Id, "Kalem", "Free", null, 2m, 1000m, null, "None", 0m, 20m, null, "Standard", UnitCost: 700m));
        var response = await client.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/present", new QuoteTransitionRequest((await GetQuoteAsync(client, quote.Id)).Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<QuoteDto>())!;
    }

    private static async Task<QuoteDto> GetQuoteAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(ModulixUser Admin, ModulixUser Rep, Guid CompanyId, string Slug, Guid TenantId)> SetUpAsync()
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

        await factory.CreateRoleAsync(tenant.Id, RepPermissions, Scope.All, rep);
        using var client = await LoginAsync(factory, admin);
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        return (admin, rep, company.Id, tenant.Slug, tenant.Id);
    }

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> host, ModulixUser user)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        });
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
