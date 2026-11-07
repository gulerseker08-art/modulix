using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContractSavedViewsToGridViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Eski sözleşme görünümleri ortak tabloya kopyalanır (ADR-0036). Migration kullanıcısının oturum tenant'ı yoktur; RLS açıkken
            // SELECT 0 satır döner ve INSERT engellenir, bu yüzden politika kopya süresince kapatılır. Kurallar: sahip kullanıcının o
            // tenant'taki üyeliği (yoksa satır atlanır), ekran contracts.contracts ve varsayılan sütunları, süzgeçteki sort kendi sütununa,
            // search atılır, ad 60 karaktere kısaltılır (çakışan ad bir kez alınır), üye başına en fazla 20 görünüm.
            migrationBuilder.Sql("ALTER SECURITY POLICY [core].[TenantIsolationPolicy] WITH (STATE = OFF);");
            migrationBuilder.Sql("""
                ;WITH v AS (
                    SELECT sv.TenantId, m.Id AS MembershipId, LEFT(LTRIM(RTRIM(sv.Name)), 60) AS Name, sv.Filters, sv.CreatedAt, sv.UpdatedAt,
                           ROW_NUMBER() OVER (PARTITION BY sv.TenantId, m.Id, LEFT(LTRIM(RTRIM(sv.Name)), 60) ORDER BY sv.CreatedAt) AS Dup,
                           ROW_NUMBER() OVER (PARTITION BY sv.TenantId, m.Id ORDER BY sv.CreatedAt) AS Rn
                    FROM [contracts].[ContractSavedViews] sv
                    JOIN [platform].[Memberships] m ON m.TenantId = sv.TenantId AND m.UserId = sv.OwnerUserId
                )
                INSERT INTO [core].[GridViews] ([Id], [TenantId], [MembershipId], [ScreenKey], [Name], [Columns], [Sort], [Filters], [Version], [CreatedAt], [UpdatedAt])
                SELECT NEWID(), v.TenantId, v.MembershipId, N'contracts.contracts', v.Name,
                       N'contract,customer,type,statuses,dates,critical,total,owner',
                       NULLIF(c.SortValue, N''), NULLIF(x.Rest, N''), 1, v.CreatedAt, v.UpdatedAt
                FROM v
                CROSS APPLY (SELECT N'&' + ISNULL(v.Filters, N'') + N'&' AS F) a
                CROSS APPLY (SELECT CHARINDEX(N'&sort=', a.F) AS P) b
                CROSS APPLY (SELECT CASE WHEN b.P > 0 THEN SUBSTRING(a.F, b.P + 6, CHARINDEX(N'&', a.F, b.P + 6) - b.P - 6) ELSE N'' END AS SortValue) c
                CROSS APPLY (SELECT CASE WHEN b.P > 0 THEN STUFF(a.F, b.P, CHARINDEX(N'&', a.F, b.P + 6) - b.P, N'') ELSE a.F END AS F2) d
                CROSS APPLY (SELECT CHARINDEX(N'&search=', d.F2) AS Q) e
                CROSS APPLY (SELECT CASE WHEN e.Q > 0 THEN STUFF(d.F2, e.Q, CHARINDEX(N'&', d.F2, e.Q + 8) - e.Q, N'') ELSE d.F2 END AS F3) g
                CROSS APPLY (SELECT CASE WHEN LEN(g.F3) <= 2 THEN N'' ELSE SUBSTRING(g.F3, 2, LEN(g.F3) - 2) END AS Rest) x
                WHERE v.Dup = 1 AND v.Rn <= 20 AND v.Name <> N''
                  AND NOT EXISTS (SELECT 1 FROM [core].[GridViews] gv
                                  WHERE gv.TenantId = v.TenantId AND gv.MembershipId = v.MembershipId AND gv.ScreenKey = N'contracts.contracts' AND gv.Name = v.Name);
                """);
            migrationBuilder.Sql("ALTER SECURITY POLICY [core].[TenantIsolationPolicy] WITH (STATE = ON);");

            // Tablo politikadan çıkarılmadan düşürülemez (SCHEMABINDING).
            TenantIsolationSql.RemoveTable(migrationBuilder, "contracts", "ContractSavedViews");
            migrationBuilder.DropTable(
                name: "ContractSavedViews",
                schema: "contracts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContractSavedViews",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Filters = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractSavedViews", x => x.Id);
                    table.UniqueConstraint("AK_ContractSavedViews_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractSavedViews_TenantId_OwnerUserId_Name",
                schema: "contracts",
                table: "ContractSavedViews",
                columns: new[] { "TenantId", "OwnerUserId", "Name" },
                unique: true);

            TenantIsolationSql.AddTable(migrationBuilder, "contracts", "ContractSavedViews");
        }
    }
}
