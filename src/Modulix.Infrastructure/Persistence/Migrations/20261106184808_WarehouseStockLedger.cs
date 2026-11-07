using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WarehouseStockLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Lots",
                schema: "warehouse",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StockItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BillToPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeclarationLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lots", x => x.Id);
                    table.UniqueConstraint("AK_Lots_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Lots_CustomsDeclarationLines_TenantId_DeclarationLineId",
                        columns: x => new { x.TenantId, x.DeclarationLineId },
                        principalSchema: "warehouse",
                        principalTable: "CustomsDeclarationLines",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Lots_Parties_TenantId_BillToPartyId",
                        columns: x => new { x.TenantId, x.BillToPartyId },
                        principalSchema: "core",
                        principalTable: "Parties",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Lots_Parties_TenantId_OwnerPartyId",
                        columns: x => new { x.TenantId, x.OwnerPartyId },
                        principalSchema: "core",
                        principalTable: "Parties",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Lots_StockItems_TenantId_StockItemId",
                        columns: x => new { x.TenantId, x.StockItemId },
                        principalSchema: "warehouse",
                        principalTable: "StockItems",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockBalances",
                schema: "warehouse",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockBalances", x => x.Id);
                    table.UniqueConstraint("AK_StockBalances_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_StockBalances_NonNegative", "[Quantity] >= 0");
                    table.ForeignKey(
                        name: "FK_StockBalances_Locations_TenantId_LocationId",
                        columns: x => new { x.TenantId, x.LocationId },
                        principalSchema: "warehouse",
                        principalTable: "Locations",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockBalances_Lots_TenantId_LotId",
                        columns: x => new { x.TenantId, x.LotId },
                        principalSchema: "warehouse",
                        principalTable: "Lots",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockMovements",
                schema: "warehouse",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TypeKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromLocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ToLocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReasonKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    DocumentReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReversesMovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements", x => x.Id);
                    table.UniqueConstraint("AK_StockMovements_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_StockMovements_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_StockMovements_Locations_TenantId_FromLocationId",
                        columns: x => new { x.TenantId, x.FromLocationId },
                        principalSchema: "warehouse",
                        principalTable: "Locations",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_Locations_TenantId_ToLocationId",
                        columns: x => new { x.TenantId, x.ToLocationId },
                        principalSchema: "warehouse",
                        principalTable: "Locations",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_Lots_TenantId_LotId",
                        columns: x => new { x.TenantId, x.LotId },
                        principalSchema: "warehouse",
                        principalTable: "Lots",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_StockMovements_TenantId_ReversesMovementId",
                        columns: x => new { x.TenantId, x.ReversesMovementId },
                        principalSchema: "warehouse",
                        principalTable: "StockMovements",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Lots_TenantId_BillToPartyId",
                schema: "warehouse",
                table: "Lots",
                columns: new[] { "TenantId", "BillToPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Lots_TenantId_DeclarationLineId",
                schema: "warehouse",
                table: "Lots",
                columns: new[] { "TenantId", "DeclarationLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_Lots_TenantId_OwnerPartyId",
                schema: "warehouse",
                table: "Lots",
                columns: new[] { "TenantId", "OwnerPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Lots_TenantId_StockItemId",
                schema: "warehouse",
                table: "Lots",
                columns: new[] { "TenantId", "StockItemId" });

            migrationBuilder.CreateIndex(
                name: "UX_Lots_LotNo",
                schema: "warehouse",
                table: "Lots",
                columns: new[] { "TenantId", "LotNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockBalances_TenantId_LocationId",
                schema: "warehouse",
                table: "StockBalances",
                columns: new[] { "TenantId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "UX_StockBalances_LotLocation",
                schema: "warehouse",
                table: "StockBalances",
                columns: new[] { "TenantId", "LotId", "LocationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_TenantId_CorrelationId",
                schema: "warehouse",
                table: "StockMovements",
                columns: new[] { "TenantId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_TenantId_FromLocationId",
                schema: "warehouse",
                table: "StockMovements",
                columns: new[] { "TenantId", "FromLocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_TenantId_LotId_OccurredAt",
                schema: "warehouse",
                table: "StockMovements",
                columns: new[] { "TenantId", "LotId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_TenantId_OccurredAt",
                schema: "warehouse",
                table: "StockMovements",
                columns: new[] { "TenantId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_TenantId_ToLocationId",
                schema: "warehouse",
                table: "StockMovements",
                columns: new[] { "TenantId", "ToLocationId" });

            migrationBuilder.CreateIndex(
                name: "UX_StockMovements_IdempotencyKey",
                schema: "warehouse",
                table: "StockMovements",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_StockMovements_Reverses",
                schema: "warehouse",
                table: "StockMovements",
                columns: new[] { "TenantId", "ReversesMovementId" },
                unique: true,
                filter: "[ReversesMovementId] IS NOT NULL");

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman). Şema ve GRANT ilk depo migration'ındadır.
            TenantIsolationSql.AddTable(migrationBuilder, "warehouse", "Lots");
            TenantIsolationSql.AddTable(migrationBuilder, "warehouse", "StockMovements");
            TenantIsolationSql.AddTable(migrationBuilder, "warehouse", "StockBalances");

            // Hareket defteri yalnızca eklenir (ADR-0032 K13): veritabanı düzeyinde güncelleme ve silme reddedilir (uygulama katmanı da reddeder). Tek istisna
            // tenant verisinin tamamen silinmesidir: tenant-delete komutu yalnızca kendi bağlantısında AllowLedgerPurge oturum değerini 1 yapar.
            // INSTEAD OF tetikleyicisi içindeki DELETE aynı tabloda tetikleyiciyi yeniden çalıştırmaz.
            migrationBuilder.Sql("""
                CREATE TRIGGER [warehouse].[TR_StockMovements_AppendOnly] ON [warehouse].[StockMovements]
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF NOT EXISTS (SELECT 1 FROM inserted)
                       AND ISNULL(TRY_CAST(SESSION_CONTEXT(N'AllowLedgerPurge') AS int), 0) = 1
                    BEGIN
                        DELETE m FROM [warehouse].[StockMovements] m INNER JOIN deleted d ON d.[Id] = m.[Id];
                        RETURN;
                    END;
                    THROW 51000, N'Stok hareket defteri yalnızca eklenir; hareket değiştirilemez ya da silinemez. Düzeltmek için ters kayıt oluşturun.', 1;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [warehouse].[TR_StockMovements_AppendOnly];");
            TenantIsolationSql.RemoveTable(migrationBuilder, "warehouse", "StockBalances");
            TenantIsolationSql.RemoveTable(migrationBuilder, "warehouse", "StockMovements");
            TenantIsolationSql.RemoveTable(migrationBuilder, "warehouse", "Lots");

            migrationBuilder.DropTable(
                name: "StockBalances",
                schema: "warehouse");

            migrationBuilder.DropTable(
                name: "StockMovements",
                schema: "warehouse");

            migrationBuilder.DropTable(
                name: "Lots",
                schema: "warehouse");
        }
    }
}
