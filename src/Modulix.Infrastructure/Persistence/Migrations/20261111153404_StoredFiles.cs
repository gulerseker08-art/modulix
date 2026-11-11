using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoredFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoredFiles",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Classification = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ScanStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredFiles", x => x.Id);
                    table.UniqueConstraint("AK_StoredFiles_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_TenantId_EntityKey_RecordId",
                schema: "core",
                table: "StoredFiles",
                columns: new[] { "TenantId", "EntityKey", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_TenantId_StorageKey",
                schema: "core",
                table: "StoredFiles",
                columns: new[] { "TenantId", "StorageKey" },
                unique: true);

            TenantIsolationSql.AddTable(migrationBuilder, "core", "StoredFiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "core", "StoredFiles");

            migrationBuilder.DropTable(
                name: "StoredFiles",
                schema: "core");
        }
    }
}
