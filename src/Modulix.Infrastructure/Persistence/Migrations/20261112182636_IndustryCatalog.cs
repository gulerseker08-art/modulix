using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IndustryCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IndustryCode",
                schema: "core",
                table: "Parties",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Industries",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Industries", x => x.Id);
                    table.UniqueConstraint("AK_Industries_Code", x => x.Code);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Parties_IndustryCode",
                schema: "core",
                table: "Parties",
                column: "IndustryCode");

            migrationBuilder.CreateIndex(
                name: "IX_Parties_TenantId_IndustryCode",
                schema: "core",
                table: "Parties",
                columns: new[] { "TenantId", "IndustryCode" });

            migrationBuilder.AddForeignKey(
                name: "FK_Parties_Industries_IndustryCode",
                schema: "core",
                table: "Parties",
                column: "IndustryCode",
                principalSchema: "platform",
                principalTable: "Industries",
                principalColumn: "Code",
                onDelete: ReferentialAction.Restrict);

            // Başlangıç sektörleri (ürün standardı, ADR-0043). Yoksa eklenir; sonradan platformdan değiştirilen ad ezilmez.
            migrationBuilder.Sql(@"
INSERT INTO [platform].[Industries] ([Id], [Code], [Name], [IsActive])
SELECT NEWID(), v.[Code], v.[Name], 1
FROM (VALUES
    (N'tarim-hayvancilik', N'Tarım ve hayvancılık'),
    (N'gida-icecek', N'Gıda ve içecek'),
    (N'tekstil-hazir-giyim', N'Tekstil ve hazır giyim'),
    (N'deri-ayakkabi', N'Deri ve ayakkabı'),
    (N'mobilya-orman-urunleri', N'Mobilya ve orman ürünleri'),
    (N'kagit-ambalaj-baski', N'Kâğıt, ambalaj ve baskı'),
    (N'kimya-plastik-kaucuk', N'Kimya, plastik ve kauçuk'),
    (N'ilac-saglik-urunleri', N'İlaç ve sağlık ürünleri'),
    (N'metal-makine', N'Metal ve makine'),
    (N'otomotiv', N'Otomotiv'),
    (N'elektrik-elektronik', N'Elektrik ve elektronik'),
    (N'enerji', N'Enerji'),
    (N'madencilik', N'Madencilik'),
    (N'insaat', N'İnşaat'),
    (N'gayrimenkul', N'Gayrimenkul'),
    (N'toptan-perakende-ticaret', N'Toptan ve perakende ticaret'),
    (N'e-ticaret', N'E-ticaret'),
    (N'lojistik-tasimacilik', N'Lojistik ve taşımacılık'),
    (N'denizcilik-liman', N'Denizcilik ve liman'),
    (N'havacilik', N'Havacılık'),
    (N'turizm-konaklama', N'Turizm ve konaklama'),
    (N'saglik-hizmetleri', N'Sağlık hizmetleri'),
    (N'egitim', N'Eğitim'),
    (N'bilisim-yazilim', N'Bilişim ve yazılım'),
    (N'telekomunikasyon', N'Telekomünikasyon'),
    (N'finans-sigorta', N'Finans ve sigorta'),
    (N'profesyonel-hizmetler', N'Danışmanlık ve profesyonel hizmetler'),
    (N'medya-reklam', N'Medya ve reklam'),
    (N'kamu-sivil-toplum', N'Kamu ve sivil toplum'),
    (N'diger', N'Diğer')
) AS v([Code], [Name])
WHERE NOT EXISTS (SELECT 1 FROM [platform].[Industries] i WHERE i.[Code] = v.[Code]);");

            // Geri doldurma: CRM firmasının eski serbest metin sektörü, adına göre (büyük/küçük harf duyarsız, tam eşleşme) kataloğa
            // eşlenir. Eşleşmeyen metin silinmez ([crm].[Companies].[Industry] kalır, ekranda salt okunur). Migration oturumunda tenant
            // değeri yoktur; RLS açıkken UPDATE sessizce 0 satır etkiler, bu yüzden politika geçici kapatılır. Yinelenebilir: kodu olan
            // firmaya dokunmaz.
            migrationBuilder.Sql("ALTER SECURITY POLICY [core].[TenantIsolationPolicy] WITH (STATE = OFF);");

            migrationBuilder.Sql(@"
UPDATE p SET p.[IndustryCode] = i.[Code]
FROM [core].[Parties] p
JOIN [crm].[Companies] c ON c.[TenantId] = p.[TenantId] AND c.[PartyId] = p.[Id]
JOIN [platform].[Industries] i ON i.[Name] = LTRIM(RTRIM(c.[Industry]))
WHERE p.[IndustryCode] IS NULL AND c.[Industry] IS NOT NULL;");

            migrationBuilder.Sql(@"
UPDATE c SET c.[Industry] = NULL
FROM [crm].[Companies] c
JOIN [core].[Parties] p ON p.[TenantId] = c.[TenantId] AND p.[Id] = c.[PartyId]
JOIN [platform].[Industries] i ON i.[Code] = p.[IndustryCode]
WHERE c.[Industry] IS NOT NULL AND i.[Name] = LTRIM(RTRIM(c.[Industry]));");

            migrationBuilder.Sql("ALTER SECURITY POLICY [core].[TenantIsolationPolicy] WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Parties_Industries_IndustryCode",
                schema: "core",
                table: "Parties");

            migrationBuilder.DropTable(
                name: "Industries",
                schema: "platform");

            migrationBuilder.DropIndex(
                name: "IX_Parties_IndustryCode",
                schema: "core",
                table: "Parties");

            migrationBuilder.DropIndex(
                name: "IX_Parties_TenantId_IndustryCode",
                schema: "core",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "IndustryCode",
                schema: "core",
                table: "Parties");
        }
    }
}
