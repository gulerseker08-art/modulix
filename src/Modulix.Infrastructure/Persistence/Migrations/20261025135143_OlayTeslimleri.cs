using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OlayTeslimleri : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventDeliveries",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subscription = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: true),
                    Ordered = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventDeliveries_OutboxMessages_EventId",
                        column: x => x.EventId,
                        principalSchema: "platform",
                        principalTable: "OutboxMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventDeliveries_EventId",
                schema: "platform",
                table: "EventDeliveries",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_EventDeliveries_Status_CompletedAt",
                schema: "platform",
                table: "EventDeliveries",
                columns: new[] { "Status", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EventDeliveries_Status_NextAttemptAt",
                schema: "platform",
                table: "EventDeliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EventDeliveries_TenantId_Subscription_EntityKey_RecordId_Sequence",
                schema: "platform",
                table: "EventDeliveries",
                columns: new[] { "TenantId", "Subscription", "EntityKey", "RecordId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_EventDeliveries_TenantId_Subscription_EventId",
                schema: "platform",
                table: "EventDeliveries",
                columns: new[] { "TenantId", "Subscription", "EventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventDeliveries",
                schema: "platform");
        }
    }
}
