using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Modulix.Infrastructure.Persistence;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class LeadManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceLeadId",
                schema: "crm",
                table: "Opportunities",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeadId",
                schema: "crm",
                table: "Activities",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                schema: "crm",
                table: "Activities",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Leads",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeadNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Priority = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    NormalizedPhone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    JobTitle = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Website = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    WebsiteDomain = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Country = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductInterest = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NeedDescription = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    EstimatedBudget = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    TargetPurchaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Industry = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SourceDetail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Referrer = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ExternalSource = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExternalSubmissionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UtmSource = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmMedium = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmCampaign = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FirstReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FirstContactAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FollowUpAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    QualifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DisqualifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DisqualificationReason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ConvertedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConvertedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConvertedCompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConvertedContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConvertedOpportunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leads", x => x.Id);
                    table.UniqueConstraint("AK_Leads_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Leads_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leads_Companies_TenantId_ConvertedCompanyId",
                        columns: x => new { x.TenantId, x.ConvertedCompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leads_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leads_Contacts_TenantId_ConvertedContactId",
                        columns: x => new { x.TenantId, x.ConvertedContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leads_Opportunities_TenantId_ConvertedOpportunityId",
                        columns: x => new { x.TenantId, x.ConvertedOpportunityId },
                        principalSchema: "crm",
                        principalTable: "Opportunities",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_Opportunities_SourceLead",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "TenantId", "SourceLeadId" },
                unique: true,
                filter: "[SourceLeadId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_LeadId",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "LeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_CompanyId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_ContactId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_ConvertedCompanyId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "ConvertedCompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_ConvertedContactId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "ConvertedContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_ConvertedOpportunityId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "ConvertedOpportunityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_ExternalSource_ExternalSubmissionId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "ExternalSource", "ExternalSubmissionId" },
                unique: true,
                filter: "[ExternalSource] IS NOT NULL AND [ExternalSubmissionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_FirstReceivedAt",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "FirstReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_LeadNumber",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "LeadNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_NormalizedEmail",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "NormalizedEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_NormalizedPhone",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "NormalizedPhone" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_Status_OwnerId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "Status", "OwnerId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_Leads_TenantId_LeadId",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "LeadId" },
                principalSchema: "crm",
                principalTable: "Leads",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Opportunities_Leads_TenantId_SourceLeadId",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "TenantId", "SourceLeadId" },
                principalSchema: "crm",
                principalTable: "Leads",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // Her tenant tablosu satır düzeyinde izolasyon politikasına girer (ADR-0009).
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "Leads");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "Leads");

            migrationBuilder.DropForeignKey(
                name: "FK_Activities_Leads_TenantId_LeadId",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropForeignKey(
                name: "FK_Opportunities_Leads_TenantId_SourceLeadId",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropTable(
                name: "Leads",
                schema: "crm");

            migrationBuilder.DropIndex(
                name: "UX_Opportunities_SourceLead",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropIndex(
                name: "IX_Activities_TenantId_LeadId",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "SourceLeadId",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "LeadId",
                schema: "crm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "Outcome",
                schema: "crm",
                table: "Activities");
        }
    }
}
