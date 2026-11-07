using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Records;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Tenancy;
using Modulix.Shared.Auth;
using Modulix.Shared.Records;
using static Modulix.Modules.Warehouse.WarehouseModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0032 dilim 6a (stok hareket defteri, K13, K17): giriş, çıkış, transfer ve ters kayıtla bakiyenin defterden türemesi ve her hareketin denetimi;
/// doğrulama (lokasyon biçimi, yetersiz stok, pasif parti/lokasyon, ters kayıt kuralları); defterin yalnızca eklenmesi (uygulamada ve veritabanında);
/// tekrar anahtarı; eşzamanlı çıkış yarışı; tenant izolasyonu; yetki ve tenant silmede defterin temizlenmesi.
/// </summary>
public class WarehouseStockLedgerTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Lots = "/api/records/warehouse.lot";
    private const string Movements = "/api/records/warehouse.stock-movement";
    private const string Balances = "/api/records/warehouse.stock-balance";
    private const string Parties = "/api/records/core.party";

    [Fact]
    public async Task Receipt_issue_transfer_and_reversals_move_the_balance_and_every_movement_is_audited()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var w = await WorldAsync(admin);

        // Giriş: sunucu zamanı, kullanıcıyı, üyeliği ve işlem kimliğini yazar; istemciden gelen değerler yok sayılır.
        var spoofed = Guid.NewGuid().ToString();
        var receipt = await PostMovementAsync(admin, Move("Receipt", w.Lot, to: w.A, quantity: 100, extra: new JsonObject
        {
            ["userId"] = spoofed, ["occurredAt"] = "2001-01-01T00:00:00Z", ["documentReference"] = "IRS-1", ["reasonKey"] = "Return",
        }));
        Assert.Equal(s.Tenant.AdminId.ToString(), receipt.Values["userId"]!.GetValue<string>());
        Assert.NotEqual("2001-01-01T00:00:00Z", receipt.Values["occurredAt"]!.GetValue<string>());
        Assert.StartsWith(DateTime.UtcNow.ToString("yyyy-MM-dd"), receipt.Values["occurredAt"]!.GetValue<string>());
        Assert.NotNull(receipt.Values["correlationId"]);
        Assert.Equal(100m, await BalanceAsync(admin, w.Lot, w.A));

        var issue = await PostMovementAsync(admin, Move("Issue", w.Lot, from: w.A, quantity: 30));
        var transfer = await PostMovementAsync(admin, Move("Transfer", w.Lot, from: w.A, to: w.B, quantity: 20));
        Assert.Equal((50m, 20m), (await BalanceAsync(admin, w.Lot, w.A), await BalanceAsync(admin, w.Lot, w.B)));

        // Liste ve süzgeçler: yeniden eskiye; tür, lokasyon (kaynak ya da hedef), parti, tarih aralığı ve arama.
        Assert.Equal([transfer.Id, issue.Id, receipt.Id], (await ListAsync(admin, Movements, $"lotId={w.Lot}")).Select(i => i.Id));
        Assert.Equal([transfer.Id], (await ListAsync(admin, Movements, "typeKey=Transfer")).Where(i => i.Values["lotId"]!.GetValue<string>() == w.Lot.ToString()).Select(i => i.Id));
        Assert.Equal([transfer.Id], (await ListAsync(admin, Movements, $"locationId={w.B}")).Select(i => i.Id));
        Assert.Equal(3, (await ListAsync(admin, Movements, $"locationId={w.A}")).Count);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        Assert.Equal(3, (await ListAsync(admin, Movements, $"lotId={w.Lot}&occurredFrom={today}&occurredTo={today}")).Count);
        Assert.Empty(await ListAsync(admin, Movements, $"lotId={w.Lot}&occurredTo={DateTime.UtcNow.AddDays(-1):yyyy-MM-dd}"));
        Assert.Equal([receipt.Id], (await ListAsync(admin, Movements, $"lotId={w.Lot}&search=IRS-1")).Select(i => i.Id));
        Assert.Equal(2, (await ListAsync(admin, Balances, $"stockItemId={w.StockItem}")).Count);

        // Düzeltme: önceki hareketin ters kaydı; parti, miktar ve lokasyonlar özgün hareketten gelir.
        var undoTransfer = await PostMovementAsync(admin, new JsonObject { ["typeKey"] = "Correction", ["reversesMovementId"] = transfer.Id.ToString() });
        Assert.Equal((w.Lot.ToString(), w.B.ToString(), w.A.ToString(), 20m),
            (undoTransfer.Values["lotId"]!.GetValue<string>(), undoTransfer.Values["fromLocationId"]!.GetValue<string>(),
             undoTransfer.Values["toLocationId"]!.GetValue<string>(), undoTransfer.Values["quantity"]!.GetValue<decimal>()));
        Assert.Equal((70m, 0m), (await BalanceAsync(admin, w.Lot, w.A), await BalanceAsync(admin, w.Lot, w.B)));
        Assert.Single(await ListAsync(admin, Balances, $"lotId={w.Lot}&nonZero=true"));
        await PostMovementAsync(admin, new JsonObject { ["typeKey"] = "Correction", ["reversesMovementId"] = issue.Id.ToString() });
        Assert.Equal(100m, await BalanceAsync(admin, w.Lot, w.A));
        Assert.Equal([undoTransfer.Id], (await ListAsync(admin, Movements, $"reversesMovementId={transfer.Id}")).Select(i => i.Id));

        // Her hareket denetim kaydına "oluşturuldu" olarak yazılır.
        using var scope = factory.OpenScope(s.Tenant.Id);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityKey == "warehouse.stock-movement").ToListAsync();
        Assert.Equal(5, audit.Count);
        Assert.All(audit, a => Assert.Equal(AuditAction.Created, a.Action));
        Assert.Contains(audit, a => a.RecordId == transfer.Id);
    }

    [Fact]
    public async Task Movement_shape_stock_availability_lots_locations_and_reversals_are_validated()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var w = await WorldAsync(admin);
        await PostMovementAsync(admin, Move("Receipt", w.Lot, to: w.A, quantity: 10));

        async Task<ValidationProblemDetails> Reject(JsonObject values) =>
            await ValidationProblemAsync(await admin.PostAsJsonAsync(Movements, new RecordWriteRequest(values)));

        // Lokasyon biçimi kategoriye göredir; miktar zorunlu, pozitif ve en çok 3 ondalık; tür açıkça seçilir ve listede olmalı.
        Assert.Contains("fromLocationId", (await Reject(Move("Receipt", w.Lot, from: w.A, to: w.B, quantity: 1))).Errors.Keys);
        Assert.Contains("toLocationId", (await Reject(Move("Receipt", w.Lot, quantity: 1))).Errors.Keys);
        Assert.Contains("toLocationId", (await Reject(Move("Issue", w.Lot, from: w.A, to: w.B, quantity: 1))).Errors.Keys);
        Assert.Contains("fromLocationId", (await Reject(Move("Issue", w.Lot, quantity: 1))).Errors.Keys);
        Assert.Contains("toLocationId", (await Reject(Move("Transfer", w.Lot, to: w.B, quantity: 1))).Errors.Keys);
        Assert.Contains("toLocationId", (await Reject(Move("Transfer", w.Lot, from: w.A, to: w.A, quantity: 1))).Errors.Keys);
        Assert.Contains("quantity", (await Reject(Move("Receipt", w.Lot, to: w.A, quantity: 0))).Errors.Keys);
        Assert.Contains("quantity", (await Reject(Move("Receipt", w.Lot, to: w.A, quantity: -5))).Errors.Keys);
        Assert.Contains("quantity", (await Reject(Move("Receipt", w.Lot, to: w.A, quantity: 1.2345m))).Errors.Keys);
        var noQuantity = Move("Receipt", w.Lot, to: w.A, quantity: 1);
        noQuantity.Remove("quantity");
        Assert.Contains("quantity", (await Reject(noQuantity)).Errors.Keys);
        var noType = Move("Receipt", w.Lot, to: w.A, quantity: 1);
        noType.Remove("typeKey");
        Assert.Equal(["Bu alan zorunludur."], (await Reject(noType)).Errors["typeKey"]);
        Assert.Contains("typeKey", (await Reject(Move("Bilinmeyen", w.Lot, to: w.A, quantity: 1))).Errors.Keys);
        var noLot = Move("Receipt", w.Lot, to: w.A, quantity: 1);
        noLot.Remove("lotId");
        Assert.Contains("lotId", (await Reject(noLot)).Errors.Keys);
        Assert.Contains("lotId", (await Reject(Move("Receipt", Guid.NewGuid(), to: w.A, quantity: 1))).Errors.Keys);
        Assert.Contains("toLocationId", (await Reject(Move("Receipt", w.Lot, to: Guid.NewGuid(), quantity: 1))).Errors.Keys);
        Assert.Contains("reasonKey", (await Reject(Move("Receipt", w.Lot, to: w.A, quantity: 1, extra: new JsonObject { ["reasonKey"] = "Yok" }))).Errors.Keys);
        Assert.Contains("reversesMovementId", (await Reject(Move("Receipt", w.Lot, to: w.A, quantity: 1, extra: new JsonObject { ["reversesMovementId"] = Guid.NewGuid().ToString() }))).Errors.Keys);

        // Yetersiz stok: bakiyesi olmayan lokasyondan, bakiyeden fazla çıkış, transfer. Hiçbiri bakiyeyi değiştirmez.
        Assert.Contains("Yetersiz stok", (await Reject(Move("Issue", w.Lot, from: w.B, quantity: 1))).Errors["quantity"][0]);
        Assert.Contains("Yetersiz stok", (await Reject(Move("Issue", w.Lot, from: w.A, quantity: 10.001m))).Errors["quantity"][0]);
        Assert.Contains("Yetersiz stok", (await Reject(Move("Transfer", w.Lot, from: w.A, to: w.B, quantity: 11))).Errors["quantity"][0]);
        Assert.Equal((10m, 0m), (await BalanceAsync(admin, w.Lot, w.A), await BalanceAsync(admin, w.Lot, w.B)));

        // Pasif parti yeni hareket almaz; pasif lokasyona giriş yapılamaz ama pasif lokasyondan çıkış yapılabilir.
        var inactiveLot = await PostAsync(admin, Lots, LotValues("PASIF-1", w.StockItem, w.Owner));
        await PutAsync(admin, Lots, inactiveLot, new JsonObject { ["isActive"] = false });
        Assert.Contains("lotId", (await Reject(Move("Receipt", inactiveLot.Id, to: w.A, quantity: 1))).Errors.Keys);

        // Ters kayıt kuralları: geri alınan hareket belirtilir, parti/lokasyon/miktar verilmez, bir hareket bir kez geri alınır,
        // düzeltme geri alınamaz, tüketilmiş girişin tersi yetersiz stoğa takılır.
        var receipt2 = await PostMovementAsync(admin, Move("Receipt", w.Lot, to: w.A, quantity: 5, extra: new JsonObject()));
        Assert.Contains("reversesMovementId", (await Reject(new JsonObject { ["typeKey"] = "Correction" })).Errors.Keys);
        Assert.Contains("quantity", (await Reject(new JsonObject { ["typeKey"] = "Correction", ["reversesMovementId"] = receipt2.Id.ToString(), ["quantity"] = 1 })).Errors.Keys);
        Assert.Contains("lotId", (await Reject(new JsonObject { ["typeKey"] = "Correction", ["reversesMovementId"] = receipt2.Id.ToString(), ["lotId"] = w.Lot.ToString() })).Errors.Keys);
        Assert.Contains("reversesMovementId", (await Reject(new JsonObject { ["typeKey"] = "Correction", ["reversesMovementId"] = Guid.NewGuid().ToString() })).Errors.Keys);
        var undo = await PostMovementAsync(admin, new JsonObject { ["typeKey"] = "Correction", ["reversesMovementId"] = receipt2.Id.ToString() });
        Assert.Equal("Bu hareket zaten geri alınmış.", (await Reject(new JsonObject { ["typeKey"] = "Correction", ["reversesMovementId"] = receipt2.Id.ToString() })).Errors["reversesMovementId"][0]);
        Assert.Contains("düzeltme", (await Reject(new JsonObject { ["typeKey"] = "Correction", ["reversesMovementId"] = undo.Id.ToString() })).Errors["reversesMovementId"][0]);
        var consumed = await PostMovementAsync(admin, Move("Receipt", w.Lot, to: w.B, quantity: 3));
        await PostMovementAsync(admin, Move("Issue", w.Lot, from: w.B, quantity: 3));
        Assert.Contains("Yetersiz stok", (await Reject(new JsonObject { ["typeKey"] = "Correction", ["reversesMovementId"] = consumed.Id.ToString() })).Errors["quantity"][0]);

        // Pasif lokasyona giriş yapılamaz, ama pasife alınmış lokasyondan stok çıkarılabilir.
        var locA = (await admin.GetFromJsonAsync<RecordResponse>($"/api/records/warehouse.location/{w.A}"))!;
        var locB = (await admin.GetFromJsonAsync<RecordResponse>($"/api/records/warehouse.location/{w.B}"))!;
        await PutAsync(admin, "/api/records/warehouse.location", locB, new JsonObject { ["isActive"] = false });
        Assert.Contains("toLocationId", (await Reject(Move("Receipt", w.Lot, to: w.B, quantity: 1))).Errors.Keys);
        await PutAsync(admin, "/api/records/warehouse.location", locA, new JsonObject { ["isActive"] = false });
        await PostMovementAsync(admin, Move("Issue", w.Lot, from: w.A, quantity: 4));
        Assert.Equal(6m, await BalanceAsync(admin, w.Lot, w.A));
    }

    [Fact]
    public async Task Ledger_only_appends_in_the_application_and_in_the_database_and_lots_with_movements_stay()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var w = await WorldAsync(admin);
        var movement = await PostMovementAsync(admin, Move("Receipt", w.Lot, to: w.A, quantity: 10));

        // Uygulama: hareket değiştirilemez, silinemez; bakiye doğrudan oluşturulamaz, değiştirilemez, silinemez.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"{Movements}/{movement.Id}", new RecordWriteRequest(new JsonObject { ["documentReference"] = "x" }, movement.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Movements}/{movement.Id}?version={movement.Version}")).StatusCode);
        var balance = (await ListAsync(admin, Balances, $"lotId={w.Lot}")).Single();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(Balances, new RecordWriteRequest(new JsonObject { ["lotId"] = w.Lot.ToString(), ["quantity"] = 999 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"{Balances}/{balance.Id}", new RecordWriteRequest(new JsonObject { ["quantity"] = 999 }, balance.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Balances}/{balance.Id}?version={balance.Version}")).StatusCode);
        Assert.Equal(10m, await BalanceAsync(admin, w.Lot, w.A));

        // Veritabanı: uygulama dışı doğrudan UPDATE/DELETE tetikleyici tarafından reddedilir; eksi bakiye kısıtla engellenir.
        var update = await Assert.ThrowsAsync<SqlException>(() => factory.ScalarAsync<int>(s.Tenant.Id,
            "UPDATE [warehouse].[StockMovements] SET [Quantity] = 1 WHERE [Id] = @id; SELECT 1;", ("@id", movement.Id)));
        Assert.Contains("yalnızca eklenir", update.Message);
        var delete = await Assert.ThrowsAsync<SqlException>(() => factory.ScalarAsync<int>(s.Tenant.Id,
            "DELETE FROM [warehouse].[StockMovements] WHERE [Id] = @id; SELECT 1;", ("@id", movement.Id)));
        Assert.Contains("yalnızca eklenir", delete.Message);
        await Assert.ThrowsAsync<SqlException>(() => factory.ScalarAsync<int>(s.Tenant.Id,
            "UPDATE [warehouse].[StockBalances] SET [Quantity] = -1 WHERE [Id] = @id; SELECT 1;", ("@id", balance.Id)));
        Assert.Equal(1, await factory.ScalarAsync<int>(s.Tenant.Id, "SELECT COUNT(*) FROM [warehouse].[StockMovements] WHERE [Id] = @id", ("@id", movement.Id)));

        // Hareketi olan parti silinemez (pasife alınır); hareketi olmayan parti silinebilir.
        var lot = (await admin.GetFromJsonAsync<RecordResponse>($"{Lots}/{w.Lot}"))!;
        var blocked = await admin.DeleteAsync($"{Lots}/{lot.Id}?version={lot.Version}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var spare = await PostAsync(admin, Lots, LotValues("BOS-1", w.StockItem, w.Owner));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Lots}/{spare.Id}?version={spare.Version}")).StatusCode);

        // Tenant verisinin tamamen silinmesi tek meşru istisnadır: tenant-delete defteri de temizler.
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ITenantDataTransfer>().DeleteAsync(s.Tenant.Id, CancellationToken.None);
        Assert.Contains(result.Tables, t => t is { Schema: "warehouse", Table: "StockMovements" });
        Assert.Equal(0, await factory.ScalarAsync<int>(null, "SELECT COUNT(*) FROM [warehouse].[StockMovements] WHERE [TenantId] = @t", ("@t", s.Tenant.Id)));
    }

    [Fact]
    public async Task Same_idempotency_key_does_not_produce_a_second_movement()
    {
        var s = await SetUpAsync();
        var other = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var adminB = await LoginAsync(other.Admin);
        var w = await WorldAsync(admin);
        var wB = await WorldAsync(adminB);

        var first = await PostMovementAsync(admin, Move("Receipt", w.Lot, to: w.A, quantity: 10, key: "terminal-42"));
        var retry = await admin.PostAsJsonAsync(Movements, new RecordWriteRequest(Move("Receipt", w.Lot, to: w.A, quantity: 10, key: "terminal-42")));
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Contains(first.Id.ToString(), await retry.Content.ReadAsStringAsync());
        Assert.Equal(10m, await BalanceAsync(admin, w.Lot, w.A));
        Assert.Single(await ListAsync(admin, Movements, $"lotId={w.Lot}"));

        // Anahtar tenant'a özeldir; boş anahtarlar çakışmaz.
        await PostMovementAsync(adminB, Move("Receipt", wB.Lot, to: wB.A, quantity: 5, key: "terminal-42"));
        await PostMovementAsync(admin, Move("Receipt", w.Lot, to: w.A, quantity: 1));
        await PostMovementAsync(admin, Move("Receipt", w.Lot, to: w.A, quantity: 1));
        Assert.Equal(12m, await BalanceAsync(admin, w.Lot, w.A));
    }

    [Fact]
    public async Task Concurrent_issues_never_drive_the_balance_negative_and_the_ledger_matches_the_balance()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var w = await WorldAsync(admin);
        await PostMovementAsync(admin, Move("Receipt", w.Lot, to: w.A, quantity: 10));

        // 10 birimlik bakiyeden 3'er birimlik 8 eşzamanlı çıkış: en çok 3'ü başarılı olabilir; kalanlar yetersiz stok ya da çakışma (409) alır.
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            admin.PostAsJsonAsync(Movements, new RecordWriteRequest(Move("Issue", w.Lot, from: w.A, quantity: 3, key: $"race-{i}")))));
        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity }));
        Assert.InRange(succeeded, 1, 3);

        var balance = await BalanceAsync(admin, w.Lot, w.A);
        Assert.Equal(10m - (3m * succeeded), balance);
        Assert.True(balance >= 0);

        // Bakiye defterden türemiştir: girişler - çıkışlar.
        var movements = await ListAsync(admin, Movements, $"lotId={w.Lot}");
        var derived = movements.Sum(m => m.Values["typeKey"]!.GetValue<string>() == "Receipt" ? m.Values["quantity"]!.GetValue<decimal>() : -m.Values["quantity"]!.GetValue<decimal>());
        Assert.Equal(balance, derived);
        Assert.Equal(succeeded + 1, movements.Count);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_use_the_ledger_of_a_tenant()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var adminA = await LoginAsync(a.Admin);
        using var adminB = await LoginAsync(b.Admin);
        var wA = await WorldAsync(adminA);
        var wB = await WorldAsync(adminB);
        var movementA = await PostMovementAsync(adminA, Move("Receipt", wA.Lot, to: wA.A, quantity: 10));

        // B, A'nın parti/hareket/bakiyesini göremez; varlığı belli edilmez.
        Assert.Empty((await ListAsync(adminB, Movements, $"lotId={wA.Lot}")));
        Assert.Empty((await ListAsync(adminB, Balances, $"lotId={wA.Lot}")));
        Assert.DoesNotContain(await ListAsync(adminB, Lots, null), l => l.Id == wA.Lot);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Movements}/{movementA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"{Lots}/{wA.Lot}")).StatusCode);

        // B, A'nın partisine ya da lokasyonuna hareket yazamaz ve A'nın hareketini geri alamaz.
        async Task<ValidationProblemDetails> Reject(JsonObject values) =>
            await ValidationProblemAsync(await adminB.PostAsJsonAsync(Movements, new RecordWriteRequest(values)));
        Assert.Contains("lotId", (await Reject(Move("Receipt", wA.Lot, to: wB.A, quantity: 1))).Errors.Keys);
        Assert.Contains("toLocationId", (await Reject(Move("Receipt", wB.Lot, to: wA.A, quantity: 1))).Errors.Keys);
        Assert.Contains("reversesMovementId", (await Reject(new JsonObject { ["typeKey"] = "Correction", ["reversesMovementId"] = movementA.Id.ToString() })).Errors.Keys);
        Assert.Equal(10m, await BalanceAsync(adminA, wA.Lot, wA.A));

        // Parti numarası TenantId ile birlikte benzersizdir: B aynı numarayla kendi partisini açar.
        var sameNo = await PostAsync(adminB, Lots, LotValues("LOT-ORTAK", wB.StockItem, wB.Owner));
        var inA = await PostAsync(adminA, Lots, LotValues("LOT-ORTAK", wA.StockItem, wA.Owner));
        Assert.NotEqual(sameNo.Id, inA.Id);
    }

    [Fact]
    public async Task Lots_movements_and_balances_need_their_own_permissions()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        using var lotsOnly = await LoginAsync(s.LotsOnly);
        using var reader = await LoginAsync(s.MovementsReader);
        using var poster = await LoginAsync(s.MovementsPoster);
        var w = await WorldAsync(admin);
        var movement = await PostMovementAsync(admin, Move("Receipt", w.Lot, to: w.A, quantity: 10));

        // Parti izni hareket ve bakiye vermez; hareket okuma izni hareket yazdırmaz ve parti yönetmez.
        Assert.Equal(HttpStatusCode.OK, (await lotsOnly.GetAsync($"{Lots}/{w.Lot}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lotsOnly.GetAsync(Movements)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lotsOnly.GetAsync(Balances)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lotsOnly.PostAsJsonAsync(Movements, new RecordWriteRequest(Move("Receipt", w.Lot, to: w.A, quantity: 1)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await lotsOnly.PostAsJsonAsync(Lots, new RecordWriteRequest(LotValues("YENI-1", w.StockItem, w.Owner)))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Movements}/{movement.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(Balances)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Movements, new RecordWriteRequest(Move("Receipt", w.Lot, to: w.A, quantity: 1)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(Lots)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Lots, new RecordWriteRequest(LotValues("YENI-2", w.StockItem, w.Owner)))).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await poster.PostAsJsonAsync(Movements, new RecordWriteRequest(Move("Receipt", w.Lot, to: w.A, quantity: 1)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await poster.PostAsJsonAsync(Lots, new RecordWriteRequest(LotValues("YENI-3", w.StockItem, w.Owner)))).StatusCode);
        Assert.Equal(11m, await BalanceAsync(admin, w.Lot, w.A));
    }

    [Fact]
    public async Task Lot_keeps_its_item_owner_and_declaration_line_and_validates_its_number()
    {
        var s = await SetUpAsync();
        using var admin = await LoginAsync(s.Admin);
        var w = await WorldAsync(admin);
        var billTo = await PartyAsync(admin, "Fatura Tarafı");
        var lot = await PostAsync(admin, Lots, LotValues("lot-x1", w.StockItem, w.Owner, billTo));
        Assert.Equal("LOT-X1", lot.Values["lotNo"]!.GetValue<string>());
        Assert.Equal(billTo.ToString(), lot.Values["billToPartyId"]!.GetValue<string>());

        async Task<ValidationProblemDetails> Reject(JsonObject values) => await ValidationProblemAsync(await admin.PostAsJsonAsync(Lots, new RecordWriteRequest(values)));
        Assert.Equal(["Bu parti numarası başka bir partide kullanılıyor."], (await Reject(LotValues("LOT-X1", w.StockItem, w.Owner))).Errors["lotNo"]);
        Assert.Contains("lotNo", (await Reject(LotValues("LOT X", w.StockItem, w.Owner))).Errors.Keys);
        Assert.Contains("stockItemId", (await Reject(LotValues("LOT-X2", Guid.NewGuid(), w.Owner))).Errors.Keys);
        Assert.Contains("ownerPartyId", (await Reject(LotValues("LOT-X3", w.StockItem, Guid.NewGuid()))).Errors.Keys);
        var withLine = LotValues("LOT-X4", w.StockItem, w.Owner);
        withLine["declarationLineId"] = Guid.NewGuid().ToString();
        Assert.Contains("declarationLineId", (await Reject(withLine)).Errors.Keys);

        // Ürün ve malik sonradan değişmez; faturalanan taraf ve açıklama değişebilir.
        var change = await ValidationProblemAsync(await admin.PutAsJsonAsync($"{Lots}/{lot.Id}",
            new RecordWriteRequest(new JsonObject { ["ownerPartyId"] = billTo.ToString() }, lot.Version)));
        Assert.Contains("ownerPartyId", change.Errors.Keys);
        var updated = await PutAsync(admin, Lots, lot, new JsonObject { ["description"] = "Açıklandı", ["billToPartyId"] = null });
        Assert.Null(updated.Values["billToPartyId"]);

        // Malikin firması, partisi olduğu sürece silinemez (ortak kayıt koruması).
        var owner = (await admin.GetFromJsonAsync<RecordResponse>($"{Parties}/{w.Owner}"))!;
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{Parties}/{w.Owner}?version={owner.Version}")).StatusCode);
    }

    // ---- Yardımcılar ----

    private static JsonObject Move(string type, Guid lot, Guid? from = null, Guid? to = null, decimal? quantity = null, string? key = null, JsonObject? extra = null)
    {
        var values = new JsonObject { ["typeKey"] = type, ["lotId"] = lot.ToString() };
        if (from is { } f) { values["fromLocationId"] = f.ToString(); }
        if (to is { } t) { values["toLocationId"] = t.ToString(); }
        if (quantity is { } q) { values["quantity"] = q; }
        if (key is not null) { values["idempotencyKey"] = key; }
        if (extra is not null) { foreach (var (name, node) in extra) { values[name] = node?.DeepClone(); } }
        return values;
    }

    private static JsonObject LotValues(string lotNo, Guid stockItem, Guid owner, Guid? billTo = null)
    {
        var values = new JsonObject { ["lotNo"] = lotNo, ["stockItemId"] = stockItem.ToString(), ["ownerPartyId"] = owner.ToString() };
        if (billTo is { } b) { values["billToPartyId"] = b.ToString(); }
        return values;
    }

    private sealed record World(Guid StockItem, Guid Owner, Guid Lot, Guid A, Guid B);

    /// <summary>Bir ürün, bir malik firma, bir parti ve iki lokasyon (tek seviyeli bir tesiste) kurar.</summary>
    private async Task<World> WorldAsync(HttpClient admin)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var facility = await PostAsync(admin, "/api/records/warehouse.facility", new JsonObject { ["code"] = $"T{suffix}", ["name"] = "Hareket tesisi" });
        await PostAsync(admin, "/api/records/warehouse.location-level", new JsonObject
        {
            ["facilityId"] = facility.Id.ToString(), ["position"] = 1, ["key"] = "bin", ["name"] = "Göz",
            ["characterSet"] = "alphanumeric", ["minLength"] = 1, ["maxLength"] = 8,
        });
        var a = await PostAsync(admin, "/api/records/warehouse.location", new JsonObject { ["facilityId"] = facility.Id.ToString(), ["value"] = "A1" });
        var b = await PostAsync(admin, "/api/records/warehouse.location", new JsonObject { ["facilityId"] = facility.Id.ToString(), ["value"] = "B1" });
        var item = await PostAsync(admin, "/api/records/warehouse.stock-item", new JsonObject { ["code"] = $"URUN-{suffix}", ["name"] = "Test ürünü" });
        var owner = await PartyAsync(admin, $"Malik {suffix}");
        var lot = await PostAsync(admin, Lots, LotValues($"LOT-{suffix}", item.Id, owner));
        return new World(item.Id, owner, lot.Id, a.Id, b.Id);
    }

    private sealed record Tenant(Guid Id, Guid AdminId);

    private async Task<(Tenant Tenant, ModulixUser Admin, ModulixUser LotsOnly, ModulixUser MovementsReader, ModulixUser MovementsPoster)> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "warehouse", enabled: true);
        await factory.SetModuleAsync(tenant.Id, "crm", enabled: true);
        var admin = await factory.CreateUserAsync(tenant);
        var lotsOnly = await factory.CreateUserAsync(tenant);
        var reader = await factory.CreateUserAsync(tenant);
        var poster = await factory.CreateUserAsync(tenant);
        using (var scope = factory.OpenScope(tenant.Id))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
            var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
            Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(admin.Id, tenant.Id), [system.Id], CancellationToken.None));
        }

        await factory.CreateRoleAsync(tenant.Id, [LotsRead, LotsWrite, StockItemsRead], Scope.All, lotsOnly);
        await factory.CreateRoleAsync(tenant.Id, [MovementsRead], Scope.All, reader);
        await factory.CreateRoleAsync(tenant.Id, [MovementsRead, MovementsWrite], Scope.All, poster);
        return (new Tenant(tenant.Id, admin.Id), admin, lotsOnly, reader, poster);
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task<Guid> PartyAsync(HttpClient client, string name) =>
        (await PostAsync(client, Parties, new JsonObject { ["name"] = name, ["taxNumber"] = Guid.NewGuid().ToString("N")[..10] })).Id;

    private static async Task<decimal> BalanceAsync(HttpClient client, Guid lot, Guid location)
    {
        var rows = await ListAsync(client, Balances, $"lotId={lot}&locationId={location}");
        return rows.Count == 0 ? 0m : rows.Single().Values["quantity"]!.GetValue<decimal>();
    }

    private static async Task<RecordResponse> PostMovementAsync(HttpClient client, JsonObject values) => await PostAsync(client, Movements, values);

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string url, string? query)
    {
        var response = await client.GetAsync($"{url}?pageSize=100{(query is null ? string.Empty : "&" + query)}");
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
