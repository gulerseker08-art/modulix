using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LogisticsCarrierTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CarrierTerms",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CarrierPartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AgreementNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    PaymentTermDays = table.Column<int>(type: "int", nullable: true),
                    RateNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PerformanceNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarrierTerms", x => x.Id);
                    table.UniqueConstraint("AK_CarrierTerms_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_CarrierTerms_Carrier",
                schema: "logistics",
                table: "CarrierTerms",
                columns: new[] { "TenantId", "CarrierPartyId", "ValidFrom" });

            migrationBuilder.CreateIndex(
                name: "UX_CarrierTerms_AgreementNo",
                schema: "logistics",
                table: "CarrierTerms",
                columns: new[] { "TenantId", "CarrierPartyId", "AgreementNo" },
                unique: true,
                filter: "[AgreementNo] IS NOT NULL");

            // Yeni tenant tablosu Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "logistics", "CarrierTerms");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "logistics", "CarrierTerms");

            migrationBuilder.DropTable(
                name: "CarrierTerms",
                schema: "logistics");
        }
    }
}
