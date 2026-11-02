using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketingUnsubscribe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommunicationLinkTokens",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunicationLinkTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommunicationLinkTokens_TenantId_TokenHash",
                schema: "core",
                table: "CommunicationLinkTokens",
                columns: new[] { "TenantId", "TokenHash" },
                unique: true);

            // Yeni tenant tablosu Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "core", "CommunicationLinkTokens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "CommunicationLinkTokens");

            migrationBuilder.DropTable(
                name: "CommunicationLinkTokens",
                schema: "core");
        }
    }
}
