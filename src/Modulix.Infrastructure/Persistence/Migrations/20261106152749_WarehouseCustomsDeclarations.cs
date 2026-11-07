using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WarehouseCustomsDeclarations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomsDeclarations",
                schema: "warehouse",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationNo = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RegistrationDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CustomsOffice = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RegimeKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    StatusKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ImporterPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrokerPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomsDeclarations", x => x.Id);
                    table.UniqueConstraint("AK_CustomsDeclarations_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_CustomsDeclarations_Parties_TenantId_BrokerPartyId",
                        columns: x => new { x.TenantId, x.BrokerPartyId },
                        principalSchema: "core",
                        principalTable: "Parties",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomsDeclarations_Parties_TenantId_ImporterPartyId",
                        columns: x => new { x.TenantId, x.ImporterPartyId },
                        principalSchema: "core",
                        principalTable: "Parties",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomsDeclarationLines",
                schema: "warehouse",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeclarationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    Gtip = table.Column<string>(type: "varchar(14)", unicode: false, maxLength: 14, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ContainerTypeKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ContainerCount = table.Column<int>(type: "int", nullable: false),
                    GrossKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    NetKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    OriginCountryKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomsDeclarationLines", x => x.Id);
                    table.UniqueConstraint("AK_CustomsDeclarationLines_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_CustomsDeclarationLines_Weights", "[NetKg] >= 0 AND [NetKg] <= [GrossKg]");
                    table.ForeignKey(
                        name: "FK_CustomsDeclarationLines_CustomsDeclarations_TenantId_DeclarationId",
                        columns: x => new { x.TenantId, x.DeclarationId },
                        principalSchema: "warehouse",
                        principalTable: "CustomsDeclarations",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomsDeclarationLines_TenantId_Gtip",
                schema: "warehouse",
                table: "CustomsDeclarationLines",
                columns: new[] { "TenantId", "Gtip" });

            migrationBuilder.CreateIndex(
                name: "UX_CustomsDeclarationLines_LineNo",
                schema: "warehouse",
                table: "CustomsDeclarationLines",
                columns: new[] { "TenantId", "DeclarationId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomsDeclarations_TenantId_BrokerPartyId",
                schema: "warehouse",
                table: "CustomsDeclarations",
                columns: new[] { "TenantId", "BrokerPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomsDeclarations_TenantId_ImporterPartyId",
                schema: "warehouse",
                table: "CustomsDeclarations",
                columns: new[] { "TenantId", "ImporterPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomsDeclarations_TenantId_RegistrationDate",
                schema: "warehouse",
                table: "CustomsDeclarations",
                columns: new[] { "TenantId", "RegistrationDate" });

            migrationBuilder.CreateIndex(
                name: "UX_CustomsDeclarations_ExternalReference",
                schema: "warehouse",
                table: "CustomsDeclarations",
                columns: new[] { "TenantId", "ExternalReference" },
                unique: true,
                filter: "[ExternalReference] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CustomsDeclarations_RegistrationNo",
                schema: "warehouse",
                table: "CustomsDeclarations",
                columns: new[] { "TenantId", "RegistrationNo" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman). Şema ve GRANT ilk depo migration'ındadır.
            TenantIsolationSql.AddTable(migrationBuilder, "warehouse", "CustomsDeclarations");
            TenantIsolationSql.AddTable(migrationBuilder, "warehouse", "CustomsDeclarationLines");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "warehouse", "CustomsDeclarationLines");
            TenantIsolationSql.RemoveTable(migrationBuilder, "warehouse", "CustomsDeclarations");

            migrationBuilder.DropTable(
                name: "CustomsDeclarationLines",
                schema: "warehouse");

            migrationBuilder.DropTable(
                name: "CustomsDeclarations",
                schema: "warehouse");
        }
    }
}
