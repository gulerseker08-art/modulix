using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class QuoteManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Quotes",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionNo = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OpportunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PriceListId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ApprovalStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DeliveryStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    ReportingCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    ExchangeRateDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PriceIncludesTax = table.Column<bool>(type: "bit", nullable: false),
                    CalculationPolicyVersion = table.Column<int>(type: "int", nullable: false),
                    DocumentDiscountKind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DocumentDiscountValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    PaymentTerms = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DeliveryTerms = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CommercialTerms = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    InternalNotes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ListTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DiscountTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    NetTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    GrossTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    OptionalNet = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RecurringSummary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CalculatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PresentedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CustomerNameSnapshot = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
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
                    table.PrimaryKey("PK_Quotes", x => x.Id);
                    table.UniqueConstraint("AK_Quotes_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Quotes_Revision", "[RevisionNo] >= 1");
                    table.CheckConstraint("CK_Quotes_Totals", "[NetTotal] >= 0 AND [TaxTotal] >= 0 AND [GrossTotal] >= 0");
                    table.ForeignKey(
                        name: "FK_Quotes_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Quotes_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Quotes_Opportunities_TenantId_OpportunityId",
                        columns: x => new { x.TenantId, x.OpportunityId },
                        principalSchema: "crm",
                        principalTable: "Opportunities",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Quotes_PriceLists_TenantId_PriceListId",
                        columns: x => new { x.TenantId, x.PriceListId },
                        principalSchema: "crm",
                        principalTable: "PriceLists",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteEvents",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FromStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteEvents", x => x.Id);
                    table.UniqueConstraint("AK_QuoteEvents_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_QuoteEvents_Quotes_TenantId_QuoteId",
                        columns: x => new { x.TenantId, x.QuoteId },
                        principalSchema: "crm",
                        principalTable: "Quotes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteLines",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StableKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    GroupName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UnitName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    DiscountKind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DiscountValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    TaxRatePercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    TaxCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsSelected = table.Column<bool>(type: "bit", nullable: false),
                    ParentStableKey = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecurringPeriod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    UnitCost = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    Included = table.Column<bool>(type: "bit", nullable: false),
                    ListAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LineDiscount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DocumentDiscountShare = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Net = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Tax = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Gross = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteLines", x => x.Id);
                    table.UniqueConstraint("AK_QuoteLines_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_QuoteLines_Amounts", "[Quantity] > 0 AND [UnitPrice] >= 0 AND [DiscountValue] >= 0");
                    table.CheckConstraint("CK_QuoteLines_Tax", "[TaxRatePercent] >= 0 AND [TaxRatePercent] <= 100");
                    table.ForeignKey(
                        name: "FK_QuoteLines_Products_TenantId_ProductId",
                        columns: x => new { x.TenantId, x.ProductId },
                        principalSchema: "crm",
                        principalTable: "Products",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QuoteLines_Quotes_TenantId_QuoteId",
                        columns: x => new { x.TenantId, x.QuoteId },
                        principalSchema: "crm",
                        principalTable: "Quotes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteEvents_TenantId_QuoteId_CreatedAt",
                schema: "crm",
                table: "QuoteEvents",
                columns: new[] { "TenantId", "QuoteId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteLines_TenantId_ProductId",
                schema: "crm",
                table: "QuoteLines",
                columns: new[] { "TenantId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteLines_TenantId_QuoteId_Position",
                schema: "crm",
                table: "QuoteLines",
                columns: new[] { "TenantId", "QuoteId", "Position" });

            migrationBuilder.CreateIndex(
                name: "UX_QuoteLines_QuoteKey",
                schema: "crm",
                table: "QuoteLines",
                columns: new[] { "TenantId", "QuoteId", "StableKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_CompanyId",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_ContactId",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_OpportunityId",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "OpportunityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_OwnerId_Status",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "OwnerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_PriceListId",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "PriceListId" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_Status_ValidUntil",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "Status", "ValidUntil" });

            migrationBuilder.CreateIndex(
                name: "UX_Quotes_FamilyAccepted",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "FamilyId" },
                unique: true,
                filter: "[Status] = 'Accepted'");

            migrationBuilder.CreateIndex(
                name: "UX_Quotes_FamilyRevision",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "FamilyId", "RevisionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Quotes_NumberRevision",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "QuoteNumber", "RevisionNo" },
                unique: true);

            // Her tenant tablosu satır düzeyinde izolasyon politikasına girer (ADR-0009).
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "Quotes");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "QuoteLines");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "QuoteEvents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "QuoteEvents");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "QuoteLines");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "Quotes");

            migrationBuilder.DropTable(
                name: "QuoteEvents",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "QuoteLines",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Quotes",
                schema: "crm");
        }
    }
}
