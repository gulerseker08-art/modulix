using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Contracts.Lifecycle;
using Modulix.Shared.Auth;
using Modulix.Shared.Contracts;
using Modulix.Shared.Records;
using Modulix.Shared.Settings;
using static Modulix.Modules.Contracts.ContractsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0022 dilim 6: imza. Mutlu yol (sıralı manuel imza ve doğrulama, paralel simülasyon sağlayıcısı, yürürlüğe geçiş), doğrulama (dosya, tarih, imzacı, belge, ön koşullar),
/// webhook güvenliği (imza, zaman damgası, idempotency, sıra), tenant izolasyonu ve yetki (yönetme/doğrulama ayrımı, kendi imzasını doğrulayamama, kanıt indirme).
/// </summary>
public class ContractSignatureFlowTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Parties = "/api/records/" + SharedEntities.Party;
    private const string Contracts = "/api/contracts/contracts";
    private static readonly byte[] SignedPdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\n%%EOF");
    private static readonly JsonSerializerOptions Web = JsonSerializerOptions.Web;

    private sealed record Setup(
        Tenant Tenant, ModulixUser Admin, ModulixUser Approver, ModulixUser Manager, ModulixUser Verifier, ModulixUser Signer, ModulixUser Reader, Guid PartyId);

    [Fact]
    public async Task Sequential_manual_signing_is_verified_in_order_completes_the_contract_and_puts_the_signed_version_in_force()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var manager = await LoginAsync(s.Manager);
        using var verifier = await LoginAsync(s.Verifier);
        var (contract, document) = await ApprovedWithDocumentAsync(admin, s);

        var started = await StartAsync(manager, contract, document, "Manual", "Sequential",
            new SignatoryInputDto("External", Name: "Zeynep Kaya", Capacity: "Müşteri yetkilisi"),
            new SignatoryInputDto("Internal", UserId: s.Signer.Id, Capacity: "Genel müdür"));
        Assert.Equal("Pending", started.SignatureStatus);
        var request = Assert.Single(started.Requests);
        Assert.Equal(document.Sha256, request.DocumentSha256);
        Assert.Equal(["Sent", "Waiting"], request.Signatories.Select(x => x.Status));
        Assert.Equal([true, false], request.Signatories.Select(x => x.CanSignNow));
        Assert.Equal("Pending", (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{contract.Id}"))!.SignatureStatus);

        // Sıra: ikinci imzacı birinciden önce imzalayamaz; geçersiz dosya ve gelecek tarih reddedilir.
        var first = request.Signatories[0];
        var second = request.Signatories[1];
        Assert.Equal(HttpStatusCode.Conflict, (await ManualAsync(verifier, contract.Id, second.Id, SignedPdf)).StatusCode);
        var text = await ValidationProblemAsync(await ManualAsync(verifier, contract.Id, first.Id, Encoding.UTF8.GetBytes("bu bir pdf değil")));
        Assert.Contains("file", text.Errors.Keys);
        var future = await ValidationProblemAsync(await ManualAsync(verifier, contract.Id, first.Id, SignedPdf, signedOn: Today().AddDays(2)));
        Assert.Contains("signedOn", future.Errors.Keys);

        var afterFirst = await ReadAsync<ContractSignatureDto>(await ManualAsync(verifier, contract.Id, first.Id, SignedPdf, "Islak imzalı nüsha"));
        Assert.Equal("PartiallySigned", afterFirst.SignatureStatus);
        var signedFirst = afterFirst.Requests[0].Signatories[0];
        Assert.Equal(("Signed", "ManualScan", s.Verifier.Id), (signedFirst.Status, signedFirst.Assurance, signedFirst.VerifiedBy));
        Assert.NotNull(signedFirst.EvidenceFileId);
        Assert.Equal("Sent", afterFirst.Requests[0].Signatories[1].Status);

        // Kanıt dosyası: indirme olarak, koklanmadan ve sandbox'lı sunulur; baytlar yüklenenle aynıdır.
        var file = await verifier.GetAsync($"{Contracts}/{contract.Id}/signature/files/{signedFirst.EvidenceFileId}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/pdf", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", file.Headers.GetValues("Content-Security-Policy").Single());
        Assert.NotNull(file.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal(SignedPdf, await file.Content.ReadAsByteArrayAsync());

        // İç imzacı kendi imzasını doğrulayamaz (başka bir yetkili doğrular).
        using var signerClient = await LoginAsync(s.Signer);
        Assert.Equal(HttpStatusCode.Forbidden, (await ManualAsync(signerClient, contract.Id, second.Id, SignedPdf)).StatusCode);

        var done = await ReadAsync<ContractSignatureDto>(await ManualAsync(verifier, contract.Id, second.Id, SignedPdf));
        Assert.Equal("Completed", done.SignatureStatus);
        Assert.Equal("Completed", done.Requests[0].Status);
        Assert.NotNull(done.Requests[0].CompletedAt);

        // Tamamlanınca sürüm imzalı olur, yürürlük sürümü yazılır ve başlangıç ileri tarihliyse durum "başlangıcı bekleniyor"dur.
        var after = (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{contract.Id}"))!;
        Assert.Equal(("Completed", 1, "AwaitingStart"), (after.SignatureStatus, after.EffectiveVersionNo, after.EffectiveStatus));
        Assert.Equal("Signed", (await admin.GetFromJsonAsync<ContractVersionDto>($"{Contracts}/{contract.Id}/versions/1"))!.Status);
        var history = (await admin.GetFromJsonAsync<List<ContractHistoryDto>>($"{Contracts}/{contract.Id}/history"))!.Select(h => h.Kind).ToList();
        Assert.Contains("SignatureRequested", history);
        Assert.Contains("SignatorySigned", history);
        Assert.Contains("SignatureCompleted", history);

        // İmzalı sürüm yerinde düzenlenmez ve yeniden imzaya gönderilemez.
        Assert.Equal(HttpStatusCode.Conflict, (await StartRawAsync(manager, contract, document, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Başka Kişi"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PutAsJsonAsync($"{Contracts}/{contract.Id}", Save(after) with { Title = "Değişti" })).StatusCode);
    }

    [Fact]
    public async Task Start_refuses_unapproved_contracts_example_or_draft_documents_and_incomplete_signatory_lists()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var manager = await LoginAsync(s.Manager);
        var template = await PublishedTemplateAsync(admin, "Basit", "Bu belge {{sozlesme.numara}} numaralı sözleşmedir.");
        var exampleTemplate = await PostAsync<ContractTemplateDto>(admin, "/api/contracts/templates",
            new SaveContractTemplateRequest("Örnek metin", "Bu örnek {{sozlesme.numara}} içindir.", IsExample: true));
        exampleTemplate = await PublishAsync(admin, exampleTemplate.Id, exampleTemplate.Version);

        // Onaysız sözleşme: taslak kopya belge üretilebilir ama imzaya gönderilemez.
        var draft = await ReadyAsync(admin, s.PartyId);
        var draftDoc = await GenerateAsync(admin, draft.Id, template.Id);
        Assert.False(draftDoc.IsFinal);
        Assert.Equal(HttpStatusCode.Conflict, (await StartRawAsync(manager, draft, draftDoc, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Ali Veli"))).StatusCode);

        // Onaylı sözleşme: taslak kopya (onay öncesi üretilen) belge ve örnek metinden üretilen belge reddedilir.
        var approved = await ApproveAsync(admin, s, draft);
        var errors = await ValidationProblemAsync(await StartRawAsync(manager, approved, draftDoc, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Ali Veli")));
        Assert.Contains("documentId", errors.Errors.Keys);
        var example = await GenerateAsync(admin, approved.Id, exampleTemplate.Id);
        Assert.True(example.IsExample);
        Assert.Contains("documentId", (await ValidationProblemAsync(await StartRawAsync(manager, approved, example, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Ali Veli")))).Errors.Keys);

        var document = await GenerateAsync(admin, approved.Id, template.Id);
        Assert.True(document.IsFinal);
        Assert.Contains("method", (await ValidationProblemAsync(await StartRawAsync(manager, approved, document, "Provider", "Sequential", new SignatoryInputDto("External", Name: "Ali", Email: "ali@ornek.test")))).Errors.Keys);
        Assert.Contains("method", (await ValidationProblemAsync(await StartRawAsync(manager, approved, document, "Fax", "Sequential", new SignatoryInputDto("External", Name: "Ali")))).Errors.Keys);
        Assert.Contains("mode", (await ValidationProblemAsync(await StartRawAsync(manager, approved, document, "Manual", "Zincir", new SignatoryInputDto("External", Name: "Ali")))).Errors.Keys);
        Assert.Contains("signatories", (await ValidationProblemAsync(await StartRawAsync(manager, approved, document, "Manual", "Sequential"))).Errors.Keys);
        Assert.Contains("signatories", (await ValidationProblemAsync(await StartRawAsync(manager, approved, document, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Ali", IsRequired: false)))).Errors.Keys);
        Assert.NotEmpty((await ValidationProblemAsync(await StartRawAsync(manager, approved, document, "Manual", "Sequential", new SignatoryInputDto("External", Name: "A")))).Errors);
        Assert.NotEmpty((await ValidationProblemAsync(await StartRawAsync(manager, approved, document, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Ali", Email: "gecersiz")))).Errors);
        Assert.NotEmpty((await ValidationProblemAsync(await StartRawAsync(manager, approved, document, "Manual", "Sequential", new SignatoryInputDto("Internal", UserId: Guid.NewGuid())))).Errors);
        Assert.NotEmpty((await ValidationProblemAsync(await StartRawAsync(manager, approved, document, "Manual", "Sequential",
            new SignatoryInputDto("Internal", UserId: s.Signer.Id), new SignatoryInputDto("Internal", UserId: s.Signer.Id)))).Errors);

        // Eski sürüm numarası 409 verir; geçerli istek tek açık istek bırakır, ikincisi 409 alır.
        var current = (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{approved.Id}"))!; // belge üretimi sürümü artırır
        var stale = await manager.PostAsJsonAsync($"{Contracts}/{approved.Id}/signature", new StartSignatureRequest(current.Version - 1, document.Id, "Manual", "Sequential", [new SignatoryInputDto("External", Name: "Ali Veli")]));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await StartAsync(manager, current, document, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Ali Veli"));
        current = (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{approved.Id}"))!;
        Assert.Equal(HttpStatusCode.Conflict, (await StartRawAsync(manager, current, document, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Ali Veli"))).StatusCode);
    }

    [Fact]
    public async Task Provider_simulation_runs_in_parallel_through_signed_webhooks_with_idempotency_ordering_and_replay_protection()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var manager = await LoginAsync(s.Manager);
        using var anonymous = factory.CreateBrowserClient();
        await SetSettingAsync(admin, "contracts.signature.provider", "simulation");
        var (contract, document) = await ApprovedWithDocumentAsync(admin, s);

        var started = await StartAsync(manager, contract, document, "Provider", "Parallel",
            new SignatoryInputDto("External", Name: "Zeynep Kaya", Email: "zeynep@ornek.test"),
            new SignatoryInputDto("External", Name: "Mert Demir", Email: "mert@ornek.test"));
        var request = Assert.Single(started.Requests);
        Assert.True(request.IsSimulation);
        Assert.Equal("simulation", request.Provider);
        Assert.All(request.Signatories, x => Assert.Equal("Sent", x.Status));
        Assert.StartsWith(s.Tenant.Id.ToString("N") + ".", request.ProviderReference);

        // Kimlik doğrulama imzadır: imzasız, yanlış gizli anahtarlı, bozulmuş gövdeli ve eski zaman damgalı çağrılar reddedilir.
        var viewed = new { eventId = "evt-1", sequence = 1, type = "Viewed", reference = request.ProviderReference, signatoryPosition = 1 };
        Assert.Equal(HttpStatusCode.Unauthorized, (await WebhookAsync(anonymous, viewed, secret: "yanlis")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WebhookAsync(anonymous, viewed, timestamp: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WebhookAsync(anonymous, viewed, tamper: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WebhookAsync(anonymous, viewed, sign: false)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await WebhookAsync(anonymous, viewed, provider: "bilinmeyen")).StatusCode);

        // Geçerli olay uygulanır; aynı olay kimliği tekrar gelirse yinelenen sayılır ve ikinci kez yazılmaz.
        Assert.Equal("Applied", await OutcomeAsync(await WebhookAsync(anonymous, viewed)));
        Assert.Equal("Duplicate", await OutcomeAsync(await WebhookAsync(anonymous, viewed)));
        var afterView = (await manager.GetFromJsonAsync<ContractSignatureDto>($"{Contracts}/{contract.Id}/signature"))!.Requests[0];
        Assert.Equal("Viewed", afterView.Signatories[0].Status);
        Assert.Single(afterView.Events, e => e.ProviderEventId == "evt-1");

        // Sıra: olay numarası artmalı; küçük numaralı yeni olay uygulanmaz ama kaydedilir.
        Assert.Equal("OutOfOrder", await OutcomeAsync(await WebhookAsync(anonymous, new { eventId = "evt-0", sequence = 1, type = "Signed", reference = request.ProviderReference, signatoryPosition = 2 })));
        Assert.Equal("Sent", (await manager.GetFromJsonAsync<ContractSignatureDto>($"{Contracts}/{contract.Id}/signature"))!.Requests[0].Signatories[1].Status);
        Assert.Contains((await manager.GetFromJsonAsync<ContractSignatureDto>($"{Contracts}/{contract.Id}/signature"))!.Requests[0].Events, e => e.ProviderEventId == "evt-0" && e.Outcome == "OutOfOrder");

        // İmza: simülasyon "nitelikli" düzey bildirse bile hiçbir zaman nitelikli sayılmaz; yöntem manuel taramadan ayrıdır.
        Assert.Equal("Applied", await OutcomeAsync(await WebhookAsync(anonymous, new { eventId = "evt-2", sequence = 2, type = "Signed", reference = request.ProviderReference, signatoryPosition = 1, assurance = "ProviderQualified" })));
        var partial = (await manager.GetFromJsonAsync<ContractSignatureDto>($"{Contracts}/{contract.Id}/signature"))!;
        Assert.Equal("PartiallySigned", partial.SignatureStatus);
        Assert.Equal("SimulatedElectronic", partial.Requests[0].Signatories[0].Assurance);

        Assert.Equal("Applied", await OutcomeAsync(await WebhookAsync(anonymous, new { eventId = "evt-3", sequence = 3, type = "Signed", reference = request.ProviderReference, signatoryPosition = 2 })));
        var completed = (await manager.GetFromJsonAsync<ContractSignatureDto>($"{Contracts}/{contract.Id}/signature"))!;
        Assert.Equal("Completed", completed.SignatureStatus);
        Assert.All(completed.Requests[0].Signatories, x => Assert.Equal("SimulatedElectronic", x.Assurance));
        Assert.Equal(1, (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{contract.Id}"))!.EffectiveVersionNo);

        // Kapanmış isteğe gelen olay yok sayılır (kayıt altına alınır, durum değişmez).
        Assert.Equal("Ignored", await OutcomeAsync(await WebhookAsync(anonymous, new { eventId = "evt-4", sequence = 4, type = "Declined", reference = request.ProviderReference, signatoryPosition = 1 })));
        Assert.Equal("Completed", (await manager.GetFromJsonAsync<ContractSignatureDto>($"{Contracts}/{contract.Id}/signature"))!.SignatureStatus);

        // Bilinmeyen referans, bozuk referans ve geçersiz olay.
        Assert.Equal(HttpStatusCode.NotFound, (await WebhookAsync(anonymous, new { eventId = "x1", sequence = 1, type = "Signed", reference = $"{s.Tenant.Id:N}.yok", signatoryPosition = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await WebhookAsync(anonymous, new { eventId = "x2", sequence = 1, type = "Signed", reference = "bozuk", signatoryPosition = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await WebhookAsync(anonymous, new { eventId = "x3", sequence = 1, type = "Signed", reference = $"{Guid.NewGuid():N}.abcdef", signatoryPosition = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await WebhookAsync(anonymous, new { eventId = "x4", sequence = 1, type = "Imzalandi", reference = request.ProviderReference, signatoryPosition = 1 })).StatusCode);
    }

    [Fact]
    public async Task A_required_decline_rejects_the_request_cancel_and_remind_are_recorded_and_a_new_request_can_follow()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var manager = await LoginAsync(s.Manager);
        await SetSettingAsync(admin, "contracts.signature.provider", "simulation");
        var (contract, document) = await ApprovedWithDocumentAsync(admin, s);

        var started = await StartAsync(manager, contract, document, "Provider", "Sequential",
            new SignatoryInputDto("External", Name: "Zeynep Kaya", Email: "zeynep@ornek.test"),
            new SignatoryInputDto("External", Name: "Mert Demir", Email: "mert@ornek.test"));
        var first = started.Requests[0].Signatories[0];
        var second = started.Requests[0].Signatories[1];

        // Hatırlatma yalnızca sırası gelen imzacı için ve saatte bir kaydedilir; bildirim gönderilmez.
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsync($"{Contracts}/{contract.Id}/signature/signatories/{second.Id}/remind", null)).StatusCode);
        var reminded = await ReadAsync<ContractSignatureDto>(await manager.PostAsync($"{Contracts}/{contract.Id}/signature/signatories/{first.Id}/remind", null));
        Assert.Equal(1, reminded.Requests[0].Signatories[0].ReminderCount);
        Assert.Contains(reminded.Requests[0].Events, e => e.EventType == "Reminded" && e.Note!.Contains("gönderilmedi"));
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsync($"{Contracts}/{contract.Id}/signature/signatories/{first.Id}/remind", null)).StatusCode);

        // Simülasyon olayı gerçek olayla aynı yoldan uygulanır; sırasız imza (ikinci imzacı önce) uygulanmaz.
        var early = await manager.PostAsJsonAsync($"{Contracts}/{contract.Id}/signature/simulate", new SimulateSignatureRequest(2, "Signed"));
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        var declined = await ReadAsync<ContractSignatureDto>(await manager.PostAsJsonAsync($"{Contracts}/{contract.Id}/signature/simulate", new SimulateSignatureRequest(1, "Declined", "Fiyat kabul edilmedi")));
        Assert.Equal("Rejected", declined.SignatureStatus);
        Assert.Equal(["Declined", "Cancelled"], declined.Requests[0].Signatories.Select(x => x.Status));
        Assert.Equal("Fiyat kabul edilmedi", declined.Requests[0].Signatories[0].DeclineReason);
        Assert.Equal("Rejected", (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{contract.Id}"))!.SignatureStatus);

        // Reddedilen istekten sonra yeni istek başlatılabilir (sürüm aynı); iptal gerekçe ister ve kaydı kapatır.
        var current = (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{contract.Id}"))!;
        var again = await StartAsync(manager, current, document, "Manual", "Parallel", new SignatoryInputDto("External", Name: "Zeynep Kaya"));
        Assert.Equal(2, again.Requests.Count);
        Assert.Equal("Pending", again.SignatureStatus);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync($"{Contracts}/{contract.Id}/signature/cancel", new SignatureReasonRequest(" "))).StatusCode);
        var cancelled = await ReadAsync<ContractSignatureDto>(await manager.PostAsJsonAsync($"{Contracts}/{contract.Id}/signature/cancel", new SignatureReasonRequest("Müşteri vazgeçti")));
        Assert.Equal("Cancelled", cancelled.SignatureStatus);
        Assert.Equal(("Cancelled", "Müşteri vazgeçti"), (cancelled.Requests[0].Status, cancelled.Requests[0].CloseReason));
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"{Contracts}/{contract.Id}/signature/cancel", new SignatureReasonRequest("tekrar"))).StatusCode);

        // Süresi dolan istek: işlem yapılamaz, süre dolumu sonlandırılır; süre dolmadan sonlandırılamaz.
        current = (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{contract.Id}"))!;
        var third = await StartAsync(manager, current, document, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Zeynep Kaya"));
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsync($"{Contracts}/{contract.Id}/signature/expire", null)).StatusCode);
        using (var scope = factory.OpenScope(s.Tenant.Id))
        {
            var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE contracts.ContractSignatureRequests SET ExpiresAt = DATEADD(hour, -2, SYSDATETIMEOFFSET()) WHERE Id = {0}", third.Requests[0].Id);
        }

        Assert.True((await manager.GetFromJsonAsync<ContractSignatureDto>($"{Contracts}/{contract.Id}/signature"))!.Requests[0].IsExpired);
        using var verifier = await LoginAsync(s.Verifier);
        Assert.Equal(HttpStatusCode.Conflict, (await ManualAsync(verifier, contract.Id, third.Requests[0].Signatories[0].Id, SignedPdf)).StatusCode);
        var expired = await ReadAsync<ContractSignatureDto>(await manager.PostAsync($"{Contracts}/{contract.Id}/signature/expire", null));
        Assert.Equal("Expired", expired.SignatureStatus);
        Assert.Equal("Expired", expired.Requests[0].Signatories[0].Status);
    }

    [Fact]
    public async Task Tenants_cannot_see_or_change_each_others_signatures_files_or_webhook_references()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var managerA = await LoginAsync(a.Manager);
        using var verifierA = await LoginAsync(a.Verifier);
        using var adminB = await LoginAsync(b.Admin);
        using var managerB = await LoginAsync(b.Manager);
        using var verifierB = await LoginAsync(b.Verifier);
        using var anonymous = factory.CreateBrowserClient();
        await SetSettingAsync(adminA, "contracts.signature.provider", "simulation");
        var (contract, document) = await ApprovedWithDocumentAsync(adminA, a);
        var started = await StartAsync(managerA, contract, document, "Provider", "Sequential", new SignatoryInputDto("External", Name: "Zeynep Kaya", Email: "zeynep@ornek.test"));
        var request = started.Requests[0];

        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"{Contracts}/{contract.Id}/signature")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostAsJsonAsync($"{Contracts}/{contract.Id}/signature/cancel", new SignatureReasonRequest("sızıntı"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostAsJsonAsync($"{Contracts}/{contract.Id}/signature/simulate", new SimulateSignatureRequest(1, "Signed"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ManualAsync(verifierB, contract.Id, request.Signatories[0].Id, SignedPdf)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostAsJsonAsync($"{Contracts}/{contract.Id}/signature", new StartSignatureRequest(1, document.Id, "Manual", "Sequential", [new SignatoryInputDto("External", Name: "Ali Veli")]))).StatusCode);

        // A'nın kanıt dosyası B'den indirilemez.
        await SetSettingAsync(adminA, "contracts.signature.provider", "none");
        var manualContract = await ApprovedWithDocumentAsync(adminA, a);
        var manual = await StartAsync(managerA, manualContract.Contract, manualContract.Document, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Zeynep Kaya"));
        var evidence = (await ReadAsync<ContractSignatureDto>(await ManualAsync(verifierA, manualContract.Contract.Id, manual.Requests[0].Signatories[0].Id, SignedPdf))).Requests[0].Signatories[0].EvidenceFileId;
        Assert.Equal(HttpStatusCode.NotFound, (await verifierB.GetAsync($"{Contracts}/{manualContract.Contract.Id}/signature/files/{evidence}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await verifierA.GetAsync($"{Contracts}/{manualContract.Contract.Id}/signature/files/{evidence}")).StatusCode);

        // Webhook: B'nin tenant önekiyle A'nın rastgele parçası eşleşmez; A'nın gerçek referansı yalnızca A'nın isteğine uygulanır.
        var forged = $"{b.Tenant.Id:N}.{request.ProviderReference![33..]}";
        Assert.Equal(HttpStatusCode.NotFound, (await WebhookAsync(anonymous, new { eventId = "f1", sequence = 1, type = "Signed", reference = forged, signatoryPosition = 1 })).StatusCode);
        Assert.Equal("Applied", await OutcomeAsync(await WebhookAsync(anonymous, new { eventId = "f2", sequence = 1, type = "Viewed", reference = request.ProviderReference, signatoryPosition = 1 })));
        Assert.Empty((await adminB.GetFromJsonAsync<PagedResult<ContractDto>>(Contracts))!.Items.Where(c => c.Id == contract.Id));
    }

    [Fact]
    public async Task Starting_verifying_and_downloading_are_separate_permissions_and_system_events_are_not_available_to_users()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var manager = await LoginAsync(s.Manager);
        using var verifier = await LoginAsync(s.Verifier);
        using var reader = await LoginAsync(s.Reader);
        using var approver = await LoginAsync(s.Approver);
        var (contract, document) = await ApprovedWithDocumentAsync(admin, s);

        // Başlatma yalnızca imza yöneticisinde; doğrulayıcı ve okuyucu başlatamaz.
        Assert.Equal(HttpStatusCode.Forbidden, (await StartRawAsync(verifier, contract, document, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Ali Veli"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartRawAsync(reader, contract, document, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Ali Veli"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartRawAsync(approver, contract, document, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Ali Veli"))).StatusCode);

        var started = await StartAsync(manager, contract, document, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Zeynep Kaya"));
        var signatory = started.Requests[0].Signatories[0];

        // Doğrulama yalnızca doğrulayıcıda; yönetici doğrulayamaz. Ret, hatırlatma ve iptal yönetim iznidir.
        Assert.Equal(HttpStatusCode.Forbidden, (await ManualAsync(manager, contract.Id, signatory.Id, SignedPdf)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ManualAsync(reader, contract.Id, signatory.Id, SignedPdf)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await verifier.PostAsJsonAsync($"{Contracts}/{contract.Id}/signature/signatories/{signatory.Id}/decline", new SignatureReasonRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await verifier.PostAsync($"{Contracts}/{contract.Id}/signature/signatories/{signatory.Id}/remind", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"{Contracts}/{contract.Id}/signature/cancel", new SignatureReasonRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"{Contracts}/{contract.Id}/signature/simulate", new SimulateSignatureRequest(1, "Signed"))).StatusCode);

        // Manuel isteğe simülasyon uygulanamaz; ret manuel istekte yönetici tarafından gerekçeyle kaydedilir.
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"{Contracts}/{contract.Id}/signature/simulate", new SimulateSignatureRequest(1, "Signed"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await manager.PostAsJsonAsync($"{Contracts}/{contract.Id}/signature/signatories/{signatory.Id}/decline", new SignatureReasonRequest(" "))).StatusCode);

        // Okuma serbest (sözleşme görünürlüğü); kanıt indirme ayrı izin ister (yönetici indiremez, doğrulayıcı indirir).
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Contracts}/{contract.Id}/signature")).StatusCode);
        var evidence = (await ReadAsync<ContractSignatureDto>(await ManualAsync(verifier, contract.Id, signatory.Id, SignedPdf))).Requests[0].Signatories[0].EvidenceFileId;
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"{Contracts}/{contract.Id}/signature/files/{evidence}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{Contracts}/{contract.Id}/signature/files/{evidence}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await verifier.GetAsync($"{Contracts}/{contract.Id}/signature/files/{evidence}")).StatusCode);
    }

    [Fact]
    public async Task Cancelling_the_contract_closes_the_open_request_and_reopening_the_draft_after_a_cancelled_signature_resets_the_status()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var manager = await LoginAsync(s.Manager);

        var (first, firstDocument) = await ApprovedWithDocumentAsync(admin, s);
        await StartAsync(manager, first, firstDocument, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Zeynep Kaya"));
        var current = (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{first.Id}"))!;
        var cancelled = await RunAsync(admin, first.Id, "cancel", current.Version, "Müşteri vazgeçti");
        Assert.Equal("Cancelled", cancelled.SignatureStatus);
        var closed = (await manager.GetFromJsonAsync<ContractSignatureDto>($"{Contracts}/{first.Id}/signature"))!.Requests[0];
        Assert.Equal("Cancelled", closed.Status);
        Assert.All(closed.Signatories, x => Assert.Equal("Cancelled", x.Status));

        var (second, secondDocument) = await ApprovedWithDocumentAsync(admin, s);
        await StartAsync(manager, second, secondDocument, "Manual", "Sequential", new SignatoryInputDto("External", Name: "Zeynep Kaya"));
        Assert.Equal("Cancelled", (await ReadAsync<ContractSignatureDto>(await manager.PostAsJsonAsync($"{Contracts}/{second.Id}/signature/cancel", new SignatureReasonRequest("Yanlış belge")))).SignatureStatus);
        current = (await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{second.Id}"))!;
        var reopened = await RunAsync(admin, second.Id, "reopen-draft", current.Version, "Düzeltme");
        Assert.Equal(("Draft", "NotSent", 2), (reopened.PreparationStatus, reopened.SignatureStatus, reopened.CurrentVersionNo));
    }

    // ---- yardımcılar

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "contracts", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var approver = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        var verifier = await factory.CreateUserAsync(tenant);
        var signer = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, FinancialsRead, ContractsApprove], Scope.All, approver);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, SignaturesManage], Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, SignaturesVerify, DocumentsDownload], Scope.All, verifier);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead, SignaturesVerify, SignaturesManage], Scope.All, signer);
        await factory.CreateRoleAsync(tenant.Id, [ContractsRead], Scope.All, reader);
        using var client = await LoginAsync(admin);
        var party = await PostRecordAsync(client, Parties, new JsonObject { ["name"] = "Anka Lojistik A.Ş." });
        return new Setup(tenant, admin, approver, manager, verifier, signer, reader, party.Id);
    }

    private static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul")).DateTime);

    /// <summary>İki kalemli sözleşmeyi yayıma hazır hâle getirir (başlangıç gelecekte: imza sonrası "başlangıcı bekleniyor").</summary>
    private static async Task<ContractDto> ReadyAsync(HttpClient client, Guid partyId)
    {
        var start = Today().AddDays(30);
        var contract = await PostAsync<ContractDto>(client, Contracts, new SaveContractRequest("Yıllık bakım", partyId, StartDate: start, EndDate: start.AddYears(1).AddDays(-1)));
        await PostAsync<ContractLineDto>(client, "/api/contracts/contract-lines", new SaveContractLineRequest(contract.Id, "Kurulum", Quantity: 2m, UnitPrice: 1000m, TaxRatePercent: 20m));
        contract = (await client.GetFromJsonAsync<ContractDto>($"{Contracts}/{contract.Id}"))!;
        contract = await RunAsync(client, contract.Id, "send-to-review", contract.Version);
        return await RunAsync(client, contract.Id, "mark-ready", contract.Version);
    }

    private async Task<ContractDto> ApproveAsync(HttpClient admin, Setup s, ContractDto ready)
    {
        using var approver = await LoginAsync(s.Approver);
        var submitted = await RunAsync(admin, ready.Id, "submit-for-approval", ready.Version);
        return await RunAsync(approver, ready.Id, "approve", submitted.Version);
    }

    private async Task<(ContractDto Contract, ContractDocumentDto Document)> ApprovedWithDocumentAsync(HttpClient admin, Setup s)
    {
        var template = await PublishedTemplateAsync(admin, $"Basit {Guid.NewGuid():N}"[..20], "Bu belge {{sozlesme.numara}} numaralı sözleşmedir.");
        var approved = await ApproveAsync(admin, s, await ReadyAsync(admin, s.PartyId));
        var document = await GenerateAsync(admin, approved.Id, template.Id);
        Assert.True(document.IsFinal);
        return ((await admin.GetFromJsonAsync<ContractDto>($"{Contracts}/{approved.Id}"))!, document);
    }

    private static async Task<ContractDocumentDto> GenerateAsync(HttpClient client, Guid contractId, Guid templateId)
    {
        var response = await client.PostAsJsonAsync($"{Contracts}/{contractId}/documents", new GenerateContractDocumentRequest(templateId));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ContractDocumentDto>())!;
    }

    private static async Task<ContractTemplateDto> PublishedTemplateAsync(HttpClient client, string name, string body)
    {
        var template = await PostAsync<ContractTemplateDto>(client, "/api/contracts/templates", new SaveContractTemplateRequest(name, body));
        return await PublishAsync(client, template.Id, template.Version);
    }

    private static async Task<ContractTemplateDto> PublishAsync(HttpClient client, Guid id, long version)
    {
        var response = await client.PostAsJsonAsync($"/api/contracts/templates/{id}/publish", new TemplateActionRequest(version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ContractTemplateDto>())!;
    }

    private static Task<HttpResponseMessage> StartRawAsync(HttpClient client, ContractDto contract, ContractDocumentDto document, string method, string mode, params SignatoryInputDto[] signatories) =>
        client.PostAsJsonAsync($"{Contracts}/{contract.Id}/signature", new StartSignatureRequest(contract.Version, document.Id, method, mode, signatories));

    private static async Task<ContractSignatureDto> StartAsync(HttpClient client, ContractDto contract, ContractDocumentDto document, string method, string mode, params SignatoryInputDto[] signatories)
    {
        var response = await StartRawAsync(client, contract, document, method, mode, signatories);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ContractSignatureDto>())!;
    }

    private static Task<HttpResponseMessage> ManualAsync(HttpClient client, Guid contractId, Guid signatoryId, byte[] content, string? note = null, DateOnly? signedOn = null) =>
        client.PostAsJsonAsync($"{Contracts}/{contractId}/signature/signatories/{signatoryId}/manual",
            new RecordManualSignatureRequest(signedOn ?? Today(), "imzali-sozlesme.pdf", Convert.ToBase64String(content), note));

    private async Task<HttpResponseMessage> WebhookAsync(
        HttpClient client, object payload, string? secret = null, long? timestamp = null, bool tamper = false, bool sign = true, string provider = "simulation")
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Web);
        var stamp = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        var signature = SignatureWebhook.Compute(secret ?? ModulixApiFactory.SignatureWebhookSecret, stamp, body);
        if (tamper)
        {
            body = JsonSerializer.SerializeToUtf8Bytes(payload, Web).Concat(" "u8.ToArray()).ToArray();
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/public/contracts/signature/{provider}/events") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Modulix-Timestamp", stamp);
        if (sign)
        {
            request.Headers.Add("X-Modulix-Signature", signature);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> OutcomeAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<SignatureWebhookResult>())!.Outcome;
    }

    private static async Task<ContractDto> RunAsync(HttpClient client, Guid id, string action, long version, string? reason = null)
    {
        var response = await client.PostAsJsonAsync($"{Contracts}/{id}/{action}", new ContractTransitionRequest(version, reason));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ContractDto>())!;
    }

    private static SaveContractRequest Save(ContractDto c) => new(
        c.Title, c.PartyId, c.Type, c.Description, c.PersonId, c.OwnerId, c.PreviousContractId, c.StartDate, c.EndDate, c.IsIndefinite, c.Currency, c.PriceIncludesTax,
        c.ContractValue, c.PaymentTerms, c.RenewalMode, c.RenewalPeriodMonths, c.NoticePeriodDays, c.Confidentiality, c.HasNonStandardTerms, c.RiskLevel, c.Version);

    private static async Task SetSettingAsync(HttpClient client, string key, string value)
    {
        var current = (await client.GetFromJsonAsync<SettingsResponse>("/api/settings"))!;
        var version = current.Settings.FirstOrDefault(x => x.Key == key)?.Version ?? 0;
        var response = await client.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(new Dictionary<string, SettingWriteDto> { [key] = new(value, version) }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
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
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
