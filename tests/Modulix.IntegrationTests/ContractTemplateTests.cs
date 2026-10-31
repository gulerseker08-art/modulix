using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Auth;
using Modulix.Shared.Contracts;
using Modulix.Shared.Records;
using QuestPDF.Fluent;
using static Modulix.Modules.Contracts.ContractsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0022 dilim 4: şablon yaşam döngüsü (düzenleme ve yayımlama ayrı izin, sürümler, arşiv), belge üretimi (sürüm/şablon/veri anlık görüntüsü, SHA-256,
/// şablon değişse de eski belge sabit), eksik veri, tenant izolasyonu, indirme ve finansal izin.
/// </summary>
public class ContractTemplateTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Parties = "/api/records/" + SharedEntities.Party;

    private const string Body = "# SÖZLEŞME {{sozlesme.numara}}\n\nMüşteri: {{musteri.unvan}} ({{musteri.vergi_no}})\nBaşlangıç: {{sozlesme.baslangic_tarihi}} — Bitiş: {{sozlesme.bitis_tarihi}}\n\n{{kalem_tablosu}}\n\n{{imza_alani}}";

    [Fact]
    public async Task Editing_and_publishing_are_separate_permissions_versions_are_immutable_and_archived_templates_are_read_only()
    {
        var s = await SetUpAsync();
        using var editor = await LoginAsync(s.Editor);
        using var publisher = await LoginAsync(s.Publisher);

        // Geçersiz metin (bilinmeyen değişken, kod) kaydedilmez; satır numarası söylenir.
        var invalid = await editor.PostAsJsonAsync("/api/contracts/templates", new SaveContractTemplateRequest("Hatalı", "Merhaba {{yok.degisken}}\n{% if x %}"));
        var errors = (await ValidationProblemAsync(invalid)).Errors["body"];
        Assert.Contains(errors, e => e.Contains("Satır 1") && e.Contains("Bilinmeyen değişken"));
        Assert.Contains(errors, e => e.Contains("Satır 2"));

        var template = await PostAsync<ContractTemplateDto>(editor, "/api/contracts/templates", new SaveContractTemplateRequest("Hizmet şablonu", Body, UsageScope: "Hizmet sözleşmeleri"));
        Assert.Equal(("Draft", null, 64), (template.Status, template.PublishedVersionNo, template.BodySha256.Length));

        // Aynı ad ve dil ikinci kez açılamaz.
        var duplicate = await editor.PostAsJsonAsync("/api/contracts/templates", new SaveContractTemplateRequest("Hizmet şablonu", Body));
        Assert.Contains("zaten var", (await ValidationProblemAsync(duplicate)).Errors["name"][0]);

        // Düzenleyen yayımlayamaz (403); yayımlayan düzenleyemez (403).
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync($"/api/contracts/templates/{template.Id}/publish", new TemplateActionRequest(template.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await publisher.PutAsJsonAsync($"/api/contracts/templates/{template.Id}", Save(template) with { Description = "x" })).StatusCode);

        var published = await PublishAsync(publisher, template.Id, template.Version, "İlk sürüm");
        Assert.Equal(("Published", 1), (published.Status, published.PublishedVersionNo));
        // Değişiklik yokken yeniden yayımlama 409.
        Assert.Equal(HttpStatusCode.Conflict, (await publisher.PostAsJsonAsync($"/api/contracts/templates/{template.Id}/publish", new TemplateActionRequest(published.Version))).StatusCode);

        // Çalışma kopyası değişir, yayımlanmış sürüm değişmez; yayımlayınca V2 oluşur.
        var edited = await PutAsync<ContractTemplateDto>(editor, $"/api/contracts/templates/{template.Id}", Save(published) with { Body = Body + "\n\nEk madde." });
        Assert.NotEqual(published.BodySha256, edited.BodySha256);
        var v1 = (await publisher.GetFromJsonAsync<ContractTemplateVersionDto>($"/api/contracts/templates/{template.Id}/versions/1"))!;
        Assert.Equal(published.BodySha256, v1.Sha256);
        Assert.DoesNotContain("Ek madde.", v1.Body);
        var republished = await PublishAsync(publisher, template.Id, edited.Version, null);
        Assert.Equal(2, republished.PublishedVersionNo);
        var versions = (await publisher.GetFromJsonAsync<List<ContractTemplateVersionDto>>($"/api/contracts/templates/{template.Id}/versions"))!;
        Assert.Equal([2, 1], versions.Select(v => v.VersionNo));
        Assert.Null(versions[0].Body); // liste gövde taşımaz

        // Arşiv: gerekçe zorunlu, arşivli şablon düzenlenemez/yayımlanamaz, geri alınınca yayımlanmış döner; yayımlanmış şablon silinemez.
        Assert.True((await ValidationProblemAsync(await publisher.PostAsJsonAsync($"/api/contracts/templates/{template.Id}/archive", new TemplateActionRequest(republished.Version)))).Errors.ContainsKey("reason"));
        var archived = await ArchiveAsync(publisher, template.Id, republished.Version, "Yeni şablonla değişti");
        Assert.Equal("Archived", archived.Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await editor.PutAsJsonAsync($"/api/contracts/templates/{template.Id}", Save(archived) with { Description = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await publisher.PostAsJsonAsync($"/api/contracts/templates/{template.Id}/publish", new TemplateActionRequest(archived.Version))).StatusCode);
        var restore = await publisher.PostAsJsonAsync($"/api/contracts/templates/{template.Id}/restore", new TemplateActionRequest(archived.Version));
        Assert.Equal("Published", (await restore.Content.ReadFromJsonAsync<ContractTemplateDto>())!.Status);
        var current = (await editor.GetFromJsonAsync<ContractTemplateDto>($"/api/contracts/templates/{template.Id}"))!;
        Assert.Equal(HttpStatusCode.Conflict, (await editor.DeleteAsync($"/api/contracts/templates/{template.Id}?version={current.Version}")).StatusCode);

        // Hiç yayımlanmamış taslak silinebilir.
        var draft = await PostAsync<ContractTemplateDto>(editor, "/api/contracts/templates", new SaveContractTemplateRequest("Silinecek", "Merhaba {{musteri.unvan}}"));
        Assert.Equal(HttpStatusCode.NoContent, (await editor.DeleteAsync($"/api/contracts/templates/{draft.Id}?version={draft.Version}")).StatusCode);

        // Değişken kataloğu ve örnek metin (örnek olduğu açık).
        var variables = (await editor.GetFromJsonAsync<List<TemplateVariableDto>>("/api/contracts/templates/variables"))!;
        Assert.Contains(variables, v => v is { Key: "sozlesme.genel_toplam", Financial: true });
        var sample = (await editor.GetFromJsonAsync<TemplateSampleDto>("/api/contracts/templates/sample"))!;
        Assert.Contains("hukuki inceleme yapılmamıştır", sample.Notice);
        Assert.Equal(HttpStatusCode.Created, (await editor.PostAsJsonAsync("/api/contracts/templates", new SaveContractTemplateRequest(sample.Name, sample.Body, IsExample: true))).StatusCode);
    }

    [Fact]
    public async Task Document_is_generated_from_the_published_version_pinned_with_hash_and_unaffected_by_later_template_or_party_changes()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var template = await PublishedTemplateAsync(admin, "Sözleşme", Body);
        var contract = await NewContractAsync(admin, s, withLine: true);

        // Önizleme: bloklar ve eksik yok; taslak kopya (dondurulmamış).
        var preview = await admin.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/documents/preview", new PreviewContractDocumentRequest(template.Id));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var rendered = (await preview.Content.ReadFromJsonAsync<ContractPreviewDto>())!;
        Assert.Empty(rendered.Missing);
        Assert.False(rendered.IsFinal);
        Assert.Contains(rendered.Blocks, b => b.Kind == "Heading" && b.Text!.Contains(contract.Number));
        Assert.Contains(rendered.Blocks, b => b.Kind == "Table" && b.Rows!.Count == 1 && b.Rows[0].Any(c => c.Contains("2.400,00 TRY")));
        Assert.Contains(rendered.Blocks, b => b.Kind == "Paragraph" && b.Text!.Contains("Çağlayan Gıda A.Ş.") && b.Text.Contains("1234567890"));

        var response = await admin.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/documents", new GenerateContractDocumentRequest(template.Id));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var document = (await response.Content.ReadFromJsonAsync<ContractDocumentDto>())!;
        Assert.Equal((1, 1, false, false, 64), (document.ContractVersionNo, document.TemplateVersionNo, document.IsFinal, document.IsExample, document.Sha256.Length));
        Assert.EndsWith(".pdf", document.FileName);

        // Dosya: PDF, özet baytların SHA-256'sıyla aynı, önbelleğe alınmaz.
        var file = await admin.GetAsync($"/api/contracts/contracts/{contract.Id}/documents/{document.Id}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/pdf", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", file.Headers.CacheControl!.ToString());
        var bytes = await file.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.Equal(document.Sha256, Convert.ToHexString(SHA256.HashData(bytes)));
        Assert.Equal(document.SizeBytes, bytes.Length);

        var history = (await admin.GetFromJsonAsync<List<ContractHistoryDto>>($"/api/contracts/contracts/{contract.Id}/history"))!;
        Assert.Contains(history, h => h.Kind == "DocumentGenerated" && h.Reason!.Contains(document.Sha256[..12]));

        // Şablon değişip V2 yayımlanır, müşteri kartı değişir: eski belge aynı kalır, yeni belge V2'den üretilir.
        var edited = await PutAsync<ContractTemplateDto>(admin, $"/api/contracts/templates/{template.Id}", Save(template) with { Body = Body + "\n\nEk madde." });
        await PublishAsync(admin, template.Id, edited.Version, "V2");
        var party = (await admin.GetFromJsonAsync<RecordResponse>($"{Parties}/{s.PartyId}"))!;
        var rename = await admin.PutAsJsonAsync($"{Parties}/{s.PartyId}", new RecordWriteRequest(new JsonObject { ["name"] = "Yeni Ünvan A.Ş." }, party.Version));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        var same = (await admin.GetFromJsonAsync<List<ContractDocumentDto>>($"/api/contracts/contracts/{contract.Id}/documents"))!.Single();
        Assert.Equal((document.Sha256, 1), (same.Sha256, same.TemplateVersionNo));
        var again = await admin.GetByteArrayAsync($"/api/contracts/contracts/{contract.Id}/documents/{document.Id}/file");
        Assert.Equal(bytes, again);

        var second = await PostAsync<ContractDocumentDto>(admin, $"/api/contracts/contracts/{contract.Id}/documents", new GenerateContractDocumentRequest(template.Id));
        Assert.Equal(2, second.TemplateVersionNo);
        Assert.NotEqual(document.Sha256, second.Sha256);

        // Sürüm dondurulunca (onaya gönderim) belge kesin içeriktir.
        var current = (await admin.GetFromJsonAsync<ContractDto>($"/api/contracts/contracts/{contract.Id}"))!;
        foreach (var action in new[] { "send-to-review", "mark-ready", "submit-for-approval" })
        {
            current = await RunAsync(admin, contract.Id, action, current.Version);
        }

        var final = await PostAsync<ContractDocumentDto>(admin, $"/api/contracts/contracts/{contract.Id}/documents", new GenerateContractDocumentRequest(template.Id));
        Assert.True(final.IsFinal);
    }

    [Fact]
    public async Task Missing_data_unpublished_or_archived_templates_and_wrong_type_do_not_produce_a_document()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var template = await PublishedTemplateAsync(admin, "Eksik veri", "Telefon: {{musteri.telefon}} / E-posta: {{musteri.eposta}} / {{musteri.unvan}}");
        var contract = await NewContractAsync(admin, s, withLine: false);

        var missing = await admin.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/documents", new GenerateContractDocumentRequest(template.Id));
        var message = (await ValidationProblemAsync(missing)).Errors["templateId"][0];
        Assert.Contains("Müşteri telefonu", message);
        Assert.Contains("Müşteri e-postası", message);
        Assert.Empty((await admin.GetFromJsonAsync<List<ContractDocumentDto>>($"/api/contracts/contracts/{contract.Id}/documents"))!);

        // Kalem tablosu istenir ama kalem yok.
        var table = await PublishedTemplateAsync(admin, "Tablolu", "{{kalem_tablosu}}");
        Assert.Contains("Kalem tablosu", (await ValidationProblemAsync(await admin.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/documents", new GenerateContractDocumentRequest(table.Id)))).Errors["templateId"][0]);

        // Yayımlanmamış şablon: belge üretilmez; çalışma kopyası önizlenebilir ama belge değildir.
        var draft = await PostAsync<ContractTemplateDto>(admin, "/api/contracts/templates", new SaveContractTemplateRequest("Taslak", "{{musteri.unvan}}"));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/documents", new GenerateContractDocumentRequest(draft.Id))).StatusCode);
        var draftPreview = (await (await admin.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/documents/preview", new PreviewContractDocumentRequest(draft.Id, UseDraft: true))).Content.ReadFromJsonAsync<ContractPreviewDto>())!;
        Assert.True(draftPreview.IsDraftTemplate);

        // Sözleşme türüne bağlı şablon başka türde kullanılamaz.
        var typed = await PostAsync<ContractTemplateDto>(admin, "/api/contracts/templates", new SaveContractTemplateRequest("Yalnız satış", "{{musteri.unvan}}", ContractType: "Sales"));
        await PublishAsync(admin, typed.Id, typed.Version, null);
        Assert.Contains("sözleşme türü", (await ValidationProblemAsync(await admin.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/documents", new GenerateContractDocumentRequest(typed.Id)))).Errors["templateId"][0]);

        // Arşivlenmiş şablondan belge üretilmez.
        var ok = await PublishedTemplateAsync(admin, "Arşivlenecek", "{{musteri.unvan}}");
        var current = (await admin.GetFromJsonAsync<ContractTemplateDto>($"/api/contracts/templates/{ok.Id}"))!;
        await ArchiveAsync(admin, ok.Id, current.Version, "Kullanımdan kalktı");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/documents", new GenerateContractDocumentRequest(ok.Id))).StatusCode);
    }

    [Fact]
    public async Task Tenants_and_unauthorized_users_cannot_read_generate_or_download_documents_and_financial_templates_need_the_financial_permission()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        using var generator = await LoginAsync(a.Generator);
        using var reader = await LoginAsync(a.Reader);
        var simple = await PublishedTemplateAsync(adminA, "Basit", "{{musteri.unvan}} — {{sozlesme.numara}}");
        var financial = await PublishedTemplateAsync(adminA, "Tutarlı", "Toplam: {{sozlesme.genel_toplam}}");
        var contract = await NewContractAsync(adminA, a, withLine: true);
        var document = await PostAsync<ContractDocumentDto>(adminA, $"/api/contracts/contracts/{contract.Id}/documents", new GenerateContractDocumentRequest(simple.Id));

        // Başka tenant: sözleşme, şablon, belge listesi, üretim, indirme 404.
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/contracts/templates/{simple.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/contracts/contracts/{contract.Id}/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/contracts/contracts/{contract.Id}/documents/{document.Id}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/documents", new GenerateContractDocumentRequest(simple.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/contracts/templates/{simple.Id}/versions")).StatusCode);
        var templatesB = (await adminB.GetFromJsonAsync<PagedResult<ContractTemplateDto>>("/api/contracts/templates"))!;
        Assert.DoesNotContain(templatesB.Items, t => t.Id == simple.Id);

        // Üretme izni olan ama indirme/finansal izni olmayan kullanıcı: üretir, indiremez; tutarlı şablonu üretemez.
        Assert.Equal(HttpStatusCode.Created, (await generator.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/documents", new GenerateContractDocumentRequest(simple.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await generator.GetAsync($"/api/contracts/contracts/{contract.Id}/documents/{document.Id}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await generator.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/documents", new GenerateContractDocumentRequest(financial.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await generator.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/documents/preview", new PreviewContractDocumentRequest(financial.Id))).StatusCode);

        // Yalnızca okuyucu: belge listesini görür (meta), üretemez ve indiremez; çalışma kopyası önizleyemez.
        Assert.Single((await reader.GetFromJsonAsync<List<ContractDocumentDto>>($"/api/contracts/contracts/{contract.Id}/documents"))!, d => d.Id == document.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/documents", new GenerateContractDocumentRequest(simple.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/contracts/contracts/{contract.Id}/documents/{document.Id}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/contracts/contracts/{contract.Id}/documents/preview", new PreviewContractDocumentRequest(simple.Id, UseDraft: true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/contracts/templates", new SaveContractTemplateRequest("Yetkisiz", "x"))).StatusCode);
    }

    [Fact]
    public void Pdf_has_multiple_pages_for_long_tables_and_marks_example_and_draft_copies()
    {
        var rows = Enumerable.Range(1, 150).Select(i => (IReadOnlyList<string>)[i.ToString(), $"Çalışma saati — şişirilmiş ünvanlı kalem {i}", "1", "100,00 TRY", "20", "Tek seferlik", "100,00 TRY", "120,00 TRY"]).ToList();
        var model = new Modulix.Modules.Contracts.Lifecycle.ContractPdfModel(
            "Yıllık bakım", "SZ-20261031-TEST12", 1, "Örnek şablon", 1, "Modül Hizmetleri A.Ş.", "İzmir · Vergi no 1234567890", "Gizlidir", IsExample: true, IsFinal: false,
            new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero),
            [
                new Modulix.Modules.Contracts.Lifecycle.HeadingBlock(1, "SÖZLEŞME"),
                new Modulix.Modules.Contracts.Lifecycle.ParagraphBlock("Çağlayan Şişecam Gıda A.Ş. ile İğdır Öğüt arasında ğüşiöçİı karakterleri denetlenir."),
                new Modulix.Modules.Contracts.Lifecycle.TableBlock(["#", "Kalem", "Miktar", "Birim fiyat", "Vergi %", "Dönem", "Net", "Toplam"], rows, ["Genel toplam: 18.000,00 TRY"]),
                new Modulix.Modules.Contracts.Lifecycle.PageBreakBlock(),
                new Modulix.Modules.Contracts.Lifecycle.SignatureBlock([new("Müşteri adına", "Çağlayan"), new("Hizmet veren adına", "Modül")]),
            ]);

        var pdf = Modulix.Api.Contracts.ContractPdf.Render(model);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
        var images = Modulix.Api.Contracts.ContractPdf.Build(model).GenerateImages().ToList();
        Assert.True(images.Count >= 4, $"150 satırlık tablo ve sayfa sonu en az 4 sayfa üretmeli, üretilen: {images.Count}.");

        // Aynı girdiden iki kez üretim aynı sayfa sayısını verir (üst bilgi sabit).
        Assert.Equal(images.Count, Modulix.Api.Contracts.ContractPdf.Build(model).GenerateImages().Count());
    }

    private static async Task<ContractDto> RunAsync(HttpClient client, Guid id, string action, long version)
    {
        var response = await client.PostAsJsonAsync($"/api/contracts/contracts/{id}/{action}", new ContractTransitionRequest(version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ContractDto>())!;
    }

    private static SaveContractTemplateRequest Save(ContractTemplateDto t) =>
        new(t.Name, t.Body, t.Language, t.ContractType, t.UsageScope, t.Description, t.IsExample, t.Version);

    private static async Task<ContractTemplateDto> PublishedTemplateAsync(HttpClient client, string name, string body)
    {
        var template = await PostAsync<ContractTemplateDto>(client, "/api/contracts/templates", new SaveContractTemplateRequest(name, body));
        return await PublishAsync(client, template.Id, template.Version, null);
    }

    private static async Task<ContractTemplateDto> PublishAsync(HttpClient client, Guid id, long version, string? notes)
    {
        var response = await client.PostAsJsonAsync($"/api/contracts/templates/{id}/publish", new TemplateActionRequest(version, notes));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ContractTemplateDto>())!;
    }

    private static async Task<ContractTemplateDto> ArchiveAsync(HttpClient client, Guid id, long version, string reason)
    {
        var response = await client.PostAsJsonAsync($"/api/contracts/templates/{id}/archive", new TemplateActionRequest(version, reason));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ContractTemplateDto>())!;
    }

    private async Task<ContractDto> NewContractAsync(HttpClient client, Setup s, bool withLine)
    {
        var contract = await PostAsync<ContractDto>(client, "/api/contracts/contracts", new SaveContractRequest(
            "Yıllık bakım", s.PartyId, StartDate: new DateOnly(2026, 11, 1), EndDate: new DateOnly(2027, 10, 31)));
        if (withLine)
        {
            await PostAsync<ContractLineDto>(client, "/api/contracts/contract-lines", new SaveContractLineRequest(contract.Id, "Kurulum", Quantity: 2m, UnitPrice: 1000m, TaxRatePercent: 20m));
            contract = (await client.GetFromJsonAsync<ContractDto>($"/api/contracts/contracts/{contract.Id}"))!;
        }

        return contract;
    }

    private sealed record Setup(ModulixUser Admin, ModulixUser Editor, ModulixUser Publisher, ModulixUser Generator, ModulixUser Reader, Guid PartyId);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "contracts", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var editor = await factory.CreateUserAsync(tenant);
        var publisher = await factory.CreateUserAsync(tenant);
        var generator = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, TemplatesEdit], Scope.All, editor);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, TemplatesPublish], Scope.All, publisher);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, DocumentsGenerate], Scope.All, generator);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead], Scope.All, reader);
        using var client = await LoginAsync(admin);
        var response = await client.PostAsJsonAsync(Parties, new RecordWriteRequest(new JsonObject
        {
            ["name"] = "Çağlayan Gıda A.Ş.", ["taxNumber"] = "1234567890", ["taxOffice"] = "Konak", ["address"] = "Alsancak Mah. 1. Sk. No:1", ["city"] = "İzmir",
        }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new Setup(admin, editor, publisher, generator, reader, (await response.Content.ReadFromJsonAsync<RecordResponse>())!.Id);
    }

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

    private static async Task<T> PutAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PutAsJsonAsync(url, body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
