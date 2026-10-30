using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        /// <summary>Row-Level Security politikasına giren tenant tabloları (TenantSettings politikayla birlikte oluşturulur).</summary>
        private static readonly (string Schema, string Table)[] TenantTables =
        [
            ("core", "AuditLog"),
            ("core", "Branches"),
            ("core", "Companies"),
            ("core", "MembershipCompanies"),
            ("core", "MembershipRoles"),
            ("core", "Parties"),
            ("core", "People"),
            ("core", "Roles"),
            ("core", "TeamMembers"),
            ("core", "Teams"),
            ("core", "TenantLogos"),
            ("crm", "Activities"),
            ("crm", "ActivityLinks"),
            ("crm", "ActivityParticipants"),
            ("crm", "Addresses"),
            ("crm", "Companies"),
            ("crm", "CompanyContacts"),
            ("crm", "CompanyRelations"),
            ("crm", "ContactPoints"),
            ("crm", "Contacts"),
            ("crm", "DiscountPolicies"),
            ("crm", "ExchangeRates"),
            ("crm", "LeadForms"),
            ("crm", "Leads"),
            ("crm", "Locations"),
            ("crm", "MeasureUnits"),
            ("crm", "MergedRecords"),
            ("crm", "Opportunities"),
            ("crm", "OpportunityCompetitors"),
            ("crm", "OpportunityContacts"),
            ("crm", "OpportunityHistory"),
            ("crm", "PriceListItems"),
            ("crm", "PriceLists"),
            ("crm", "ProductCategories"),
            ("crm", "ProductComponents"),
            ("crm", "Products"),
            ("crm", "QuoteApprovalDelegations"),
            ("crm", "QuoteApprovalRules"),
            ("crm", "QuoteDecisions"),
            ("crm", "QuoteDocuments"),
            ("crm", "QuoteEvents"),
            ("crm", "QuoteLines"),
            ("crm", "QuoteShareLinks"),
            ("crm", "Quotes"),
            ("crm", "TaxRates"),
            ("meta", "ChoiceLists"),
            ("meta", "CustomFields"),
            ("meta", "LookupSettings"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "crm");

            migrationBuilder.EnsureSchema(
                name: "core");

            migrationBuilder.EnsureSchema(
                name: "meta");

            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.CreateTable(
                name: "AuditLog",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    Changes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Subscription = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

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

            migrationBuilder.CreateTable(
                name: "Companies",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                    table.UniqueConstraint("AK_Companies_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "CustomFields",
                schema: "meta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Required = table.Column<bool>(type: "bit", nullable: false),
                    ShowInList = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFields", x => x.Id);
                    table.UniqueConstraint("AK_CustomFields_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "DiscountPolicies",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Percent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Stackable = table.Column<bool>(type: "bit", nullable: false),
                    MaxTotalPercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    ApprovalAbovePercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscountPolicies", x => x.Id);
                    table.UniqueConstraint("AK_DiscountPolicies_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_DiscountPolicies_Value", "([Percent] IS NOT NULL AND [Amount] IS NULL AND [Percent] > 0 AND [Percent] <= 100) OR ([Percent] IS NULL AND [Amount] IS NOT NULL AND [Amount] > 0 AND [Currency] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "EmailMessages",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ToAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ToName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TextBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExchangeRates",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    ToCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    ValidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRates", x => x.Id);
                    table.UniqueConstraint("AK_ExchangeRates_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_ExchangeRates_Pair", "[FromCurrency] <> [ToCurrency]");
                    table.CheckConstraint("CK_ExchangeRates_Rate", "[Rate] > 0");
                });

            migrationBuilder.CreateTable(
                name: "LeadForms",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FormKey = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DefaultOwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AllowedOrigins = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ServerOnly = table.Column<bool>(type: "bit", nullable: false),
                    SecretHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    SecretRotatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadForms", x => x.Id);
                    table.UniqueConstraint("AK_LeadForms_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "LookupSettings",
                schema: "meta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LookupKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Searchable = table.Column<bool>(type: "bit", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LookupSettings", x => x.Id);
                    table.UniqueConstraint("AK_LookupSettings_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "MeasureUnits",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    GroupKey = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FactorToBase = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeasureUnits", x => x.Id);
                    table.UniqueConstraint("AK_MeasureUnits_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_MeasureUnits_Factor", "[FactorToBase] > 0");
                });

            migrationBuilder.CreateTable(
                name: "MergedRecords",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MergedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MergedRecords", x => x.Id);
                    table.UniqueConstraint("AK_MergedRecords_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    EntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CausationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Depth = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

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
                name: "ProductCategories",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCategories", x => x.Id);
                    table.UniqueConstraint("AK_ProductCategories_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ProductCategories_ProductCategories_TenantId_ParentId",
                        columns: x => new { x.TenantId, x.ParentId },
                        principalSchema: "crm",
                        principalTable: "ProductCategories",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteApprovalDelegations",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteApprovalDelegations", x => x.Id);
                    table.UniqueConstraint("AK_QuoteApprovalDelegations_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "QuoteApprovalRules",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Step = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    MinDiscountPercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    MinGrossTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ApproverId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteApprovalRules", x => x.Id);
                    table.UniqueConstraint("AK_QuoteApprovalRules_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_QuoteApprovalRules_Condition", "([MinDiscountPercent] IS NOT NULL AND [MinDiscountPercent] >= 0 AND [MinDiscountPercent] < 100) OR ([MinGrossTotal] IS NOT NULL AND [MinGrossTotal] >= 0)");
                    table.CheckConstraint("CK_QuoteApprovalRules_Step", "[Step] BETWEEN 1 AND 9");
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Permissions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                    table.UniqueConstraint("AK_Roles_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "TaxRates",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RatePercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRates", x => x.Id);
                    table.UniqueConstraint("AK_TaxRates_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_TaxRates_Dates", "[ValidTo] IS NULL OR [ValidFrom] < [ValidTo]");
                    table.CheckConstraint("CK_TaxRates_Rate", "[RatePercent] >= 0 AND [RatePercent] <= 100");
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                    table.UniqueConstraint("AK_Teams_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "TenantLogos",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentType = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Data = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantLogos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProfileKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ProfileAppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantSettings",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsPlatformAdministrator = table.Column<bool>(type: "bit", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Branches",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Branches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Branches_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "core",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EventDeliveries",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subscription = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: true),
                    Ordered = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventDeliveries_OutboxMessages_EventId",
                        column: x => x.EventId,
                        principalSchema: "platform",
                        principalTable: "OutboxMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Companies",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Industry = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Tags = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                    table.UniqueConstraint("AK_Companies_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Companies_Parties_TenantId_PartyId",
                        columns: x => new { x.TenantId, x.PartyId },
                        principalSchema: "core",
                        principalTable: "Parties",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
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

            migrationBuilder.CreateTable(
                name: "Products",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BaseUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Barcode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Brand = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Tags = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SaleStart = table.Column<DateOnly>(type: "date", nullable: true),
                    SaleEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    TaxCategory = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    PriceIncludesTax = table.Column<bool>(type: "bit", nullable: false),
                    QuantityPrecision = table.Column<int>(type: "int", nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CostCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    ServiceDurationMinutes = table.Column<int>(type: "int", nullable: true),
                    DeliveryMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ChargeType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RecurringPeriod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                    table.UniqueConstraint("AK_Products_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Products_Precision", "[QuantityPrecision] BETWEEN 0 AND 4");
                    table.CheckConstraint("CK_Products_SaleDates", "[SaleStart] IS NULL OR [SaleEnd] IS NULL OR [SaleStart] <= [SaleEnd]");
                    table.ForeignKey(
                        name: "FK_Products_MeasureUnits_TenantId_BaseUnitId",
                        columns: x => new { x.TenantId, x.BaseUnitId },
                        principalSchema: "crm",
                        principalTable: "MeasureUnits",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Products_ProductCategories_TenantId_CategoryId",
                        columns: x => new { x.TenantId, x.CategoryId },
                        principalSchema: "crm",
                        principalTable: "ProductCategories",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ModuleEntitlements",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModuleKey = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModuleEntitlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModuleEntitlements_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    InvitedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSignInAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => x.Id);
                    table.UniqueConstraint("AK_Memberships_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Memberships_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Memberships_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "platform",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserClaims",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserClaims_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "platform",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserLogins",
                schema: "platform",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_UserLogins_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "platform",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSessions",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IpAddress = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSessions_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "platform",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserTokens",
                schema: "platform",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_UserTokens_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "platform",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CompanyRelations",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentCompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChildCompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyRelations", x => x.Id);
                    table.UniqueConstraint("AK_CompanyRelations_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_CompanyRelations_NoSelf", "[ParentCompanyId] <> [ChildCompanyId]");
                    table.ForeignKey(
                        name: "FK_CompanyRelations_Companies_TenantId_ChildCompanyId",
                        columns: x => new { x.TenantId, x.ChildCompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyRelations_Companies_TenantId_ParentCompanyId",
                        columns: x => new { x.TenantId, x.ParentCompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PriceLists",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Segment = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ValidFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ValidTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TierMethod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLists", x => x.Id);
                    table.UniqueConstraint("AK_PriceLists_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_PriceLists_Dates", "[ValidFrom] IS NULL OR [ValidTo] IS NULL OR [ValidFrom] < [ValidTo]");
                    table.ForeignKey(
                        name: "FK_PriceLists_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Contacts",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.Id);
                    table.UniqueConstraint("AK_Contacts_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Contacts_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Contacts_People_TenantId_PersonId",
                        columns: x => new { x.TenantId, x.PersonId },
                        principalSchema: "core",
                        principalTable: "People",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductComponents",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductComponents", x => x.Id);
                    table.UniqueConstraint("AK_ProductComponents_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_ProductComponents_Quantity", "[Quantity] > 0 AND [PackageId] <> [ComponentId]");
                    table.ForeignKey(
                        name: "FK_ProductComponents_Products_TenantId_ComponentId",
                        columns: x => new { x.TenantId, x.ComponentId },
                        principalSchema: "crm",
                        principalTable: "Products",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductComponents_Products_TenantId_PackageId",
                        columns: x => new { x.TenantId, x.PackageId },
                        principalSchema: "crm",
                        principalTable: "Products",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MembershipCompanies",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ValidUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MembershipCompanies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MembershipCompanies_Branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "core",
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MembershipCompanies_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "core",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MembershipCompanies_Memberships_TenantId_MembershipId",
                        columns: x => new { x.TenantId, x.MembershipId },
                        principalSchema: "platform",
                        principalTable: "Memberships",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MembershipRoles",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MembershipRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MembershipRoles_Memberships_TenantId_MembershipId",
                        columns: x => new { x.TenantId, x.MembershipId },
                        principalSchema: "platform",
                        principalTable: "Memberships",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MembershipRoles_Roles_TenantId_RoleId",
                        columns: x => new { x.TenantId, x.RoleId },
                        principalSchema: "core",
                        principalTable: "Roles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeamMembers",
                schema: "core",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamMembers_Memberships_TenantId_MembershipId",
                        columns: x => new { x.TenantId, x.MembershipId },
                        principalSchema: "platform",
                        principalTable: "Memberships",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamMembers_Teams_TenantId_TeamId",
                        columns: x => new { x.TenantId, x.TeamId },
                        principalSchema: "core",
                        principalTable: "Teams",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceListItems",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PriceListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MinQuantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    MaxQuantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    IsFree = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceListItems", x => x.Id);
                    table.UniqueConstraint("AK_PriceListItems_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_PriceListItems_Tier", "[MinQuantity] >= 0 AND ([MaxQuantity] IS NULL OR [MaxQuantity] > [MinQuantity]) AND [UnitPrice] >= 0 AND ([IsFree] = 0 OR [UnitPrice] = 0)");
                    table.ForeignKey(
                        name: "FK_PriceListItems_PriceLists_TenantId_PriceListId",
                        columns: x => new { x.TenantId, x.PriceListId },
                        principalSchema: "crm",
                        principalTable: "PriceLists",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceListItems_Products_TenantId_ProductId",
                        columns: x => new { x.TenantId, x.ProductId },
                        principalSchema: "crm",
                        principalTable: "Products",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CompanyContacts",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Department = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    JobTitle = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Roles = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    IsDecisionMaker = table.Column<bool>(type: "bit", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyContacts", x => x.Id);
                    table.UniqueConstraint("AK_CompanyContacts_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_CompanyContacts_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyContacts_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Locations",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                    table.UniqueConstraint("AK_Locations_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Locations_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Locations_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Addresses",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Country = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: false),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    District = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Line1 = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Line2 = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    NormalizedText = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Addresses", x => x.Id);
                    table.UniqueConstraint("AK_Addresses_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Addresses_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Addresses_Locations_TenantId_LocationId",
                        columns: x => new { x.TenantId, x.LocationId },
                        principalSchema: "crm",
                        principalTable: "Locations",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContactPoints",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompanyContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    NormalizedValue = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsShared = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactPoints", x => x.Id);
                    table.UniqueConstraint("AK_ContactPoints_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_ContactPoints_Owner", "(([CompanyId] IS NOT NULL AND [ContactId] IS NULL) OR ([CompanyId] IS NULL AND [ContactId] IS NOT NULL)) AND ([LocationId] IS NULL OR [CompanyId] IS NOT NULL) AND ([CompanyContactId] IS NULL OR [ContactId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ContactPoints_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContactPoints_CompanyContacts_TenantId_CompanyContactId",
                        columns: x => new { x.TenantId, x.CompanyContactId },
                        principalSchema: "crm",
                        principalTable: "CompanyContacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContactPoints_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContactPoints_Locations_TenantId_LocationId",
                        columns: x => new { x.TenantId, x.LocationId },
                        principalSchema: "crm",
                        principalTable: "Locations",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Activities",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OpportunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Priority = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    StartAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsAllDay = table.Column<bool>(type: "bit", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    OnlineMeetingUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsPrivate = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EstimatedMinutes = table.Column<int>(type: "int", nullable: true),
                    ActualMinutes = table.Column<int>(type: "int", nullable: true),
                    RescheduleCount = table.Column<int>(type: "int", nullable: false),
                    PreviousActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalSource = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activities", x => x.Id);
                    table.UniqueConstraint("AK_Activities_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Activities_Activities_TenantId_PreviousActivityId",
                        columns: x => new { x.TenantId, x.PreviousActivityId },
                        principalSchema: "crm",
                        principalTable: "Activities",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Activities_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Activities_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivityLinks",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityLinks", x => x.Id);
                    table.UniqueConstraint("AK_ActivityLinks_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ActivityLinks_Activities_TenantId_ActivityId",
                        columns: x => new { x.TenantId, x.ActivityId },
                        principalSchema: "crm",
                        principalTable: "Activities",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivityParticipants",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityParticipants", x => x.Id);
                    table.UniqueConstraint("AK_ActivityParticipants_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ActivityParticipants_Activities_TenantId_ActivityId",
                        columns: x => new { x.TenantId, x.ActivityId },
                        principalSchema: "crm",
                        principalTable: "Activities",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActivityParticipants_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Leads",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeadNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Priority = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    NormalizedPhone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    JobTitle = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Website = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    WebsiteDomain = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Country = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductInterest = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NeedDescription = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    EstimatedBudget = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    TargetPurchaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Industry = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SourceDetail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Referrer = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ExternalSource = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExternalSubmissionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UtmSource = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmMedium = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmCampaign = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FirstReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FirstContactAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FollowUpAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    QualifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DisqualifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DisqualificationReason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ConvertedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConvertedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConvertedCompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConvertedContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConvertedOpportunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leads", x => x.Id);
                    table.UniqueConstraint("AK_Leads_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Leads_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leads_Companies_TenantId_ConvertedCompanyId",
                        columns: x => new { x.TenantId, x.ConvertedCompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leads_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leads_Contacts_TenantId_ConvertedContactId",
                        columns: x => new { x.TenantId, x.ConvertedContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Opportunities",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Stage = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    PrimaryQuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpectedCloseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Probability = table.Column<int>(type: "int", nullable: true),
                    LostReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SourceLeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    OpportunityNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Priority = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    NeedDescription = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CurrentProblem = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ExpectedBenefit = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Scope = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    SuccessCriteria = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Risks = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DecisionProcess = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TargetDecisionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CustomerBudget = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    BudgetStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ActualCloseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    WonAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    WonCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    WinReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HandoverNote = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    LossReason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    WinningCompetitorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecontactDate = table.Column<DateOnly>(type: "date", nullable: true),
                    HoldReviewDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NextAction = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NextActionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LastInteractionAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StageEnteredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProbabilityManual = table.Column<bool>(type: "bit", nullable: false),
                    ClosureCycle = table.Column<int>(type: "int", nullable: false),
                    CloseDateChangeCount = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Opportunities", x => x.Id);
                    table.UniqueConstraint("AK_Opportunities_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Opportunities_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Opportunities_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Opportunities_Leads_TenantId_SourceLeadId",
                        columns: x => new { x.TenantId, x.SourceLeadId },
                        principalSchema: "crm",
                        principalTable: "Leads",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

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

            migrationBuilder.CreateTable(
                name: "Quotes",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionNo = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OpportunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PriceListId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ApprovalStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DeliveryStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    ReportingCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    ExchangeRateDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PriceIncludesTax = table.Column<bool>(type: "bit", nullable: false),
                    CalculationPolicyVersion = table.Column<int>(type: "int", nullable: false),
                    DocumentDiscountKind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DocumentDiscountValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    PaymentTerms = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DeliveryTerms = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CommercialTerms = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    InternalNotes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ListTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DiscountTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    NetTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    GrossTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    OptionalNet = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RecurringSummary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CalculatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PresentedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CustomerNameSnapshot = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
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
                    table.PrimaryKey("PK_Quotes", x => x.Id);
                    table.UniqueConstraint("AK_Quotes_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_Quotes_Revision", "[RevisionNo] >= 1");
                    table.CheckConstraint("CK_Quotes_Totals", "[NetTotal] >= 0 AND [TaxTotal] >= 0 AND [GrossTotal] >= 0");
                    table.ForeignKey(
                        name: "FK_Quotes_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "crm",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Quotes_Contacts_TenantId_ContactId",
                        columns: x => new { x.TenantId, x.ContactId },
                        principalSchema: "crm",
                        principalTable: "Contacts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Quotes_Opportunities_TenantId_OpportunityId",
                        columns: x => new { x.TenantId, x.OpportunityId },
                        principalSchema: "crm",
                        principalTable: "Opportunities",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Quotes_PriceLists_TenantId_PriceListId",
                        columns: x => new { x.TenantId, x.PriceListId },
                        principalSchema: "crm",
                        principalTable: "PriceLists",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteDocuments",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionNo = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    Sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    TemplateVersion = table.Column<int>(type: "int", nullable: false),
                    Snapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteDocuments", x => x.Id);
                    table.UniqueConstraint("AK_QuoteDocuments_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_QuoteDocuments_Quotes_TenantId_QuoteId",
                        columns: x => new { x.TenantId, x.QuoteId },
                        principalSchema: "crm",
                        principalTable: "Quotes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteEvents",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_QuoteEvents", x => x.Id);
                    table.UniqueConstraint("AK_QuoteEvents_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_QuoteEvents_Quotes_TenantId_QuoteId",
                        columns: x => new { x.TenantId, x.QuoteId },
                        principalSchema: "crm",
                        principalTable: "Quotes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteLines",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StableKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    GroupName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UnitName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    DiscountKind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DiscountValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    TaxRatePercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    TaxCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsSelected = table.Column<bool>(type: "bit", nullable: false),
                    ParentStableKey = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecurringPeriod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    UnitCost = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    Included = table.Column<bool>(type: "bit", nullable: false),
                    ListAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LineDiscount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DocumentDiscountShare = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
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
                    table.PrimaryKey("PK_QuoteLines", x => x.Id);
                    table.UniqueConstraint("AK_QuoteLines_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_QuoteLines_Amounts", "[Quantity] > 0 AND [UnitPrice] >= 0 AND [DiscountValue] >= 0");
                    table.CheckConstraint("CK_QuoteLines_Tax", "[TaxRatePercent] >= 0 AND [TaxRatePercent] <= 100");
                    table.ForeignKey(
                        name: "FK_QuoteLines_Products_TenantId_ProductId",
                        columns: x => new { x.TenantId, x.ProductId },
                        principalSchema: "crm",
                        principalTable: "Products",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QuoteLines_Quotes_TenantId_QuoteId",
                        columns: x => new { x.TenantId, x.QuoteId },
                        principalSchema: "crm",
                        principalTable: "Quotes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteShareLinks",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    TokenHint = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    RecipientName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RecipientEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevokeReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FirstViewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteShareLinks", x => x.Id);
                    table.UniqueConstraint("AK_QuoteShareLinks_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_QuoteShareLinks_QuoteDocuments_TenantId_DocumentId",
                        columns: x => new { x.TenantId, x.DocumentId },
                        principalSchema: "crm",
                        principalTable: "QuoteDocuments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QuoteShareLinks_Quotes_TenantId_QuoteId",
                        columns: x => new { x.TenantId, x.QuoteId },
                        principalSchema: "crm",
                        principalTable: "Quotes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QuoteDecisions",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShareLinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SignerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SignerEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    SignerTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TermsStatement = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ClientAddressMasked = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteDecisions", x => x.Id);
                    table.UniqueConstraint("AK_QuoteDecisions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_QuoteDecisions_Decision", "[Decision] IN ('Accepted','Rejected')");
                    table.ForeignKey(
                        name: "FK_QuoteDecisions_QuoteDocuments_TenantId_DocumentId",
                        columns: x => new { x.TenantId, x.DocumentId },
                        principalSchema: "crm",
                        principalTable: "QuoteDocuments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QuoteDecisions_QuoteShareLinks_TenantId_ShareLinkId",
                        columns: x => new { x.TenantId, x.ShareLinkId },
                        principalSchema: "crm",
                        principalTable: "QuoteShareLinks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QuoteDecisions_Quotes_TenantId_QuoteId",
                        columns: x => new { x.TenantId, x.QuoteId },
                        principalSchema: "crm",
                        principalTable: "Quotes",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_CompanyId",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_ContactId",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_DueAt",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_ExternalSource_ExternalId",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "ExternalSource", "ExternalId" },
                unique: true,
                filter: "[ExternalSource] IS NOT NULL AND [ExternalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_LeadId",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "LeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_OpportunityId",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "OpportunityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_OwnerId_CompletedAt",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "OwnerId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_OwnerId_Status_DueAt",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "OwnerId", "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_PreviousActivityId",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "PreviousActivityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_StartAt",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "StartAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_TenantId_StartDate",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLinks_TenantId_ActivityId_EntityKey_RecordId",
                schema: "crm",
                table: "ActivityLinks",
                columns: new[] { "TenantId", "ActivityId", "EntityKey", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLinks_TenantId_EntityKey_RecordId",
                schema: "crm",
                table: "ActivityLinks",
                columns: new[] { "TenantId", "EntityKey", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "UX_ActivityLinks_Primary",
                schema: "crm",
                table: "ActivityLinks",
                columns: new[] { "TenantId", "ActivityId" },
                unique: true,
                filter: "[IsPrimary] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityParticipants_TenantId_ActivityId_ContactId",
                schema: "crm",
                table: "ActivityParticipants",
                columns: new[] { "TenantId", "ActivityId", "ContactId" },
                unique: true,
                filter: "[ContactId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityParticipants_TenantId_ActivityId_NormalizedEmail",
                schema: "crm",
                table: "ActivityParticipants",
                columns: new[] { "TenantId", "ActivityId", "NormalizedEmail" },
                unique: true,
                filter: "[NormalizedEmail] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityParticipants_TenantId_ActivityId_UserId",
                schema: "crm",
                table: "ActivityParticipants",
                columns: new[] { "TenantId", "ActivityId", "UserId" },
                unique: true,
                filter: "[UserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityParticipants_TenantId_ContactId",
                schema: "crm",
                table: "ActivityParticipants",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityParticipants_TenantId_UserId",
                schema: "crm",
                table: "ActivityParticipants",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Addresses_TenantId_LocationId",
                schema: "crm",
                table: "Addresses",
                columns: new[] { "TenantId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "UX_Addresses_PrimaryPerOwner",
                schema: "crm",
                table: "Addresses",
                columns: new[] { "TenantId", "CompanyId", "LocationId" },
                unique: true,
                filter: "[IsPrimary] = 1 AND [IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_TenantId_EntityKey_RecordId_OccurredAt",
                schema: "core",
                table: "AuditLog",
                columns: new[] { "TenantId", "EntityKey", "RecordId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_TenantId_OccurredAt",
                schema: "core",
                table: "AuditLog",
                columns: new[] { "TenantId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_TenantId_UserId_OccurredAt",
                schema: "core",
                table: "AuditLog",
                columns: new[] { "TenantId", "UserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Branches_TenantId_CompanyId_Name",
                schema: "core",
                table: "Branches",
                columns: new[] { "TenantId", "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChoiceLists_TenantId_ListKey",
                schema: "meta",
                table: "ChoiceLists",
                columns: new[] { "TenantId", "ListKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId_Default",
                schema: "core",
                table: "Companies",
                column: "TenantId",
                unique: true,
                filter: "[IsDefault] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId_Name",
                schema: "core",
                table: "Companies",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId_OwnerId",
                schema: "crm",
                table: "Companies",
                columns: new[] { "TenantId", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId_PartyId",
                schema: "crm",
                table: "Companies",
                columns: new[] { "TenantId", "PartyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyContacts_TenantId_ContactId",
                schema: "crm",
                table: "CompanyContacts",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "UX_CompanyContacts_ActivePair",
                schema: "crm",
                table: "CompanyContacts",
                columns: new[] { "TenantId", "CompanyId", "ContactId" },
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_CompanyContacts_PrimaryPerCompany",
                schema: "crm",
                table: "CompanyContacts",
                columns: new[] { "TenantId", "CompanyId" },
                unique: true,
                filter: "[IsPrimary] = 1 AND [IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyRelations_TenantId_ChildCompanyId",
                schema: "crm",
                table: "CompanyRelations",
                columns: new[] { "TenantId", "ChildCompanyId" });

            migrationBuilder.CreateIndex(
                name: "UX_CompanyRelations_ActivePair",
                schema: "crm",
                table: "CompanyRelations",
                columns: new[] { "TenantId", "ParentCompanyId", "ChildCompanyId" },
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ContactPoints_TenantId_Channel_NormalizedValue",
                schema: "crm",
                table: "ContactPoints",
                columns: new[] { "TenantId", "Channel", "NormalizedValue" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactPoints_TenantId_CompanyContactId",
                schema: "crm",
                table: "ContactPoints",
                columns: new[] { "TenantId", "CompanyContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactPoints_TenantId_CompanyId",
                schema: "crm",
                table: "ContactPoints",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactPoints_TenantId_ContactId",
                schema: "crm",
                table: "ContactPoints",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactPoints_TenantId_LocationId",
                schema: "crm",
                table: "ContactPoints",
                columns: new[] { "TenantId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "UX_ContactPoints_PrimaryPerCompany",
                schema: "crm",
                table: "ContactPoints",
                columns: new[] { "TenantId", "CompanyId", "Channel" },
                unique: true,
                filter: "[IsPrimary] = 1 AND [IsActive] = 1 AND [CompanyId] IS NOT NULL AND [LocationId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_ContactPoints_PrimaryPerContact",
                schema: "crm",
                table: "ContactPoints",
                columns: new[] { "TenantId", "ContactId", "CompanyContactId", "Channel" },
                unique: true,
                filter: "[IsPrimary] = 1 AND [IsActive] = 1 AND [ContactId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_ContactPoints_PrimaryPerLocation",
                schema: "crm",
                table: "ContactPoints",
                columns: new[] { "TenantId", "LocationId", "Channel" },
                unique: true,
                filter: "[IsPrimary] = 1 AND [IsActive] = 1 AND [LocationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_CompanyId",
                schema: "crm",
                table: "Contacts",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_OwnerId",
                schema: "crm",
                table: "Contacts",
                columns: new[] { "TenantId", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_PersonId",
                schema: "crm",
                table: "Contacts",
                columns: new[] { "TenantId", "PersonId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomFields_TenantId_EntityKey_Key",
                schema: "meta",
                table: "CustomFields",
                columns: new[] { "TenantId", "EntityKey", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscountPolicies_TenantId_IsActive_Priority",
                schema: "crm",
                table: "DiscountPolicies",
                columns: new[] { "TenantId", "IsActive", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_Status_NextAttemptAt",
                schema: "platform",
                table: "EmailMessages",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_TenantId_CreatedAt",
                schema: "platform",
                table: "EmailMessages",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_TenantId_Reference",
                schema: "platform",
                table: "EmailMessages",
                columns: new[] { "TenantId", "Reference" });

            migrationBuilder.CreateIndex(
                name: "IX_EventDeliveries_EventId",
                schema: "platform",
                table: "EventDeliveries",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_EventDeliveries_Status_CompletedAt",
                schema: "platform",
                table: "EventDeliveries",
                columns: new[] { "Status", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EventDeliveries_Status_NextAttemptAt",
                schema: "platform",
                table: "EventDeliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EventDeliveries_TenantId_Subscription_EntityKey_RecordId_Sequence",
                schema: "platform",
                table: "EventDeliveries",
                columns: new[] { "TenantId", "Subscription", "EntityKey", "RecordId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_EventDeliveries_TenantId_Subscription_EventId",
                schema: "platform",
                table: "EventDeliveries",
                columns: new[] { "TenantId", "Subscription", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ExchangeRates_PairDay",
                schema: "crm",
                table: "ExchangeRates",
                columns: new[] { "TenantId", "FromCurrency", "ToCurrency", "ValidOn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadForms_TenantId_FormKey",
                schema: "crm",
                table: "LeadForms",
                columns: new[] { "TenantId", "FormKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_CompanyId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_ContactId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_ConvertedCompanyId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "ConvertedCompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_ConvertedContactId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "ConvertedContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_ConvertedOpportunityId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "ConvertedOpportunityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_ExternalSource_ExternalSubmissionId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "ExternalSource", "ExternalSubmissionId" },
                unique: true,
                filter: "[ExternalSource] IS NOT NULL AND [ExternalSubmissionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_FirstReceivedAt",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "FirstReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_LeadNumber",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "LeadNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_NormalizedEmail",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "NormalizedEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_NormalizedPhone",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "NormalizedPhone" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_TenantId_Status_OwnerId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "Status", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_TenantId_CompanyId",
                schema: "crm",
                table: "Locations",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_TenantId_ContactId",
                schema: "crm",
                table: "Locations",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "UX_Locations_CodePerCompany",
                schema: "crm",
                table: "Locations",
                columns: new[] { "TenantId", "CompanyId", "Code" },
                unique: true,
                filter: "[Code] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LookupSettings_TenantId_LookupKey",
                schema: "meta",
                table: "LookupSettings",
                columns: new[] { "TenantId", "LookupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_MeasureUnits_Code",
                schema: "crm",
                table: "MeasureUnits",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MembershipCompanies_BranchId",
                schema: "core",
                table: "MembershipCompanies",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_MembershipCompanies_CompanyId",
                schema: "core",
                table: "MembershipCompanies",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_MembershipCompanies_MembershipId_CompanyId",
                schema: "core",
                table: "MembershipCompanies",
                columns: new[] { "MembershipId", "CompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MembershipCompanies_TenantId_CompanyId",
                schema: "core",
                table: "MembershipCompanies",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipCompanies_TenantId_MembershipId",
                schema: "core",
                table: "MembershipCompanies",
                columns: new[] { "TenantId", "MembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipRoles_TenantId_MembershipId_RoleId",
                schema: "core",
                table: "MembershipRoles",
                columns: new[] { "TenantId", "MembershipId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MembershipRoles_TenantId_RoleId",
                schema: "core",
                table: "MembershipRoles",
                columns: new[] { "TenantId", "RoleId" });

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_TenantId_InvitedAt",
                schema: "platform",
                table: "Memberships",
                columns: new[] { "TenantId", "InvitedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_TenantId_UserId",
                schema: "platform",
                table: "Memberships",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_UserId",
                schema: "platform",
                table: "Memberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MergedRecords_TenantId_EntityKey_TargetId",
                schema: "crm",
                table: "MergedRecords",
                columns: new[] { "TenantId", "EntityKey", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "UX_MergedRecords_Source",
                schema: "crm",
                table: "MergedRecords",
                columns: new[] { "TenantId", "EntityKey", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModuleEntitlements_TenantId_ModuleKey",
                schema: "platform",
                table: "ModuleEntitlements",
                columns: new[] { "TenantId", "ModuleKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_CompanyId",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_ContactId",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_OwnerId",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "TenantId", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_PrimaryQuoteId",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "TenantId", "PrimaryQuoteId" });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_TenantId_Stage",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "TenantId", "Stage" });

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
                name: "UX_Opportunities_SourceLead",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "TenantId", "SourceLeadId" },
                unique: true,
                filter: "[SourceLeadId] IS NOT NULL");

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

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_DispatchedAt_OccurredAt",
                schema: "platform",
                table: "OutboxMessages",
                columns: new[] { "DispatchedAt", "OccurredAt" });

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

            migrationBuilder.CreateIndex(
                name: "IX_PriceListItems_TenantId_ProductId",
                schema: "crm",
                table: "PriceListItems",
                columns: new[] { "TenantId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "UX_PriceListItems_Tier",
                schema: "crm",
                table: "PriceListItems",
                columns: new[] { "TenantId", "PriceListId", "ProductId", "MinQuantity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_TenantId_CompanyId",
                schema: "crm",
                table: "PriceLists",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_TenantId_Status_Scope",
                schema: "crm",
                table: "PriceLists",
                columns: new[] { "TenantId", "Status", "Scope" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_TenantId_ParentId",
                schema: "crm",
                table: "ProductCategories",
                columns: new[] { "TenantId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductComponents_TenantId_ComponentId",
                schema: "crm",
                table: "ProductComponents",
                columns: new[] { "TenantId", "ComponentId" });

            migrationBuilder.CreateIndex(
                name: "UX_ProductComponents_Pair",
                schema: "crm",
                table: "ProductComponents",
                columns: new[] { "TenantId", "PackageId", "ComponentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_TenantId_BaseUnitId",
                schema: "crm",
                table: "Products",
                columns: new[] { "TenantId", "BaseUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_TenantId_CategoryId",
                schema: "crm",
                table: "Products",
                columns: new[] { "TenantId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_TenantId_Status_Name",
                schema: "crm",
                table: "Products",
                columns: new[] { "TenantId", "Status", "Name" });

            migrationBuilder.CreateIndex(
                name: "UX_Products_Code",
                schema: "crm",
                table: "Products",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuoteApprovalDelegations_TenantId_FromUserId_IsActive",
                schema: "crm",
                table: "QuoteApprovalDelegations",
                columns: new[] { "TenantId", "FromUserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteApprovalDelegations_TenantId_ToUserId_IsActive",
                schema: "crm",
                table: "QuoteApprovalDelegations",
                columns: new[] { "TenantId", "ToUserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteApprovalRules_TenantId_IsActive_Priority",
                schema: "crm",
                table: "QuoteApprovalRules",
                columns: new[] { "TenantId", "IsActive", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteDecisions_TenantId_DocumentId",
                schema: "crm",
                table: "QuoteDecisions",
                columns: new[] { "TenantId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteDecisions_TenantId_QuoteId_CreatedAt",
                schema: "crm",
                table: "QuoteDecisions",
                columns: new[] { "TenantId", "QuoteId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteDecisions_TenantId_ShareLinkId",
                schema: "crm",
                table: "QuoteDecisions",
                columns: new[] { "TenantId", "ShareLinkId" });

            migrationBuilder.CreateIndex(
                name: "UX_QuoteDocuments_Quote",
                schema: "crm",
                table: "QuoteDocuments",
                columns: new[] { "TenantId", "QuoteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuoteEvents_TenantId_QuoteId_CreatedAt",
                schema: "crm",
                table: "QuoteEvents",
                columns: new[] { "TenantId", "QuoteId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteLines_TenantId_ProductId",
                schema: "crm",
                table: "QuoteLines",
                columns: new[] { "TenantId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteLines_TenantId_QuoteId_Position",
                schema: "crm",
                table: "QuoteLines",
                columns: new[] { "TenantId", "QuoteId", "Position" });

            migrationBuilder.CreateIndex(
                name: "UX_QuoteLines_QuoteKey",
                schema: "crm",
                table: "QuoteLines",
                columns: new[] { "TenantId", "QuoteId", "StableKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_CompanyId",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_ContactId",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_OpportunityId",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "OpportunityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_OwnerId_Status",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "OwnerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_PriceListId",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "PriceListId" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_Status_ValidUntil",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "Status", "ValidUntil" });

            migrationBuilder.CreateIndex(
                name: "UX_Quotes_FamilyAccepted",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "FamilyId" },
                unique: true,
                filter: "[Status] = 'Accepted'");

            migrationBuilder.CreateIndex(
                name: "UX_Quotes_FamilyRevision",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "FamilyId", "RevisionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Quotes_NumberRevision",
                schema: "crm",
                table: "Quotes",
                columns: new[] { "TenantId", "QuoteNumber", "RevisionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuoteShareLinks_TenantId_DocumentId",
                schema: "crm",
                table: "QuoteShareLinks",
                columns: new[] { "TenantId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteShareLinks_TenantId_QuoteId_CreatedAt",
                schema: "crm",
                table: "QuoteShareLinks",
                columns: new[] { "TenantId", "QuoteId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_QuoteShareLinks_Token",
                schema: "crm",
                table: "QuoteShareLinks",
                columns: new[] { "TenantId", "TokenHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_TenantId_Name",
                schema: "core",
                table: "Roles",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_TenantId_SystemRole",
                schema: "core",
                table: "Roles",
                column: "TenantId",
                unique: true,
                filter: "[IsSystem] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_TaxRates_CodeFrom",
                schema: "crm",
                table: "TaxRates",
                columns: new[] { "TenantId", "Code", "ValidFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_TenantId_MembershipId",
                schema: "core",
                table: "TeamMembers",
                columns: new[] { "TenantId", "MembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamMembers_TenantId_TeamId_MembershipId",
                schema: "core",
                table: "TeamMembers",
                columns: new[] { "TenantId", "TeamId", "MembershipId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Teams_TenantId_Name",
                schema: "core",
                table: "Teams",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantLogos_TenantId",
                schema: "core",
                table: "TenantLogos",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                schema: "platform",
                table: "Tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_TenantId_CompanyId_Key",
                schema: "core",
                table: "TenantSettings",
                columns: new[] { "TenantId", "CompanyId", "Key" },
                unique: true,
                filter: "[CompanyId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_TenantId_Key",
                schema: "core",
                table: "TenantSettings",
                columns: new[] { "TenantId", "Key" },
                unique: true,
                filter: "[CompanyId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserClaims_UserId",
                schema: "platform",
                table: "UserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserLogins_UserId",
                schema: "platform",
                table: "UserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "platform",
                table: "Users",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "platform",
                table: "Users",
                column: "NormalizedUserName",
                unique: true,
                filter: "[NormalizedUserName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_UserId",
                schema: "platform",
                table: "UserSessions",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_Leads_TenantId_LeadId",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "LeadId" },
                principalSchema: "crm",
                principalTable: "Leads",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_Opportunities_TenantId_OpportunityId",
                schema: "crm",
                table: "Activities",
                columns: new[] { "TenantId", "OpportunityId" },
                principalSchema: "crm",
                principalTable: "Opportunities",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Leads_Opportunities_TenantId_ConvertedOpportunityId",
                schema: "crm",
                table: "Leads",
                columns: new[] { "TenantId", "ConvertedOpportunityId" },
                principalSchema: "crm",
                principalTable: "Opportunities",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
            // Row-Level Security (ADR-0002, ikinci katman; ADR-0020 tek baseline): politika ilk tabloyla oluşturulur, her tenant
            // tablosu eklenir. Yeni tenant tablosu kendi migration'ında TenantIsolationSql.AddTable ile girer.
            TenantIsolationSql.CreatePolicy(migrationBuilder, "core", "TenantSettings");
            foreach (var (schema, table) in TenantTables)
            {
                TenantIsolationSql.AddTable(migrationBuilder, schema, table);
            }

            // Denetim kaydı append-only: uygulama rolü core.AuditLog üzerinde UPDATE/DELETE yapamaz (rol yoksa, ör. tasarım zamanı, atlanır).
            migrationBuilder.Sql(
                "IF DATABASE_PRINCIPAL_ID(N'modulix_app_role') IS NOT NULL DENY UPDATE, DELETE ON [core].[AuditLog] TO [modulix_app_role];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.DropPolicy(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "FK_Contacts_Companies_TenantId_CompanyId",
                schema: "crm",
                table: "Contacts");

            migrationBuilder.DropForeignKey(
                name: "FK_Leads_Companies_TenantId_CompanyId",
                schema: "crm",
                table: "Leads");

            migrationBuilder.DropForeignKey(
                name: "FK_Leads_Companies_TenantId_ConvertedCompanyId",
                schema: "crm",
                table: "Leads");

            migrationBuilder.DropForeignKey(
                name: "FK_Opportunities_Companies_TenantId_CompanyId",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropForeignKey(
                name: "FK_Leads_Contacts_TenantId_ContactId",
                schema: "crm",
                table: "Leads");

            migrationBuilder.DropForeignKey(
                name: "FK_Leads_Contacts_TenantId_ConvertedContactId",
                schema: "crm",
                table: "Leads");

            migrationBuilder.DropForeignKey(
                name: "FK_Opportunities_Contacts_TenantId_ContactId",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropForeignKey(
                name: "FK_Opportunities_Leads_TenantId_SourceLeadId",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropTable(
                name: "ActivityLinks",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "ActivityParticipants",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Addresses",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "AuditLog",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ChoiceLists",
                schema: "meta");

            migrationBuilder.DropTable(
                name: "CompanyRelations",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "ContactPoints",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "CustomFields",
                schema: "meta");

            migrationBuilder.DropTable(
                name: "DiscountPolicies",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "EmailMessages",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "EventDeliveries",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "ExchangeRates",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "LeadForms",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "LookupSettings",
                schema: "meta");

            migrationBuilder.DropTable(
                name: "MembershipCompanies",
                schema: "core");

            migrationBuilder.DropTable(
                name: "MembershipRoles",
                schema: "core");

            migrationBuilder.DropTable(
                name: "MergedRecords",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "ModuleEntitlements",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "OpportunityCompetitors",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "OpportunityContacts",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "OpportunityHistory",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "PriceListItems",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "ProductComponents",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "QuoteApprovalDelegations",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "QuoteApprovalRules",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "QuoteDecisions",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "QuoteEvents",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "QuoteLines",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "TaxRates",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "TeamMembers",
                schema: "core");

            migrationBuilder.DropTable(
                name: "TenantLogos",
                schema: "core");

            migrationBuilder.DropTable(
                name: "TenantSettings",
                schema: "core");

            migrationBuilder.DropTable(
                name: "UserClaims",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "UserLogins",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "UserSessions",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "UserTokens",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "Activities",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "CompanyContacts",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Locations",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "Branches",
                schema: "core");

            migrationBuilder.DropTable(
                name: "Roles",
                schema: "core");

            migrationBuilder.DropTable(
                name: "QuoteShareLinks",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Products",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Memberships",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "Teams",
                schema: "core");

            migrationBuilder.DropTable(
                name: "Companies",
                schema: "core");

            migrationBuilder.DropTable(
                name: "QuoteDocuments",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "MeasureUnits",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "ProductCategories",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Tenants",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "Quotes",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "PriceLists",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Companies",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Contacts",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "People",
                schema: "core");

            migrationBuilder.DropTable(
                name: "Parties",
                schema: "core");

            migrationBuilder.DropTable(
                name: "Leads",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Opportunities",
                schema: "crm");
        }
    }
}
