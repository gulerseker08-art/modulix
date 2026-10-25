using System;
using System.Globalization;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChoiceLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Stage",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                schema: "crm",
                table: "Companies",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "Source",
                schema: "crm",
                table: "Companies",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Type",
                schema: "crm",
                table: "Activities",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.CreateTable(
                name: "ChoiceLists",
                schema: "meta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ListKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChoiceLists", x => x.Id);
                    table.UniqueConstraint("AK_ChoiceLists_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChoiceLists_TenantId_ListKey",
                schema: "meta",
                table: "ChoiceLists",
                columns: new[] { "TenantId", "ListKey" },
                unique: true);

            TenantIsolationSql.AddTable(migrationBuilder, "meta", "ChoiceLists");

            // Sayılar hazır değerlerin anahtarlarına çevrilir (eski enum adları; API zaten bu adları kullanıyordu).
            foreach (var (table, column, values) in Columns)
            {
                migrationBuilder.Sql(ChoiceKeySql.Map("crm", table, column, [.. values.Select((key, i) => ((i + 1).ToString(CultureInfo.InvariantCulture), key))]));
            }
        }

        private static readonly (string Table, string Column, string[] Values)[] Columns =
        [
            ("Opportunities", "Stage", ["Lead", "Qualified", "Proposal", "Negotiation", "Won", "Lost"]),
            ("Companies", "Status", ["Prospect", "Customer", "Inactive"]),
            ("Companies", "Source", ["Referral", "Website", "Event", "Advertising", "ColdCall", "Other"]),
            ("Activities", "Type", ["Call", "Meeting", "Email", "Task", "Note"]),
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Firmanın eklediği değerlerin sayı karşılığı yoktur: aşama ve durumda ilk değere, kaynakta "Diğer"e, tipte "Görev"e döner.
            foreach (var (table, column, values) in Columns)
            {
                var otherwise = column switch { "Source" => "6", "Type" => "4", _ => "1" };
                migrationBuilder.Sql(ChoiceKeySql.Map(
                    "crm", table, column, [.. values.Select((key, i) => (key, (i + 1).ToString(CultureInfo.InvariantCulture)))], otherwise));
            }

            TenantIsolationSql.RemoveTable(migrationBuilder, "meta", "ChoiceLists");

            migrationBuilder.DropTable(
                name: "ChoiceLists",
                schema: "meta");

            migrationBuilder.AlterColumn<int>(
                name: "Stage",
                schema: "crm",
                table: "Opportunities",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(40)",
                oldMaxLength: 40);

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                schema: "crm",
                table: "Companies",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(40)",
                oldMaxLength: 40);

            migrationBuilder.AlterColumn<int>(
                name: "Source",
                schema: "crm",
                table: "Companies",
                type: "int",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(40)",
                oldMaxLength: 40,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Type",
                schema: "crm",
                table: "Activities",
                type: "int",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(40)",
                oldMaxLength: 40);
        }
    }
}
