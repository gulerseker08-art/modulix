using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CommunicationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommunicationPreferences",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EvidenceRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChangedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunicationPreferences", x => x.Id);
                    table.UniqueConstraint("AK_CommunicationPreferences_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "CommunicationSuppressions",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LiftedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LiftedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LiftReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunicationSuppressions", x => x.Id);
                    table.UniqueConstraint("AK_CommunicationSuppressions_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ConsentEvents",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ToStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EvidenceRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ChangedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsentEvents_CommunicationPreferences_TenantId_PreferenceId",
                        columns: x => new { x.TenantId, x.PreferenceId },
                        principalSchema: "core",
                        principalTable: "CommunicationPreferences",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommunicationPreferences_TenantId_Channel_Address_Purpose",
                schema: "core",
                table: "CommunicationPreferences",
                columns: new[] { "TenantId", "Channel", "Address", "Purpose" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommunicationSuppressions_TenantId_Channel_Address",
                schema: "core",
                table: "CommunicationSuppressions",
                columns: new[] { "TenantId", "Channel", "Address" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsentEvents_TenantId_PreferenceId_ChangedAt",
                schema: "core",
                table: "ConsentEvents",
                columns: new[] { "TenantId", "PreferenceId", "ChangedAt" });

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "core", "CommunicationPreferences");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "ConsentEvents");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "CommunicationSuppressions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "CommunicationSuppressions");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "ConsentEvents");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "CommunicationPreferences");

            migrationBuilder.DropTable(
                name: "CommunicationSuppressions",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ConsentEvents",
                schema: "core");

            migrationBuilder.DropTable(
                name: "CommunicationPreferences",
                schema: "core");
        }
    }
}
