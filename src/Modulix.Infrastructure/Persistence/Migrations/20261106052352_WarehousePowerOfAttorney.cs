using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WarehousePowerOfAttorney : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PowersOfAttorney",
                schema: "warehouse",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImporterPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrokerPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    Scope = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DocumentReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PowersOfAttorney", x => x.Id);
                    table.UniqueConstraint("AK_PowersOfAttorney_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_PowersOfAttorney_Dates", "[ValidTo] IS NULL OR [ValidTo] >= [ValidFrom]");
                    table.ForeignKey(
                        name: "FK_PowersOfAttorney_Parties_TenantId_BrokerPartyId",
                        columns: x => new { x.TenantId, x.BrokerPartyId },
                        principalSchema: "core",
                        principalTable: "Parties",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PowersOfAttorney_Parties_TenantId_ImporterPartyId",
                        columns: x => new { x.TenantId, x.ImporterPartyId },
                        principalSchema: "core",
                        principalTable: "Parties",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PowersOfAttorney_TenantId_BrokerPartyId_ImporterPartyId",
                schema: "warehouse",
                table: "PowersOfAttorney",
                columns: new[] { "TenantId", "BrokerPartyId", "ImporterPartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_PowersOfAttorney_TenantId_ImporterPartyId",
                schema: "warehouse",
                table: "PowersOfAttorney",
                columns: new[] { "TenantId", "ImporterPartyId" });

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman). Şema ve GRANT ilk depo migration'ındadır.
            TenantIsolationSql.AddTable(migrationBuilder, "warehouse", "PowersOfAttorney");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "warehouse", "PowersOfAttorney");

            migrationBuilder.DropTable(
                name: "PowersOfAttorney",
                schema: "warehouse");
        }
    }
}
