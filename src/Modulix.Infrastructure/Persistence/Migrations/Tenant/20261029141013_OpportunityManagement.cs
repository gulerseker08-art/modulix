using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class OpportunityManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ActualCloseDate",
                schema: "crm",
                table: "Opportunities",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BudgetStatus",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<int>(
                name: "CloseDateChangeCount",
                schema: "crm",
                table: "Opportunities",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ClosureCycle",
                schema: "crm",
                table: "Opportunities",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "CurrentProblem",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CustomerBudget",
                schema: "crm",
                table: "Opportunities",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionProcess",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedBenefit",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HandoverNote",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "HoldReviewDate",
                schema: "crm",
                table: "Opportunities",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastInteractionAt",
                schema: "crm",
                table: "Opportunities",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LossReason",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NeedDescription",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NextAction",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "NextActionDate",
                schema: "crm",
                table: "Opportunities",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpportunityNumber",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Normal");

            migrationBuilder.AddColumn<bool>(
                name: "ProbabilityManual",
                schema: "crm",
                table: "Opportunities",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "RecontactDate",
                schema: "crm",
                table: "Opportunities",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Risks",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StageEnteredAt",
                schema: "crm",
                table: "Opportunities",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Open");

            migrationBuilder.AddColumn<string>(
                name: "StatusReason",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuccessCriteria",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "TargetDecisionDate",
                schema: "crm",
                table: "Opportunities",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Type",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WinReason",
                schema: "crm",
                table: "Opportunities",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WinningCompetitorId",
                schema: "crm",
                table: "Opportunities",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WonAmount",
                schema: "crm",
                table: "Opportunities",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WonCurrency",
                schema: "crm",
                table: "Opportunities",
                type: "char(3)",
                unicode: false,
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OpportunityCompetitors",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Strengths = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Weaknesses = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    AlternativeAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    AlternativeCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunityCompetitors", x => x.Id);
                    table.UniqueConstraint("AK_OpportunityCompetitors_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_OpportunityCompetitors_Opportunities_TenantId_OpportunityId",
                        columns: x => new { x.TenantId, x.OpportunityId },
                        principalSchema: "crm",
                        principalTable: "Opportunities",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpportunityContacts",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Roles = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunityContacts", x => x.Id);
                    table.UniqueConstraint("AK_OpportunityContacts_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_OpportunityContacts_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpportunityContacts_Opportunities_TenantId_OpportunityId",
                        columns: x => new { x.TenantId, x.OpportunityId },
                        principalSchema: "crm",
                        principalTable: "Opportunities",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpportunityHistory",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Cycle = table.Column<int>(type: "int", nullable: false),
                    FromStage = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ToStage = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    FromStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    FromCloseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ToCloseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunityHistory", x => x.Id);
                    table.UniqueConstraint("AK_OpportunityHistory_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_OpportunityHistory_Opportunities_TenantId_OpportunityId",
                        columns: x => new { x.TenantId, x.OpportunityId },
                        principalSchema: "crm",
                        principalTable: "Opportunities",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            // ---- Mevcut kayıtların korunması (ADR-0017): yeni zorunlu kolonlar mevcut fırsatlardan türetilir.

            // Satır güvenliği (RLS) oturum değeri olmadan satırları gizler; migration bu veritabanının tek tenant verisini toplu günceller. Politika
            // yalnızca geri doldurma boyunca (aynı işlemde) kapatılır, hata olursa işlemle birlikte eski haline döner.
            migrationBuilder.Sql("ALTER SECURITY POLICY [core].[TenantIsolationPolicy] WITH (STATE = OFF);");

            // Fırsat numarası: tenant içinde oluşturulma sırasına göre benzersiz (yeni kayıtlar rastgele sonek alır).
            migrationBuilder.Sql("""
                WITH n AS (
                    SELECT [Id], ROW_NUMBER() OVER (PARTITION BY [TenantId] ORDER BY [CreatedAt], [Id]) AS rn
                    FROM [crm].[Opportunities])
                UPDATE o SET [OpportunityNumber] =
                    N'OP-' + FORMAT(o.[CreatedAt], 'yyyyMMdd') + N'-M' + RIGHT(N'00000' + CAST(n.rn AS nvarchar(10)), 5)
                FROM [crm].[Opportunities] o JOIN n ON n.[Id] = o.[Id];
                """);

            // Durum: aşamanın kategorisinden. Hazır "Won"/"Lost" anahtarları ve firmanın listesindeki kazanıldı/kaybedildi kategorili aşamalar.
            migrationBuilder.Sql("""
                UPDATE [crm].[Opportunities] SET [Status] = N'Won' WHERE [Stage] = N'Won';
                UPDATE [crm].[Opportunities] SET [Status] = N'Lost' WHERE [Stage] = N'Lost';
                UPDATE o SET [Status] = CASE v.[category] WHEN N'won' THEN N'Won' ELSE N'Lost' END
                FROM [crm].[Opportunities] o
                JOIN [meta].[ChoiceLists] l ON l.[TenantId] = o.[TenantId] AND l.[ListKey] = N'crm.opportunity-stage'
                CROSS APPLY OPENJSON(l.[ValuesJson]) WITH ([key] nvarchar(100) '$.key', [category] nvarchar(30) '$.category') v
                WHERE v.[key] = o.[Stage] AND v.[category] IN (N'won', N'lost');
                """);

            // Kapanış bilgileri ve aşamaya giriş zamanı. Eski kayıtlarda yapılandırılmış kaybetme nedeni yoktur (LossReason boş kalır;
            // serbest metin LostReason korunur). Tahmini tutar kazanılan tutar sayılır; yeniden kapanışta düzeltilebilir.
            migrationBuilder.Sql("""
                UPDATE [crm].[Opportunities] SET [StageEnteredAt] = [UpdatedAt];
                UPDATE [crm].[Opportunities]
                SET [ActualCloseDate] = CAST([UpdatedAt] AS date)
                WHERE [Status] IN (N'Won', N'Lost');
                UPDATE [crm].[Opportunities]
                SET [WonAmount] = [Amount], [WonCurrency] = [Currency]
                WHERE [Status] = N'Won' AND [Amount] IS NOT NULL;
                """);

            // Tarihçe başlangıcı: oluşturulma ve (kapalıysa) kapanış satırı. Aşama geçmişi önceden tutulmadığı için eski geçişler yoktur.
            migrationBuilder.Sql("""
                INSERT INTO [crm].[OpportunityHistory]
                    ([Id], [TenantId], [CreatedAt], [UpdatedAt], [Version], [OpportunityId], [Kind], [At], [Cycle], [ToStage], [ToStatus])
                SELECT NEWID(), [TenantId], [CreatedAt], [CreatedAt], 1, [Id], N'Created', [CreatedAt], 1,
                    CASE WHEN [Status] = N'Open' THEN [Stage] END, N'Open'
                FROM [crm].[Opportunities];
                INSERT INTO [crm].[OpportunityHistory]
                    ([Id], [TenantId], [CreatedAt], [UpdatedAt], [Version], [OpportunityId], [Kind], [At], [Cycle], [FromStatus], [ToStage], [ToStatus])
                SELECT NEWID(), [TenantId], [UpdatedAt], [UpdatedAt], 1, [Id], N'Status', [UpdatedAt], 1, N'Open', [Stage], [Status]
                FROM [crm].[Opportunities] WHERE [Status] IN (N'Won', N'Lost');
                """);

            migrationBuilder.Sql("ALTER SECURITY POLICY [core].[TenantIsolationPolicy] WITH (STATE = ON);");

            TenantIsolationSql.AddTable(migrationBuilder, "crm", "OpportunityContacts");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "OpportunityCompetitors");
            TenantIsolationSql.AddTable(migrationBuilder, "crm", "OpportunityHistory");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_Status_ExpectedCloseDate",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "TenantId", "Status", "ExpectedCloseDate" });

            migrationBuilder.CreateIndex(
                name: "UX_Opportunities_Number",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "TenantId", "OpportunityNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityCompetitors_TenantId_OpportunityId",
                schema: "crm",
                table: "OpportunityCompetitors",
                columns: new[] { "TenantId", "OpportunityId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityContacts_TenantId_ContactId",
                schema: "crm",
                table: "OpportunityContacts",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityContacts_TenantId_OpportunityId_ContactId",
                schema: "crm",
                table: "OpportunityContacts",
                columns: new[] { "TenantId", "OpportunityId", "ContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityHistory_TenantId_OpportunityId_At",
                schema: "crm",
                table: "OpportunityHistory",
                columns: new[] { "TenantId", "OpportunityId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "OpportunityContacts");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "OpportunityCompetitors");
            TenantIsolationSql.RemoveTable(migrationBuilder, "crm", "OpportunityHistory");

            migrationBuilder.DropTable(
                name: "OpportunityCompetitors",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "OpportunityContacts",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "OpportunityHistory",
                schema: "crm");

            migrationBuilder.DropIndex(
                name: "IX_Opportunities_TenantId_Status_ExpectedCloseDate",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropIndex(
                name: "UX_Opportunities_Number",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ActualCloseDate",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "BudgetStatus",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "CloseDateChangeCount",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ClosureCycle",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "CurrentProblem",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "CustomerBudget",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "DecisionProcess",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "Description",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ExpectedBenefit",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "HandoverNote",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "HoldReviewDate",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "LastInteractionAt",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "LossReason",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "NeedDescription",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "NextAction",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "NextActionDate",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "OpportunityNumber",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "Priority",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ProbabilityManual",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "RecontactDate",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "Risks",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "Scope",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "StageEnteredAt",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "StatusReason",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "SuccessCriteria",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "TargetDecisionDate",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "Type",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "WinReason",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "WinningCompetitorId",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "WonAmount",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "WonCurrency",
                schema: "crm",
                table: "Opportunities");
        }
    }
}
