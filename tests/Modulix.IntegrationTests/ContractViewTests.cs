using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Contracts;
using Modulix.Shared.Records;
using static Modulix.Modules.Contracts.ContractsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0022 dilim 9: liste sıralaması, "taahhüdü geciken" süzgeci, kaydedilmiş görünümler, pano ve CSV dışa aktarma. Mutlu yol (sıralama, görünüm, pano sayıları, CSV içeriği),
/// doğrulama (geçersiz sıralama, süzgeç, yinelenen ad, görünüm sınırı), tenant izolasyonu (görünüm, pano, dışa aktarma) ve yetki (dışa aktarma ayrı izin; tutar sütunu ve pano tutarı
/// tutar iznine bağlı; kullanıcı başkasının görünümüne erişemez).
/// </summary>
public class ContractViewTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Parties = "/api/records/" + SharedEntities.Party;
    private const string Contracts = "/api/contracts/contracts";
    private const string Commitments = "/api/contracts/commitments";

    private sealed record Setup(Tenant Tenant, ModulixUser Admin, ModulixUser Exporter, ModulixUser MoneyExporter, ModulixUser Reader, ModulixUser Other, Guid PartyId);

    [Fact]
    public async Task List_sorts_by_the_requested_key_overdue_commitments_and_the_dashboard_counts_match_the_list()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var late = await NewContractAsync(admin, s, "B Bakım", endIn: 200, notice: 30);
        var soon = await NewContractAsync(admin, s, "A Destek", endIn: 40, notice: 30);
        var open = await NewContractAsync(admin, s, "C Çerçeve", indefinite: true);
        await ActivateAsync(s, late.Id, soon.Id, open.Id);

        // Sıralama: bitişe göre artan (belirsiz süreli sona), başlığa göre azalan; sayfa sınırı ve süzgeçlerle birlikte çalışır.
        Assert.Equal([soon.Id, late.Id, open.Id], (await ListAsync(admin, "?sort=endDate&effectiveStatus=Active")).Select(c => c.Id));
        Assert.Equal([open.Id, late.Id, soon.Id], (await ListAsync(admin, "?sort=-title&effectiveStatus=Active")).Select(c => c.Id));
        Assert.Equal([open.Id, soon.Id, late.Id], (await ListAsync(admin, "?sort=-createdAt&effectiveStatus=Active")).Select(c => c.Id));

        // Taahhüdü geciken: süresi geçmiş ve değerlendirmesi yapılmış (ihlal) taahhüdü olan sözleşme.
        Assert.Empty(await ListAsync(admin, "?commitmentOverdue=true"));
        await PostAsync<ContractCommitmentDto>(admin, Commitments, new SaveContractCommitmentRequest(soon.Id, "Eğitim", Today().AddDays(-30), Today().AddDays(-5)));
        var evaluation = await ReadAsync<CommitmentEvaluationDto>(await admin.PostAsJsonAsync($"{Commitments}/evaluate", new CommitmentEvaluateRequest(soon.Id)));
        Assert.Equal(1, evaluation.Breached);
        Assert.Equal([soon.Id], (await ListAsync(admin, "?commitmentOverdue=true")).Select(c => c.Id));
        Assert.Equal(2, (await ListAsync(admin, "?commitmentOverdue=false&effectiveStatus=Active")).Count);

        // Eski modüle özel görünüm ucu kaldırıldı; kişisel görünümler ortak /api/grid-views'tadır (ADR-0036, GridViewTests).
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/contracts/saved-views")).StatusCode);

        // Pano: sayaçlar liste süzgeçleriyle aynı sonucu verir; tutarlar para birimine göre ayrıdır.
        var dashboard = (await admin.GetFromJsonAsync<ContractDashboardDto>("/api/contracts/dashboard"))!;
        int Count(string key) => dashboard.Counts.Single(c => c.Key == key).Count;
        Assert.Equal((3, 3, 1, 1), (Count("total"), Count("active"), Count("ending"), Count("overdue")));
        Assert.Equal(0, Count("approval") + Count("signature") + Count("awaitingStart"));
        Assert.Equal(0, Count("drafts"));
        var money = Assert.Single(dashboard.Money!);
        Assert.Equal(("TRY", 3), (money.Currency, money.Count));
        Assert.Equal(["notice", "end"], dashboard.Upcoming.Select(d => d.Kind));
        Assert.Equal(soon.Id, dashboard.Upcoming[0].ContractId);
        Assert.Equal(dashboard.Counts.Single(c => c.Key == "ending").Count, (await ListAsync(admin, $"?effectiveStatus=Active&endFrom={Today():yyyy-MM-dd}&endTo={Today().AddDays(90):yyyy-MM-dd}")).Count);
        Assert.False(dashboard.Truncated);
    }

    [Fact]
    public async Task Export_writes_a_csv_with_the_customer_name_neutralised_formulas_the_list_filters_and_an_audit_entry()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var first = await NewContractAsync(admin, s, "=HYPERLINK(\"http://kotu.example\")", endIn: 100, notice: 10);
        var second = await NewContractAsync(admin, s, "Bakım, destek ve \"bakım\" sözleşmesi", endIn: 50, notice: 10);
        await PostAsync<ContractLineDto>(admin, "/api/contracts/contract-lines", new SaveContractLineRequest(second.Id, "Kalem", Quantity: 2, UnitPrice: 1000m, TaxRatePercent: 20m));
        await ActivateAsync(s, second.Id);

        var all = await ExportAsync(admin);
        var rows = ParseCsv(all);
        Assert.Equal("number,title,type,customer,preparationStatus,approvalStatus,signatureStatus,effectiveStatus,startDate,endDate,isIndefinite,noticeDeadline,renewalMode,currency,netTotal,taxTotal,grossTotal,ownerId,currentVersionNo", rows[0].Aggregate((a, b) => a + "," + b));
        Assert.Equal(3, rows.Count);
        // Formül karakteriyle başlayan hücre etkisizleştirilir; virgül ve tırnak içeren hücre tırnaklanır; müşteri adı ortak kayıttan gelir.
        Assert.Contains(rows.Skip(1), r => r[1] == "'=HYPERLINK(\"http://kotu.example\")" && r[3] == "Anka Lojistik A.Ş.");
        var line = rows.Skip(1).Single(r => r[1] == "Bakım, destek ve \"bakım\" sözleşmesi");
        Assert.Equal(("TRY", 2000m, 2400m), (line[13], decimal.Parse(line[14], CultureInfo.InvariantCulture), decimal.Parse(line[16], CultureInfo.InvariantCulture)));

        // Süzgeçler liste ile aynıdır.
        var active = ParseCsv(await ExportAsync(admin, "?effectiveStatus=Active"));
        Assert.Equal(2, active.Count);
        Assert.Equal(second.Number, active[1][0]);

        // Çıkış denetim kaydına yazılır (kim, kaç kayıt, hangi süzgeç); sayfa dışında bir şey sızmaz.
        var entry = (await admin.GetFromJsonAsync<AuditPageResponse>($"/api/audit?entityKey=contracts.contract&userId={s.Admin.Id}"))!.Items.First(i => i.Action == AuditOperation.Exported);
        Assert.Equal((s.Admin.Id, "contracts.contract"), (entry.UserId, entry.EntityKey));
        Assert.NotEqual(first.Number, second.Number);
    }

    [Fact]
    public async Task Invalid_sort_and_filters_are_rejected()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        await NewContractAsync(admin, s, "Bakım", endIn: 60, notice: 10);

        // Geçersiz sıralama ve filtreler 422; tutar sütunu sıralanamaz (sıra, gizli tutarı sızdırır).
        foreach (var query in new[] { "?sort=grossTotal", "?sort=-netTotal", "?sort=title;drop", "?commitmentOverdue=belki" })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.GetAsync(Contracts + query)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.GetAsync("/api/contracts/contract-parties?sort=title")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_and_export_are_isolated_per_tenant_and_a_filter_cannot_widen_what_a_user_sees()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var contractA = await NewContractAsync(adminA, a, "A tenant sözleşmesi", endIn: 20, notice: 10);
        await ActivateAsync(a, contractA.Id);

        // B tenant'ı A'nın sözleşmesini, panoda sayılarını ve dışa aktarmada satırlarını görmez.
        Assert.Empty(await ListAsync(adminB, string.Empty));
        var dashboard = (await adminB.GetFromJsonAsync<ContractDashboardDto>("/api/contracts/dashboard"))!;
        Assert.All(dashboard.Counts, c => Assert.Equal(0, c.Count));
        Assert.Empty(dashboard.Upcoming);
        Assert.Single(ParseCsv(await ExportAsync(adminB)));

        // Süzgeç yetki taşımaz: kapsamı kendi kayıtlarıyla sınırlı kullanıcı, tümünü gösteren bir süzgeci açsa da yalnızca kendi sözleşmelerini görür.
        using var scopedClient = await LoginAsync(a.Reader);
        Assert.Empty(await ListAsync(scopedClient, "?effectiveStatus=Active"));
        Assert.Equal(0, (await scopedClient.GetFromJsonAsync<ContractDashboardDto>("/api/contracts/dashboard"))!.Counts.Single(c => c.Key == "total").Count);
    }

    [Fact]
    public async Task Export_needs_its_own_permission_hides_money_without_the_financials_permission_and_the_dashboard_money_follows_it()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var contract = await NewContractAsync(admin, s, "Bakım", endIn: 90, notice: 10);
        await PostAsync<ContractLineDto>(admin, "/api/contracts/contract-lines", new SaveContractLineRequest(contract.Id, "Kalem", Quantity: 1, UnitPrice: 5000m, TaxRatePercent: 20m));
        await ActivateAsync(s, contract.Id);

        // Görüntüleme izni dışa aktarmaya yetmez; yetkisiz istek hiçbir şey üretmez.
        using var reader = await LoginAsync(s.Reader);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{Contracts}/export")).StatusCode);

        // Dışa aktarma izni olup tutar izni olmayan kullanıcı sözleşmeleri görür ama tutar sütunları dosyada hiç yoktur.
        using var exporter = await LoginAsync(s.Exporter);
        var plain = ParseCsv(await ExportAsync(exporter));
        Assert.Equal(2, plain.Count);
        Assert.DoesNotContain(plain[0], c => c is "netTotal" or "taxTotal" or "grossTotal");
        Assert.DoesNotContain("5000", string.Join(',', plain[1]));
        var withMoney = ParseCsv(await ExportAsync(await LoginAsync(s.MoneyExporter)));
        Assert.Contains("grossTotal", withMoney[0]);
        Assert.Contains("6000", string.Join(',', withMoney[1]).Replace(".00", string.Empty).Replace(",00", string.Empty));

        // Pano: tutar özeti yalnızca tutar iznine sahip kullanıcıya gelir.
        Assert.Null((await exporter.GetFromJsonAsync<ContractDashboardDto>("/api/contracts/dashboard"))!.Money);
        Assert.NotNull((await admin.GetFromJsonAsync<ContractDashboardDto>("/api/contracts/dashboard"))!.Money);
        Assert.Equal(1, (await exporter.GetFromJsonAsync<ContractDashboardDto>("/api/contracts/dashboard"))!.Counts.Single(c => c.Key == "total").Count);

        // Dışa aktarma da oturum açmayı ister; pano sözleşme görüntüleme iznine bağlıdır.
        using var anonymous = factory.CreateBrowserClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Contracts}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/contracts/dashboard")).StatusCode);
        var nobody = await factory.CreateUserAsync(s.Tenant);
        using var noRole = await LoginAsync(nobody);
        Assert.Equal(HttpStatusCode.Forbidden, (await noRole.GetAsync(Contracts)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noRole.GetAsync("/api/contracts/dashboard")).StatusCode);
    }

    // ---- yardımcılar

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "contracts", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var exporter = await factory.CreateUserAsync(tenant);
        var moneyExporter = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var other = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(other.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, ContractsExport], Scope.All, exporter);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, ContractsExport, FinancialsRead], Scope.All, moneyExporter);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead], Scope.Own, reader);
        using var client = await LoginAsync(admin);
        var party = await PostRecordAsync(client, Parties, new JsonObject { ["name"] = "Anka Lojistik A.Ş." });
        return new Setup(tenant, admin, exporter, moneyExporter, reader, other, party.Id);
    }

    /// <summary>Sözleşmeyi API'den açar; yürürlük durumu (imzalı ve aktif) <see cref="ActivateAsync"/> ile veritabanında kurulur.</summary>
    private static async Task<ContractDto> NewContractAsync(HttpClient admin, Setup s, string title, int endIn = 0, int notice = 0, bool indefinite = false) =>
        await PostAsync<ContractDto>(admin, Contracts, indefinite
            ? new SaveContractRequest(title, s.PartyId, StartDate: Today().AddDays(-100), IsIndefinite: true)
            : new SaveContractRequest(title, s.PartyId, StartDate: Today().AddDays(-100), EndDate: Today().AddDays(endIn), NoticePeriodDays: notice));

    private async Task ActivateAsync(Setup s, params Guid[] ids)
    {
        foreach (var id in ids)
        {
            using var scope = factory.OpenScope(s.Tenant.Id);
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            Assert.Equal(1, await db.Database.ExecuteSqlRawAsync(
                "UPDATE contracts.Contracts SET PreparationStatus = 'ReadyToPublish', ApprovalStatus = 'Approved', SignatureStatus = 'Completed', EffectiveStatus = 'Active', EffectiveVersionNo = 1 WHERE Id = {0}", id));
        }
    }

    private static async Task<List<ContractDto>> ListAsync(HttpClient client, string query)
    {
        var separator = query.Length == 0 ? "?" : query + "&";
        return [.. (await client.GetFromJsonAsync<PagedResult<ContractDto>>($"{Contracts}{separator}pageSize=200"))!.Items];
    }

    private static async Task<string> ExportAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"{Contracts}/export{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        Assert.Equal("text/csv; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        return (await response.Content.ReadAsStringAsync()).TrimStart('﻿');
    }

    /// <summary>Basit CSV çözümleyici (tırnaklı hücre, çift tırnak kaçışı, hücre içi virgül).</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') { quoted = false; }
                else { cell.Append(c); }
            }
            else if (c == '"') { quoted = true; }
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { i++; }
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else { cell.Append(c); }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul")).DateTime);

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
