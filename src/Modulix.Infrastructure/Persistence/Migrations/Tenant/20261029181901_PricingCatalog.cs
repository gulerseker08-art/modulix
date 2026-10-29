using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class PricingCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscountPolicies",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Percent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Stackable = table.Column<bool>(type: "bit", nullable: false),
                    MaxTotalPercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    ApprovalAbovePercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscountPolicies", x => x.Id);
                    table.UniqueConstraint("AK_DiscountPolicies_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_DiscountPolicies_Value", "([Percent] IS NOT NULL AND [Amount] IS NULL AND [Percent] > 0 AND [Percent] <= 100) OR ([Percent] IS NULL AND [Amount] IS NOT NULL AND [Amount] > 0 AND [Currency] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "ExchangeRates",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    ToCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    ValidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRates", x => x.Id);
                    table.UniqueConstraint("AK_ExchangeRates_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_ExchangeRates_Pair", "[FromCurrency] <> [ToCurrency]");
                    table.CheckConstraint("CK_ExchangeRates_Rate", "[Rate] > 0");
                });

            migrationBuilder.CreateTable(
                name: "PriceLists",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Segment = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ValidFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ValidTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TierMethod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLists", x => x.Id);
                    table.UniqueConstraint("AK_PriceLists_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_PriceLists_Dates", "[ValidFrom] IS NULL OR [ValidTo] IS NULL OR [ValidFrom] < [ValidTo]");
                    table.ForeignKey(
                        name: "FK_PriceLists_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductComponents",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductComponents", x => x.Id);
                    table.UniqueConstraint("AK_ProductComponents_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_ProductComponents_Quantity", "[Quantity] > 0 AND [PackageId] <> [ComponentId]");
                    table.ForeignKey(
                        name: "FK_ProductComponents_Products_TenantId_ComponentId",
                        columns: x => new { x.TenantId, x.ComponentId },
                        principalSchema: "crm",
                        principalTable: "Products",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductComponents_Products_TenantId_PackageId",
                        columns: x => new { x.TenantId, x.PackageId },
                        principalSchema: "crm",
                        principalTable: "Products",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxRates",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RatePercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRates", x => x.Id);
                    table.UniqueConstraint("AK_TaxRates_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_TaxRates_Dates", "[ValidTo] IS NULL OR [ValidFrom] < [ValidTo]");
                    table.CheckConstraint("CK_TaxRates_Rate", "[RatePercent] >= 0 AND [RatePercent] <= 100");
                });

            migrationBuilder.CreateTable(
                name: "PriceListItems",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PriceListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MinQuantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    MaxQuantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    IsFree = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceListItems", x => x.Id);
                    table.UniqueConstraint("AK_PriceListItems_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_PriceListItems_Tier", "[MinQuantity] >= 0 AND ([MaxQuantity] IS NULL OR [MaxQuantity] > [MinQuantity]) AND [UnitPrice] >= 0 AND ([IsFree] = 0 OR [UnitPrice] = 0)");
                    table.ForeignKey(
                        name: "FK_PriceListItems_PriceLists_TenantId_PriceListId",
                        columns: x => new { x.TenantId, x.PriceListId },
                        principalSchema: "crm",
                        principalTable: "PriceLists",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceListItems_Products_TenantId_ProductId",
                        columns: x => new { x.TenantId, x.ProductId },
                        principalSchema: "crm",
                        principalTable: "Products",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscountPolicies_TenantId_IsActive_Priority",
                schema: "crm",
                table: "DiscountPolicies",
                columns: new[] { "TenantId", "IsActive", "Priority" });

            migrationBuilder.CreateIndex(
                name: "UX_ExchangeRates_PairDay",
                schema: "crm",
                table: "ExchangeRates",
                columns: new[] { "TenantId", "FromCurrency", "ToCurrency", "ValidOn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceListItems_TenantId_ProductId",
                schema: "crm",
                table: "PriceListItems",
                columns: new[] { "TenantId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "UX_PriceListItems_Tier",
                schema: "crm",
                table: "PriceListItems",
                columns: new[] { "TenantId", "PriceListId", "ProductId", "MinQuantity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_TenantId_CompanyId",
                schema: "crm",
                table: "PriceLists",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_TenantId_Status_Scope",
                schema: "crm",
                table: "PriceLists",
                columns: new[] { "TenantId", "Status", "Scope" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductComponents_TenantId_ComponentId",
                schema: "crm",
                table: "ProductComponents",
                columns: new[] { "TenantId", "ComponentId" });

            migrationBuilder.CreateIndex(
                name: "UX_ProductComponents_Pair",
                schema: "crm",
                table: "ProductComponents",
                columns: new[] { "TenantId", "PackageId", "ComponentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_TaxRates_CodeFrom",
                schema: "crm",
                table: "TaxRates",
                columns: new[] { "TenantId", "Code", "ValidFrom" },
                unique: true);

            // Her tenant tablosu satır düzeyinde izolasyon politikasına girer (ADR-0009).
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "TaxRates");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "ExchangeRates");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "PriceLists");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "PriceListItems");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "ProductComponents");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "DiscountPolicies");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "DiscountPolicies");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "ProductComponents");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "PriceListItems");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "PriceLists");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "ExchangeRates");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "TaxRates");

            migrationBuilder.DropTable(
                name: "DiscountPolicies",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "ExchangeRates",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "PriceListItems",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "ProductComponents",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "TaxRates",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "PriceLists",
                schema: "crm");
        }
    }
}
