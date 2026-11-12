using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CoreCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Credentials",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerEntityKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TypeKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DocumentNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IssuedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, defaultValue: "active"),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credentials", x => x.Id);
                    table.UniqueConstraint("AK_Credentials_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_Owner",
                schema: "core",
                table: "Credentials",
                columns: new[] { "TenantId", "OwnerEntityKey", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_ValidTo",
                schema: "core",
                table: "Credentials",
                columns: new[] { "TenantId", "ValidTo" });

            migrationBuilder.CreateIndex(
                name: "UX_Credentials_DocumentNo",
                schema: "core",
                table: "Credentials",
                columns: new[] { "TenantId", "OwnerEntityKey", "OwnerId", "TypeKey", "DocumentNo" },
                unique: true,
                filter: "[DocumentNo] IS NOT NULL");

            // Süreli belge tenant verisidir: Row-Level Security politikasına eklenir.
            TenantIsolationSql.AddTable(migrationBuilder, "core", "Credentials");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "Credentials");

            migrationBuilder.DropTable(
                name: "Credentials",
                schema: "core");
        }
    }
}
