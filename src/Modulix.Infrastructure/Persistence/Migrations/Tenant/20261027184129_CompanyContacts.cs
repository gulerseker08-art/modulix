using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Modulix.Infrastructure.Persistence;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class CompanyContacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyContacts",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Department = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    JobTitle = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Roles = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    IsDecisionMaker = table.Column<bool>(type: "bit", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyContacts", x => x.Id);
                    table.UniqueConstraint("AK_CompanyContacts_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_CompanyContacts_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyContacts_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyContacts_TenantId_ContactId",
                schema: "crm",
                table: "CompanyContacts",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "UX_CompanyContacts_ActivePair",
                schema: "crm",
                table: "CompanyContacts",
                columns: new[] { "TenantId", "CompanyId", "ContactId" },
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_CompanyContacts_PrimaryPerCompany",
                schema: "crm",
                table: "CompanyContacts",
                columns: new[] { "TenantId", "CompanyId" },
                unique: true,
                filter: "[IsPrimary] = 1 AND [IsActive] = 1");

            TenantIsolationSql.AddTable(migrationBuilder, "crm", "CompanyContacts");

            // Backfill (ADR-0011): her Contact.CompanyId bir aktif ilişkiye dönüşür. İlişki kimliği kişinin kimliğidir (eski
            // kimlikler korunur); başlangıç tarihi kişinin oluşturulma günü, unvan ortak kişideki unvandır. Contact.CompanyId
            // olduğu gibi kalır (birincil firma aynasıdır). RLS politikası oturumda tenant değeri ister; migration tüm
            // satırlara dokunduğu için politika yalnızca bu deyimler boyunca kapatılır.
            migrationBuilder.Sql($"ALTER SECURITY POLICY {TenantIsolationSql.PolicyName} WITH (STATE = OFF);");
            migrationBuilder.Sql(@"
                INSERT INTO [crm].[CompanyContacts]
                    ([Id], [TenantId], [CreatedAt], [UpdatedAt], [Version], [CustomFields], [CompanyId], [ContactId],
                     [Department], [JobTitle], [Roles], [IsPrimary], [IsDecisionMaker], [StartDate], [EndDate], [IsActive])
                SELECT c.[Id], c.[TenantId], c.[CreatedAt], SYSDATETIMEOFFSET(), 1, NULL, c.[CompanyId], c.[Id],
                       NULL, p.[Title], NULL, 0, 0, CAST(c.[CreatedAt] AS date), NULL, 1
                FROM [crm].[Contacts] c
                LEFT JOIN [core].[People] p ON p.[TenantId] = c.[TenantId] AND p.[Id] = c.[PersonId]
                WHERE c.[CompanyId] IS NOT NULL;");

            // Veri kaybı kontrolü: firması olan her kişinin aktif ilişkisi olmalı; değilse migration geri alınır.
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM [crm].[Contacts] c
                           WHERE c.[CompanyId] IS NOT NULL
                             AND NOT EXISTS (SELECT 1 FROM [crm].[CompanyContacts] l
                                             WHERE l.[ContactId] = c.[Id] AND l.[CompanyId] = c.[CompanyId] AND l.[IsActive] = 1))
                    THROW 51000, N'CompanyContacts backfill eksik: firması olan bir kişi için ilişki oluşmadı.', 1;");
            migrationBuilder.Sql($"ALTER SECURITY POLICY {TenantIsolationSql.PolicyName} WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "CompanyContacts");

            migrationBuilder.DropTable(
                name: "CompanyContacts",
                schema: "crm");
        }
    }
}
