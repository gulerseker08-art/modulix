using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupportRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Tickets_Status",
                schema: "support",
                table: "Tickets");

            migrationBuilder.AddColumn<Guid>(
                name: "MergedIntoId",
                schema: "support",
                table: "Tickets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceTicketId",
                schema: "support",
                table: "Messages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequiredFields",
                schema: "support",
                table: "Categories",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PriorityMatrix",
                schema: "support",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Impact = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Urgency = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Priority = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
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
                    table.PrimaryKey("PK_PriorityMatrix", x => x.Id);
                    table.UniqueConstraint("AK_PriorityMatrix_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_PriorityMatrix_Impact", "[Impact] IN ('Low','Medium','High')");
                    table.CheckConstraint("CK_PriorityMatrix_Priority", "[Priority] IN ('Low','Normal','High','Critical')");
                    table.CheckConstraint("CK_PriorityMatrix_Urgency", "[Urgency] IN ('Low','Medium','High')");
                });

            migrationBuilder.CreateTable(
                name: "RoutingRules",
                schema: "support",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    TypeKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Priority = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QueueId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
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
                    table.PrimaryKey("PK_RoutingRules", x => x.Id);
                    table.UniqueConstraint("AK_RoutingRules_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_RoutingRules_Action", "[QueueId] IS NOT NULL OR [OwnerId] IS NOT NULL");
                    table.CheckConstraint("CK_RoutingRules_Channel", "[Channel] IS NULL OR [Channel] IN ('User','Phone','Email','Web','Portal','Api')");
                    table.CheckConstraint("CK_RoutingRules_Priority", "[Priority] IS NULL OR [Priority] IN ('Low','Normal','High','Critical')");
                    table.ForeignKey(
                        name: "FK_RoutingRules_Categories_TenantId_CategoryId",
                        columns: x => new { x.TenantId, x.CategoryId },
                        principalSchema: "support",
                        principalTable: "Categories",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoutingRules_Parties_TenantId_PartyId",
                        columns: x => new { x.TenantId, x.PartyId },
                        principalSchema: "core",
                        principalTable: "Parties",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoutingRules_Queues_TenantId_QueueId",
                        columns: x => new { x.TenantId, x.QueueId },
                        principalSchema: "support",
                        principalTable: "Queues",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketTasks",
                schema: "support",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AssigneeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsBlocking = table.Column<bool>(type: "bit", nullable: false),
                    IsDone = table.Column<bool>(type: "bit", nullable: false),
                    DoneAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DoneBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketTasks", x => x.Id);
                    table.UniqueConstraint("AK_TicketTasks_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_TicketTasks_Tickets_TenantId_TicketId",
                        columns: x => new { x.TenantId, x.TicketId },
                        principalSchema: "support",
                        principalTable: "Tickets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_MergedIntoId",
                schema: "support",
                table: "Tickets",
                columns: new[] { "TenantId", "MergedIntoId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tickets_Status",
                schema: "support",
                table: "Tickets",
                sql: "[Status] IN ('New','Triage','Assigned','InProgress','WaitingCustomer','WaitingInternal','Resolved','Closed','Reopened','Cancelled','Merged')");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TenantId_SourceTicketId",
                schema: "support",
                table: "Messages",
                columns: new[] { "TenantId", "SourceTicketId" });

            migrationBuilder.CreateIndex(
                name: "UX_PriorityMatrix_Cell",
                schema: "support",
                table: "PriorityMatrix",
                columns: new[] { "TenantId", "Impact", "Urgency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoutingRules_TenantId_CategoryId",
                schema: "support",
                table: "RoutingRules",
                columns: new[] { "TenantId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoutingRules_TenantId_IsActive_Order",
                schema: "support",
                table: "RoutingRules",
                columns: new[] { "TenantId", "IsActive", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_RoutingRules_TenantId_PartyId",
                schema: "support",
                table: "RoutingRules",
                columns: new[] { "TenantId", "PartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoutingRules_TenantId_QueueId",
                schema: "support",
                table: "RoutingRules",
                columns: new[] { "TenantId", "QueueId" });

            migrationBuilder.CreateIndex(
                name: "UX_RoutingRules_Name",
                schema: "support",
                table: "RoutingRules",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketTasks_TenantId_TicketId_IsDone",
                schema: "support",
                table: "TicketTasks",
                columns: new[] { "TenantId", "TicketId", "IsDone" });

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Tickets_TenantId_SourceTicketId",
                schema: "support",
                table: "Messages",
                columns: new[] { "TenantId", "SourceTicketId" },
                principalSchema: "support",
                principalTable: "Tickets",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Tickets_TenantId_MergedIntoId",
                schema: "support",
                table: "Tickets",
                columns: new[] { "TenantId", "MergedIntoId" },
                principalSchema: "support",
                principalTable: "Tickets",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, "support", table);
            }
        }

        private static readonly string[] TenantTables = ["PriorityMatrix", "RoutingRules", "TicketTasks"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, "support", table);
            }

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Tickets_TenantId_SourceTicketId",
                schema: "support",
                table: "Messages");

            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_Tickets_TenantId_MergedIntoId",
                schema: "support",
                table: "Tickets");

            migrationBuilder.DropTable(
                name: "PriorityMatrix",
                schema: "support");

            migrationBuilder.DropTable(
                name: "RoutingRules",
                schema: "support");

            migrationBuilder.DropTable(
                name: "TicketTasks",
                schema: "support");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_TenantId_MergedIntoId",
                schema: "support",
                table: "Tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tickets_Status",
                schema: "support",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Messages_TenantId_SourceTicketId",
                schema: "support",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MergedIntoId",
                schema: "support",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "SourceTicketId",
                schema: "support",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "RequiredFields",
                schema: "support",
                table: "Categories");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tickets_Status",
                schema: "support",
                table: "Tickets",
                sql: "[Status] IN ('New','Triage','Assigned','InProgress','WaitingCustomer','WaitingInternal','Resolved','Closed','Reopened','Cancelled')");
        }
    }
}
