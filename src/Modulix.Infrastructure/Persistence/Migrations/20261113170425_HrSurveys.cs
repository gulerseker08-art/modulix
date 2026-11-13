using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrSurveys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Surveys",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsAnonymous = table.Column<bool>(type: "bit", nullable: false),
                    AudienceDepartmentKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ClosesOn = table.Column<DateOnly>(type: "date", nullable: true),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Surveys", x => x.Id);
                    table.UniqueConstraint("AK_Surveys_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "SurveyQuestions",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SurveyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    QuestionType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Options = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyQuestions", x => x.Id);
                    table.UniqueConstraint("AK_SurveyQuestions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_SurveyQuestions_Surveys_TenantId_SurveyId",
                        columns: x => new { x.TenantId, x.SurveyId },
                        principalSchema: "hr",
                        principalTable: "Surveys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SurveyResponses",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SurveyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RespondentUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyResponses", x => x.Id);
                    table.UniqueConstraint("AK_SurveyResponses_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_SurveyResponses_Surveys_TenantId_SurveyId",
                        columns: x => new { x.TenantId, x.SurveyId },
                        principalSchema: "hr",
                        principalTable: "Surveys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SurveyAnswers",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResponseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SurveyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rating = table.Column<int>(type: "int", nullable: true),
                    Choice = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TextValue = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CustomFields = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyAnswers", x => x.Id);
                    table.UniqueConstraint("AK_SurveyAnswers_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_SurveyAnswers_SurveyQuestions_TenantId_QuestionId",
                        columns: x => new { x.TenantId, x.QuestionId },
                        principalSchema: "hr",
                        principalTable: "SurveyQuestions",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SurveyAnswers_SurveyResponses_TenantId_ResponseId",
                        columns: x => new { x.TenantId, x.ResponseId },
                        principalSchema: "hr",
                        principalTable: "SurveyResponses",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SurveyAnswers_Question",
                schema: "hr",
                table: "SurveyAnswers",
                columns: new[] { "TenantId", "SurveyId", "QuestionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SurveyAnswers_TenantId_QuestionId",
                schema: "hr",
                table: "SurveyAnswers",
                columns: new[] { "TenantId", "QuestionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SurveyAnswers_TenantId_ResponseId",
                schema: "hr",
                table: "SurveyAnswers",
                columns: new[] { "TenantId", "ResponseId" });

            migrationBuilder.CreateIndex(
                name: "IX_SurveyQuestions_TenantId_SurveyId",
                schema: "hr",
                table: "SurveyQuestions",
                columns: new[] { "TenantId", "SurveyId" });

            migrationBuilder.CreateIndex(
                name: "UX_SurveyResponses_Respondent",
                schema: "hr",
                table: "SurveyResponses",
                columns: new[] { "TenantId", "SurveyId", "RespondentUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Surveys_Status",
                schema: "hr",
                table: "Surveys",
                columns: new[] { "TenantId", "Status" });

            // Yeni tenant tabloları Row-Level Security politikasına girer (ADR-0002/0020, ikinci katman).
            TenantIsolationSql.AddTable(migrationBuilder, "hr", "Surveys");
            TenantIsolationSql.AddTable(migrationBuilder, "hr", "SurveyQuestions");
            TenantIsolationSql.AddTable(migrationBuilder, "hr", "SurveyResponses");
            TenantIsolationSql.AddTable(migrationBuilder, "hr", "SurveyAnswers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantIsolationSql.RemoveTable(migrationBuilder, "hr", "SurveyAnswers");
            TenantIsolationSql.RemoveTable(migrationBuilder, "hr", "SurveyResponses");
            TenantIsolationSql.RemoveTable(migrationBuilder, "hr", "SurveyQuestions");
            TenantIsolationSql.RemoveTable(migrationBuilder, "hr", "Surveys");

            migrationBuilder.DropTable(
                name: "SurveyAnswers",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "SurveyQuestions",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "SurveyResponses",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "Surveys",
                schema: "hr");
        }
    }
}
