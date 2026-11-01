using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupportChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Channel",
                schema: "support",
                table: "Messages",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalMessageId",
                schema: "support",
                table: "Messages",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InReplyTo",
                schema: "support",
                table: "Messages",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SenderAddress",
                schema: "support",
                table: "Messages",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Attachments",
                schema: "support",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ScanStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.UniqueConstraint("AK_Attachments_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Attachments_Size", "[SizeBytes] > 0 AND [SizeBytes] <= 5242880");
                    table.ForeignKey(
                        name: "FK_Attachments_Messages_TenantId_MessageId",
                        columns: x => new { x.TenantId, x.MessageId },
                        principalSchema: "support",
                        principalTable: "Messages",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Attachments_Tickets_TenantId_TicketId",
                        columns: x => new { x.TenantId, x.TicketId },
                        principalSchema: "support",
                        principalTable: "Tickets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Surveys",
                schema: "support",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Cycle = table.Column<int>(type: "int", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RecipientEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    QueuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Rating = table.Column<int>(type: "int", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Surveys", x => x.Id);
                    table.UniqueConstraint("AK_Surveys_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Surveys_Answer", "([AnsweredAt] IS NULL AND [Rating] IS NULL) OR ([AnsweredAt] IS NOT NULL AND [Rating] IS NOT NULL)");
                    table.CheckConstraint("CK_Surveys_Rating", "[Rating] IS NULL OR ([Rating] BETWEEN 1 AND 5)");
                    table.ForeignKey(
                        name: "FK_Surveys_Tickets_TenantId_TicketId",
                        columns: x => new { x.TenantId, x.TicketId },
                        principalSchema: "support",
                        principalTable: "Tickets",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_TenantId_SenderAddress_CreatedAt",
                schema: "support",
                table: "Messages",
                columns: new[] { "TenantId", "SenderAddress", "CreatedAt" },
                filter: "[SenderAddress] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Messages_External",
                schema: "support",
                table: "Messages",
                columns: new[] { "TenantId", "Channel", "ExternalMessageId" },
                unique: true,
                filter: "[ExternalMessageId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_TenantId_MessageId",
                schema: "support",
                table: "Attachments",
                columns: new[] { "TenantId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_TenantId_TicketId",
                schema: "support",
                table: "Attachments",
                columns: new[] { "TenantId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "UX_Attachments_Content",
                schema: "support",
                table: "Attachments",
                columns: new[] { "TenantId", "MessageId", "Sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Surveys_TenantId_AnsweredAt_Rating",
                schema: "support",
                table: "Surveys",
                columns: new[] { "TenantId", "AnsweredAt", "Rating" });

            migrationBuilder.CreateIndex(
                name: "UX_Surveys_Cycle",
                schema: "support",
                table: "Surveys",
                columns: new[] { "TenantId", "TicketId", "Cycle" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Surveys_Token",
                schema: "support",
                table: "Surveys",
                columns: new[] { "TenantId", "TokenHash" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, "support", table);
            }
        }

        private static readonly string[] TenantTables = ["Attachments", "Surveys"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, "support", table);
            }

            migrationBuilder.DropTable(
                name: "Attachments",
                schema: "support");

            migrationBuilder.DropTable(
                name: "Surveys",
                schema: "support");

            migrationBuilder.DropIndex(
                name: "IX_Messages_TenantId_SenderAddress_CreatedAt",
                schema: "support",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "UX_Messages_External",
                schema: "support",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Channel",
                schema: "support",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ExternalMessageId",
                schema: "support",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "InReplyTo",
                schema: "support",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "SenderAddress",
                schema: "support",
                table: "Messages");
        }
    }
}
