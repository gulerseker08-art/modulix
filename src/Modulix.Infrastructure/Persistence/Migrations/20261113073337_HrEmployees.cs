using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrEmployees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "hr");

            migrationBuilder.CreateTable(
                name: "Employees",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    JobTitle = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    DepartmentKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ManagerEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EmploymentType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    WorkLocation = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    HireDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TerminationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    NationalId = table.Column<string>(type: "char(11)", unicode: false, fixedLength: true, maxLength: 11, nullable: true),
                    BirthDate = table.Column<DateOnly>(type: "date", nullable: true),
                    HomeAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EmergencyContactName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    EmergencyContactPhone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Iban = table.Column<string>(type: "varchar(26)", unicode: false, maxLength: 26, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employees", x => x.Id);
                    table.UniqueConstraint("AK_Employees_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Employees_Employees_TenantId_ManagerEmployeeId",
                        columns: x => new { x.TenantId, x.ManagerEmployeeId },
                        principalSchema: "hr",
                        principalTable: "Employees",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_Manager",
                schema: "hr",
                table: "Employees",
                columns: new[] { "TenantId", "ManagerEmployeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_Status",
                schema: "hr",
                table: "Employees",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_Employees_NationalId",
                schema: "hr",
                table: "Employees",
                columns: new[] { "TenantId", "NationalId" },
                unique: true,
                filter: "[NationalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Employees_Number",
                schema: "hr",
                table: "Employees",
                columns: new[] { "TenantId", "EmployeeNumber" },
                unique: true,
                filter: "[EmployeeNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Employees_Person",
                schema: "hr",
                table: "Employees",
                columns: new[] { "TenantId", "PersonId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Employees_User",
                schema: "hr",
                table: "Employees",
                columns: new[] { "TenantId", "UserId" },
                unique: true,
                filter: "[UserId] IS NOT NULL");

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "hr", "Employees");

            // Uygulama kullanıcısı yeni şemada yalnızca veri işlemi yapar (init.sql ile aynı; mevcut veritabanları için burada da verilir).
            migrationBuilder.Sql(
                "IF DATABASE_PRINCIPAL_ID(N'modulix_app_role') IS NOT NULL GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[hr] TO [modulix_app_role];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "hr", "Employees");

            migrationBuilder.DropTable(
                name: "Employees",
                schema: "hr");
        }
    }
}
