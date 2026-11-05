using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RelationshipRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerSectors",
                schema: "core",
                table: "Parties",
                type: "nvarchar(1020)",
                maxLength: 1020,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RelationshipRoles",
                schema: "core",
                table: "Parties",
                type: "nvarchar(1020)",
                maxLength: 1020,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceNeeds",
                schema: "core",
                table: "Parties",
                type: "nvarchar(1020)",
                maxLength: 1020,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RelationshipRoles",
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
                    table.PrimaryKey("PK_RelationshipRoles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RelationshipRoles_Code",
                schema: "platform",
                table: "RelationshipRoles",
                column: "Code",
                unique: true);

            // Başlangıç rolleri (ürün standardı, ADR-0029 K9). Yoksa eklenir; sonradan platformdan değiştirilen ad ezilmez. Antrepo/gümrük
            // rolleri ADR-0032 K2 gereği vardır; "doğrulanmamış" olan yalnızca lojistik katalogudur, bu rol adları genel ticari terimlerdir.
            migrationBuilder.Sql(@"
INSERT INTO [platform].[RelationshipRoles] ([Id], [Code], [Name], [IsActive])
SELECT NEWID(), v.[Code], v.[Name], 1
FROM (VALUES
    (N'musteri', N'Müşteri'),
    (N'tedarikci', N'Tedarikçi'),
    (N'tasiyici', N'Taşıyıcı'),
    (N'acente', N'Acente'),
    (N'alt-yuklenici', N'Alt yüklenici'),
    (N'ithalatci-malik', N'İthalatçı / malik'),
    (N'gumruk-musaviri', N'Gümrük müşaviri')
) AS v([Code], [Name])
WHERE NOT EXISTS (SELECT 1 FROM [platform].[RelationshipRoles] r WHERE r.[Code] = v.[Code]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RelationshipRoles",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "CustomerSectors",
                schema: "core",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "RelationshipRoles",
                schema: "core",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "ServiceNeeds",
                schema: "core",
                table: "Parties");
        }
    }
}
