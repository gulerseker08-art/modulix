using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupportIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ContractId",
                schema: "support",
                table: "SlaPolicies",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TicketLinks",
                schema: "support",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketLinks", x => x.Id);
                    table.UniqueConstraint("AK_TicketLinks_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_TicketLinks_Kind", "[EntityKey] IN ('contracts.contract','orders.order','orders.delivery')");
                    table.ForeignKey(
                        name: "FK_TicketLinks_Tickets_TenantId_TicketId",
                        columns: x => new { x.TenantId, x.TicketId },
                        principalSchema: "support",
                        principalTable: "Tickets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketLinks_TenantId_EntityKey_RecordId",
                schema: "support",
                table: "TicketLinks",
                columns: new[] { "TenantId", "EntityKey", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "UX_TicketLinks_Record",
                schema: "support",
                table: "TicketLinks",
                columns: new[] { "TenantId", "TicketId", "EntityKey", "RecordId" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "support", "TicketLinks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "support", "TicketLinks");

            migrationBuilder.DropTable(
                name: "TicketLinks",
                schema: "support");

            migrationBuilder.DropColumn(
                name: "ContractId",
                schema: "support",
                table: "SlaPolicies");
        }
    }
}
