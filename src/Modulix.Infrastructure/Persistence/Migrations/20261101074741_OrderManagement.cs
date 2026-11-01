using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orders");

            migrationBuilder.CreateTable(
                name: "Orders",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceQuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceQuoteNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SourceQuoteRevisionNo = table.Column<int>(type: "int", nullable: false),
                    SourceQuoteSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    SourceOpportunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContractNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ConversionMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcceptanceMethod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AcceptedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AcceptedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AcceptanceNote = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    QuoteValidUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    HandoverStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FulfillmentStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CustomerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BillingAddress = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DeliveryAddress = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    PriceIncludesTax = table.Column<bool>(type: "bit", nullable: false),
                    ReportingCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    ExchangeRateDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PaymentTerms = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DeliveryTerms = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CommercialTerms = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Commitments = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    OperationsNotes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PlannedStartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PlannedDeliveryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NetTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    GrossTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.UniqueConstraint("AK_Orders_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Orders_FulfillmentStatus", "[FulfillmentStatus] IN ('NotStarted','Planned','Partial','Completed')");
                    table.CheckConstraint("CK_Orders_HandoverStatus", "[HandoverStatus] IN ('NotStarted','Preparing','Ready','Sent','InfoRequested','Accepted')");
                    table.CheckConstraint("CK_Orders_Status", "[Status] IN ('Draft','PendingApproval','Approved','Cancelled','Closed')");
                    table.ForeignKey(
                        name: "FK_Orders_Parties_TenantId_PartyId",
                        columns: x => new { x.TenantId, x.PartyId },
                        principalSchema: "core",
                        principalTable: "Parties",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Orders_People_TenantId_PersonId",
                        columns: x => new { x.TenantId, x.PersonId },
                        principalSchema: "core",
                        principalTable: "People",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteLineConsumptions",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcceptedQuantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    AcceptedNet = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    AcceptedTax = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ConvertedQuantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    ConvertedNet = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ConvertedTax = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteLineConsumptions", x => x.Id);
                    table.UniqueConstraint("AK_QuoteLineConsumptions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_QuoteLineConsumptions_Limit", "[ConvertedQuantity] >= 0 AND [ConvertedQuantity] <= [AcceptedQuantity]");
                });

            migrationBuilder.CreateTable(
                name: "OrderEvents",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FromStatus = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderEvents", x => x.Id);
                    table.UniqueConstraint("AK_OrderEvents_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_OrderEvents_Orders_TenantId_OrderId",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalSchema: "orders",
                        principalTable: "Orders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderLines",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ParentSourceLineKey = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    AcceptedQuantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    ListAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LineDiscount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxRatePercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    Net = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Tax = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Gross = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RecurringPeriod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SourceQuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceQuoteLineKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveredQuantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    IsCancelled = table.Column<bool>(type: "bit", nullable: false),
                    PlannedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderLines", x => x.Id);
                    table.UniqueConstraint("AK_OrderLines_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_OrderLines_Quantity", "[Quantity] > 0 AND [DeliveredQuantity] >= 0 AND [DeliveredQuantity] <= [Quantity]");
                    table.ForeignKey(
                        name: "FK_OrderLines_Orders_TenantId_OrderId",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalSchema: "orders",
                        principalTable: "Orders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderEvents_TenantId_OrderId_CreatedAt",
                schema: "orders",
                table: "OrderEvents",
                columns: new[] { "TenantId", "OrderId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_TenantId_OrderId_Position",
                schema: "orders",
                table: "OrderLines",
                columns: new[] { "TenantId", "OrderId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_TenantId_SourceQuoteId_SourceQuoteLineKey",
                schema: "orders",
                table: "OrderLines",
                columns: new[] { "TenantId", "SourceQuoteId", "SourceQuoteLineKey" });

            migrationBuilder.CreateIndex(
                name: "UX_OrderLines_SourceLine",
                schema: "orders",
                table: "OrderLines",
                columns: new[] { "TenantId", "OrderId", "SourceQuoteLineKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_FulfillmentStatus_PlannedDeliveryDate",
                schema: "orders",
                table: "Orders",
                columns: new[] { "TenantId", "FulfillmentStatus", "PlannedDeliveryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_OwnerId_Status",
                schema: "orders",
                table: "Orders",
                columns: new[] { "TenantId", "OwnerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_PartyId",
                schema: "orders",
                table: "Orders",
                columns: new[] { "TenantId", "PartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_PersonId",
                schema: "orders",
                table: "Orders",
                columns: new[] { "TenantId", "PersonId" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_SourceOpportunityId",
                schema: "orders",
                table: "Orders",
                columns: new[] { "TenantId", "SourceOpportunityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_SourceQuoteId",
                schema: "orders",
                table: "Orders",
                columns: new[] { "TenantId", "SourceQuoteId" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_Status_HandoverStatus",
                schema: "orders",
                table: "Orders",
                columns: new[] { "TenantId", "Status", "HandoverStatus" });

            migrationBuilder.CreateIndex(
                name: "UX_Orders_Number",
                schema: "orders",
                table: "Orders",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_QuoteLineConsumptions_Line",
                schema: "orders",
                table: "QuoteLineConsumptions",
                columns: new[] { "TenantId", "QuoteId", "LineKey" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, "orders", table);
            }

            // Uygulama kullanıcısı yeni şemada yalnızca veri işlemi yapar (init.sql ile aynı; mevcut veritabanları için burada da verilir).
            migrationBuilder.Sql(
                "IF DATABASE_PRINCIPAL_ID(N'modulix_app_role') IS NOT NULL GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[orders] TO [modulix_app_role];");
        }

        private static readonly string[] TenantTables = ["Orders", "OrderLines", "OrderEvents", "QuoteLineConsumptions"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, "orders", table);
            }

            migrationBuilder.DropTable(
                name: "OrderEvents",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "OrderLines",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "QuoteLineConsumptions",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "Orders",
                schema: "orders");
        }
    }
}
