using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CommunicationTopics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Topic",
                schema: "marketing",
                table: "Campaigns",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CommunicationTopicPreferences",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    TopicKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OptedOut = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ChangedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunicationTopicPreferences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommunicationTopics",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunicationTopics", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommunicationTopicPreferences_TenantId_Channel_TopicKey_Address",
                schema: "core",
                table: "CommunicationTopicPreferences",
                columns: new[] { "TenantId", "Channel", "TopicKey", "Address" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommunicationTopics_TenantId_Key",
                schema: "core",
                table: "CommunicationTopics",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            TenantIsolationSql.AddTable(migrationBuilder, "core", "CommunicationTopics");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "CommunicationTopicPreferences");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "CommunicationTopicPreferences");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "CommunicationTopics");

            migrationBuilder.DropTable(
                name: "CommunicationTopicPreferences",
                schema: "core");

            migrationBuilder.DropTable(
                name: "CommunicationTopics",
                schema: "core");

            migrationBuilder.DropColumn(
                name: "Topic",
                schema: "marketing",
                table: "Campaigns");
        }
    }
}
