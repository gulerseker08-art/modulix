using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Parties",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TaxOffice = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TaxNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    Website = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Parties", x => x.Id);
                    table.UniqueConstraint("AK_Parties_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "People",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.Id);
                    table.UniqueConstraint("AK_People_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_People_Parties_TenantId_PartyId",
                        columns: x => new { x.TenantId, x.PartyId },
                        principalSchema: "core",
                        principalTable: "Parties",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddColumn<Guid>(
                name: "PartyId",
                schema: "crm",
                table: "Companies",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PersonId",
                schema: "crm",
                table: "Contacts",
                type: "uniqueidentifier",
                nullable: true);

            // Firma ve kişi verisi ortak kayda taşınır (kimlikler aynı kalır); taşınan kolonlar sonra kaldırılır.
            migrationBuilder.Sql(SharedRecordSql.MoveCrmToShared);

            migrationBuilder.AlterColumn<Guid>(
                name: "PartyId",
                schema: "crm",
                table: "Companies",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PersonId",
                schema: "crm",
                table: "Contacts",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.DropIndex(
                name: "IX_Contacts_TenantId_LastName_FirstName",
                schema: "crm",
                table: "Contacts");

            migrationBuilder.DropIndex(
                name: "IX_Companies_TenantId_Name",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropIndex(
                name: "IX_Companies_TenantId_TaxNumber",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Email",
                schema: "crm",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "FirstName",
                schema: "crm",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "LastName",
                schema: "crm",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "Phone",
                schema: "crm",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "Title",
                schema: "crm",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "Address",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "City",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Email",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Name",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Phone",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "TaxNumber",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Website",
                schema: "crm",
                table: "Companies");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_PersonId",
                schema: "crm",
                table: "Contacts",
                columns: new[] { "TenantId", "PersonId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId_PartyId",
                schema: "crm",
                table: "Companies",
                columns: new[] { "TenantId", "PartyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Parties_TenantId_Name",
                schema: "core",
                table: "Parties",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Parties_TenantId_TaxNumber",
                schema: "core",
                table: "Parties",
                columns: new[] { "TenantId", "TaxNumber" },
                unique: true,
                filter: "[TaxNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_People_TenantId_Email",
                schema: "core",
                table: "People",
                columns: new[] { "TenantId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_People_TenantId_LastName_FirstName",
                schema: "core",
                table: "People",
                columns: new[] { "TenantId", "LastName", "FirstName" });

            migrationBuilder.CreateIndex(
                name: "IX_People_TenantId_PartyId",
                schema: "core",
                table: "People",
                columns: new[] { "TenantId", "PartyId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Companies_Parties_TenantId_PartyId",
                schema: "crm",
                table: "Companies",
                columns: new[] { "TenantId", "PartyId" },
                principalSchema: "core",
                principalTable: "Parties",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Contacts_People_TenantId_PersonId",
                schema: "crm",
                table: "Contacts",
                columns: new[] { "TenantId", "PersonId" },
                principalSchema: "core",
                principalTable: "People",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            TenantIsolationSql.AddTable(migrationBuilder, "core", "Parties");
            TenantIsolationSql.AddTable(migrationBuilder, "core", "People");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Companies_Parties_TenantId_PartyId",
                schema: "crm",
                table: "Companies");

            migrationBuilder.DropForeignKey(
                name: "FK_Contacts_People_TenantId_PersonId",
                schema: "crm",
                table: "Contacts");

            migrationBuilder.DropIndex(
                name: "IX_Contacts_TenantId_PersonId",
                schema: "crm",
                table: "Contacts");

            migrationBuilder.DropIndex(
                name: "IX_Companies_TenantId_PartyId",
                schema: "crm",
                table: "Companies");

            migrationBuilder.AddColumn<string>(
                name: "Email",
                schema: "crm",
                table: "Contacts",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                schema: "crm",
                table: "Contacts",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LastName",
                schema: "crm",
                table: "Contacts",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                schema: "crm",
                table: "Contacts",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                schema: "crm",
                table: "Contacts",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Address",
                schema: "crm",
                table: "Companies",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                schema: "crm",
                table: "Companies",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                schema: "crm",
                table: "Companies",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                schema: "crm",
                table: "Companies",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                schema: "crm",
                table: "Companies",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxNumber",
                schema: "crm",
                table: "Companies",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Website",
                schema: "crm",
                table: "Companies",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.Sql(SharedRecordSql.MoveSharedToCrm);

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_LastName_FirstName",
                schema: "crm",
                table: "Contacts",
                columns: new[] { "TenantId", "LastName", "FirstName" });

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId_Name",
                schema: "crm",
                table: "Companies",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId_TaxNumber",
                schema: "crm",
                table: "Companies",
                columns: new[] { "TenantId", "TaxNumber" },
                unique: true,
                filter: "[TaxNumber] IS NOT NULL");

            migrationBuilder.DropColumn(
                name: "PersonId",
                schema: "crm",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "PartyId",
                schema: "crm",
                table: "Companies");

            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "People");
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "Parties");

            migrationBuilder.DropTable(
                name: "People",
                schema: "core");

            migrationBuilder.DropTable(
                name: "Parties",
                schema: "core");
        }
    }
}
