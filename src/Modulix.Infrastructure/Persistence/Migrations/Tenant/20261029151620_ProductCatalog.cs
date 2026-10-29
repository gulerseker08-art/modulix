using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class ProductCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MeasureUnits",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    GroupKey = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FactorToBase = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeasureUnits", x => x.Id);
                    table.UniqueConstraint("AK_MeasureUnits_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_MeasureUnits_Factor", "[FactorToBase] > 0");
                });

            migrationBuilder.CreateTable(
                name: "ProductCategories",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCategories", x => x.Id);
                    table.UniqueConstraint("AK_ProductCategories_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ProductCategories_ProductCategories_TenantId_ParentId",
                        columns: x => new { x.TenantId, x.ParentId },
                        principalSchema: "crm",
                        principalTable: "ProductCategories",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BaseUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Barcode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Brand = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Tags = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SaleStart = table.Column<DateOnly>(type: "date", nullable: true),
                    SaleEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    TaxCategory = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    PriceIncludesTax = table.Column<bool>(type: "bit", nullable: false),
                    QuantityPrecision = table.Column<int>(type: "int", nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CostCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    ServiceDurationMinutes = table.Column<int>(type: "int", nullable: true),
                    DeliveryMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ChargeType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RecurringPeriod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                    table.UniqueConstraint("AK_Products_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Products_Precision", "[QuantityPrecision] BETWEEN 0 AND 4");
                    table.CheckConstraint("CK_Products_SaleDates", "[SaleStart] IS NULL OR [SaleEnd] IS NULL OR [SaleStart] <= [SaleEnd]");
                    table.ForeignKey(
                        name: "FK_Products_MeasureUnits_TenantId_BaseUnitId",
                        columns: x => new { x.TenantId, x.BaseUnitId },
                        principalSchema: "crm",
                        principalTable: "MeasureUnits",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Products_ProductCategories_TenantId_CategoryId",
                        columns: x => new { x.TenantId, x.CategoryId },
                        principalSchema: "crm",
                        principalTable: "ProductCategories",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_MeasureUnits_Code",
                schema: "crm",
                table: "MeasureUnits",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_TenantId_ParentId",
                schema: "crm",
                table: "ProductCategories",
                columns: new[] { "TenantId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_TenantId_BaseUnitId",
                schema: "crm",
                table: "Products",
                columns: new[] { "TenantId", "BaseUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_TenantId_CategoryId",
                schema: "crm",
                table: "Products",
                columns: new[] { "TenantId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_TenantId_Status_Name",
                schema: "crm",
                table: "Products",
                columns: new[] { "TenantId", "Status", "Name" });

            migrationBuilder.CreateIndex(
                name: "UX_Products_Code",
                schema: "crm",
                table: "Products",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            // Her tenant tablosu satır düzeyinde izolasyon politikasına girer (ADR-0009).
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "ProductCategories");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "MeasureUnits");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "Products");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "Products");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "MeasureUnits");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "ProductCategories");

            migrationBuilder.DropTable(
                name: "Products",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "MeasureUnits",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "ProductCategories",
                schema: "crm");
        }
    }
}
