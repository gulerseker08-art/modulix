using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContractCommitments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContractCommitmentFiles",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurrenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    Sha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
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
                    table.PrimaryKey("PK_ContractCommitmentFiles", x => x.Id);
                    table.UniqueConstraint("AK_ContractCommitmentFiles_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ContractCommitments",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Obligor = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResponsibleUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EscalationUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Recurrence = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    RecurrenceEndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    GraceDays = table.Column<int>(type: "int", nullable: false),
                    EscalateAfterDays = table.Column<int>(type: "int", nullable: false),
                    MetricName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Direction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    TargetValue = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Tolerance = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    DependsOnCommitmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConsequenceNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractCommitments", x => x.Id);
                    table.UniqueConstraint("AK_ContractCommitments_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_ContractCommitments_Grace", "[GraceDays] BETWEEN 0 AND 365 AND [EscalateAfterDays] BETWEEN 0 AND 365");
                    table.ForeignKey(
                        name: "FK_ContractCommitments_ContractCommitments_TenantId_DependsOnCommitmentId",
                        columns: x => new { x.TenantId, x.DependsOnCommitmentId },
                        principalSchema: "contracts",
                        principalTable: "ContractCommitments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContractCommitments_Contracts_TenantId_ContractId",
                        columns: x => new { x.TenantId, x.ContractId },
                        principalSchema: "contracts",
                        principalTable: "Contracts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContractCommitmentOccurrences",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommitmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CompletedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CompletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActualValue = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    MetTarget = table.Column<bool>(type: "bit", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EvidenceFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BreachedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BreachReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FollowUpStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FollowUpActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EscalatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EscalationActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WaivedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WaiveReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractCommitmentOccurrences", x => x.Id);
                    table.UniqueConstraint("AK_ContractCommitmentOccurrences_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ContractCommitmentOccurrences_ContractCommitments_TenantId_CommitmentId",
                        columns: x => new { x.TenantId, x.CommitmentId },
                        principalSchema: "contracts",
                        principalTable: "ContractCommitments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractCommitmentFiles_TenantId_OccurrenceId",
                schema: "contracts",
                table: "ContractCommitmentFiles",
                columns: new[] { "TenantId", "OccurrenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractCommitmentOccurrences_TenantId_ContractId_Status_DueDate",
                schema: "contracts",
                table: "ContractCommitmentOccurrences",
                columns: new[] { "TenantId", "ContractId", "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractCommitmentOccurrences_TenantId_Status_DueDate",
                schema: "contracts",
                table: "ContractCommitmentOccurrences",
                columns: new[] { "TenantId", "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "UX_ContractCommitmentOccurrences_Sequence",
                schema: "contracts",
                table: "ContractCommitmentOccurrences",
                columns: new[] { "TenantId", "CommitmentId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractCommitments_TenantId_ContractId_Status",
                schema: "contracts",
                table: "ContractCommitments",
                columns: new[] { "TenantId", "ContractId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractCommitments_TenantId_DependsOnCommitmentId",
                schema: "contracts",
                table: "ContractCommitments",
                columns: new[] { "TenantId", "DependsOnCommitmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractCommitments_TenantId_ResponsibleUserId_Status",
                schema: "contracts",
                table: "ContractCommitments",
                columns: new[] { "TenantId", "ResponsibleUserId", "Status" });

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020).
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, "contracts", table);
            }
        }

        private static readonly string[] TenantTables = ["ContractCommitments", "ContractCommitmentOccurrences", "ContractCommitmentFiles"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, "contracts", table);
            }

            migrationBuilder.DropTable(
                name: "ContractCommitmentFiles",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "ContractCommitmentOccurrences",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "ContractCommitments",
                schema: "contracts");
        }
    }
}
