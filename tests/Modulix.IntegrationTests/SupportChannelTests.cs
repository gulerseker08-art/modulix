using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Shared.Auth;
using Modulix.Shared.Settings;
using Modulix.Shared.Support;
using static Modulix.Modules.Support.SupportModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0024 dilim 4: mesaj ekleri (tür/imza doğrulaması, iç not görünürlüğü), gelen kanal girişi (tekrar, eşleştirme, otomatik yanıt, gönderen doğrulaması, sel sınırı),
/// memnuniyet anketi (süreli tek kullanımlık bağlantı, düşük puan, yeni döngü), CSV dışa aktarma ve toplu işlem. Her özellik mutlu yol, doğrulama, yetki ve tenant izolasyonuyla sınanır.
/// </summary>
public class SupportChannelTests : IClassFixture<ModulixApiFactory>, IAsyncLifetime
{
    private static readonly string[] AgentPermissions = [TicketsRead, TicketsCreate, TicketsWrite, TicketsTake, TicketsResolve, MessagesReply, NotesWrite, InternalRead];
    private static readonly string[] LeadPermissions = [.. AgentPermissions, TicketsAssign, TicketsClose, TicketsReopen, TicketsCancel, PriorityOverride, SlaRecalculate, TicketsMerge];
    private static readonly string[] ManagerPermissions = [.. LeadPermissions, SettingsManage, SlaManage, TicketsExport, "core.settings.manage"];
    private static readonly string[] IntegrationPermissions = [TicketsRead, TicketsCreate, MessagesReply, InboundIngest];

    private readonly ModulixApiFactory factory;
    private readonly WebApplicationFactory<Program> host;

    public SupportChannelTests(ModulixApiFactory factory)
    {
        this.factory = factory;
        // Anket bağlantısı genel adresle kurulur; testte adres ayarlıdır.
        host = factory.WithWebHostBuilder(builder => builder.UseSetting("App:PublicUrl", "https://app.modulix.test"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await host.DisposeAsync();

    private sealed record Setup(Tenant Tenant, Guid PartyId, ModulixUser Manager, ModulixUser Lead, ModulixUser Agent, ModulixUser Agent2, ModulixUser Viewer, ModulixUser Integration, ModulixUser Writer);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "support", enabled: true);
        var manager = await factory.CreateUserAsync(tenant);
        var lead = await factory.CreateUserAsync(tenant);
        var agent = await factory.CreateUserAsync(tenant);
        var agent2 = await factory.CreateUserAsync(tenant);
        var viewer = await factory.CreateUserAsync(tenant);
        var integration = await factory.CreateUserAsync(tenant);
        var writer = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, ManagerPermissions, Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, LeadPermissions, Scope.All, lead);
        await factory.CreateRoleAsync(tenant.Id, AgentPermissions, Scope.Own, agent);
        await factory.CreateRoleAsync(tenant.Id, AgentPermissions, Scope.Own, agent2);
        await factory.CreateRoleAsync(tenant.Id, [TicketsRead], Scope.All, viewer);
        await factory.CreateRoleAsync(tenant.Id, IntegrationPermissions, Scope.All, integration);
        // Giriş ve yanıt izni var, talep oluşturma izni yok.
        await factory.CreateRoleAsync(tenant.Id, [TicketsRead, MessagesReply, InboundIngest], Scope.All, writer);

        Guid partyId;
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            var party = new Party { Name = "Kanal Müşterisi A.Ş.", Email = "kanal@musteri.example.test" };
            db.Set<Party>().Add(party);
            await db.SaveChangesAsync();
            partyId = party.Id;
        }

        return new Setup(tenant, partyId, manager, lead, agent, agent2, viewer, integration, writer);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private HttpClient Anonymous() => host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });

    private static async Task<T> CreateAsync<T>(HttpClient client, string path, object request)
    {
        var response = await client.PostAsJsonAsync($"/api/support/{path}", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<TicketDto> TicketAsync(HttpClient client, Setup s, string subject = "Talep", string? reporterEmail = null, string? clientKey = null) =>
        CreateAsync<TicketDto>(client, "tickets", new SaveTicketRequest(Subject: subject, PartyId: s.PartyId, ReporterEmail: reporterEmail, ClientKey: clientKey));

    private static async Task<TicketDto> GetAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<TicketDto>($"/api/support/tickets/{id}"))!;

    private static Task<HttpResponseMessage> ActAsync(HttpClient client, Guid id, string action, TicketActionRequest request) => client.PostAsJsonAsync($"/api/support/tickets/{id}/{action}", request);

    private static async Task<TicketDto> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketDto>())!;
    }

    private static async Task<TicketDto> TakeAsync(HttpClient client, TicketDto t) => await OkAsync(await ActAsync(client, t.Id, "take", new TicketActionRequest(t.Version)));

    private static async Task<TicketDto> StartAsync(HttpClient client, TicketDto t) => await OkAsync(await ActAsync(client, t.Id, "start", new TicketActionRequest((await GetAsync(client, t.Id)).Version)));

    private static async Task<TicketDto> ResolveAsync(HttpClient client, TicketDto t) =>
        await OkAsync(await ActAsync(client, t.Id, "resolve", new TicketActionRequest((await GetAsync(client, t.Id)).Version, ResolutionCode: "Resolved", ResolutionNote: "Çözüldü.")));

    private static async Task<List<TicketHistoryDto>> HistoryAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<List<TicketHistoryDto>>($"/api/support/tickets/{id}/history"))!;

    private static async Task<List<TicketMessageDto>> MessagesAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<List<TicketMessageDto>>($"/api/support/tickets/{id}/messages"))!;

    private static async Task<TicketMessageDto> MessageAsync(HttpClient client, Guid ticketId, string kind, string body)
    {
        var response = await client.PostAsJsonAsync($"/api/support/tickets/{ticketId}/messages", new AddTicketMessageRequest(kind, body));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TicketMessageDto>())!;
    }

    private static async Task SetSettingAsync(HttpClient client, string key, string value)
    {
        var current = (await client.GetFromJsonAsync<SettingsResponse>("/api/settings"))!;
        var version = current.Settings.FirstOrDefault(x => x.Key == key)?.Version ?? 0;
        var response = await client.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { [key] = new(value, version) }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task SqlAsync(Guid tenantId, string sql, params object[] args)
    {
        using var scope = factory.OpenScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.True(await db.Database.ExecuteSqlRawAsync(sql, args) >= 1);
    }

    // ---- Ekler

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF");

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid ticketId, Guid messageId, string fileName, byte[] content)
    {
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await client.PostAsync($"/api/support/tickets/{ticketId}/messages/{messageId}/attachments?fileName={Uri.EscapeDataString(fileName)}", body);
    }

    private static async Task<AttachmentDto> UploadOkAsync(HttpClient client, Guid ticketId, Guid messageId, string fileName, byte[] content)
    {
        var response = await UploadAsync(client, ticketId, messageId, fileName, content);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AttachmentDto>())!;
    }

    private static async Task<List<AttachmentDto>> AttachmentsAsync(HttpClient client, Guid ticketId) =>
        (await client.GetFromJsonAsync<List<AttachmentDto>>($"/api/support/tickets/{ticketId}/attachments"))!;

    [Fact]
    public async Task Attachments_are_validated_by_extension_and_signature_and_downloaded_as_safe_attachments()
    {
        var s = await SetUpAsync();
        using var lead = await LoginAsync(s.Lead);
        var ticket = await TakeAsync(lead, await TicketAsync(lead, s, "Ekli talep"));
        var reply = await MessageAsync(lead, ticket.Id, "reply", "Belgeyi ekte bulabilirsiniz.");

        var added = await UploadOkAsync(lead, ticket.Id, reply.Id, "..\\..\\Fatura 2026.pdf", Pdf);
        Assert.Equal("Fatura 2026.pdf", added.FileName); // yol parçaları atılır
        Assert.Equal(("application/pdf", Pdf.Length, "NotScanned", false), (added.ContentType, added.SizeBytes, added.ScanStatus, added.IsInternal));
        Assert.Equal(64, added.Sha256.Length);
        Assert.Contains(await HistoryAsync(lead, ticket.Id), h => h.Kind == "AttachmentAdded" && h.Reason == "Fatura 2026.pdf");

        var download = await lead.GetAsync($"/api/support/tickets/{ticket.Id}/attachments/{added.Id}/file");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Pdf, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("no-store", download.Headers.CacheControl!.ToString());

        // Doğrulama: tür izni, imza uyuşması, boş dosya, çalıştırılabilir ad, tekrar, sınırlar.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(lead, ticket.Id, reply.Id, "program.exe", Pdf)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(lead, ticket.Id, reply.Id, "sayfa.html", "<script>alert(1)</script>"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(lead, ticket.Id, reply.Id, "sahte.pdf", "MZ-calistirilabilir"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(lead, ticket.Id, reply.Id, "bos.txt", [])).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(lead, ticket.Id, reply.Id, "fatura.exe.pdf", Pdf)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(lead, ticket.Id, reply.Id, "kopya.pdf", Pdf)).StatusCode);
        var tooBig = new byte[5 * 1024 * 1024 + 1];
        Encoding.ASCII.GetBytes("%PDF").CopyTo(tooBig, 0);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await UploadAsync(lead, ticket.Id, reply.Id, "buyuk.pdf", tooBig)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(lead, ticket.Id, Guid.NewGuid(), "x.txt", "metin"u8.ToArray())).StatusCode);
        for (var i = 1; i < 5; i++)
        {
            await UploadOkAsync(lead, ticket.Id, reply.Id, $"not{i}.txt", Encoding.UTF8.GetBytes($"içerik {i}"));
        }

        var sixth = await UploadAsync(lead, ticket.Id, reply.Id, "altinci.txt", "altı"u8.ToArray());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, sixth.StatusCode);
        Assert.Equal(5, (await AttachmentsAsync(lead, ticket.Id)).Count);

        // Kapalı talebe ek eklenmez.
        var done = await ResolveAsync(lead, await StartAsync(lead, ticket));
        await OkAsync(await ActAsync(lead, done.Id, "close", new TicketActionRequest((await GetAsync(lead, done.Id)).Version)));
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(lead, ticket.Id, reply.Id, "gec.txt", "geç"u8.ToArray())).StatusCode);
    }

    [Fact]
    public async Task Attachments_follow_message_visibility_permissions_and_tenant_isolation()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var lead = await LoginAsync(s.Lead);
        using var viewer = await LoginAsync(s.Viewer);
        using var foreign = await LoginAsync(other.Lead);
        using var agent2 = await LoginAsync(s.Agent2);
        var ticket = await TakeAsync(lead, await TicketAsync(lead, s));
        var reply = await MessageAsync(lead, ticket.Id, "reply", "Genel yanıt");
        var note = await MessageAsync(lead, ticket.Id, "note", "İç değerlendirme");
        var publicFile = await UploadOkAsync(lead, ticket.Id, reply.Id, "genel.txt", "genel"u8.ToArray());
        var internalFile = await UploadOkAsync(lead, ticket.Id, note.Id, "ic-not.txt", "gizli iç not eki"u8.ToArray());
        Assert.True(internalFile.IsInternal);
        Assert.Contains(await HistoryAsync(lead, ticket.Id), h => h.Kind == "InternalAttachmentAdded");

        // İç not izni olmayan okuyucu genel eki görür, iç not ekini ne listede ne tarihçede görür ne indirir.
        Assert.Equal(["genel.txt"], (await AttachmentsAsync(viewer, ticket.Id)).Select(a => a.FileName));
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/support/tickets/{ticket.Id}/attachments/{publicFile.Id}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/support/tickets/{ticket.Id}/attachments/{internalFile.Id}/file")).StatusCode);
        Assert.DoesNotContain(await HistoryAsync(viewer, ticket.Id), h => h.Kind == "InternalAttachmentAdded");
        Assert.Equal(2, (await AttachmentsAsync(lead, ticket.Id)).Count);

        // Yetki: yalnızca okuyabilen kişi ek yükleyemez; iç not ekine ulaşamayan, iç nota ek de ekleyemez.
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(viewer, ticket.Id, reply.Id, "izin.txt", "x"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(viewer, ticket.Id, note.Id, "ic.txt", "x"u8.ToArray())).StatusCode);

        // Veri kapsamı: başkasının talebinin ekleri 404 (var olup olmadığı sızmaz).
        Assert.Equal(HttpStatusCode.NotFound, (await agent2.GetAsync($"/api/support/tickets/{ticket.Id}/attachments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent2.GetAsync($"/api/support/tickets/{ticket.Id}/attachments/{publicFile.Id}/file")).StatusCode);

        // Tenant izolasyonu.
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/support/tickets/{ticket.Id}/attachments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/support/tickets/{ticket.Id}/attachments/{publicFile.Id}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(foreign, ticket.Id, reply.Id, "sizinti.txt", "x"u8.ToArray())).StatusCode);

        // Oturumsuz erişim yok.
        using var anonymous = Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/support/tickets/{ticket.Id}/attachments/{publicFile.Id}/file")).StatusCode);
    }

    // ---- Gelen kanal

    private static InboundMessageRequest Email(string id, string from, string subject = "Faturam hatalı", string body = "Merhaba, faturamda hata var.", string? inReplyTo = null,
        string? autoSubmitted = null, bool nullReturnPath = false) =>
        new("Email", id, inReplyTo, null, from, "Müşteri Kişi", subject, body, autoSubmitted, null, false, nullReturnPath);

    private static async Task<InboundResultDto> IngestAsync(HttpClient client, InboundMessageRequest request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.PostAsJsonAsync("/api/support/inbound", request);
        Assert.True(response.StatusCode == expected || (expected == HttpStatusCode.OK && response.StatusCode == HttpStatusCode.Created), await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<InboundResultDto>())!;
    }

    [Fact]
    public async Task Inbound_email_creates_threads_and_deduplicates_and_only_the_known_sender_can_join_a_ticket()
    {
        var s = await SetUpAsync();
        using var integration = await LoginAsync(s.Integration);
        using var lead = await LoginAsync(s.Lead);
        var from = "Musteri@Firma.Example.Test";

        var first = await IngestAsync(integration, Email("<m1@mail.firma.test>", from, "Re: Fwd: Faturam hatalı"), HttpStatusCode.Created);
        Assert.Equal("Created", first.Outcome);
        var ticket = await GetAsync(lead, first.TicketId!.Value);
        Assert.Equal(("Faturam hatalı", "Email", "musteri@firma.example.test", "Müşteri Kişi"), (ticket.Subject, ticket.Channel, ticket.ReporterEmail, ticket.ReporterName));
        var message = Assert.Single(await MessagesAsync(lead, ticket.Id));
        Assert.Equal(("Inbound", "Public", "Email"), (message.Direction, message.Visibility, message.Channel));
        Assert.Equal(message.Id, first.MessageId);

        // Tekrar: aynı kanal ve mesaj kimliği hiçbir şey yazmaz.
        var again = await IngestAsync(integration, Email("<m1@mail.firma.test>", from));
        Assert.Equal(("Duplicate", ticket.Id, first.MessageId), (again.Outcome, again.TicketId, again.MessageId));
        Assert.Single(await MessagesAsync(lead, ticket.Id));

        // Zincir: In-Reply-To ile eşleşir (adres büyük/küçük harf fark etmez).
        var reply = await IngestAsync(integration, Email("<m2@mail.firma.test>", "MUSTERI@firma.example.test", "Re: Faturam hatalı", "Ek bilgi.", inReplyTo: "<m1@mail.firma.test>"));
        Assert.Equal(("Appended", ticket.Id), (reply.Outcome, reply.TicketId));

        // Konudaki talep numarası ile eşleşir.
        var byNumber = await IngestAsync(integration, Email("<m3@mail.firma.test>", from, $"Re: [{ticket.Number}] Faturam hatalı", "Numara ile."));
        Assert.Equal(("Appended", ticket.Id), (byNumber.Outcome, byNumber.TicketId));
        Assert.Equal(3, (await MessagesAsync(lead, ticket.Id)).Count);

        // Talep numarasını bilen ama başvuran olmayan gönderen talebe mesaj sokamaz: yeni talep açılır, özgün talep değişmez.
        var intruder = await IngestAsync(integration, Email("<x1@baska.test>", "saldirgan@baska.example.test", $"[{ticket.Number}] Benim de talebim", "Sızma denemesi."), HttpStatusCode.Created);
        Assert.Equal("Created", intruder.Outcome);
        Assert.NotEqual(ticket.Id, intruder.TicketId);
        Assert.Equal(3, (await MessagesAsync(lead, ticket.Id)).Count);
        var chain = await IngestAsync(integration, Email("<x2@baska.test>", "saldirgan@baska.example.test", "Re: konu", "Zincir tahmini.", inReplyTo: "<m1@mail.firma.test>"), HttpStatusCode.Created);
        Assert.NotEqual(ticket.Id, chain.TicketId);

        // Kapalı talebe gelen yanıt ilişkili yeni talep açar.
        var taken = await TakeAsync(lead, await GetAsync(lead, ticket.Id));
        var resolved = await ResolveAsync(lead, await StartAsync(lead, taken));
        await OkAsync(await ActAsync(lead, resolved.Id, "close", new TicketActionRequest((await GetAsync(lead, resolved.Id)).Version)));
        var late = await IngestAsync(integration, Email("<m4@mail.firma.test>", from, "Re: Faturam hatalı", "Hâlâ çözülmedi.", inReplyTo: "<m3@mail.firma.test>"), HttpStatusCode.Created);
        Assert.Equal("Created", late.Outcome);
        Assert.Equal(ticket.Id, (await GetAsync(lead, late.TicketId!.Value)).ParentTicketId);
    }

    [Fact]
    public async Task Automatic_replies_never_create_or_extend_tickets_and_invalid_input_and_missing_permissions_are_rejected()
    {
        var s = await SetUpAsync();
        using var integration = await LoginAsync(s.Integration);
        using var lead = await LoginAsync(s.Lead);
        using var writer = await LoginAsync(s.Writer);
        var from = "otomatik@firma.example.test";
        var first = await IngestAsync(integration, Email("<a1@mail.firma.test>", from), HttpStatusCode.Created);
        var before = (await lead.GetFromJsonAsync<RecordPage>("/api/support/tickets?pageSize=100"))!.TotalCount;

        foreach (var automatic in new[]
        {
            Email("<a2@mail.firma.test>", from, "Out of office", "Ofis dışındayım.", inReplyTo: "<a1@mail.firma.test>", autoSubmitted: "auto-replied"),
            Email("<a3@mail.firma.test>", "mailer-daemon@firma.example.test", "Undeliverable", "Teslim edilemedi.", inReplyTo: "<a1@mail.firma.test>"),
            Email("<a4@mail.firma.test>", from, "Bounce", "Geri döndü.", nullReturnPath: true),
            Email("<a5@mail.firma.test>", "noreply@firma.example.test", "Bilgilendirme", "Otomatik."),
        })
        {
            var result = await IngestAsync(integration, automatic);
            Assert.Equal(("IgnoredAutomatic", null), (result.Outcome, result.TicketId));
        }

        Assert.Equal(before, (await lead.GetFromJsonAsync<RecordPage>("/api/support/tickets?pageSize=100"))!.TotalCount);
        Assert.Single(await MessagesAsync(lead, first.TicketId!.Value));

        // Doğrulama.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await integration.PostAsJsonAsync("/api/support/inbound", new InboundMessageRequest("Fax", "<v1>", FromAddress: from, Body: "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await integration.PostAsJsonAsync("/api/support/inbound", new InboundMessageRequest("Email", "", FromAddress: from, Body: "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await integration.PostAsJsonAsync("/api/support/inbound", new InboundMessageRequest("Email", "<v2>", FromAddress: from, Body: "  "))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await integration.PostAsJsonAsync("/api/support/inbound", new InboundMessageRequest("Email", "<v3>", Body: "gövde"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await integration.PostAsJsonAsync("/api/support/inbound", new InboundMessageRequest("Email", "<v4>", FromAddress: "adres-degil", Body: "gövde"))).StatusCode);

        // Yetki: giriş izni olmayan 403 (lead dahil); izni olup oluşturma/yanıt izinlerinden biri eksikse servis reddeder.
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.PostAsJsonAsync("/api/support/inbound", Email("<p1>", from))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync("/api/support/inbound", Email("<p2>", from))).StatusCode);
        using var anonymous = Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/support/inbound", Email("<p3>", from))).StatusCode);
    }

    [Fact]
    public async Task Inbound_is_tenant_isolated_and_a_sender_flood_is_throttled()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var integration = await LoginAsync(s.Integration);
        using var foreign = await LoginAsync(other.Integration);
        using var foreignLead = await LoginAsync(other.Lead);
        using var lead = await LoginAsync(s.Lead);
        var from = "musteri@ortak.example.test";
        var mine = await IngestAsync(integration, Email("<t1@mail.test>", from), HttpStatusCode.Created);

        // Aynı gönderen, aynı Message-Id ve başka tenant'ın zinciri: başka tenant'ta kendi talebini açar, ilk tenant'a hiçbir şey sızmaz ve tekrar sayılmaz.
        var theirs = await IngestAsync(foreign, Email("<t1@mail.test>", from, inReplyTo: "<t1@mail.test>"), HttpStatusCode.Created);
        Assert.Equal("Created", theirs.Outcome);
        Assert.NotEqual(mine.TicketId, theirs.TicketId);
        Assert.Single(await MessagesAsync(lead, mine.TicketId!.Value));
        Assert.Equal(HttpStatusCode.NotFound, (await foreignLead.GetAsync($"/api/support/tickets/{mine.TicketId}")).StatusCode);

        // Sel: aynı gönderenden saatte en çok 30 mesaj.
        for (var i = 0; i < 29; i++)
        {
            await IngestAsync(integration, Email($"<f{i}@mail.test>", from, "Re: spam", $"Mesaj {i}", inReplyTo: "<t1@mail.test>"));
        }

        var throttled = await integration.PostAsJsonAsync("/api/support/inbound", Email("<f-last@mail.test>", from, "Re: spam", "Fazla", inReplyTo: "<t1@mail.test>"));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal(30, (await MessagesAsync(lead, mine.TicketId!.Value)).Count);
    }

    // ---- Memnuniyet anketi

    private static readonly Regex SurveyLink = new(@"anket/(?<tenant>[0-9a-f]{32})#t=(?<token>[A-Za-z0-9_-]+)", RegexOptions.Compiled);

    private async Task<(string Tenant, string Token)> SurveyLinkAsync(Guid tenantId, string address)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var mail = await db.EmailMessages.AsNoTracking().Where(e => e.TenantId == tenantId && e.ToAddress == address).OrderByDescending(e => e.CreatedAt).FirstAsync();
        var match = SurveyLink.Match(mail.TextBody!);
        Assert.True(match.Success, mail.TextBody);
        return (match.Groups["tenant"].Value, match.Groups["token"].Value);
    }

    private static async Task<HttpResponseMessage> SurveyAsync(HttpClient anonymous, string tenant, string action, object body) =>
        await anonymous.PostAsJsonAsync($"/api/public/support-survey/{tenant}/{action}", body);

    private async Task<List<SurveyDto>> SurveysAsync(HttpClient client, Guid ticketId) =>
        (await client.GetFromJsonAsync<List<SurveyDto>>($"/api/support/tickets/{ticketId}/surveys"))!;

    [Fact]
    public async Task A_survey_is_sent_on_resolution_when_enabled_answered_once_with_a_valid_link_and_low_scores_are_flagged()
    {
        var s = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var lead = await LoginAsync(s.Lead);
        using var viewer = await LoginAsync(s.Viewer);
        using var anonymous = Anonymous();
        var address = $"anket-{Guid.NewGuid():N}@musteri.example.test";

        // Ayar kapalıyken (varsayılan) anket açılmaz.
        var quiet = await ResolveAsync(lead, await StartAsync(lead, await TakeAsync(lead, await TicketAsync(lead, s, "Sessiz", address))));
        Assert.Empty(await SurveysAsync(lead, quiet.Id));

        await SetSettingAsync(manager, "support.survey.enabled", "true");
        var ticket = await TakeAsync(lead, await TicketAsync(lead, s, "Anketli", address));
        var resolved = await ResolveAsync(lead, await StartAsync(lead, ticket));
        var survey = Assert.Single(await SurveysAsync(viewer, resolved.Id));
        Assert.Equal(("Sent", 1, null, null), (survey.State, survey.Cycle, survey.Rating, survey.Comment));
        Assert.NotNull(survey.QueuedAt);

        var (tenantKey, token) = await SurveyLinkAsync(s.Tenant.Id, address);
        Assert.Equal(s.Tenant.Id.ToString("N"), tenantKey);

        // Geçersiz bağlantılar tek tip 404 verir: yanlış belirteç, yanlış tenant, biçimi bozuk belirteç.
        Assert.Equal(HttpStatusCode.NotFound, (await SurveyAsync(anonymous, tenantKey, "view", new PublicSurveyTokenRequest(new string('a', 43)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SurveyAsync(anonymous, tenantKey, "view", new PublicSurveyTokenRequest("kisa"))).StatusCode);
        var otherTenant = await factory.CreateTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await SurveyAsync(anonymous, otherTenant.Id.ToString("N"), "view", new PublicSurveyTokenRequest(token))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SurveyAsync(anonymous, Guid.NewGuid().ToString("N"), "view", new PublicSurveyTokenRequest(token))).StatusCode);

        var view = await (await SurveyAsync(anonymous, tenantKey, "view", new PublicSurveyTokenRequest(token))).Content.ReadFromJsonAsync<PublicSurveyView>();
        Assert.Equal(("open", true, resolved.Number), (view!.State, view.CanAnswer, view.TicketNumber));

        // Doğrulama: puan aralığı ve yorum uzunluğu.
        Assert.Equal(HttpStatusCode.BadRequest, (await SurveyAsync(anonymous, tenantKey, "answer", new PublicSurveyAnswerRequest(token, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SurveyAsync(anonymous, tenantKey, "answer", new PublicSurveyAnswerRequest(token, 6))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SurveyAsync(anonymous, tenantKey, "answer", new PublicSurveyAnswerRequest(token, 4, new string('x', 1001)))).StatusCode);
        Assert.Equal("Sent", Assert.Single(await SurveysAsync(lead, resolved.Id)).State);

        var answered = await (await SurveyAsync(anonymous, tenantKey, "answer", new PublicSurveyAnswerRequest(token, 5, "Çok teşekkürler"))).Content.ReadFromJsonAsync<PublicSurveyView>();
        Assert.Equal(("answered", 5), (answered!.State, answered.Rating));

        // Tek kullanım: ikinci yanıt reddedilir ve ilk yanıt değişmez.
        Assert.Equal(HttpStatusCode.Conflict, (await SurveyAsync(anonymous, tenantKey, "answer", new PublicSurveyAnswerRequest(token, 1, "Fikir değiştirdim"))).StatusCode);
        var stored = Assert.Single(await SurveysAsync(lead, resolved.Id));
        Assert.Equal(("Answered", 5, "Çok teşekkürler"), (stored.State, stored.Rating, stored.Comment));
        var history = await HistoryAsync(lead, resolved.Id);
        Assert.Contains(history, h => h.Kind == "SurveyAnswered" && h.Reason == "Puan: 5/5");
        Assert.DoesNotContain(history, h => h.Kind == "LowSatisfaction");

        // Düşük puan ayrı tarihçe olayıdır (takip için).
        var lowAddress = $"dusuk-{Guid.NewGuid():N}@musteri.example.test";
        var low = await ResolveAsync(lead, await StartAsync(lead, await TakeAsync(lead, await TicketAsync(lead, s, "Düşük puan", lowAddress))));
        var (lowTenant, lowToken) = await SurveyLinkAsync(s.Tenant.Id, lowAddress);
        Assert.Equal(HttpStatusCode.OK, (await SurveyAsync(anonymous, lowTenant, "answer", new PublicSurveyAnswerRequest(lowToken, 1))).StatusCode);
        Assert.Contains(await HistoryAsync(lead, low.Id), h => h.Kind == "LowSatisfaction");

        // Süresi dolan bağlantı yanıtlanamaz.
        var lateAddress = $"gec-{Guid.NewGuid():N}@musteri.example.test";
        var late = await ResolveAsync(lead, await StartAsync(lead, await TakeAsync(lead, await TicketAsync(lead, s, "Geç anket", lateAddress))));
        var (lateTenant, lateToken) = await SurveyLinkAsync(s.Tenant.Id, lateAddress);
        await SqlAsync(s.Tenant.Id, "UPDATE support.Surveys SET ExpiresAt = DATEADD(DAY, -1, SYSDATETIMEOFFSET()) WHERE TicketId = {0}", late.Id);
        var expired = await (await SurveyAsync(anonymous, lateTenant, "view", new PublicSurveyTokenRequest(lateToken))).Content.ReadFromJsonAsync<PublicSurveyView>();
        Assert.Equal(("expired", false), (expired!.State, expired.CanAnswer));
        Assert.Equal(HttpStatusCode.Conflict, (await SurveyAsync(anonymous, lateTenant, "answer", new PublicSurveyAnswerRequest(lateToken, 3))).StatusCode);
        Assert.Equal("Expired", Assert.Single(await SurveysAsync(lead, late.Id)).State);
    }

    [Fact]
    public async Task Reopened_tickets_get_a_new_survey_cycle_and_survey_results_respect_scope_and_tenant()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var lead = await LoginAsync(s.Lead);
        using var agent2 = await LoginAsync(s.Agent2);
        using var foreign = await LoginAsync(other.Lead);
        await SetSettingAsync(manager, "support.survey.enabled", "true");
        var address = $"dongu-{Guid.NewGuid():N}@musteri.example.test";
        var resolved = await ResolveAsync(lead, await StartAsync(lead, await TakeAsync(lead, await TicketAsync(lead, s, "Döngü", address))));
        Assert.Equal(1, Assert.Single(await SurveysAsync(lead, resolved.Id)).Cycle);

        var reopened = await OkAsync(await ActAsync(lead, resolved.Id, "reopen", new TicketActionRequest((await GetAsync(lead, resolved.Id)).Version, "Tekrar bozuldu")));
        var again = await ResolveAsync(lead, await StartAsync(lead, reopened));
        Assert.Equal([1, 2], (await SurveysAsync(lead, again.Id)).Select(x => x.Cycle));

        // Başkasının talebinin anketi (kapsam) ve başka tenant: 404.
        Assert.Equal(HttpStatusCode.NotFound, (await agent2.GetAsync($"/api/support/tickets/{again.Id}/surveys")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/support/tickets/{again.Id}/surveys")).StatusCode);
        using var anonymous = Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/support/tickets/{again.Id}/surveys")).StatusCode);

        // Alıcı e-postası olmayan talepte anket açılmaz.
        var noEmail = await ResolveAsync(lead, await StartAsync(lead, await TakeAsync(lead, await CreateAsync<TicketDto>(lead, "tickets", new SaveTicketRequest(Subject: "Adressiz", ReporterName: "Telefonla arayan")))));
        Assert.Empty(await SurveysAsync(lead, noEmail.Id));
    }

    // ---- Dışa aktarma

    [Fact]
    public async Task Export_is_permissioned_scoped_audited_neutralises_formulas_and_leaves_out_messages()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var manager = await LoginAsync(s.Manager);
        using var lead = await LoginAsync(s.Lead);
        using var agent = await LoginAsync(s.Agent);
        using var foreign = await LoginAsync(other.Manager);
        var formula = await TicketAsync(lead, s, "=HYPERLINK(\"http://kotu.test\")");
        var plain = await TicketAsync(lead, s, "Normal, virgüllü konu");
        await MessageAsync(lead, plain.Id, "note", "ÖZEL-İÇ-NOT-METNİ");
        await TicketAsync(foreign, other, "Başka tenant talebi");

        var response = await manager.GetAsync("/api/support/tickets/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("number,subject,", csv.TrimStart((char)0xFEFF));
        Assert.Contains(plain.Number, csv);
        Assert.Contains("\"Normal, virgüllü konu\"", csv);
        Assert.Contains("'=HYPERLINK", csv); // formül etkisizleştirilir
        Assert.DoesNotContain(",=HYPERLINK", csv);
        Assert.DoesNotContain("ÖZEL-İÇ-NOT-METNİ", csv);
        Assert.DoesNotContain("Başka tenant talebi", csv);
        Assert.Contains(formula.Number, csv);

        // Süzgeç liste ekranıyla aynıdır.
        var filtered = await (await manager.GetAsync($"/api/support/tickets/export?number={plain.Number}")).Content.ReadAsStringAsync();
        Assert.Contains(plain.Number, filtered);
        Assert.DoesNotContain(formula.Number, filtered);

        // Yetki: dışa aktarma izni olmayan 403; denetim kaydı yazıldı.
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.GetAsync("/api/support/tickets/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/support/tickets/export")).StatusCode);
        using var scope = factory.OpenScope(s.Tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        Assert.True(await db.AuditLog.CountAsync(a => a.Action == AuditAction.Exported && a.EntityKey == "support.ticket") >= 2);
    }

    // ---- Toplu işlem

    private static async Task<List<BulkTicketResultDto>> BulkAsync(HttpClient client, BulkTicketsRequest request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.PostAsJsonAsync("/api/support/tickets/bulk", request);
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        return expected == HttpStatusCode.OK ? (await response.Content.ReadFromJsonAsync<List<BulkTicketResultDto>>())! : [];
    }

    [Fact]
    public async Task Bulk_actions_apply_each_tickets_own_rules_and_report_every_outcome()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var lead = await LoginAsync(s.Lead);
        using var agent = await LoginAsync(s.Agent);
        using var foreign = await LoginAsync(other.Lead);
        var a = await TicketAsync(lead, s, "Toplu A");
        var b = await TicketAsync(lead, s, "Toplu B");
        var foreignTicket = await TicketAsync(foreign, other, "Başka tenant");

        // Atama: kendi tenant'ındakiler atanır; başka tenant'ın ve olmayan talep tek tip NotFound alır; işlem diğerlerini engellemez.
        var results = await BulkAsync(lead, new BulkTicketsRequest("assign", [a.Id, foreignTicket.Id, Guid.NewGuid(), b.Id], OwnerId: s.Agent.Id));
        Assert.Equal([true, false, false, true], results.Select(r => r.Succeeded));
        Assert.Equal(["NotFound", "NotFound"], results.Where(r => !r.Succeeded).Select(r => r.Code));
        Assert.Equal((s.Agent.Id, "Assigned"), ((await GetAsync(lead, a.Id)).OwnerId!.Value, (await GetAsync(lead, a.Id)).Status));
        Assert.Equal("New", (await GetAsync(foreign, foreignTicket.Id)).Status);
        Assert.Contains(await HistoryAsync(lead, a.Id), h => h.Kind == "Assigned");

        // Kapatma: çözülmemiş talep kendi kuralına takılır (409), çözülmüş talep kapanır; sonuçlar talep başına.
        var c = await ResolveAsync(lead, await StartAsync(lead, await TakeAsync(lead, await TicketAsync(lead, s, "Toplu C"))));
        var closed = await BulkAsync(lead, new BulkTicketsRequest("close", [a.Id, c.Id]));
        Assert.Equal([false, true], closed.Select(r => r.Succeeded));
        Assert.Equal("Conflict", closed[0].Code);
        Assert.Equal("Closed", (await GetAsync(lead, c.Id)).Status);

        // İptal gerekçe ister (talep başına 422); yetkisiz kullanıcı toplu işlemde de yetkisizdir (her talepte PermissionDenied).
        var noReason = await BulkAsync(lead, new BulkTicketsRequest("cancel", [b.Id]));
        Assert.Equal(("ValidationFailed", false), (noReason.Single().Code, noReason.Single().Succeeded));
        var denied = await BulkAsync(agent, new BulkTicketsRequest("close", [a.Id]));
        Assert.Equal("PermissionDenied", denied.Single().Code);

        // İstek doğrulaması: bilinmeyen eylem, boş liste, üst sınır.
        await BulkAsync(lead, new BulkTicketsRequest("delete", [a.Id]), HttpStatusCode.UnprocessableEntity);
        await BulkAsync(lead, new BulkTicketsRequest("assign", []), HttpStatusCode.UnprocessableEntity);
        await BulkAsync(lead, new BulkTicketsRequest("assign", [.. Enumerable.Range(0, 51).Select(_ => Guid.NewGuid())], OwnerId: s.Agent.Id), HttpStatusCode.UnprocessableEntity);
        using var anonymous = Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/support/tickets/bulk", new BulkTicketsRequest("close", [a.Id]))).StatusCode);
    }

    private sealed record RecordPage(int TotalCount);
}
