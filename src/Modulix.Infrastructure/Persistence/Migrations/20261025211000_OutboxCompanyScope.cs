using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modulix.Infrastructure.Persistence;

#nullable disable

namespace Modulix.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ModulixDbContext))]
[Migration("20261025211000_OutboxCompanyScope")]
public sealed class OutboxCompanyScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "CompanyId",
            schema: "platform",
            table: "OutboxMessages",
            type: "uniqueidentifier",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CompanyId",
            schema: "platform",
            table: "OutboxMessages");
    }
}
