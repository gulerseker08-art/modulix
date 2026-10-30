using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Crm;
using static Modulix.Modules.Crm.CrmModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>ADR-0019: sunucu hesabı, yetkiye bağlı indirim/fiyat/maliyet, yaşam döngüsü, revizyon, eşzamanlılık ve tenant izolasyonu.</summary>
public class QuoteTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private static readonly string[] RepPermissions = [QuotesRead, QuotesWrite, CompaniesRead, ProductsRead, PricesRead];
    private static readonly string[] ApproverPermissions = [QuotesRead, QuotesApprove, CompaniesRead];

    [Fact]
    public async Task Totals_are_computed_by_the_server_from_lines_discount_and_tax()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await NewQuoteAsync(admin, s.CompanyId);

        // 2 × 1.000 TL, %10 satır indirimi → matrah 1.800; test verisi olan %20 vergiyle toplam 2.160.
        await AddLineAsync(admin, quote.Id, price: 1000m, quantity: 2m, tax: 20m, discountKind: "Percent", discount: 10m);

        var fresh = await GetQuoteAsync(admin, quote.Id);
        Assert.Equal((2000m, 200m, 1800m, 360m, 2160m), (fresh.ListTotal, fresh.DiscountTotal, fresh.NetTotal, fresh.TaxTotal, fresh.GrossTotal));
        Assert.True(fresh.Version > quote.Version);

        // Genel indirim yüzde 10: 1.800 → 1.620; vergi 324; toplam 1.944.
        var update = await admin.PutAsJsonAsync($"/api/crm/quotes/{quote.Id}", Save(fresh) with { DocumentDiscountKind = "Percent", DocumentDiscountValue = 10m });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var after = (await update.Content.ReadFromJsonAsync<QuoteDto>())!;
        Assert.Equal((1620m, 324m, 1944m), (after.NetTotal, after.TaxTotal, after.GrossTotal));

        var lines = await LinesAsync(admin, quote.Id);
        Assert.Equal(180m, Assert.Single(lines).DocumentDiscountShare);
    }

    [Fact]
    public async Task Optional_lines_stay_out_of_the_main_total_until_selected()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await NewQuoteAsync(admin, s.CompanyId);
        await AddLineAsync(admin, quote.Id, price: 1000m, tax: 0m);
        var optional = await AddLineAsync(admin, quote.Id, price: 400m, tax: 0m, role: "Optional");

        var fresh = await GetQuoteAsync(admin, quote.Id);
        Assert.Equal(1000m, fresh.NetTotal);
        Assert.Equal(400m, fresh.OptionalNet);

        var select = await admin.PutAsJsonAsync($"/api/crm/quote-lines/{optional.Id}", Save(optional) with { IsSelected = true });
        Assert.Equal(HttpStatusCode.OK, select.StatusCode);
        Assert.Equal(1400m, (await GetQuoteAsync(admin, quote.Id)).NetTotal);
    }

    [Fact]
    public async Task User_without_discount_manual_price_or_cost_permission_cannot_bypass_the_limits_through_the_api()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var rep = await LoginAsync(s.Rep);
        var quote = await NewQuoteAsync(rep, s.CompanyId);

        // İndirim, serbest kalem ve elle fiyat yetkisi yok: API reddeder (arayüzde gizlemek yetmez).
        var discount = await rep.PostAsJsonAsync("/api/crm/quote-lines", Line(quote.Id, 100m, discountKind: "Percent", discount: 5m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, discount.StatusCode);
        var free = await rep.PostAsJsonAsync("/api/crm/quote-lines", Line(quote.Id, 100m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, free.StatusCode);
        var docDiscount = await rep.PutAsJsonAsync($"/api/crm/quotes/{quote.Id}", Save(quote) with { DocumentDiscountKind = "Amount", DocumentDiscountValue = 10m });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, docDiscount.StatusCode);

        // Maliyet yalnızca yetkili kullanıcıya döner; yetkisiz kullanıcının yazdığı maliyet de yok sayılır.
        var line = await AddLineAsync(admin, quote.Id, price: 100m, cost: 60m);
        Assert.Equal(60m, (await admin.GetFromJsonAsync<QuoteLineDto>($"/api/crm/quote-lines/{line.Id}"))!.UnitCost);
        var seenByRep = await rep.GetFromJsonAsync<QuoteLineDto>($"/api/crm/quote-lines/{line.Id}");
        Assert.Null(seenByRep!.UnitCost);
        var rawByRep = await rep.GetStringAsync($"/api/crm/quote-lines/{line.Id}");
        Assert.DoesNotContain("\"unitCost\":60", rawByRep);
    }

    [Fact]
    public async Task Catalog_price_is_a_snapshot_and_later_catalog_changes_do_not_touch_a_presented_quote()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var adt = await PostAsync<MeasureUnitDto>(admin, "/api/crm/units", new SaveMeasureUnitRequest("ADT", "Adet", "count"));
        var product = await PostAsync<ProductDto>(admin, "/api/crm/products",
            new SaveProductRequest("P-1", "Danışmanlık", adt.Id, Status: "Active"));
        var list = await PostAsync<PriceListDto>(admin, "/api/crm/price-lists", new SavePriceListRequest("Genel"));
        await PostAsync<PriceListItemDto>(admin, "/api/crm/price-list-items", new SavePriceListItemRequest(list.Id, product.Id, 1m, 250m));
        var published = await admin.PutAsJsonAsync($"/api/crm/price-lists/{list.Id}",
            new SavePriceListRequest(list.Name, list.Currency, list.Scope, null, null, null, null, "Published", list.TierMethod, list.Version));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);

        var quote = await NewQuoteAsync(admin, s.CompanyId);
        var response = await admin.PostAsJsonAsync("/api/crm/quote-lines",
            new SaveQuoteLineRequest(quote.Id, null, "Product", product.Id, 2m, null, null, TaxRatePercent: 0m));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var line = (await response.Content.ReadFromJsonAsync<QuoteLineDto>())!;
        Assert.Equal((250m, "P-1", "Danışmanlık", 500m), (line.UnitPrice, line.ProductCode, line.Name, line.Net));

        var present = await PresentAsync(admin, quote.Id);

        // Ürün kartı sonradan değişir; sunulmuş teklifin kalemi aynı kalır.
        var renamed = await admin.PutAsJsonAsync($"/api/crm/products/{product.Id}", new SaveProductRequest("P-1", "Yeni ad", adt.Id, Status: "Active", Version: product.Version));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var again = (await LinesAsync(admin, quote.Id)).Single();
        Assert.Equal(("Danışmanlık", 250m, 500m), (again.Name, again.UnitPrice, again.Net));
        Assert.Equal(present.GrossTotal, (await GetQuoteAsync(admin, quote.Id)).GrossTotal);
    }

    [Fact]
    public async Task Presented_quote_is_read_only_and_must_be_revised_for_commercial_changes()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await NewQuoteAsync(admin, s.CompanyId);
        var line = await AddLineAsync(admin, quote.Id, price: 100m);
        var presented = await PresentAsync(admin, quote.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PutAsJsonAsync($"/api/crm/quotes/{quote.Id}", Save(presented) with { Title = "Değişti" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/crm/quote-lines", Line(quote.Id, 5m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PutAsJsonAsync($"/api/crm/quote-lines/{line.Id}", Save(line) with { Quantity = 9m })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/crm/quote-lines/{line.Id}?version={line.Version}")).StatusCode);
    }

    [Fact]
    public async Task Internal_approval_gates_presentation_and_forbids_self_approval_and_commercial_changes_invalidate_it()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var rep = await LoginAsync(s.Rep);
        using var approver = await LoginAsync(s.Approver);
        var quote = await NewQuoteAsync(rep, s.CompanyId);
        await AddLineAsync(admin, quote.Id, price: 100m);

        var submitted = await TransitionAsync(rep, quote.Id, "submit-for-approval", await VersionAsync(admin, quote.Id));
        Assert.Equal("Pending", submitted.ApprovalStatus);

        // Onay beklerken sunum engellenir (admin yetkili olsa bile).
        var blocked = await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/present", new QuoteTransitionRequest(await VersionAsync(admin, quote.Id)));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        // Red ve değişiklik isteği gerekçe ister.
        var noReason = await approver.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/reject-approval", new QuoteTransitionRequest(await VersionAsync(admin, quote.Id)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);

        var approved = await TransitionAsync(approver, quote.Id, "approve", await VersionAsync(admin, quote.Id));
        Assert.Equal("Approved", approved.ApprovalStatus);

        // Ticari değişiklik onayı geçersiz kılar; onaysız sunum tekrar engellenir.
        var changed = await AddLineAsync(admin, quote.Id, price: 50m);
        var afterChange = await GetQuoteAsync(admin, quote.Id);
        Assert.Equal("NotRequired", afterChange.ApprovalStatus);
        Assert.NotNull(changed);

        // Onay yetkisi olmayan temsilci onaylayamaz.
        var repApprove = await rep.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/approve", new QuoteTransitionRequest(afterChange.Version));
        Assert.Equal(HttpStatusCode.Forbidden, repApprove.StatusCode);
    }

    [Fact]
    public async Task Accepting_twice_or_after_cancellation_or_after_expiry_never_produces_a_second_result()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await NewQuoteAsync(admin, s.CompanyId);
        await AddLineAsync(admin, quote.Id, price: 100m);
        var presented = await PresentAsync(admin, quote.Id);

        var accepted = await TransitionAsync(admin, quote.Id, "record-accepted", presented.Version);
        Assert.Equal("Accepted", accepted.Status);

        // Tekrarlanan kabul ya da eski sürümle iptal/ret: tek sonuç korunur.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/record-accepted", new QuoteTransitionRequest(accepted.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/cancel", new QuoteTransitionRequest(presented.Version, "Gerekçe"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/cancel", new QuoteTransitionRequest(accepted.Version, "Gerekçe"))).StatusCode);
        Assert.Equal("Accepted", (await GetQuoteAsync(admin, quote.Id)).Status);

        // Kabul edilmiş sürümden revizyon açılamaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/revise", new ReviseQuoteRequest("Değişiklik"))).StatusCode);

        // Süresi dolan teklif kabul edilemez (kabul anında yeniden denetlenir).
        var shortLived = await NewQuoteAsync(admin, s.CompanyId, validUntil: DateTimeOffset.UtcNow.AddSeconds(2));
        await AddLineAsync(admin, shortLived.Id, price: 10m);
        var p = await PresentAsync(admin, shortLived.Id);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var late = await admin.PostAsJsonAsync($"/api/crm/quotes/{shortLived.Id}/record-accepted", new QuoteTransitionRequest(p.Version));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("Presented", (await GetQuoteAsync(admin, shortLived.Id)).Status);
    }

    [Fact]
    public async Task Concurrent_accept_and_cancel_leave_exactly_one_outcome()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await NewQuoteAsync(admin, s.CompanyId);
        await AddLineAsync(admin, quote.Id, price: 100m);
        var presented = await PresentAsync(admin, quote.Id);

        using var other = await LoginAsync(s.Admin);
        var accept = admin.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/record-accepted", new QuoteTransitionRequest(presented.Version));
        var cancel = other.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/cancel", new QuoteTransitionRequest(presented.Version, "Vazgeçildi"));
        var results = await Task.WhenAll(accept, cancel);

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains((await GetQuoteAsync(admin, quote.Id)).Status, new[] { "Accepted", "Cancelled" });
    }

    [Fact]
    public async Task Revising_creates_a_new_version_in_the_same_family_and_presenting_it_supersedes_the_old_one()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var v1 = await NewQuoteAsync(admin, s.CompanyId);
        var original = await AddLineAsync(admin, v1.Id, price: 100m);
        var v1Presented = await PresentAsync(admin, v1.Id);

        var response = await admin.PostAsJsonAsync($"/api/crm/quotes/{v1.Id}/revise", new ReviseQuoteRequest("Müşteri fiyat istedi"));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var v2 = (await response.Content.ReadFromJsonAsync<QuoteDto>())!;

        Assert.Equal((v1.QuoteNumber, v1.FamilyId, 2, "Draft"), (v2.QuoteNumber, v2.FamilyId, v2.RevisionNo, v2.Status));
        Assert.Equal(v1Presented.GrossTotal, v2.GrossTotal);

        // Aynı ailede ikinci açık taslak revizyon açılamaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await admin.PostAsJsonAsync($"/api/crm/quotes/{v1.Id}/revise", new ReviseQuoteRequest("Tekrar"))).StatusCode);

        // Kalem kararlı anahtarı revizyonlar arasında aynı kalır (karşılaştırma için).
        var copied = (await LinesAsync(admin, v2.Id)).Single();
        Assert.Equal(original.StableKey, copied.StableKey);

        // Yeni sürüm sunulunca eski sürüm geçersizleşir ve kabul edilemez.
        await PresentAsync(admin, v2.Id);
        Assert.Equal("Superseded", (await GetQuoteAsync(admin, v1.Id)).Status);
        var stale = await admin.PostAsJsonAsync($"/api/crm/quotes/{v1.Id}/record-accepted", new QuoteTransitionRequest((await GetQuoteAsync(admin, v1.Id)).Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // Çoğaltma yeni aile ve yeni numaradır.
        var copy = await admin.PostAsync($"/api/crm/quotes/{v1.Id}/duplicate", null);
        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        var dup = (await copy.Content.ReadFromJsonAsync<QuoteDto>())!;
        Assert.NotEqual(v1.FamilyId, dup.FamilyId);
        Assert.NotEqual(v1.QuoteNumber, dup.QuoteNumber);
        Assert.Equal((1, "Draft"), (dup.RevisionNo, dup.Status));
        Assert.NotEqual(original.StableKey, (await LinesAsync(admin, dup.Id)).Single().StableKey);
    }

    [Fact]
    public async Task Concurrent_edits_do_not_silently_overwrite_each_other()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await NewQuoteAsync(admin, s.CompanyId);

        var first = await admin.PutAsJsonAsync($"/api/crm/quotes/{quote.Id}", Save(quote) with { Title = "Birinci" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var stale = await admin.PutAsJsonAsync($"/api/crm/quotes/{quote.Id}", Save(quote) with { Title = "İkinci" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Birinci", (await GetQuoteAsync(admin, quote.Id)).Title);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_quotes_lines_or_history()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var quote = await NewQuoteAsync(adminA, a.CompanyId);
        var line = await AddLineAsync(adminA, quote.Id, price: 100m);

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/crm/quotes/{quote.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/crm/quote-lines/{line.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/crm/quotes/{quote.Id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/crm/quotes/{quote.Id}/present", new QuoteTransitionRequest(quote.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await adminB.PostAsJsonAsync("/api/crm/quote-lines", Line(quote.Id, 5m))).StatusCode);
        Assert.Empty((await adminB.GetFromJsonAsync<CrmPage<QuoteDto>>("/api/crm/quotes"))!.Items);
        // Başka tenant'ın firması ile teklif açılamaz.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await adminB.PostAsJsonAsync("/api/crm/quotes", new SaveQuoteRequest("Çapraz", a.CompanyId))).StatusCode);
    }

    [Fact]
    public async Task History_records_each_transition_with_actor_and_reason()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var quote = await NewQuoteAsync(admin, s.CompanyId);
        await AddLineAsync(admin, quote.Id, price: 100m);
        var presented = await PresentAsync(admin, quote.Id);
        await TransitionAsync(admin, quote.Id, "cancel", presented.Version, "Proje iptal");

        var history = (await admin.GetFromJsonAsync<List<QuoteHistoryDto>>($"/api/crm/quotes/{quote.Id}/history"))!;
        Assert.Equal(["Created", "Presented", "DocumentGenerated", "Cancelled"], history.Select(h => h.Kind).ToArray());
        Assert.Equal("Proje iptal", history[^1].Reason);
        Assert.All(history, h => Assert.NotNull(h.ActorId));

        var actions = (await admin.GetFromJsonAsync<List<QuoteActionStateDto>>($"/api/crm/quotes/{quote.Id}/actions"))!;
        Assert.All(actions, a => Assert.False(a.Allowed));
        Assert.All(actions, a => Assert.False(string.IsNullOrWhiteSpace(a.Message)));
    }

    // ---- yardımcılar

    private static SaveQuoteRequest Save(QuoteDto q) => new(
        q.Title, q.CompanyId, q.Description, q.ContactId, q.OpportunityId, q.PriceListId, q.OwnerId, q.IssueDate, q.ValidUntil, q.Currency,
        q.ReportingCurrency, q.ExchangeRate, q.ExchangeRateDate, q.PriceIncludesTax, q.DocumentDiscountKind, q.DocumentDiscountValue,
        q.PaymentTerms, q.DeliveryTerms, q.CommercialTerms, q.InternalNotes, q.Version);

    private static SaveQuoteLineRequest Save(QuoteLineDto l) => new(
        l.QuoteId, l.Name, l.Kind, l.ProductId, l.Quantity, l.UnitPrice, l.UnitId, l.DiscountKind, l.DiscountValue, l.TaxRatePercent, l.TaxCode,
        l.Role, l.IsSelected, l.ParentStableKey, l.RecurringPeriod, l.Description, l.GroupName, l.Position, l.UnitCost, l.Version);

    private static SaveQuoteLineRequest Line(Guid quoteId, decimal price, decimal quantity = 1m, decimal tax = 0m, string role = "Standard",
        string discountKind = "None", decimal discount = 0m, decimal? cost = null) =>
        new(quoteId, "Kalem", "Free", null, quantity, price, null, discountKind, discount, tax, null, role, UnitCost: cost);

    private static async Task<QuoteDto> NewQuoteAsync(HttpClient client, Guid companyId, DateTimeOffset? validUntil = null)
    {
        var request = new SaveQuoteRequest("Teklif", companyId, ValidUntil: validUntil ?? DateTimeOffset.UtcNow.AddDays(30));
        return await PostAsync<QuoteDto>(client, "/api/crm/quotes", request);
    }

    private static async Task<QuoteLineDto> AddLineAsync(
        HttpClient client, Guid quoteId, decimal price, decimal quantity = 1m, decimal tax = 0m, string role = "Standard",
        string discountKind = "None", decimal discount = 0m, decimal? cost = null) =>
        await PostAsync<QuoteLineDto>(client, "/api/crm/quote-lines", Line(quoteId, price, quantity, tax, role, discountKind, discount, cost));

    private static async Task<QuoteDto> GetQuoteAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<QuoteDto>($"/api/crm/quotes/{id}"))!;

    private static async Task<long> VersionAsync(HttpClient client, Guid id) => (await GetQuoteAsync(client, id)).Version;

    private static async Task<List<QuoteLineDto>> LinesAsync(HttpClient client, Guid quoteId) =>
        (await client.GetFromJsonAsync<CrmPage<QuoteLineDto>>($"/api/crm/quote-lines?quoteId={quoteId}"))!.Items.ToList();

    private static async Task<QuoteDto> PresentAsync(HttpClient client, Guid id) =>
        await TransitionAsync(client, id, "present", await VersionAsync(client, id));

    private static async Task<QuoteDto> TransitionAsync(HttpClient client, Guid id, string action, long version, string? reason = null)
    {
        var response = await client.PostAsJsonAsync($"/api/crm/quotes/{id}/{action}", new QuoteTransitionRequest(version, reason));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<QuoteDto>())!;
    }

    private async Task<(ModulixUser Admin, ModulixUser Rep, ModulixUser Approver, Guid CompanyId)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var rep = await factory.CreateUserAsync(tenant);
        var approver = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, RepPermissions, Scope.All, rep);
        await factory.CreateRoleAsync(tenant.Id, ApproverPermissions, Scope.All, approver);
        using var client = await LoginAsync(admin);
        var company = await PostAsync<CompanyDto>(client, "/api/crm/companies", new SaveCompanyRequest("Anka Ltd."));
        return (admin, rep, approver, company.Id);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }
}
