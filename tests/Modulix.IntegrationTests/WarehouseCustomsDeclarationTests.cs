using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using static Modulix.Modules.Warehouse.WarehouseModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0032 dilim 4a (beyanname başlığı ve satırları, K6, K10, K17): mutlu yol (oluşturma, süzme, durum, satır ekleme/güncelleme/silme),
/// doğrulama (tescil no, taraf rolleri, net ≤ brüt, yasal çekirdek alanlar, iptal edilmiş beyanname), tenant izolasyonu ve yetki.
/// </summary>
public class WarehouseCustomsDeclarationTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Declarations = "/api/records/warehouse.customs-declaration";
    private const string Lines = "/api/records/warehouse.customs-declaration-line";
    private const string Parties = "/api/records/core.party";

    [Fact]
    public async Task Declaration_is_created_filtered_given_lines_updated_and_deleted()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var importer = await PartyAsync(admin, "Mersin İthalat A.Ş.", "ithalatci-malik");
        var broker = await PartyAsync(admin, "Liman Müşavirlik Ltd.", "gumruk-musaviri");

        // Rejim ve durum verilmezse listelerin varsayılanı atanır; tescil no büyük harfe çevrilir.
        var first = await PostAsync(admin, Declarations, Declaration("25341300im00123456", importer, broker, "2026-03-10", externalReference: "EVRIM-1001"));
        Assert.Equal("25341300IM00123456", first.Values["registrationNo"]!.GetValue<string>());
        Assert.Equal("R7100", first.Values["regimeKey"]!.GetValue<string>());
        Assert.Equal("Draft", first.Values["statusKey"]!.GetValue<string>());
        var second = await PostAsync(admin, Declarations, Declaration("BEY-2", importer, null, "2026-05-02"));
        Assert.Null(second.Values["brokerPartyId"]);

        Assert.Equal([second.Id, first.Id], (await ListAsync(admin, Declarations, null)).Select(i => i.Id));
        Assert.Equal([first.Id], (await ListAsync(admin, Declarations, $"brokerPartyId={broker}")).Select(i => i.Id));
        Assert.Equal(2, (await ListAsync(admin, Declarations, $"importerPartyId={importer}")).Count);
        Assert.Equal([second.Id], (await ListAsync(admin, Declarations, "registeredFrom=2026-04-01")).Select(i => i.Id));
        Assert.Equal([first.Id], (await ListAsync(admin, Declarations, "registeredTo=2026-03-31")).Select(i => i.Id));
        Assert.Equal([first.Id], (await ListAsync(admin, Declarations, "search=evrim")).Select(i => i.Id));
        Assert.Equal([first.Id], (await ListAsync(admin, Declarations, "statusKey=Draft&search=00123456")).Select(i => i.Id));

        // Durum tescilliye alınır; satırlar eklenir (GTİP ayraçları atılır, baştaki sıfır korunur, ondalıklar aynen döner).
        var registered = await PutAsync(admin, Declarations, first, new JsonObject { ["statusKey"] = "Registered" });
        Assert.Equal("Registered", registered.Values["statusKey"]!.GetValue<string>());
        var line1 = await PostAsync(admin, Lines, Line(registered.Id, 1));
        var line2 = await PostAsync(admin, Lines, Line(registered.Id, 2, gtip: "1805.00.00", origin: "DE", gross: 10.125m, net: 10.125m, description: "Kakao tozu"));
        Assert.Equal("09023000", line1.Values["gtip"]!.GetValue<string>());
        Assert.Equal(1250.5m, line1.Values["grossKg"]!.GetValue<decimal>());
        Assert.Equal("18050000", line2.Values["gtip"]!.GetValue<string>());
        Assert.Equal(10.125m, line2.Values["netKg"]!.GetValue<decimal>());

        Assert.Equal([line1.Id, line2.Id], (await ListAsync(admin, Lines, $"declarationId={registered.Id}")).Select(i => i.Id));
        Assert.Equal([line2.Id], (await ListAsync(admin, Lines, "gtip=1805")).Select(i => i.Id));
        Assert.Equal([line2.Id], (await ListAsync(admin, Lines, "originCountryKey=DE")).Select(i => i.Id));
        Assert.Equal([line1.Id], (await ListAsync(admin, Lines, "search=çay")).Select(i => i.Id));

        var updated = await PutAsync(admin, Lines, line1, new JsonObject { ["netKg"] = 1100.25m, ["quantity"] = 40 });
        Assert.Equal(1100.25m, updated.Values["netKg"]!.GetValue<decimal>());
        Assert.True(updated.Version > line1.Version);

        // Satırları olan beyanname silinemez; önce satırlar silinir.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Declarations}/{registered.Id}?version={registered.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Lines}/{updated.Id}?version={updated.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Lines}/{line2.Id}?version={line2.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Declarations}/{registered.Id}?version={registered.Version}")).StatusCode);

        // Firma, beyannamesi olduğu sürece silinemez (ortak kayıt koruması).
        var party = (await admin.GetFromJsonAsync<RecordResponse>($"{Parties}/{importer}"))!;
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Parties}/{importer}?version={party.Version}")).StatusCode);
    }

    [Fact]
    public async Task Declaration_registration_no_parties_and_external_reference_are_validated()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var importer = await PartyAsync(admin, "İthalatçı A.Ş.", "ithalatci-malik");
        var broker = await PartyAsync(admin, "Müşavir Ltd.", "gumruk-musaviri");
        var plain = await PartyAsync(admin, "Rolsüz Firma");
        await PostAsync(admin, Declarations, Declaration("Bey-1", importer, broker, "2026-01-05", externalReference: "DIS-1"));

        // Tescil no tenant içinde benzersizdir (büyük/küçük harf yok sayılır) ve biçimi denetlenir; zorunlu alanlar eksik bırakılamaz.
        var duplicate = await ValidationProblemAsync(await admin.PostAsJsonAsync(Declarations, new RecordWriteRequest(Declaration("BEY-1", importer, broker, "2026-01-06"))));
        Assert.Equal(["Bu tescil numarası başka bir beyannamede kullanılıyor."], duplicate.Errors["registrationNo"]);
        var badNo = await ValidationProblemAsync(await admin.PostAsJsonAsync(Declarations, new RecordWriteRequest(Declaration("BEY 2", importer, broker, "2026-01-06"))));
        Assert.Contains("registrationNo", badNo.Errors.Keys);
        var missing = await ValidationProblemAsync(await admin.PostAsJsonAsync(Declarations, new RecordWriteRequest(new JsonObject { ["description"] = "Eksik" })));
        Assert.Contains("registrationNo", missing.Errors.Keys);
        Assert.Contains("registrationDate", missing.Errors.Keys);
        Assert.Contains("importerPartyId", missing.Errors.Keys);
        var badDate = await ValidationProblemAsync(await admin.PostAsJsonAsync(Declarations, new RecordWriteRequest(Declaration("BEY-3", importer, broker, "10.03.2026"))));
        Assert.Contains("registrationDate", badDate.Errors.Keys);

        // Dış referans doluysa tenant içinde benzersizdir (K8).
        var external = await ValidationProblemAsync(await admin.PostAsJsonAsync(Declarations, new RecordWriteRequest(Declaration("BEY-4", importer, broker, "2026-01-07", externalReference: "DIS-1"))));
        Assert.Contains("externalReference", external.Errors.Keys);

        // Taraflar: aynı firma olamaz; ithalatçı ve müşavir rolü gerekir (rol firma kaydından verilir).
        var same = await ValidationProblemAsync(await admin.PostAsJsonAsync(Declarations, new RecordWriteRequest(Declaration("BEY-5", importer, importer, "2026-01-07"))));
        Assert.Contains("brokerPartyId", same.Errors.Keys);
        var noImporterRole = await ValidationProblemAsync(await admin.PostAsJsonAsync(Declarations, new RecordWriteRequest(Declaration("BEY-6", plain, broker, "2026-01-07"))));
        Assert.Contains("importerPartyId", noImporterRole.Errors.Keys);
        var noBrokerRole = await ValidationProblemAsync(await admin.PostAsJsonAsync(Declarations, new RecordWriteRequest(Declaration("BEY-7", importer, plain, "2026-01-07"))));
        Assert.Contains("brokerPartyId", noBrokerRole.Errors.Keys);

        // Rejim ve durum tenant'ın listesinden gelir.
        var badRegime = await ValidationProblemAsync(await admin.PostAsJsonAsync(Declarations, new RecordWriteRequest(Declaration("BEY-8", importer, broker, "2026-01-07", regime: "R9999"))));
        Assert.Contains("regimeKey", badRegime.Errors.Keys);
        var badStatus = await ValidationProblemAsync(await admin.PostAsJsonAsync(Declarations, new RecordWriteRequest(Declaration("BEY-9", importer, broker, "2026-01-07", status: "Bilinmiyor"))));
        Assert.Contains("statusKey", badStatus.Errors.Keys);

        // Müşavir sonradan eklenebilir; rolü yoksa reddedilir.
        var noBroker = await PostAsync(admin, Declarations, Declaration("BEY-10", importer, null, "2026-01-08"));
        var withPlain = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Declarations}/{noBroker.Id}",
            new RecordWriteRequest(new JsonObject { ["brokerPartyId"] = plain.ToString() }, noBroker.Version)));
        Assert.Contains("brokerPartyId", withPlain.Errors.Keys);
        var withBroker = await PutAsync(admin, Declarations, noBroker, new JsonObject { ["brokerPartyId"] = broker.ToString() });
        Assert.Equal(broker.ToString(), withBroker.Values["brokerPartyId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Declaration_line_keeps_the_legal_core_fields_and_invariants()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var importer = await PartyAsync(admin, "İthalatçı A.Ş.", "ithalatci-malik");
        var declaration = await PostAsync(admin, Declarations, Declaration("LINE-1", importer, null, "2026-02-01"));
        var line = await PostAsync(admin, Lines, Line(declaration.Id, 1));

        async Task<ValidationProblemDetails> Reject(JsonObject values) =>
            await ValidationProblemAsync(await admin.PostAsJsonAsync(Lines, new RecordWriteRequest(values)));

        // Net kilo brüt kilodan büyük olamaz (değişmez); güncellemede de.
        Assert.Contains("netKg", (await Reject(Line(declaration.Id, 2, gross: 100m, net: 100.001m))).Errors.Keys);
        var heavier = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Lines}/{line.Id}", new RecordWriteRequest(new JsonObject { ["netKg"] = 5000 }, line.Version)));
        Assert.Contains("netKg", heavier.Errors.Keys);

        // GTİP yalnızca rakam ve 6-14 hane; satır numarası beyanname içinde benzersiz; sayı sınırları ve ondalık basamak.
        Assert.Contains("gtip", (await Reject(Line(declaration.Id, 2, gtip: "ABC123"))).Errors.Keys);
        Assert.Contains("gtip", (await Reject(Line(declaration.Id, 2, gtip: "1234"))).Errors.Keys);
        Assert.Equal(["Bu satır numarası beyannamede zaten var."], (await Reject(Line(declaration.Id, 1))).Errors["lineNo"]);
        Assert.Contains("lineNo", (await Reject(Line(declaration.Id, 0))).Errors.Keys);
        var zeroCount = Line(declaration.Id, 2);
        zeroCount["containerCount"] = 0;
        Assert.Contains("containerCount", (await Reject(zeroCount)).Errors.Keys);
        Assert.Contains("grossKg", (await Reject(Line(declaration.Id, 2, gross: 10.1234m, net: 1m))).Errors.Keys);
        var zeroQuantity = Line(declaration.Id, 2);
        zeroQuantity["quantity"] = 0;
        Assert.Contains("quantity", (await Reject(zeroQuantity)).Errors.Keys);

        // Yasal çekirdek liste alanları (kap cinsi, menşe, birim) sessizce varsayılana düşmez: açıkça seçilmeli ve listede olmalı.
        foreach (var field in new[] { "containerTypeKey", "originCountryKey", "unitKey" })
        {
            var without = Line(declaration.Id, 2);
            without.Remove(field);
            Assert.Equal(["Bu alan zorunludur."], (await Reject(without)).Errors[field]);
        }

        var unknownOrigin = Line(declaration.Id, 2, origin: "ZZ");
        Assert.Contains("originCountryKey", (await Reject(unknownOrigin)).Errors.Keys);
        var withoutGtip = Line(declaration.Id, 2);
        withoutGtip.Remove("gtip");
        Assert.Contains("gtip", (await Reject(withoutGtip)).Errors.Keys);
        Assert.Contains("declarationId", (await Reject(Line(Guid.NewGuid(), 2))).Errors.Keys);

        // Beyanname sonradan değiştirilemez (bir satır başka beyannameye taşınamaz).
        var other = await PostAsync(admin, Declarations, Declaration("LINE-2", importer, null, "2026-02-02"));
        var move = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Lines}/{line.Id}", new RecordWriteRequest(new JsonObject { ["declarationId"] = other.Id.ToString() }, line.Version)));
        Assert.Contains("declarationId", move.Errors.Keys);

        // İptal kategorisindeki beyannamenin satırları eklenemez, değiştirilemez ve silinemez (kod durumun adına değil kategorisine bakar).
        var cancelled = await PutAsync(admin, Declarations, declaration, new JsonObject { ["statusKey"] = "Cancelled" });
        Assert.Contains("declarationId", (await Reject(Line(cancelled.Id, 3))).Errors.Keys);
        var edit = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Lines}/{line.Id}", new RecordWriteRequest(new JsonObject { ["quantity"] = 5 }, line.Version)));
        Assert.Contains("declarationId", edit.Errors.Keys);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Lines}/{line.Id}?version={line.Version}")).StatusCode);
        var reopened = await PutAsync(admin, Declarations, cancelled, new JsonObject { ["statusKey"] = "Draft" });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Lines}/{line.Id}?version={line.Version}")).StatusCode);
        Assert.Equal("Draft", reopened.Values["statusKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task Declarations_of_another_tenant_are_invisible_and_unusable()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var importerA = await PartyAsync(adminA, "A İthalat", "ithalatci-malik");
        var declarationA = await PostAsync(adminA, Declarations, Declaration("ORTAK-1", importerA, null, "2026-04-01"));
        var lineA = await PostAsync(adminA, Lines, Line(declarationA.Id, 1));

        // B, A'nın beyannamesini/satırını göremez, değiştiremez, silemez ve satır ekleyemez; varlığı belli edilmez.
        Assert.Empty(await ListAsync(adminB, Declarations, null));
        Assert.Empty(await ListAsync(adminB, Lines, null));
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Declarations}/{declarationA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Lines}/{lineA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PutAsJsonAsync($"{Declarations}/{declarationA.Id}", new RecordWriteRequest(new JsonObject { ["description"] = "x" }, declarationA.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"{Lines}/{lineA.Id}?version={lineA.Version}")).StatusCode);
        var importerB = await PartyAsync(adminB, "B İthalat", "ithalatci-malik");
        var foreignDeclaration = await ValidationProblemAsync(await adminB.PostAsJsonAsync(Lines, new RecordWriteRequest(Line(declarationA.Id, 2))));
        Assert.Equal(["Beyanname bulunamadı."], foreignDeclaration.Errors["declarationId"]);
        var foreignImporter = await ValidationProblemAsync(await adminB.PostAsJsonAsync(Declarations, new RecordWriteRequest(Declaration("B-1", importerA, null, "2026-04-01"))));
        Assert.Equal(["İthalatçı bulunamadı."], foreignImporter.Errors["importerPartyId"]);

        // Benzersizlik TenantId ile birlikte tanımlıdır: B aynı tescil no ve satır no ile kendi kaydını açar.
        var declarationB = await PostAsync(adminB, Declarations, Declaration("ORTAK-1", importerB, null, "2026-04-01"));
        await PostAsync(adminB, Lines, Line(declarationB.Id, 1));
        Assert.NotEqual(declarationA.Id, declarationB.Id);
        Assert.Equal(declarationA.Id.ToString(), (await ListAsync(adminA, Lines, null)).Single().Values["declarationId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Declarations_and_lines_need_their_own_permission()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var reader = await LoginAsync(s.Reader);
        using var writer = await LoginAsync(s.Writer);
        using var attorneysOnly = await LoginAsync(s.AttorneysOnly);
        var importer = await PartyAsync(admin, "Yetki İthalat", "ithalatci-malik");
        var declaration = await PostAsync(admin, Declarations, Declaration("YETKI-1", importer, null, "2026-06-01"));
        var line = await PostAsync(admin, Lines, Line(declaration.Id, 1));

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Declarations}/{declaration.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Lines}/{line.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Declarations, new RecordWriteRequest(Declaration("R-1", importer, null, "2026-06-02")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Lines, new RecordWriteRequest(Line(declaration.Id, 2)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{Lines}/{line.Id}", new RecordWriteRequest(new JsonObject { ["quantity"] = 5 }, line.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Declarations}/{declaration.Id}?version={declaration.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await writer.PostAsJsonAsync(Declarations, new RecordWriteRequest(Declaration("W-1", importer, null, "2026-06-02")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await writer.PostAsJsonAsync(Lines, new RecordWriteRequest(Line(declaration.Id, 2)))).StatusCode);

        // Vekâlet izni beyanname izni vermez.
        Assert.Equal(HttpStatusCode.Forbidden, (await attorneysOnly.GetAsync(Declarations)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await attorneysOnly.GetAsync(Lines)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await attorneysOnly.GetAsync("/api/records/warehouse.power-of-attorney")).StatusCode);
    }

    private static JsonObject Declaration(string number, Guid importer, Guid? broker, string date, string? externalReference = null, string? regime = null, string? status = null)
    {
        var values = new JsonObject
        {
            ["registrationNo"] = number, ["registrationDate"] = date, ["importerPartyId"] = importer.ToString(), ["customsOffice"] = "Mersin Gümrük Müdürlüğü",
        };
        if (broker is { } b) { values["brokerPartyId"] = b.ToString(); }
        if (externalReference is not null) { values["externalReference"] = externalReference; }
        if (regime is not null) { values["regimeKey"] = regime; }
        if (status is not null) { values["statusKey"] = status; }
        return values;
    }

    private static JsonObject Line(
        Guid declarationId, int lineNo, string gtip = "0902.30.00", string origin = "CN", decimal gross = 1250.5m, decimal net = 1200m,
        string description = "Siyah çay (fermente)") => new()
    {
        ["declarationId"] = declarationId.ToString(), ["lineNo"] = lineNo, ["gtip"] = gtip, ["description"] = description,
        ["containerTypeKey"] = "Pallet", ["containerCount"] = 2, ["grossKg"] = gross, ["netKg"] = net,
        ["originCountryKey"] = origin, ["quantity"] = 100, ["unitKey"] = "Kilogram",
    };

    private async Task<(ModulixUser Admin, ModulixUser Reader, ModulixUser Writer, ModulixUser AttorneysOnly)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var writer = await factory.CreateUserAsync(tenant);
        var attorneysOnly = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [DeclarationsRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [DeclarationsRead, DeclarationsWrite], Scope.All, writer);
        await factory.CreateRoleAsync(tenant.Id, [AttorneysRead], Scope.All, attorneysOnly);
        return (admin, reader, writer, attorneysOnly);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<Guid> PartyAsync(HttpClient client, string name, params string[] roles) =>
        (await PostAsync(client, Parties, new JsonObject
        {
            ["name"] = name,
            ["taxNumber"] = Guid.NewGuid().ToString("N")[..10],
            ["relationshipRoles"] = new JsonArray([.. roles.Select(r => (JsonNode?)JsonValue.Create(r))]),
        })).Id;

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string url, string? query)
    {
        var response = await client.GetAsync($"{url}?pageSize=50{(query is null ? string.Empty : "&" + query)}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordPageResponse>())!.Items;
    }

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<RecordResponse> PutAsync(HttpClient client, string url, RecordResponse record, JsonObject values)
    {
        var response = await client.PutAsJsonAsync($"{url}/{record.Id}", new RecordWriteRequest(values, record.Version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
