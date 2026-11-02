using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BusinessUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessUnits",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Depth = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessUnits", x => x.Id);
                    table.UniqueConstraint("AK_BusinessUnits_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_BusinessUnits_BusinessUnits_TenantId_ParentId",
                        columns: x => new { x.TenantId, x.ParentId },
                        principalSchema: "core",
                        principalTable: "BusinessUnits",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BusinessUnitMembers",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BusinessUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessUnitMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessUnitMembers_BusinessUnits_TenantId_BusinessUnitId",
                        columns: x => new { x.TenantId, x.BusinessUnitId },
                        principalSchema: "core",
                        principalTable: "BusinessUnits",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BusinessUnitMembers_Memberships_TenantId_MembershipId",
                        columns: x => new { x.TenantId, x.MembershipId },
                        principalSchema: "platform",
                        principalTable: "Memberships",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessUnitMembers_TenantId_BusinessUnitId",
                schema: "core",
                table: "BusinessUnitMembers",
                columns: new[] { "TenantId", "BusinessUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessUnitMembers_TenantId_MembershipId",
                schema: "core",
                table: "BusinessUnitMembers",
                columns: new[] { "TenantId", "MembershipId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessUnits_TenantId_ParentId_Name",
                schema: "core",
                table: "BusinessUnits",
                columns: new[] { "TenantId", "ParentId", "Name" },
                unique: true,
                filter: "[ParentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessUnits_TenantId_Path",
                schema: "core",
                table: "BusinessUnits",
                columns: new[] { "TenantId", "Path" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessUnits_TenantId_Root",
                schema: "core",
                table: "BusinessUnits",
                column: "TenantId",
                unique: true,
                filter: "[ParentId] IS NULL");

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "core", "BusinessUnits");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "BusinessUnitMembers");

            // Geri doldurma (ADR-0026 K4): her tenant'a kök departman (merkez), her üyeliğe kök departman. Migration oturumunda
            // tenant değeri yoktur; RLS açıkken INSERT/UPDATE sessizce 0 satır etkiler, bu yüzden politika geçici kapatılır.
            // Yinelenebilir: kökü olan tenant'a ve departmanı olan üyeliğe dokunmaz.
            migrationBuilder.Sql("ALTER SECURITY POLICY [core].[TenantIsolationPolicy] WITH (STATE = OFF);");

            migrationBuilder.Sql("""
                INSERT INTO [core].[BusinessUnits] ([Id], [TenantId], [ParentId], [Name], [Code], [Kind], [Path], [Depth], [IsActive], [CreatedAt])
                SELECT NEWID(), t.[Id], NULL, LEFT(t.[Name], 200), NULL, N'Center', N'/', 0, 1, SYSDATETIMEOFFSET()
                FROM [platform].[Tenants] t
                WHERE NOT EXISTS (SELECT 1 FROM [core].[BusinessUnits] u WHERE u.[TenantId] = t.[Id] AND u.[ParentId] IS NULL);
                """);

            // Kimlik yolu uygulamadaki biçimle aynıdır: "/" + küçük harf, tiresiz kimlik + "/".
            migrationBuilder.Sql("""
                UPDATE [core].[BusinessUnits]
                SET [Path] = N'/' + LOWER(REPLACE(CAST([Id] AS nvarchar(36)), N'-', N'')) + N'/'
                WHERE [ParentId] IS NULL AND [Path] = N'/';
                """);

            migrationBuilder.Sql("""
                INSERT INTO [core].[BusinessUnitMembers] ([Id], [TenantId], [BusinessUnitId], [MembershipId])
                SELECT NEWID(), m.[TenantId], u.[Id], m.[Id]
                FROM [platform].[Memberships] m
                JOIN [core].[BusinessUnits] u ON u.[TenantId] = m.[TenantId] AND u.[ParentId] IS NULL
                WHERE NOT EXISTS (SELECT 1 FROM [core].[BusinessUnitMembers] bm WHERE bm.[MembershipId] = m.[Id]);
                """);

            migrationBuilder.Sql("ALTER SECURITY POLICY [core].[TenantIsolationPolicy] WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "BusinessUnitMembers");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "BusinessUnits");

            migrationBuilder.DropTable(
                name: "BusinessUnitMembers",
                schema: "core");

            migrationBuilder.DropTable(
                name: "BusinessUnits",
                schema: "core");
        }
    }
}
