using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations.Tenant
{
    /// <summary>
    /// Denetim kaydı append-only: uygulama rolü (modulix_app_role) core.AuditLog üzerinde UPDATE/DELETE yapamaz (ADR-0020).
    /// Tek veritabanında bu yasak tenant DB provisioning SQL'inden buraya taşındı; rol yoksa (ör. tasarım zamanı) atlanır.
    /// </summary>
    public partial class AuditLogAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                "IF DATABASE_PRINCIPAL_ID(N'modulix_app_role') IS NOT NULL DENY UPDATE, DELETE ON [core].[AuditLog] TO [modulix_app_role];");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                "IF DATABASE_PRINCIPAL_ID(N'modulix_app_role') IS NOT NULL REVOKE UPDATE, DELETE ON [core].[AuditLog] FROM [modulix_app_role];");
    }
}
