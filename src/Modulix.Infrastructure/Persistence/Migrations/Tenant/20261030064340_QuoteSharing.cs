using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class QuoteSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QuoteDocuments",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionNo = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    Sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    TemplateVersion = table.Column<int>(type: "int", nullable: false),
                    Snapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
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
                    table.PrimaryKey("PK_QuoteDocuments", x => x.Id);
                    table.UniqueConstraint("AK_QuoteDocuments_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_QuoteDocuments_Quotes_TenantId_QuoteId",
                        columns: x => new { x.TenantId, x.QuoteId },
                        principalSchema: "crm",
                        principalTable: "Quotes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteShareLinks",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    TokenHint = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    RecipientName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RecipientEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevokeReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FirstViewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteShareLinks", x => x.Id);
                    table.UniqueConstraint("AK_QuoteShareLinks_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_QuoteShareLinks_QuoteDocuments_TenantId_DocumentId",
                        columns: x => new { x.TenantId, x.DocumentId },
                        principalSchema: "crm",
                        principalTable: "QuoteDocuments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QuoteShareLinks_Quotes_TenantId_QuoteId",
                        columns: x => new { x.TenantId, x.QuoteId },
                        principalSchema: "crm",
                        principalTable: "Quotes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteDecisions",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShareLinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SignerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SignerEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    SignerTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TermsStatement = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ClientAddressMasked = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteDecisions", x => x.Id);
                    table.UniqueConstraint("AK_QuoteDecisions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_QuoteDecisions_Decision", "[Decision] IN ('Accepted','Rejected')");
                    table.ForeignKey(
                        name: "FK_QuoteDecisions_QuoteDocuments_TenantId_DocumentId",
                        columns: x => new { x.TenantId, x.DocumentId },
                        principalSchema: "crm",
                        principalTable: "QuoteDocuments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QuoteDecisions_QuoteShareLinks_TenantId_ShareLinkId",
                        columns: x => new { x.TenantId, x.ShareLinkId },
                        principalSchema: "crm",
                        principalTable: "QuoteShareLinks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QuoteDecisions_Quotes_TenantId_QuoteId",
                        columns: x => new { x.TenantId, x.QuoteId },
                        principalSchema: "crm",
                        principalTable: "Quotes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteDecisions_TenantId_DocumentId",
                schema: "crm",
                table: "QuoteDecisions",
                columns: new[] { "TenantId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteDecisions_TenantId_QuoteId_CreatedAt",
                schema: "crm",
                table: "QuoteDecisions",
                columns: new[] { "TenantId", "QuoteId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteDecisions_TenantId_ShareLinkId",
                schema: "crm",
                table: "QuoteDecisions",
                columns: new[] { "TenantId", "ShareLinkId" });

            migrationBuilder.CreateIndex(
                name: "UX_QuoteDocuments_Quote",
                schema: "crm",
                table: "QuoteDocuments",
                columns: new[] { "TenantId", "QuoteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuoteShareLinks_TenantId_DocumentId",
                schema: "crm",
                table: "QuoteShareLinks",
                columns: new[] { "TenantId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteShareLinks_TenantId_QuoteId_CreatedAt",
                schema: "crm",
                table: "QuoteShareLinks",
                columns: new[] { "TenantId", "QuoteId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_QuoteShareLinks_Token",
                schema: "crm",
                table: "QuoteShareLinks",
                columns: new[] { "TenantId", "TokenHash" },
                unique: true);

            // Her tenant tablosu satır düzeyinde izolasyon politikasına girer (ADR-0009).
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "QuoteDocuments");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "QuoteShareLinks");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "QuoteDecisions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "QuoteDecisions");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "QuoteShareLinks");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "QuoteDocuments");

            migrationBuilder.DropTable(
                name: "QuoteDecisions",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "QuoteShareLinks",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "QuoteDocuments",
                schema: "crm");
        }
    }
}
