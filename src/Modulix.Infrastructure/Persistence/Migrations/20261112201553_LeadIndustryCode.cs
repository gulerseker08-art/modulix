using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LeadIndustryCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IndustryCode",
                schema: "crm",
                table: "Leads",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Leads_IndustryCode",
                schema: "crm",
                table: "Leads",
                column: "IndustryCode");

            migrationBuilder.AddForeignKey(
                name: "FK_Leads_Industries_IndustryCode",
                schema: "crm",
                table: "Leads",
                column: "IndustryCode",
                principalSchema: "platform",
                principalTable: "Industries",
                principalColumn: "Code",
                onDelete: ReferentialAction.Restrict);

            // Geri doldurma (ADR-0043): Lead'in eski serbest metin sektörü, adına göre (büyük/küçük harf duyarsız, tam eşleşme) kataloğa
            // eşlenir; eşleşmeyen metin silinmez ([crm].[Leads].[Industry] kalır, ekranda salt okunur). Migration oturumunda tenant değeri
            // yoktur; RLS açıkken UPDATE sessizce 0 satır etkiler, bu yüzden politika geçici kapatılır. Yinelenebilir.
            migrationBuilder.Sql("ALTER SECURITY POLICY [core].[TenantIsolationPolicy] WITH (STATE = OFF);");

            migrationBuilder.Sql(@"
UPDATE l SET l.[IndustryCode] = i.[Code]
FROM [crm].[Leads] l
JOIN [platform].[Industries] i ON i.[Name] = LTRIM(RTRIM(l.[Industry]))
WHERE l.[IndustryCode] IS NULL AND l.[Industry] IS NOT NULL;");

            migrationBuilder.Sql(@"
UPDATE l SET l.[Industry] = NULL
FROM [crm].[Leads] l
JOIN [platform].[Industries] i ON i.[Code] = l.[IndustryCode]
WHERE l.[Industry] IS NOT NULL AND i.[Name] = LTRIM(RTRIM(l.[Industry]));");

            migrationBuilder.Sql("ALTER SECURITY POLICY [core].[TenantIsolationPolicy] WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Leads_Industries_IndustryCode",
                schema: "crm",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_Leads_IndustryCode",
                schema: "crm",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "IndustryCode",
                schema: "crm",
                table: "Leads");
        }
    }
}
