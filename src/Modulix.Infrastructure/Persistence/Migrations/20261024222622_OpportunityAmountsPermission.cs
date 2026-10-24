using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OpportunityAmountsPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Y8b: fırsat tutarı ayrı izne taşındı; bugün fırsatları gören roller tutarı görmeye devam eder.
            migrationBuilder.Sql(PermissionGrantSql.GrantWhereGranted("crm.opportunity-amounts.read", "crm.opportunities.read"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Geri alınmaz: izin rolde kalırsa zararsızdır (katalogda yoksa yok sayılır).
        }
    }
}
