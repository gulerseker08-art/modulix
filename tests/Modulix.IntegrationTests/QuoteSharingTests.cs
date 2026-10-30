using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>ADR-0019 dilim 3: sabit PDF + özet, paylaşım bağlantısı, müşteri sayfası, kabul/ret kanıtı, izin ve izolasyon.</summary>
public class QuoteSharingTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] RepPermissions = [QuotesRead, QuotesWrite, CompaniesRead];

    [Fact]
    public async Task Presenting_freezes_a_pdf_with_a_matching_hash_and_no_internal_data()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await NewQuoteAsync(admin, s.CompanyId, internalNotes: "GIZLI-IC-NOT");
        await AddLineAsync(admin, quote.Id, price: 1000m, quantity: 2m, tax: 20m, cost: 777m);
        await PresentAsync(admin, quote.Id);

        var documents = (await admin.GetFromJsonAsync<List<QuoteDocumentDto>>($"/api/crm/quotes/{quote.Id}/documents"))!;
        var document = Assert.Single(documents);
        var bytes = await admin.GetByteArrayAsync($"/api/crm/quotes/{quote.Id}/documents/{document.Id}/file");
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.Equal(document.Sha256, Convert.ToHexString(SHA256.HashData(bytes)));
        Assert.Equal(bytes.Length, document.SizeBytes);

        // İkinci üretim yeni belge açmaz: revizyon başına tek, değişmez belge.
        var again = await admin.PostAsync($"/api/crm/quotes/{quote.Id}/documents", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(document.Id, (await again.Content.ReadFromJsonAsync<QuoteDocumentDto>())!.Id);

        // Müşteri verisinde maliyet ve iç not yoktur.
        var token = (await CreateLinkAsync(admin, quote.Id)).Token;
        using var anonymous = factory.CreateClient();
        var raw = await (await anonymous.PostAsJsonAsync($"/api/public/quotes/{s.Slug}/view", new PublicQuoteTokenRequest(token))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("GIZLI-IC-NOT", raw);
        Assert.DoesNotContain("777", raw);
        Assert.DoesNotContain("unitCost", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Draft_quote_has_no_customer_document_and_cannot_be_shared()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await NewQuoteAsync(admin, s.CompanyId);
        await AddLineAsync(admin, quote.Id, price: 100m);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/crm/quotes/{quote.Id}/documents", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/share-links", new CreateQuoteShareLinkRequest())).StatusCode);
    }

    [Fact]
    public async Task Share_link_token_is_shown_once_and_the_customer_view_works_without_a_session()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await PresentedQuoteAsync(admin, s.CompanyId);
        var created = await CreateLinkAsync(admin, quote.Id, "Ali Veli", "ali@musteri.test");

        Assert.True(created.Token.Length >= 40);
        Assert.Equal(created.Token[^4..], created.Link.TokenHint);
        Assert.Equal($"teklif/{s.Slug}#t={created.Token}", created.Path);

        // Liste ham belirteci hiç döndürmez; yalnızca son karakterleri.
        var list = await admin.GetStringAsync($"/api/crm/quotes/{quote.Id}/share-links");
        Assert.DoesNotContain(created.Token, list);

        using var anonymous = factory.CreateClient();
        var view = await ViewAsync(anonymous, s.Slug, created.Token);
        Assert.Equal("open", view.State);
        Assert.True(view.CanDecide);
        Assert.Equal(2160m, view.Content!.GrossTotal);
        Assert.Equal("Anka Ltd.", view.Content.CustomerName);
        Assert.Contains("kabul ediyorum", view.AcceptanceStatement);

        // Müşteri aynı belgeyi PDF olarak da alır.
        var pdf = await anonymous.PostAsJsonAsync($"/api/public/quotes/{s.Slug}/pdf", new PublicQuoteTokenRequest(created.Token));
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal(view.DocumentSha256, Convert.ToHexString(SHA256.HashData(await pdf.Content.ReadAsByteArrayAsync())));
        Assert.Contains("no-store", pdf.Headers.CacheControl!.ToString());

        // İlk görüntüleme yöneticiye görünür.
        var link = (await admin.GetFromJsonAsync<List<QuoteShareLinkDto>>($"/api/crm/quotes/{quote.Id}/share-links"))!.Single();
        Assert.NotNull(link.FirstViewedAt);
    }

    [Fact]
    public async Task Unknown_malformed_or_foreign_tenant_tokens_get_the_same_not_found()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await PresentedQuoteAsync(admin, s.CompanyId);
        var created = await CreateLinkAsync(admin, quote.Id);
        var other = await factory.CreateTenantAsync();
        using var anonymous = factory.CreateClient();

        foreach (var (slug, token) in new[]
        {
            (s.Slug, new string('a', 43)), (s.Slug, "kisa"), (s.Slug, created.Token + "x"), (other.Slug, created.Token), ("yok-boyle-firma", created.Token),
        })
        {
            var response = await anonymous.PostAsJsonAsync($"/api/public/quotes/{slug}/view", new PublicQuoteTokenRequest(token));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Customer_acceptance_is_recorded_with_evidence_and_only_one_decision_wins()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await PresentedQuoteAsync(admin, s.CompanyId);
        var token = (await CreateLinkAsync(admin, quote.Id)).Token;
        using var anonymous = factory.CreateClient();
        var view = await ViewAsync(anonymous, s.Slug, token);

        // Onay kutusu işaretlenmeden kabul edilemez; belge özeti eskiyse karar reddedilir.
        var noConfirm = await DecideAsync(anonymous, s.Slug, token, "accept", view, confirm: false);
        Assert.Equal(HttpStatusCode.BadRequest, noConfirm.StatusCode);
        Assert.Contains("confirmTerms", await noConfirm.Content.ReadAsStringAsync());
        var stale = await anonymous.PostAsJsonAsync($"/api/public/quotes/{s.Slug}/decision",
            new PublicQuoteDecisionRequest(token, "accept", "Ali Veli", ConfirmTerms: true, DocumentSha256: new string('0', 64)));
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Equal("Presented", (await GetQuoteAsync(admin, quote.Id)).Status);

        // İki eşzamanlı kabul: yalnızca biri kazanır.
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => DecideAsync(anonymous, s.Slug, token, "accept", view)));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);

        var after = await GetQuoteAsync(admin, quote.Id);
        Assert.Equal("Accepted", after.Status);
        Assert.NotNull(after.DecidedAt);

        var decision = (await admin.GetFromJsonAsync<List<QuoteDecisionDto>>($"/api/crm/quotes/{quote.Id}/decisions"))!.Single();
        Assert.Equal(("Accepted", "Ali Veli", view.DocumentSha256), (decision.Decision, decision.SignerName, decision.DocumentSha256));
        Assert.Equal(view.AcceptanceStatement, decision.TermsStatement);

        var history = (await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{quote.Id}/history"))!;
        Assert.Contains(history, h => h.Kind == "CustomerAccepted");

        // Karardan sonra bağlantı sonucu gösterir, yeni karar kabul etmez.
        var closed = await ViewAsync(anonymous, s.Slug, token);
        Assert.Equal(("accepted", false), (closed.State, closed.CanDecide));
        Assert.Equal("Ali Veli", closed.Decision!.SignerName);
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(anonymous, s.Slug, token, "reject", closed, reason: "Vazgeçtik")).StatusCode);
    }

    [Fact]
    public async Task Customer_rejection_needs_a_reason_and_closes_the_quote()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await PresentedQuoteAsync(admin, s.CompanyId);
        var token = (await CreateLinkAsync(admin, quote.Id)).Token;
        using var anonymous = factory.CreateClient();
        var view = await ViewAsync(anonymous, s.Slug, token);

        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(anonymous, s.Slug, token, "reject", view)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(anonymous, s.Slug, token, "reject", view, reason: "Bütçe uygun değil")).StatusCode);

        var after = await GetQuoteAsync(admin, quote.Id);
        Assert.Equal("Rejected", after.Status);
        Assert.Contains("Bütçe", after.StatusReason);
    }

    [Fact]
    public async Task Revoked_link_shows_no_content_and_accepts_no_decision()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await PresentedQuoteAsync(admin, s.CompanyId);
        var created = await CreateLinkAsync(admin, quote.Id);
        using var anonymous = factory.CreateClient();
        var view = await ViewAsync(anonymous, s.Slug, created.Token);

        // Gerekçesiz iptal olmaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/share-links/{created.Link.Id}/revoke", new RevokeQuoteShareLinkRequest(" "))).StatusCode);
        var revoke = await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/share-links/{created.Link.Id}/revoke", new RevokeQuoteShareLinkRequest("Yanlış alıcıya gitti"));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal("Revoked", (await revoke.Content.ReadFromJsonAsync<QuoteShareLinkDto>())!.State);

        var closed = await ViewAsync(anonymous, s.Slug, created.Token);
        Assert.Equal(("link-revoked", false), (closed.State, closed.CanDecide));
        Assert.Null(closed.Content);
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(anonymous, s.Slug, created.Token, "accept", view)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await anonymous.PostAsJsonAsync($"/api/public/quotes/{s.Slug}/pdf", new PublicQuoteTokenRequest(created.Token))).StatusCode);
        Assert.Equal("Presented", (await GetQuoteAsync(admin, quote.Id)).Status);
    }

    [Fact]
    public async Task Acceptance_is_refused_after_the_validity_date_even_if_the_scheduled_expiry_has_not_run()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        // Paralel koşuda istek gecikmeleri birkaç saniyeyi bulabiliyor; süre payı geniş tutulur ve bitiş anı beklenir.
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        var quote = await NewQuoteAsync(admin, s.CompanyId, validUntil: until);
        await AddLineAsync(admin, quote.Id, price: 100m);
        await PresentAsync(admin, quote.Id);
        var token = (await CreateLinkAsync(admin, quote.Id)).Token;
        using var anonymous = factory.CreateClient();
        var view = await ViewAsync(anonymous, s.Slug, token);
        Assert.True(view.CanDecide);

        var remaining = until - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
        if (remaining > TimeSpan.Zero) { await Task.Delay(remaining); }
        var late = await DecideAsync(anonymous, s.Slug, token, "accept", view);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("expired", (await ViewAsync(anonymous, s.Slug, token)).State);
        Assert.Equal("Presented", (await GetQuoteAsync(admin, quote.Id)).Status);
    }

    [Fact]
    public async Task A_new_presented_revision_makes_the_old_link_show_only_a_notice()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var v1 = await PresentedQuoteAsync(admin, s.CompanyId);
        var token = (await CreateLinkAsync(admin, v1.Id)).Token;

        var revise = await admin.PostAsJsonAsync($"/api/crm/quotes/{v1.Id}/revise", new ReviseQuoteRequest("Fiyat güncellendi"));
        var v2 = (await revise.Content.ReadFromJsonAsync<QuoteDto>())!;
        await PresentAsync(admin, v2.Id);

        using var anonymous = factory.CreateClient();
        var view = await ViewAsync(anonymous, s.Slug, token);
        Assert.Equal(("superseded", false), (view.State, view.CanDecide));
        Assert.Null(view.Content);

        // Her revizyonun kendi belgesi vardır.
        var d1 = (await admin.GetFromJsonAsync<List<QuoteDocumentDto>>($"/api/crm/quotes/{v1.Id}/documents"))!.Single();
        var d2 = (await admin.GetFromJsonAsync<List<QuoteDocumentDto>>($"/api/crm/quotes/{v2.Id}/documents"))!.Single();
        Assert.NotEqual(d1.Id, d2.Id);
        Assert.Equal((1, 2), (d1.RevisionNo, d2.RevisionNo));
    }

    [Fact]
    public async Task Sharing_requires_its_own_permission_and_documents_follow_read_scope()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var rep = await LoginAsync(s.Rep);
        var quote = await PresentedQuoteAsync(admin, s.CompanyId);

        // Okuma izni olan paylaşım bağlantısı üretemez, bağlantıları listeleyemez.
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/share-links", new CreateQuoteShareLinkRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.GetAsync($"/api/crm/quotes/{quote.Id}/share-links")).StatusCode);

        // Belgeyi okuyabilir (iç kullanıcı), ama izinsiz kullanıcı göremez.
        var document = (await rep.GetFromJsonAsync<List<QuoteDocumentDto>>($"/api/crm/quotes/{quote.Id}/documents"))!.Single();
        Assert.Equal(HttpStatusCode.OK, (await rep.GetAsync($"/api/crm/quotes/{quote.Id}/documents/{document.Id}/file")).StatusCode);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/crm/quotes/{quote.Id}/documents")).StatusCode);

        // Bir tenant'ın belgesi başka tenant'ta bulunamaz.
        var other = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(other.Id, "crm", enabled: true);
        var stranger = await factory.CreateUserAsync(other);
        await factory.CreateRoleAsync(other.Id, [QuotesRead, QuotesShare], Scope.All, stranger);
        using var foreign = await LoginAsync(stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/crm/quotes/{quote.Id}/documents")).StatusCode);
    }

    // ---- yardımcılar

    private async Task<QuoteDto> PresentedQuoteAsync(HttpClient client, Guid companyId)
    {
        var quote = await NewQuoteAsync(client, companyId);
        await AddLineAsync(client, quote.Id, price: 1000m, quantity: 2m, tax: 20m, discountKind: "Percent", discount: 10m);
        return await PresentAsync(client, quote.Id);
    }

    private static async Task<QuoteShareLinkCreatedDto> CreateLinkAsync(HttpClient client, Guid quoteId, string? name = null, string? email = null)
    {
        var response = await client.PostAsJsonAsync($"/api/crm/quotes/{quoteId}/share-links", new CreateQuoteShareLinkRequest(RecipientName: name, RecipientEmail: email));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<QuoteShareLinkCreatedDto>())!;
    }

    private static async Task<PublicQuoteView> ViewAsync(HttpClient anonymous, string slug, string token)
    {
        var response = await anonymous.PostAsJsonAsync($"/api/public/quotes/{slug}/view", new PublicQuoteTokenRequest(token));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PublicQuoteView>())!;
    }

    private static Task<HttpResponseMessage> DecideAsync(
        HttpClient anonymous, string slug, string token, string decision, PublicQuoteView view, bool confirm = true, string? reason = null) =>
        anonymous.PostAsJsonAsync($"/api/public/quotes/{slug}/decision",
            new PublicQuoteDecisionRequest(token, decision, "Ali Veli", "ali@musteri.test", "Satın alma müdürü", reason, confirm, view.DocumentSha256));

    private static async Task<QuoteDto> NewQuoteAsync(HttpClient client, Guid companyId, DateTimeOffset? validUntil = null, string? internalNotes = null)
    {
        var request = new SaveQuoteRequest("Teklif", companyId, ValidUntil: validUntil ?? DateTimeOffset.UtcNow.AddDays(30), InternalNotes: internalNotes);
        return await PostAsync<QuoteDto>(client, "/api/crm/quotes", request);
    }

    private static async Task<QuoteLineDto> AddLineAsync(
        HttpClient client, Guid quoteId, decimal price, decimal quantity = 1m, decimal tax = 0m, string discountKind = "None", decimal discount = 0m, decimal? cost = null) =>
        await PostAsync<QuoteLineDto>(client, "/api/crm/quote-lines",
            new SaveQuoteLineRequest(quoteId, "Kalem", "Free", null, quantity, price, null, discountKind, discount, tax, null, "Standard", UnitCost: cost));

    private static async Task<QuoteDto> GetQuoteAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;

    private static async Task<QuoteDto> PresentAsync(HttpClient client, Guid id)
    {
        var response = await client.PostAsJsonAsync($"/api/crm/quotes/{id}/present", new QuoteTransitionRequest((await GetQuoteAsync(client, id)).Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<QuoteDto>())!;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(ModulixUser Admin, ModulixUser Rep, Guid CompanyId, string Slug)> SetUpAsync()
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
        using var client = await LoginAsync(admin);
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        return (admin, rep, company.Id, tenant.Slug);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
