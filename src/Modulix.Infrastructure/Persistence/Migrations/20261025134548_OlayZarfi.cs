using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OlayZarfi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ProcessedAt",
                schema: "platform",
                table: "OutboxMessages",
                newName: "DispatchedAt");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                schema: "platform",
                table: "OutboxMessages");

            migrationBuilder.AddColumn<int>(
                name: "Depth",
                schema: "platform",
                table: "OutboxMessages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.RenameIndex(
                name: "IX_OutboxMessages_ProcessedAt_OccurredAt",
                schema: "platform",
                table: "OutboxMessages",
                newName: "IX_OutboxMessages_DispatchedAt_OccurredAt");

            migrationBuilder.AddColumn<Guid>(
                name: "CausationId",
                schema: "platform",
                table: "OutboxMessages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                schema: "platform",
                table: "OutboxMessages",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                schema: "platform",
                table: "OutboxMessages",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SchemaVersion",
                schema: "platform",
                table: "OutboxMessages",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                schema: "platform",
                table: "OutboxMessages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                schema: "platform",
                table: "OutboxMessages",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                schema: "platform",
                table: "OutboxMessages",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Elle eklendi (ADR-0007, geçiş): o ana kadar yazılmış olaylar abonelere dağıtılmaz; yoksa ilk açılışta geçmiş
            // atamalar için e-posta gider. Mevcut satırlar sürüm 1, sıra numarası boş (sırasız) kalır.
            migrationBuilder.Sql("UPDATE [platform].[OutboxMessages] SET [DispatchedAt] = SYSDATETIMEOFFSET() WHERE [DispatchedAt] IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CausationId",
                schema: "platform",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                schema: "platform",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "Reason",
                schema: "platform",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "SchemaVersion",
                schema: "platform",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "Sequence",
                schema: "platform",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "platform",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "UserId",
                schema: "platform",
                table: "OutboxMessages");

            migrationBuilder.RenameColumn(
                name: "DispatchedAt",
                schema: "platform",
                table: "OutboxMessages",
                newName: "ProcessedAt");

            migrationBuilder.DropColumn(
                name: "Depth",
                schema: "platform",
                table: "OutboxMessages");

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                schema: "platform",
                table: "OutboxMessages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.RenameIndex(
                name: "IX_OutboxMessages_DispatchedAt_OccurredAt",
                schema: "platform",
                table: "OutboxMessages",
                newName: "IX_OutboxMessages_ProcessedAt_OccurredAt");
        }
    }
}
