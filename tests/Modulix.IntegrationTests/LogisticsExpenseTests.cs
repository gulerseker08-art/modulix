using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Modulix.Infrastructure.Identity;
using Modulix.Shared.Audit;
using Modulix.Shared.Auth;
using Modulix.Shared.Files;
using Modulix.Shared.Logistics;
using Modulix.Shared.Lookups;
using Modulix.Shared.Records;
using static Modulix.Modules.Logistics.LogisticsModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0038 dilim 8: sefer masrafı, onay ve yakıt alanları. Mutlu yol (operasyon yakıt ve geçiş masrafı girer, litre fiyatı türer, müdür onaylar ve
/// gerekçeyle reddeder, karar verilmiş masraf kilitlenir ve silinmez, denetim kaydı), doğrulama (tutar, para birimi, KDV, tarih, kategoriye bağlı
/// litre/kilometre/rücu kuralları, bilinmeyen tür ve sefer, iptal edilmiş sefer, gerekçesiz ret, çifte karar), tenant izolasyonu (başka tenant'ın
/// masrafı görünmez, onaylanamaz, başka tenant'ın seferine masraf açılamaz) ve yetki (yazma izni olmayan giremez, onay izni olmayan onaylayamaz ve
/// eylem listesinde görmez, okuma izni olmayan göremez). Makbuz ve fiş (bölüm 24): beklemede ve reddedilmişte eklenir ve silinir, onaylanmışta ve
/// iptal edilmiş seferde 403; başka tenant 404; yalnızca okuyan 403.
/// </summary>
public class LogisticsExpenseTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Trips = "/api/records/logistics.trip";
    private const string Expenses = "/api/records/logistics.trip-expense";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Operations_enters_expenses_manager_approves_and_rejects_and_decided_expenses_are_locked()
    {
        var s = await SetUpAsync();
        using var operations = await LoginAsync(s.Operations);
        using var manager = await LoginAsync(s.Manager);
        var trip = await PostAsync(operations, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });

        var fuel = await PostAsync(operations, Expenses, Fuel(trip.Id));
        Assert.Equal("pending", fuel.Values["status"]!.GetValue<string>());
        Assert.Equal(500m, fuel.Values["unitPrice"]!.GetValue<decimal>()); // 5000 / 10 litre
        Assert.Equal("TRY", fuel.Values["currency"]!.GetValue<string>());
        var toll = await PostAsync(operations, Expenses, Toll(trip.Id));
        Assert.Null(toll.Values["unitPrice"]);

        var page = (await operations.GetFromJsonAsync<RecordPageResponse>($"{Expenses}?tripId={trip.Id}&status=pending"))!;
        Assert.Equal(2, page.Items.Count);

        // Beklemedeki masraf düzenlenir.
        var edited = await PutAsync(operations, $"{Expenses}/{fuel.Id}", fuel.Version, new JsonObject { ["liters"] = 12.5m, ["amount"] = 6250 });
        Assert.Equal(500m, edited.Values["unitPrice"]!.GetValue<decimal>());

        var approved = await ActAsync(manager, edited, "approve");
        Assert.Equal("approved", approved.Values["status"]!.GetValue<string>());
        var rejected = await ActAsync(manager, toll, "reject", "Fiş okunmuyor");
        Assert.Equal("rejected", rejected.Values["status"]!.GetValue<string>());
        Assert.Equal("Fiş okunmuyor", rejected.Values["statusReason"]!.GetValue<string>());

        // Karar verilmiş masraf değişmez ve silinmez (onay tutara bağlıdır; K12).
        Assert.Equal(HttpStatusCode.Conflict, (await operations.PutAsJsonAsync($"{Expenses}/{approved.Id}",
            new RecordWriteRequest(new JsonObject { ["amount"] = 1 }, approved.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"{Expenses}/{rejected.Id}?version={rejected.Version}")).StatusCode);
        Assert.Equal(6250m, (await GetAsync(operations, $"{Expenses}/{approved.Id}")).Values["amount"]!.GetValue<decimal>());

        // Bekleyen listesi karar verilenleri içermez.
        Assert.Empty((await operations.GetFromJsonAsync<RecordPageResponse>($"{Expenses}?status=pending"))!.Items);
        Assert.Single((await operations.GetFromJsonAsync<RecordPageResponse>($"{Expenses}?status=approved"))!.Items);

        // Denetim kaydı: oluşturma, düzenleme ve onay yazılır.
        var history = (await manager.GetFromJsonAsync<AuditPageResponse>($"{Expenses}/{approved.Id}/history"))!;
        Assert.True(history.Items.Count >= 3, $"{history.Items.Count}");
    }

    [Fact]
    public async Task Invalid_expenses_decisions_without_reason_and_double_decisions_are_rejected()
    {
        var s = await SetUpAsync();
        using var operations = await LoginAsync(s.Operations);
        using var manager = await LoginAsync(s.Manager);
        var trip = await PostAsync(operations, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });

        async Task<ValidationProblemDetails> RejectedAsync(JsonObject values) =>
            await ValidationProblemAsync(await operations.PostAsJsonAsync(Expenses, new RecordWriteRequest(values)));

        JsonObject With(JsonObject source, string key, JsonNode? value)
        {
            var copy = (JsonObject)source.DeepClone();
            copy[key] = value;
            return copy;
        }

        var toll = Toll(trip.Id);
        Assert.Contains("amount", (await RejectedAsync(With(toll, "amount", 0))).Errors.Keys);
        Assert.Contains("amount", (await RejectedAsync(With(toll, "amount", 10.123m))).Errors.Keys);
        Assert.Contains("currency", (await RejectedAsync(With(toll, "currency", null))).Errors.Keys);
        Assert.Contains("currency", (await RejectedAsync(With(toll, "currency", "TR1"))).Errors.Keys);
        Assert.Contains("vatAmount", (await RejectedAsync(With(toll, "vatAmount", 9999))).Errors.Keys);
        Assert.Contains("description", (await RejectedAsync(With(toll, "description", " "))).Errors.Keys);
        Assert.Contains("expenseDate", (await RejectedAsync(With(toll, "expenseDate", Iso(Today.AddDays(5))))).Errors.Keys);
        Assert.Contains("kindKey", (await RejectedAsync(With(toll, "kindKey", "Yok"))).Errors.Keys);
        Assert.Contains("tripId", (await RejectedAsync(With(toll, "tripId", Guid.NewGuid().ToString()))).Errors.Keys);

        // Kategoriye bağlı kurallar: yakıtta litre zorunlu, diğerlerinde litre ve kilometre yok, rücu yalnızca cezada.
        var fuel = Fuel(trip.Id);
        fuel.Remove("liters");
        Assert.Contains("liters", (await RejectedAsync(fuel)).Errors.Keys);
        Assert.Contains("liters", (await RejectedAsync(With(toll, "liters", 5))).Errors.Keys);
        Assert.Contains("odometerKm", (await RejectedAsync(With(toll, "odometerKm", 120000))).Errors.Keys);
        Assert.Contains("chargeToDriver", (await RejectedAsync(With(toll, "chargeToDriver", true))).Errors.Keys);
        var fine = await PostAsync(operations, Expenses, With(With(With(toll, "kindKey", "Fine"), "description", "Hız cezası"), "chargeToDriver", true));
        Assert.True(fine.Values["chargeToDriver"]!.GetValue<bool>());

        // İptal edilmiş sefere masraf açılmaz.
        var cancelled = await ActAsync(manager, await PostAsync(operations, Trips, new JsonObject { ["plannedStart"] = Iso(Today) }), "cancel", "Müşteri vazgeçti", "trips");
        Assert.Equal(HttpStatusCode.Conflict, (await operations.PostAsJsonAsync(Expenses, new RecordWriteRequest(Toll(cancelled.Id)))).StatusCode);

        // Gerekçesiz ret 422; çifte karar 409.
        var pending = await PostAsync(operations, Expenses, Toll(trip.Id));
        var noReason = await manager.PostAsJsonAsync($"/api/logistics/expenses/{pending.Id}/actions", new RunLogisticsActionRequest("reject", pending.Version, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        var approved = await ActAsync(manager, pending, "approve");
        var twice = await manager.PostAsJsonAsync($"/api/logistics/expenses/{approved.Id}/actions", new RunLogisticsActionRequest("approve", approved.Version, null));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        var rejectAfter = await manager.PostAsJsonAsync($"/api/logistics/expenses/{approved.Id}/actions", new RunLogisticsActionRequest("reject", approved.Version, "Geç"));
        Assert.Equal(HttpStatusCode.Conflict, rejectAfter.StatusCode);
    }

    [Fact]
    public async Task A_rejected_expense_is_corrected_with_a_document_number_and_resubmitted_then_approved_and_locked()
    {
        var s = await SetUpAsync();
        using var operations = await LoginAsync(s.Operations);
        using var manager = await LoginAsync(s.Manager);
        using var reader = await LoginAsync(s.Reader);
        var trip = await PostAsync(operations, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });

        // Belge no kırpılır ve boş bırakılabilir; çok uzunu reddedilir.
        var toll = Toll(trip.Id);
        toll["documentNo"] = "  FŞ-2026-001 ";
        var expense = await PostAsync(operations, Expenses, toll);
        Assert.Equal("FŞ-2026-001", expense.Values["documentNo"]!.GetValue<string>());
        var tooLong = Toll(trip.Id);
        tooLong["documentNo"] = new string('9', 51);
        Assert.Contains("documentNo", (await ValidationProblemAsync(await operations.PostAsJsonAsync(Expenses, new RecordWriteRequest(tooLong)))).Errors.Keys);

        // Beklemedeki masraf yeniden gönderilemez; reddedilince giren düzeltir.
        Assert.Equal(HttpStatusCode.Conflict, (await operations.PostAsJsonAsync($"/api/logistics/expenses/{expense.Id}/actions",
            new RunLogisticsActionRequest("resubmit", expense.Version, null))).StatusCode);
        var rejected = await ActAsync(manager, expense, "reject", "Tutar fişle uyuşmuyor");
        Assert.Equal("Tutar fişle uyuşmuyor", rejected.Values["statusReason"]!.GetValue<string>());

        // Yalnızca okuma izni olan yeniden gönderemez ve düzeltemez; giren eylem listesinde "resubmit" görür, onay/ret görmez.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/logistics/expenses/{rejected.Id}/actions",
            new RunLogisticsActionRequest("resubmit", rejected.Version, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{Expenses}/{rejected.Id}",
            new RecordWriteRequest(new JsonObject { ["amount"] = 1 }, rejected.Version))).StatusCode);
        var opsActions = (await operations.GetFromJsonAsync<List<LogisticsActionDto>>($"/api/logistics/expenses/{rejected.Id}/actions"))!;
        Assert.Contains(opsActions, a => a.Action == "resubmit" && a.Allowed && !a.RequiresReason);
        Assert.DoesNotContain(opsActions, a => a.Action is "approve" or "reject");

        var corrected = await PutAsync(operations, $"{Expenses}/{rejected.Id}", rejected.Version, new JsonObject { ["amount"] = 1100, ["documentNo"] = "FŞ-2026-002" });
        Assert.Equal("rejected", corrected.Values["status"]!.GetValue<string>()); // düzeltme durumu değiştirmez
        Assert.Equal("FŞ-2026-002", corrected.Values["documentNo"]!.GetValue<string>());

        var resubmitted = await ActAsync(operations, corrected, "resubmit");
        Assert.Equal("pending", resubmitted.Values["status"]!.GetValue<string>());
        Assert.Null(resubmitted.Values["statusReason"]);

        var approved = await ActAsync(manager, resubmitted, "approve");
        Assert.Equal("approved", approved.Values["status"]!.GetValue<string>());

        // Onaylanmış masraf değişmez ve yeniden gönderilemez.
        Assert.Equal(HttpStatusCode.Conflict, (await operations.PutAsJsonAsync($"{Expenses}/{approved.Id}",
            new RecordWriteRequest(new JsonObject { ["amount"] = 1 }, approved.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await operations.PostAsJsonAsync($"/api/logistics/expenses/{approved.Id}/actions",
            new RunLogisticsActionRequest("resubmit", approved.Version, null))).StatusCode);
        Assert.Equal(1100m, (await GetAsync(operations, $"{Expenses}/{approved.Id}")).Values["amount"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task Another_tenants_expenses_cannot_be_seen_decided_or_attached_to_foreign_trips()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var opsA = await LoginAsync(a.Operations);
        using var opsB = await LoginAsync(b.Operations);
        using var managerB = await LoginAsync(b.Manager);
        var tripA = await PostAsync(opsA, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });
        var expenseA = await PostAsync(opsA, Expenses, Toll(tripA.Id));

        Assert.Equal(HttpStatusCode.NotFound, (await opsB.GetAsync($"{Expenses}/{expenseA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opsB.PutAsJsonAsync($"{Expenses}/{expenseA.Id}",
            new RecordWriteRequest(new JsonObject { ["amount"] = 1 }, expenseA.Version))).StatusCode);
        Assert.Empty((await opsB.GetFromJsonAsync<RecordPageResponse>(Expenses))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostAsJsonAsync($"/api/logistics/expenses/{expenseA.Id}/actions",
            new RunLogisticsActionRequest("approve", expenseA.Version, null))).StatusCode);

        // Reddedilmiş masrafı başka tenant yeniden gönderemez ve düzeltemez.
        using var managerA = await LoginAsync(a.Manager);
        var rejectedA = await ActAsync(managerA, expenseA, "reject", "Fiş eksik");
        Assert.Equal(HttpStatusCode.NotFound, (await opsB.PostAsJsonAsync($"/api/logistics/expenses/{rejectedA.Id}/actions",
            new RunLogisticsActionRequest("resubmit", rejectedA.Version, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opsB.PutAsJsonAsync($"{Expenses}/{rejectedA.Id}",
            new RecordWriteRequest(new JsonObject { ["amount"] = 1 }, rejectedA.Version))).StatusCode);

        // Başka tenant'ın seferine masraf açılamaz (sefer "bulunamadı").
        var foreign = await ValidationProblemAsync(await opsB.PostAsJsonAsync(Expenses, new RecordWriteRequest(Toll(tripA.Id))));
        Assert.Contains("tripId", foreign.Errors.Keys);

        Assert.Equal("rejected", (await GetAsync(opsA, $"{Expenses}/{expenseA.Id}")).Values["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_write_approve_or_read_permission_expenses_cannot_be_entered_decided_or_seen()
    {
        var s = await SetUpAsync();
        using var operations = await LoginAsync(s.Operations);
        using var manager = await LoginAsync(s.Manager);
        using var reader = await LoginAsync(s.Reader);
        using var outsider = await LoginAsync(s.Outsider);
        var trip = await PostAsync(operations, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });
        var expense = await PostAsync(operations, Expenses, Toll(trip.Id));

        // Yalnızca okuma izni olan görür ama giremez ve düzenleyemez.
        Assert.Equal(expense.Id, (await GetAsync(reader, $"{Expenses}/{expense.Id}")).Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Expenses, new RecordWriteRequest(Toll(trip.Id)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"{Expenses}/{expense.Id}",
            new RecordWriteRequest(new JsonObject { ["amount"] = 1 }, expense.Version))).StatusCode);

        // Giren (operasyon) onaylayamaz ve eylem listesinde onay/ret yoktur; müdür ikisini de görür.
        Assert.Equal(HttpStatusCode.Forbidden, (await operations.PostAsJsonAsync($"/api/logistics/expenses/{expense.Id}/actions",
            new RunLogisticsActionRequest("approve", expense.Version, null))).StatusCode);
        var opsActions = (await operations.GetFromJsonAsync<List<LogisticsActionDto>>($"/api/logistics/expenses/{expense.Id}/actions"))!;
        Assert.DoesNotContain(opsActions, a => a.Action is "approve" or "reject");
        var managerActions = (await manager.GetFromJsonAsync<List<LogisticsActionDto>>($"/api/logistics/expenses/{expense.Id}/actions"))!;
        Assert.Contains(managerActions, a => a.Action == "approve" && a.Allowed);
        Assert.Contains(managerActions, a => a.Action == "reject" && a.Allowed && a.RequiresReason);
        Assert.Equal("pending", (await GetAsync(manager, $"{Expenses}/{expense.Id}")).Values["status"]!.GetValue<string>());

        // Okuma izni olmayan hiçbir şey göremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"{Expenses}/{expense.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Expenses)).StatusCode);
    }

    [Fact]
    public async Task Expense_list_resolves_trip_numbers_only_for_readers_of_trips_in_the_same_tenant()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var managerA = await LoginAsync(a.Manager);
        using var readerA = await LoginAsync(a.Reader);
        using var managerB = await LoginAsync(b.Manager);
        var trip = await PostAsync(managerA, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });
        var number = trip.Values["number"]!.GetValue<string>();

        async Task<string> ResolveAsync(HttpClient client)
        {
            var response = await client.PostAsJsonAsync($"/api/lookups/{LogisticsAccess.ExpenseTripLookup}/resolve", new ResolveLookupRequest([trip.Id]));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        // Sefer okuma izni olan kendi tenant'ında numarayı görür; yalnızca masraf okuyan ve başka tenant'ın kullanıcısı göremez.
        var own = await ResolveAsync(managerA);
        Assert.Contains(number, own);
        Assert.DoesNotContain(number, await ResolveAsync(readerA));
        Assert.DoesNotContain(number, await ResolveAsync(managerB));
    }

    // --- makbuz ve fiş: kaydın kuralı (ADR-0038 bölüm 24, ADR-0040 K2) ---

    /// <summary>Mutlu yol: beklemedeki masrafa makbuz eklenir ve silinir; reddedilen masrafın belgesi düzeltilir (yeni fiş eklenir, eskisi silinir).</summary>
    [Fact]
    public async Task Receipts_are_added_and_removed_while_the_expense_is_pending_or_rejected()
    {
        var s = await SetUpAsync();
        using var operations = await LoginAsync(s.Operations);
        using var manager = await LoginAsync(s.Manager);
        var trip = await PostAsync(operations, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });
        var expense = await PostAsync(operations, Expenses, Toll(trip.Id));

        var wrong = await UploadAsync(operations, expense.Id, "yanlis-fis.png");
        var extra = await UploadAsync(operations, expense.Id, "ek.png");
        Assert.Equal(HttpStatusCode.NoContent, (await operations.DeleteAsync($"{Files}/{extra.Id}?version={extra.Version}")).StatusCode);
        Assert.Equal(["yanlis-fis.png"], (await ReceiptsAsync(operations, expense.Id)).Select(f => f.FileName));

        // Ret "fiş okunmuyor": giren doğru fişi ekler, yanlışını siler ve yeniden gönderir.
        var rejected = await ActAsync(manager, expense, "reject", "Fiş okunmuyor");
        await UploadAsync(operations, rejected.Id, "dogru-fis.png");
        Assert.Equal(HttpStatusCode.NoContent, (await operations.DeleteAsync($"{Files}/{wrong.Id}?version={wrong.Version}")).StatusCode);
        Assert.Equal(["dogru-fis.png"], (await ReceiptsAsync(manager, rejected.Id)).Select(f => f.FileName));
        Assert.Equal("pending", (await ActAsync(operations, rejected, "resubmit")).Values["status"]!.GetValue<string>());
    }

    /// <summary>
    /// Kural: onaylanmış masrafın ve iptal edilmiş seferdeki masrafın belgesi tür yazma izni olan kimse tarafından eklenemez ve silinemez (403);
    /// okuma ve indirme sürer.
    /// </summary>
    [Fact]
    public async Task Receipts_of_an_approved_expense_or_of_a_cancelled_trip_cannot_be_added_or_removed()
    {
        var s = await SetUpAsync();
        using var operations = await LoginAsync(s.Operations);
        using var manager = await LoginAsync(s.Manager);
        var trip = await PostAsync(operations, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });
        var expense = await PostAsync(operations, Expenses, Toll(trip.Id));
        var receipt = await UploadAsync(operations, expense.Id, "fis.png");

        await ActAsync(manager, expense, "approve");
        foreach (var client in new[] { operations, manager })
        {
            var denied = await PostFileAsync(client, expense.Id, "sonradan.png");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Contains("Onaylanmış masraf", (await denied.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{Files}/{receipt.Id}?version={receipt.Version}")).StatusCode);
        }

        Assert.Equal(["fis.png"], (await ReceiptsAsync(manager, expense.Id)).Select(f => f.FileName));
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"{Files}/{receipt.Id}/content")).StatusCode);

        // İptal edilen seferin beklemedeki masrafı: belge eklenmez, silinmez.
        var cancelledTrip = await PostAsync(operations, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });
        var pending = await PostAsync(operations, Expenses, Toll(cancelledTrip.Id));
        var pendingReceipt = await UploadAsync(operations, pending.Id, "bekleyen.png");
        Assert.Equal("cancelled", (await ActAsync(operations, cancelledTrip, "cancel", "Müşteri vazgeçti", kind: "trips")).Values["status"]!.GetValue<string>());
        var afterCancel = await PostFileAsync(operations, pending.Id, "iptal-sonrasi.png");
        Assert.Equal(HttpStatusCode.Forbidden, afterCancel.StatusCode);
        Assert.Contains("İptal edilen sefer", (await afterCancel.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        Assert.Equal(HttpStatusCode.Forbidden, (await operations.DeleteAsync($"{Files}/{pendingReceipt.Id}?version={pendingReceipt.Version}")).StatusCode);
        Assert.Single(await ReceiptsAsync(operations, pending.Id));
    }

    /// <summary>Tenant izolasyonu: başka tenant'ın masrafına belge eklenemez; belgesi listelenmez, indirilmez, silinmez (404).</summary>
    [Fact]
    public async Task Another_tenant_cannot_list_download_add_or_remove_expense_receipts()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var opsA = await LoginAsync(a.Operations);
        using var opsB = await LoginAsync(b.Operations);
        var tripA = await PostAsync(opsA, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });
        var expenseA = await PostAsync(opsA, Expenses, Toll(tripA.Id));
        var receiptA = await UploadAsync(opsA, expenseA.Id, "fis.png");

        Assert.Equal(HttpStatusCode.NotFound, (await PostFileAsync(opsB, expenseA.Id, "yabanci.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opsB.GetAsync($"{Files}?entityKey={LogisticsAccess.TripExpenseEntityKey}&recordId={expenseA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opsB.GetAsync($"{Files}/{receiptA.Id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opsB.DeleteAsync($"{Files}/{receiptA.Id}?version={receiptA.Version}")).StatusCode);

        Assert.Equal(["fis.png"], (await ReceiptsAsync(opsA, expenseA.Id)).Select(f => f.FileName));
    }

    /// <summary>Yetki: yalnızca okuyan beklemedeki masrafın belgesini görür ama ekleyemez, silemez (403); masrafı okuyamayan ulaşamaz.</summary>
    [Fact]
    public async Task Without_expense_write_permission_receipts_cannot_be_added_or_removed_even_while_pending()
    {
        var s = await SetUpAsync();
        using var operations = await LoginAsync(s.Operations);
        using var reader = await LoginAsync(s.Reader);
        using var outsider = await LoginAsync(s.Outsider);
        var trip = await PostAsync(operations, Trips, new JsonObject { ["plannedStart"] = Iso(Today) });
        var expense = await PostAsync(operations, Expenses, Toll(trip.Id));
        var receipt = await UploadAsync(operations, expense.Id, "fis.png");

        Assert.Single(await ReceiptsAsync(reader, expense.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await PostFileAsync(reader, expense.Id, "okuyan.png")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Files}/{receipt.Id}?version={receipt.Version}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await PostFileAsync(outsider, expense.Id, "disaridan.png")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await outsider.GetAsync($"{Files}/{receipt.Id}/content")).StatusCode);
        Assert.NotEqual(HttpStatusCode.NoContent, (await outsider.DeleteAsync($"{Files}/{receipt.Id}?version={receipt.Version}")).StatusCode);

        Assert.Single(await ReceiptsAsync(operations, expense.Id));
    }

    // --- yardımcılar ---

    private const string Files = "/api/files";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    private static Task<HttpResponseMessage> PostFileAsync(HttpClient client, Guid expenseId, string fileName)
    {
        var file = new ByteArrayContent([.. Png, .. Encoding.ASCII.GetBytes(Guid.NewGuid().ToString())]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent
        {
            { new StringContent(LogisticsAccess.TripExpenseEntityKey), "entityKey" },
            { new StringContent(expenseId.ToString()), "recordId" },
            { file, "file", fileName },
        };
        return client.PostAsync(Files, form);
    }

    private static async Task<StoredFileDto> UploadAsync(HttpClient client, Guid expenseId, string fileName)
    {
        var response = await PostFileAsync(client, expenseId, fileName);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<StoredFileDto>())!;
    }

    private static async Task<List<StoredFileDto>> ReceiptsAsync(HttpClient client, Guid expenseId) =>
        (await client.GetFromJsonAsync<List<StoredFileDto>>($"{Files}?entityKey={LogisticsAccess.TripExpenseEntityKey}&recordId={expenseId}"))!;

    private static JsonObject Fuel(Guid tripId) => new()
    {
        ["tripId"] = tripId.ToString(), ["kindKey"] = "Fuel", ["description"] = "Dilovası istasyonu", ["expenseDate"] = Iso(Today),
        ["amount"] = 5000, ["currency"] = "try", ["vatAmount"] = 833.33m, ["liters"] = 10, ["odometerKm"] = 120_500,
    };

    private static JsonObject Toll(Guid tripId) => new()
    {
        ["tripId"] = tripId.ToString(), ["kindKey"] = "Toll", ["description"] = "Osmangazi Köprüsü", ["expenseDate"] = Iso(Today),
        ["amount"] = 1200, ["currency"] = "TRY",
    };

    private sealed record Setup(Guid TenantId, ModulixUser Operations, ModulixUser Manager, ModulixUser Reader, ModulixUser Outsider);

    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "logistics", enabled: true);
        var operations = await factory.CreateUserAsync(tenant);
        var manager = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await factory.CreateRoleAsync(tenant.Id, [TripsRead, TripsWrite, ExpensesRead, ExpensesWrite], Scope.All, operations);
        await factory.CreateRoleAsync(tenant.Id, [TripsRead, TripsWrite, ExpensesRead, ExpensesWrite, ExpensesApprove], Scope.All, manager);
        await factory.CreateRoleAsync(tenant.Id, [ExpensesRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [TripsRead], Scope.All, outsider);
        return new Setup(tenant.Id, operations, manager, reader, outsider);
    }

    private static async Task<RecordResponse> ActAsync(HttpClient client, RecordResponse record, string action, string? reason = null, string kind = "expenses")
    {
        var response = await client.PostAsJsonAsync($"/api/logistics/{kind}/{record.Id}/actions", new RunLogisticsActionRequest(action, record.Version, reason));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<RecordResponse> PutAsync(HttpClient client, string url, long version, JsonObject values)
    {
        var response = await client.PutAsJsonAsync(url, new RecordWriteRequest(values, version));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<RecordResponse> GetAsync(HttpClient client, string url) => (await client.GetFromJsonAsync<RecordResponse>(url))!;

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
