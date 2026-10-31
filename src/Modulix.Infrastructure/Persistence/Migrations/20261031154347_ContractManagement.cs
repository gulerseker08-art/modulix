using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContractManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "contracts");

            migrationBuilder.CreateTable(
                name: "Contracts",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PreviousContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsIndefinite = table.Column<bool>(type: "bit", nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    PriceIncludesTax = table.Column<bool>(type: "bit", nullable: false),
                    ContractValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    PaymentTerms = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RenewalMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RenewalPeriodMonths = table.Column<int>(type: "int", nullable: true),
                    NoticePeriodDays = table.Column<int>(type: "int", nullable: true),
                    NoticeDeadline = table.Column<DateOnly>(type: "date", nullable: true),
                    Confidentiality = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PreparationStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ApprovalStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SignatureStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EffectiveStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CurrentVersionNo = table.Column<int>(type: "int", nullable: false),
                    EffectiveVersionNo = table.Column<int>(type: "int", nullable: true),
                    NetTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    GrossTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RecurringSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CalculatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contracts", x => x.Id);
                    table.UniqueConstraint("AK_Contracts_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Contracts_Dates", "[EndDate] IS NULL OR [StartDate] IS NULL OR [EndDate] >= [StartDate]");
                    table.CheckConstraint("CK_Contracts_Version", "[CurrentVersionNo] >= 1");
                    table.ForeignKey(
                        name: "FK_Contracts_Contracts_TenantId_PreviousContractId",
                        columns: x => new { x.TenantId, x.PreviousContractId },
                        principalSchema: "contracts",
                        principalTable: "Contracts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Contracts_Parties_TenantId_PartyId",
                        columns: x => new { x.TenantId, x.PartyId },
                        principalSchema: "core",
                        principalTable: "Parties",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Contracts_People_TenantId_PersonId",
                        columns: x => new { x.TenantId, x.PersonId },
                        principalSchema: "core",
                        principalTable: "People",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContractEvents",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FromStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractEvents", x => x.Id);
                    table.UniqueConstraint("AK_ContractEvents_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ContractEvents_Contracts_TenantId_ContractId",
                        columns: x => new { x.TenantId, x.ContractId },
                        principalSchema: "contracts",
                        principalTable: "Contracts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContractLines",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StableKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProductCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    UnitName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    DiscountPercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    TaxRatePercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    BillingPeriod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SourceQuoteLineKey = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ListAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LineDiscount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Net = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Tax = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Gross = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractLines", x => x.Id);
                    table.UniqueConstraint("AK_ContractLines_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ContractLines_Contracts_TenantId_ContractId",
                        columns: x => new { x.TenantId, x.ContractId },
                        principalSchema: "contracts",
                        principalTable: "Contracts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContractParties",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractParties", x => x.Id);
                    table.UniqueConstraint("AK_ContractParties_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ContractParties_Contracts_TenantId_ContractId",
                        columns: x => new { x.TenantId, x.ContractId },
                        principalSchema: "contracts",
                        principalTable: "Contracts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractParties_Parties_TenantId_PartyId",
                        columns: x => new { x.TenantId, x.PartyId },
                        principalSchema: "core",
                        principalTable: "Parties",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContractParties_People_TenantId_PersonId",
                        columns: x => new { x.TenantId, x.PersonId },
                        principalSchema: "core",
                        principalTable: "People",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractEvents_TenantId_ContractId_CreatedAt",
                schema: "contracts",
                table: "ContractEvents",
                columns: new[] { "TenantId", "ContractId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractLines_TenantId_ContractId_Position",
                schema: "contracts",
                table: "ContractLines",
                columns: new[] { "TenantId", "ContractId", "Position" });

            migrationBuilder.CreateIndex(
                name: "UX_ContractLines_StableKey",
                schema: "contracts",
                table: "ContractLines",
                columns: new[] { "TenantId", "ContractId", "StableKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_TenantId_ContractId",
                schema: "contracts",
                table: "ContractParties",
                columns: new[] { "TenantId", "ContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_TenantId_PartyId",
                schema: "contracts",
                table: "ContractParties",
                columns: new[] { "TenantId", "PartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractParties_TenantId_PersonId",
                schema: "contracts",
                table: "ContractParties",
                columns: new[] { "TenantId", "PersonId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_EffectiveStatus_EndDate",
                schema: "contracts",
                table: "Contracts",
                columns: new[] { "TenantId", "EffectiveStatus", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_NoticeDeadline",
                schema: "contracts",
                table: "Contracts",
                columns: new[] { "TenantId", "NoticeDeadline" });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_OwnerId_EffectiveStatus",
                schema: "contracts",
                table: "Contracts",
                columns: new[] { "TenantId", "OwnerId", "EffectiveStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_PartyId",
                schema: "contracts",
                table: "Contracts",
                columns: new[] { "TenantId", "PartyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_PersonId",
                schema: "contracts",
                table: "Contracts",
                columns: new[] { "TenantId", "PersonId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_PreviousContractId",
                schema: "contracts",
                table: "Contracts",
                columns: new[] { "TenantId", "PreviousContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_TenantId_SignatureStatus",
                schema: "contracts",
                table: "Contracts",
                columns: new[] { "TenantId", "SignatureStatus" });

            migrationBuilder.CreateIndex(
                name: "UX_Contracts_Number",
                schema: "contracts",
                table: "Contracts",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, "contracts", table);
            }

            // Uygulama kullanıcısı yeni şemada yalnızca veri işlemi yapar (init.sql ile aynı; mevcut veritabanları için burada da verilir).
            migrationBuilder.Sql(
                "IF DATABASE_PRINCIPAL_ID(N'modulix_app_role') IS NOT NULL GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[contracts] TO [modulix_app_role];");
        }

        private static readonly string[] TenantTables = ["Contracts", "ContractParties", "ContractLines", "ContractEvents"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, "contracts", table);
            }

            migrationBuilder.DropTable(
                name: "ContractEvents",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "ContractLines",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "ContractParties",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "Contracts",
                schema: "contracts");
        }
    }
}
