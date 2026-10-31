using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContractSignatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContractSignatureFiles",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SignatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_ContractSignatureFiles", x => x.Id);
                    table.UniqueConstraint("AK_ContractSignatureFiles_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ContractSignatureRequests",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ProviderReference = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    Mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CloseReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LastEventSequence = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractSignatureRequests", x => x.Id);
                    table.UniqueConstraint("AK_ContractSignatureRequests_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ContractSignatureRequests_Contracts_TenantId_ContractId",
                        columns: x => new { x.TenantId, x.ContractId },
                        principalSchema: "contracts",
                        principalTable: "Contracts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContractSignatories",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    Capacity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Assurance = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    VerifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EvidenceFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeclineReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReminderCount = table.Column<int>(type: "int", nullable: false),
                    LastReminderAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractSignatories", x => x.Id);
                    table.UniqueConstraint("AK_ContractSignatories_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ContractSignatories_ContractSignatureRequests_TenantId_RequestId",
                        columns: x => new { x.TenantId, x.RequestId },
                        principalSchema: "contracts",
                        principalTable: "ContractSignatureRequests",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContractSignatureEvents",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SignatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ProviderEventId = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    Sequence = table.Column<int>(type: "int", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractSignatureEvents", x => x.Id);
                    table.UniqueConstraint("AK_ContractSignatureEvents_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ContractSignatureEvents_ContractSignatureRequests_TenantId_RequestId",
                        columns: x => new { x.TenantId, x.RequestId },
                        principalSchema: "contracts",
                        principalTable: "ContractSignatureRequests",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractSignatories_TenantId_ContractId",
                schema: "contracts",
                table: "ContractSignatories",
                columns: new[] { "TenantId", "ContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractSignatories_TenantId_UserId_Status",
                schema: "contracts",
                table: "ContractSignatories",
                columns: new[] { "TenantId", "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_ContractSignatories_RequestPosition",
                schema: "contracts",
                table: "ContractSignatories",
                columns: new[] { "TenantId", "RequestId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractSignatureEvents_TenantId_RequestId_CreatedAt",
                schema: "contracts",
                table: "ContractSignatureEvents",
                columns: new[] { "TenantId", "RequestId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_ContractSignatureEvents_ProviderEvent",
                schema: "contracts",
                table: "ContractSignatureEvents",
                columns: new[] { "TenantId", "RequestId", "ProviderEventId" },
                unique: true,
                filter: "[ProviderEventId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ContractSignatureFiles_TenantId_ContractId",
                schema: "contracts",
                table: "ContractSignatureFiles",
                columns: new[] { "TenantId", "ContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractSignatureFiles_TenantId_SignatoryId",
                schema: "contracts",
                table: "ContractSignatureFiles",
                columns: new[] { "TenantId", "SignatoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractSignatureRequests_TenantId_ContractId_VersionNo_CreatedAt",
                schema: "contracts",
                table: "ContractSignatureRequests",
                columns: new[] { "TenantId", "ContractId", "VersionNo", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractSignatureRequests_TenantId_Status_ExpiresAt",
                schema: "contracts",
                table: "ContractSignatureRequests",
                columns: new[] { "TenantId", "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "UX_ContractSignatureRequests_OpenPerVersion",
                schema: "contracts",
                table: "ContractSignatureRequests",
                columns: new[] { "TenantId", "ContractId", "VersionNo" },
                unique: true,
                filter: "[Status] IN (N'Pending', N'PartiallySigned')");

            migrationBuilder.CreateIndex(
                name: "UX_ContractSignatureRequests_ProviderReference",
                schema: "contracts",
                table: "ContractSignatureRequests",
                columns: new[] { "TenantId", "ProviderReference" },
                unique: true,
                filter: "[ProviderReference] IS NOT NULL");

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020).
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, "contracts", table);
            }
        }

        private static readonly string[] TenantTables = ["ContractSignatureRequests", "ContractSignatories", "ContractSignatureEvents", "ContractSignatureFiles"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, "contracts", table);
            }

            migrationBuilder.DropTable(
                name: "ContractSignatories",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "ContractSignatureEvents",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "ContractSignatureFiles",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "ContractSignatureRequests",
                schema: "contracts");
        }
    }
}
