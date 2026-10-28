using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Modulix.Infrastructure.Persistence;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class CustomerDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            foreach (var table in new[] { "Locations", "Addresses", "ContactPoints", "CompanyRelations" })
            {
                TenantIsolationSql.AddTable(migrationBuilder, "crm", table);
            }

            // Backfill (ADR-0012): eski tekil alanlar yapılandırılmış ana kayıtlara kopyalanır; eski alanlar olduğu gibi kalır
            // (artık ana kaydın aynasıdır). Orijinal metin Value'da korunur, NormalizedValue yalnızca arama içindir.
            // RLS politikası oturumda tenant değeri ister; migration tüm satırlara dokunduğu için yalnızca bu deyimler boyunca kapatılır.
            migrationBuilder.Sql($"ALTER SECURITY POLICY {TenantIsolationSql.PolicyName} WITH (STATE = OFF);");

            foreach (var (channel, column, type) in new[] { ("Phone", "Phone", "Switchboard"), ("Email", "Email", "Work") })
            {
                var normalized = channel == "Phone"
                    ? @"CASE WHEN LEFT(x.[d], 1) = '+' THEN x.[d]
                            WHEN LEFT(x.[d], 2) = '00' THEN '+' + SUBSTRING(x.[d], 3, 50)
                            WHEN LEN(x.[d]) = 11 AND LEFT(x.[d], 1) = '0' THEN '+90' + SUBSTRING(x.[d], 2, 50)
                            WHEN LEN(x.[d]) = 10 AND LEFT(x.[d], 1) <> '0' THEN '+90' + x.[d]
                            WHEN LEN(x.[d]) = 12 AND LEFT(x.[d], 2) = '90' THEN '+' + x.[d]
                            ELSE x.[d] END"
                    : "x.[d]";
                var digits = channel == "Phone"
                    ? $"REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(s.[{column}])), ' ', ''), '(', ''), ')', ''), '-', ''), '.', ''), '/', '')"
                    : $"LOWER(LTRIM(RTRIM(s.[{column}])))";

                // Firma (taraf) -> firma düzeyinde paylaşılan ana nokta
                migrationBuilder.Sql($@"
                    INSERT INTO [crm].[ContactPoints]
                        ([Id], [TenantId], [CreatedAt], [UpdatedAt], [Version], [CustomFields], [CompanyId], [LocationId], [ContactId], [CompanyContactId],
                         [Channel], [Type], [Value], [NormalizedValue], [IsPrimary], [IsActive], [IsShared], [Note])
                    SELECT NEWID(), c.[TenantId], SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), 1, NULL, c.[Id], NULL, NULL, NULL,
                           N'{channel}', N'{type}', LTRIM(RTRIM(s.[{column}])), {normalized}, 1, 1, 1, NULL
                    FROM [crm].[Companies] c
                    JOIN [core].[Parties] s ON s.[TenantId] = c.[TenantId] AND s.[Id] = c.[PartyId]
                    CROSS APPLY (SELECT [d] = {digits}) x
                    WHERE LTRIM(RTRIM(ISNULL(s.[{column}], N''))) <> N'';");

                // Kişi (ortak kişi) -> kişinin genel ana noktası
                migrationBuilder.Sql($@"
                    INSERT INTO [crm].[ContactPoints]
                        ([Id], [TenantId], [CreatedAt], [UpdatedAt], [Version], [CustomFields], [CompanyId], [LocationId], [ContactId], [CompanyContactId],
                         [Channel], [Type], [Value], [NormalizedValue], [IsPrimary], [IsActive], [IsShared], [Note])
                    SELECT NEWID(), c.[TenantId], SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), 1, NULL, NULL, NULL, c.[Id], NULL,
                           N'{channel}', N'Work', LTRIM(RTRIM(s.[{column}])), {normalized}, 1, 1, 0, NULL
                    FROM [crm].[Contacts] c
                    JOIN [core].[People] s ON s.[TenantId] = c.[TenantId] AND s.[Id] = c.[PersonId]
                    CROSS APPLY (SELECT [d] = {digits}) x
                    WHERE LTRIM(RTRIM(ISNULL(s.[{column}], N''))) <> N'';");
            }

            // Taraf adresi/şehri -> firma düzeyinde ana adres (Line1 = eski adres metni; alt alanlar sonradan ayrıştırılır)
            migrationBuilder.Sql(@"
                INSERT INTO [crm].[Addresses]
                    ([Id], [TenantId], [CreatedAt], [UpdatedAt], [Version], [CustomFields], [CompanyId], [LocationId], [Type], [IsPrimary], [IsActive],
                     [Country], [City], [District], [PostalCode], [Line1], [Line2], [NormalizedText])
                SELECT NEWID(), c.[TenantId], SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), 1, NULL, c.[Id], NULL, N'Office', 1, 1,
                       'TR', NULLIF(LTRIM(RTRIM(s.[City])), N''), NULL, NULL, ISNULL(LTRIM(RTRIM(s.[Address])), N''), NULL,
                       TRANSLATE(LOWER(CONCAT_WS(N' ', NULLIF(LTRIM(RTRIM(s.[Address])), N''), NULLIF(LTRIM(RTRIM(s.[City])), N''), N'TR')),
                                 N'ıİşğüöç', N'iisguoc')
                FROM [crm].[Companies] c
                JOIN [core].[Parties] s ON s.[TenantId] = c.[TenantId] AND s.[Id] = c.[PartyId]
                WHERE LTRIM(RTRIM(ISNULL(s.[Address], N''))) <> N'' OR LTRIM(RTRIM(ISNULL(s.[City], N''))) <> N'';");

            // Veri kaybı kontrolü: dolu her eski alan için bir ana kayıt olmalı; değilse migration geri alınır.
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM [crm].[Companies] c JOIN [core].[Parties] s ON s.[TenantId] = c.[TenantId] AND s.[Id] = c.[PartyId]
                           WHERE (LTRIM(RTRIM(ISNULL(s.[Phone], N''))) <> N'' AND NOT EXISTS (SELECT 1 FROM [crm].[ContactPoints] p WHERE p.[CompanyId] = c.[Id] AND p.[Channel] = N'Phone' AND p.[IsPrimary] = 1))
                              OR (LTRIM(RTRIM(ISNULL(s.[Email], N''))) <> N'' AND NOT EXISTS (SELECT 1 FROM [crm].[ContactPoints] p WHERE p.[CompanyId] = c.[Id] AND p.[Channel] = N'Email' AND p.[IsPrimary] = 1))
                              OR ((LTRIM(RTRIM(ISNULL(s.[Address], N''))) <> N'' OR LTRIM(RTRIM(ISNULL(s.[City], N''))) <> N'')
                                  AND NOT EXISTS (SELECT 1 FROM [crm].[Addresses] a WHERE a.[CompanyId] = c.[Id] AND a.[IsPrimary] = 1)))
                    THROW 51001, N'CustomerDirectory backfill eksik: dolu bir firma iletişim/adres alanı için ana kayıt oluşmadı.', 1;");
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM [crm].[Contacts] c JOIN [core].[People] s ON s.[TenantId] = c.[TenantId] AND s.[Id] = c.[PersonId]
                           WHERE (LTRIM(RTRIM(ISNULL(s.[Phone], N''))) <> N'' AND NOT EXISTS (SELECT 1 FROM [crm].[ContactPoints] p WHERE p.[ContactId] = c.[Id] AND p.[Channel] = N'Phone' AND p.[IsPrimary] = 1))
                              OR (LTRIM(RTRIM(ISNULL(s.[Email], N''))) <> N'' AND NOT EXISTS (SELECT 1 FROM [crm].[ContactPoints] p WHERE p.[ContactId] = c.[Id] AND p.[Channel] = N'Email' AND p.[IsPrimary] = 1)))
                    THROW 51002, N'CustomerDirectory backfill eksik: dolu bir kişi iletişim alanı için ana kayıt oluşmadı.', 1;");
            migrationBuilder.Sql($"ALTER SECURITY POLICY {TenantIsolationSql.PolicyName} WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "CompanyRelations", "ContactPoints", "Addresses", "Locations" })
            {
                TenantIsolationSql.RemoveTable(migrationBuilder, "crm", table);
            }

            migrationBuilder.DropTable(
                name: "Addresses",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "CompanyRelations",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "ContactPoints",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Locations",
                schema: "crm");
        }
    }
}
