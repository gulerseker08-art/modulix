using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HrAnonymousSurveyAnswers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Kimliği gizli anketin cevabı katılıma bağlanmaz (ADR-0042 bölüm 24): ResponseId boş olabilir. Kolon indeksli ve yabancı anahtarlı
            // olduğu için önce ikisi düşürülür, sonra yeniden kurulur.
            migrationBuilder.DropForeignKey(
                name: "FK_SurveyAnswers_SurveyResponses_TenantId_ResponseId",
                schema: "hr",
                table: "SurveyAnswers");

            migrationBuilder.DropIndex(
                name: "IX_SurveyAnswers_TenantId_ResponseId",
                schema: "hr",
                table: "SurveyAnswers");

            migrationBuilder.AlterColumn<Guid>(
                name: "ResponseId",
                schema: "hr",
                table: "SurveyAnswers",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.CreateIndex(
                name: "IX_SurveyAnswers_TenantId_ResponseId",
                schema: "hr",
                table: "SurveyAnswers",
                columns: new[] { "TenantId", "ResponseId" });

            migrationBuilder.AddForeignKey(
                name: "FK_SurveyAnswers_SurveyResponses_TenantId_ResponseId",
                schema: "hr",
                table: "SurveyAnswers",
                columns: new[] { "TenantId", "ResponseId" },
                principalSchema: "hr",
                principalTable: "SurveyResponses",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddColumn<Guid>(
                name: "BallotId",
                schema: "hr",
                table: "SurveyAnswers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Geri doldurma: pusula kimliği kimliği açık ankette katılımın kimliğidir. Kimliği gizli anketlerin mevcut cevapları katılımdan koparılır
            // (yanıt başına yeni rastgele pusula; NEWID yanıt başına bir kez üretilsin diye geçici tabloda). Eski denetim satırları değişmez (denetim
            // kaydı değiştirilmez); bu yüzden bu cevaplar tam anonim değildir, ADR'de yazılı. Migration oturumunda tenant değeri yoktur; RLS açıkken
            // UPDATE sessizce 0 satır etkiler, bu yüzden politika geçici kapatılır. Yinelenebilir.
            migrationBuilder.Sql("ALTER SECURITY POLICY [core].[TenantIsolationPolicy] WITH (STATE = OFF);");

            migrationBuilder.Sql(@"
UPDATE a SET a.[BallotId] = a.[ResponseId]
FROM [hr].[SurveyAnswers] a
WHERE a.[BallotId] = '00000000-0000-0000-0000-000000000000' AND a.[ResponseId] IS NOT NULL;");

            migrationBuilder.Sql(@"
SELECT r.[Id] AS [ResponseId], NEWID() AS [BallotId]
INTO #Ballots
FROM [hr].[SurveyResponses] r
JOIN [hr].[Surveys] s ON s.[TenantId] = r.[TenantId] AND s.[Id] = r.[SurveyId]
WHERE s.[IsAnonymous] = 1;

UPDATE a SET a.[BallotId] = b.[BallotId], a.[ResponseId] = NULL
FROM [hr].[SurveyAnswers] a
JOIN #Ballots b ON b.[ResponseId] = a.[ResponseId];

DROP TABLE #Ballots;");

            migrationBuilder.Sql("ALTER SECURITY POLICY [core].[TenantIsolationPolicy] WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Kimliği gizli anketin cevabını katılıma yeniden bağlayacak bilgi yoktur (bilerek tutulmaz); ResponseId boş olabilir kalır, yalnızca
            // pusula kolonu kaldırılır.
            migrationBuilder.DropColumn(
                name: "BallotId",
                schema: "hr",
                table: "SurveyAnswers");
        }
    }
}
