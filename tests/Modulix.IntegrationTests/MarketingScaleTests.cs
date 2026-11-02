using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Crm;
using Modulix.Modules.Marketing;
using Modulix.Shared.Auth;
using Modulix.Shared.Marketing;
using Modulix.Shared.Settings;
using Xunit.Abstractions;
using static Modulix.Modules.Marketing.MarketingModule.Permissions;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0025 dilim 9: ölçek ölçümü (100.000 sentetik kişi). Yavaş ve veritabanını doldurduğu için varsayılan olarak atlanır; <c>MODULIX_SCALE=1</c> ile çalışır
/// (isteğe bağlı <c>MODULIX_SCALE_PEOPLE</c>). Ölçülen süreler test çıktısına yazılır; bütçeler yalnızca bariz gerilemeleri yakalayan geniş sınırlardır, bir performans
/// garantisi değildir. Sonuçlar ve ortam ADR'ye ve docs/pazarlama-isletim.md'ye elle işlenir.
/// </summary>
public class MarketingScaleTests(ModulixApiFactory factory, ITestOutputHelper output) : IClassFixture<ModulixApiFactory>
{
    private static readonly int People = int.TryParse(Environment.GetEnvironmentVariable("MODULIX_SCALE_PEOPLE"), out var n) && n > 0 ? n : 100_000;

    private static readonly string[] Permissions =
    [
        SegmentsRead, SegmentsWrite, SegmentsPublish, RecipientsRead, CampaignsRead, CampaignsWrite, CampaignsAudience,
        CrmModule.Permissions.ContactsRead, CrmModule.Permissions.ContactsWrite,
        CampaignsSend, CoreModule.Permissions.ManageSettings,
    ];

    [ScaleFact]
    public async Task Segment_preview_publish_and_audience_snapshot_stay_within_budget_for_a_large_tenant()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, CrmModule.ModuleKey, enabled: true);
        await factory.SetModuleAsync(tenant.Id, MarketingModule.ModuleKey, enabled: true);
        var user = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, Permissions, RecordScope.All, user);
        using var publicHost = factory.WithWebHostBuilder(b => b.UseSetting("App:PublicUrl", "https://app.modulix.test"));
        var client = publicHost.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password))).StatusCode);

        var seed = Stopwatch.StartNew();
        await SeedAsync(tenant.Id);
        output.WriteLine($"Tohum: {People:N0} kişi, {seed.Elapsed.TotalSeconds:N1} sn");

        // Kişilerin altıda biri "Satın alma" (hedef sınırının altında), üçte biri "Finans" (sınırın üstünde) unvanlı.
        var condition = new JsonObject { ["field"] = "crm.contact.title", ["operator"] = "contains", ["values"] = new JsonArray("Satın") };
        var expected = (People + 5) / 6;

        var timer = Stopwatch.StartNew();
        var preview = await client.PostAsJsonAsync("/api/marketing/segments/preview", new PreviewSegmentRequest("Person", condition, 1, 25));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Report("Segment önizleme (sayı + ilk sayfa)", timer, 15);
        Assert.Contains(expected.ToString(), (await preview.Content.ReadAsStringAsync()).Replace(".", string.Empty).Replace(",", string.Empty));

        timer.Restart();
        var created = await client.PostAsJsonAsync("/api/marketing/segments", new SaveSegmentRequest(Code: "olcek", Name: "Ölçek", Target: "Person", Kind: "Dynamic", Draft: condition.ToJsonString()));
        var segment = (await created.Content.ReadFromJsonAsync<SegmentDto>())!;
        var published = await client.PostAsJsonAsync($"/api/marketing/segments/{segment.Id}/publish", new SegmentActionRequest());
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Report("Segment yayımlama (hesaplama dahil)", timer, 30);

        var campaign = (await (await client.PostAsJsonAsync("/api/marketing/campaigns",
            new SaveCampaignRequest(Code: "olcek", Name: "Ölçek", Target: "Person", Channel: "Email", RecipientRule: "PrimaryContact"))).Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/marketing/campaign-audience",
            new SaveCampaignAudienceEntryRequest(CampaignId: campaign.Id, Mode: "Include", SegmentId: segment.Id))).StatusCode);

        timer.Restart();
        var audience = await client.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/preview", null);
        Assert.True(audience.StatusCode == HttpStatusCode.OK, await audience.Content.ReadAsStringAsync());
        Report("Hedef kitle önizleme (alıcı çözümleme + izin)", timer, 60);

        timer.Restart();
        var snapshot = await client.PostAsync($"/api/marketing/campaigns/{campaign.Id}/audience/snapshot", null);
        Assert.Equal(HttpStatusCode.Created, snapshot.StatusCode);
        var dto = (await snapshot.Content.ReadFromJsonAsync<AudienceSnapshotDto>())!;
        Report("Hedef kitle dondurma", timer, 90);
        Assert.Equal(expected, dto.EligibleCount);

        timer.Restart();
        var page = await client.GetAsync($"/api/marketing/audience-snapshots/{dto.Id}/recipients?pageSize=100");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Report("Alıcı listesi ilk sayfa", timer, 10);

        // Güvenlik sınırı: bir hedef kitle en fazla 20.000 hedefe çözülür; üstü açık bir iletiyle reddedilir (sessizce kesilmez).
        var wide = new JsonObject { ["field"] = "crm.contact.title", ["operator"] = "contains", ["values"] = new JsonArray("Finans") };
        var wideSegment = (await (await client.PostAsJsonAsync("/api/marketing/segments", new SaveSegmentRequest(Code: "genis", Name: "Geniş", Target: "Person", Kind: "Dynamic", Draft: wide.ToJsonString()))).Content.ReadFromJsonAsync<SegmentDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/marketing/segments/{wideSegment.Id}/publish", new SegmentActionRequest())).StatusCode);
        var wideCampaign = (await (await client.PostAsJsonAsync("/api/marketing/campaigns",
            new SaveCampaignRequest(Code: "genis", Name: "Geniş", Target: "Person", Channel: "Email", RecipientRule: "PrimaryContact"))).Content.ReadFromJsonAsync<CampaignDto>())!;
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/marketing/campaign-audience",
            new SaveCampaignAudienceEntryRequest(CampaignId: wideCampaign.Id, Mode: "Include", SegmentId: wideSegment.Id))).StatusCode);
        timer.Restart();
        var tooWide = await client.PostAsync($"/api/marketing/campaigns/{wideCampaign.Id}/audience/preview", null);
        Report("Sınırı aşan hedef kitle reddi", timer, 30);
        Assert.Equal(HttpStatusCode.Conflict, tooWide.StatusCode);
        Assert.Contains("en fazla", await tooWide.Content.ReadAsStringAsync());

        // Gönderim yolu: çalıştırma başlatma (teslimat satırlarını açar) ve işleme grubu (adres çözme, izin/engel denetimi, bağlantı üretimi, sağlayıcı çağrısı; test sağlayıcısı).
        await PutSettingAsync(client, MarketingModule.SettingKeys.Provider, "fake");
        await PutSettingAsync(client, MarketingModule.SettingKeys.ApprovalRequired, "false");
        await PutSettingAsync(client, MarketingModule.SettingKeys.DailyLimit, "0");
        var content = await client.PostAsJsonAsync("/api/marketing/campaign-contents", new SaveCampaignContentRequest(CampaignId: campaign.Id, Subject: "Ölçek", Body: "Ölçek denemesi https://example.test"));
        Assert.Equal(HttpStatusCode.Created, content.StatusCode);
        var contentDto = (await content.Content.ReadFromJsonAsync<CampaignContentDto>())!;
        var run = (await (await client.PostAsJsonAsync("/api/marketing/campaign-runs",
            new SaveCampaignRunRequest(CampaignId: campaign.Id, SnapshotId: dto.Id, ContentId: contentDto.Id))).Content.ReadFromJsonAsync<CampaignRunDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/submit", new RunActionRequest())).StatusCode);
        timer.Restart();
        var started = await client.PostAsJsonAsync($"/api/marketing/campaign-runs/{run.Id}/start", new RunActionRequest());
        Assert.True(started.StatusCode == HttpStatusCode.OK, await started.Content.ReadAsStringAsync());
        Report("Çalıştırma başlatma (teslimat satırlarını açma)", timer, 120);
        Assert.Equal(expected, (await client.GetFromJsonAsync<RunSummaryDto>($"/api/marketing/campaign-runs/{run.Id}/summary"))!.Total);

        timer.Restart();
        var batch = await client.PostAsync($"/api/marketing/campaign-runs/{run.Id}/process?batchSize=200", null);
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        var result = (await batch.Content.ReadFromJsonAsync<ProcessResultDto>())!;
        Report($"İşleme grubu ({result.Processed} ileti; saniyede ~{result.Processed / Math.Max(timer.Elapsed.TotalSeconds, 0.001):N0})", timer, 120);
        Assert.Equal(200, result.Processed);
    }

    private static async Task PutSettingAsync(HttpClient client, string key, string value)
    {
        var current = (await client.GetFromJsonAsync<SettingsResponse>("/api/settings"))!.Settings.First(s => s.Key == key);
        var response = await client.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { [key] = new(value, current.Version) }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private void Report(string step, Stopwatch timer, int budgetSeconds)
    {
        output.WriteLine($"{step}: {timer.Elapsed.TotalSeconds:N2} sn (bütçe {budgetSeconds} sn)");
        Assert.True(timer.Elapsed.TotalSeconds <= budgetSeconds, $"{step} bütçeyi aştı: {timer.Elapsed.TotalSeconds:N1} sn > {budgetSeconds} sn");
    }

    /// <summary>Kişi, kişi kaydı (contact), e-posta iletişim noktası ve pazarlama izni; hepsi küme tabanlı tek SQL ile (satır satır ekleme ölçümü bozar).</summary>
    private async Task SeedAsync(Guid tenantId)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        await db.Database.ExecuteSqlRawAsync(
            """
            DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();
            CREATE TABLE #n (i int NOT NULL PRIMARY KEY, personId uniqueidentifier NOT NULL, contactId uniqueidentifier NOT NULL);
            INSERT #n (i, personId, contactId)
            SELECT TOP ({1}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), NEWID(), NEWID()
            FROM sys.all_columns a CROSS JOIN sys.all_columns b;

            INSERT core.People (Id, FirstName, LastName, Email, Title, TenantId, CreatedAt, UpdatedAt, Version)
            SELECT personId, N'Kişi', CONCAT(N'S', i), CONCAT(N'olcek', i, N'@olcek.test'),
                   CASE i % 6 WHEN 1 THEN N'Satın alma' WHEN 2 THEN N'Finans' WHEN 3 THEN N'Finans' ELSE N'Pazarlama' END, {0}, @now, @now, 1
            FROM #n;

            INSERT crm.Contacts (Id, PersonId, TenantId, CreatedAt, UpdatedAt, Version)
            SELECT contactId, personId, {0}, @now, @now, 1 FROM #n;

            INSERT crm.ContactPoints (Id, ContactId, Channel, Type, Value, NormalizedValue, IsPrimary, IsActive, IsShared, TenantId, CreatedAt, UpdatedAt, Version)
            SELECT NEWID(), contactId, N'Email', N'Work', CONCAT(N'olcek', i, N'@olcek.test'), CONCAT(N'olcek', i, N'@olcek.test'), 1, 1, 0, {0}, @now, @now, 1 FROM #n;

            INSERT core.CommunicationPreferences (Id, TenantId, Channel, Address, Purpose, Status, Source, CreatedAt, ChangedAt)
            SELECT NEWID(), {0}, N'Email', CONCAT(N'olcek', i, N'@olcek.test'), N'Marketing', N'Granted', N'import', @now, @now FROM #n;
            """,
            tenantId, People);
    }
}

/// <summary>Ölçek ölçümü yalnızca <c>MODULIX_SCALE=1</c> ile çalışır; aksi halde atlanır (testin neden koşmadığı çıktıda görünür).</summary>
public sealed class ScaleFactAttribute : FactAttribute
{
    public ScaleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MODULIX_SCALE") != "1")
        {
            Skip = "Ölçek ölçümü kapalı (MODULIX_SCALE=1 ile açılır).";
        }
    }
}
