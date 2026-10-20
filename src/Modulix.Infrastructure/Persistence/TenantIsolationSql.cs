using Microsoft.EntityFrameworkCore.Migrations;

namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// SQL Server Row-Level Security (ADR-0002, ikinci katman). Migration'larda kullanılır.
///
/// Uygulama her bağlantı açılışında aktif tenant'ı salt okunur oturum değeri olarak yazar
/// (<see cref="TenantSessionContextInterceptor"/>). Politika, satırın TenantId'si bu değere
/// eşit değilse satırı gizler (FILTER) ve başka tenant'a yazmayı reddeder (BLOCK).
/// Oturum değeri yoksa hiçbir tenant satırı görünmez.
///
/// Uygulama kullanıcısı (modulix_app) politikayı değiştiremez veya kapatamaz.
/// Not: Politikaya eklenen tablonun TenantId kolonu değiştirilecekse önce <see cref="RemoveTable"/>.
/// </summary>
public static class TenantIsolationSql
{
    public const string SessionKey = "TenantId";
    public const string PolicyName = "[core].[TenantIsolationPolicy]";
    public const string PredicateFunction = "[core].[fn_TenantAccessPredicate]";

    /// <summary>Politikayı ve ilk korunan tabloyu oluşturur (politika en az bir tabloyla oluşturulabilir).</summary>
    public static void CreatePolicy(MigrationBuilder migrationBuilder, string firstSchema, string firstTable)
    {
        migrationBuilder.Sql($"""
            CREATE FUNCTION {PredicateFunction}(@TenantId uniqueidentifier)
            RETURNS TABLE
            WITH SCHEMABINDING
            AS
            RETURN SELECT 1 AS AccessResult
            WHERE @TenantId = CAST(SESSION_CONTEXT(N'{SessionKey}') AS uniqueidentifier);
            """);

        migrationBuilder.Sql($"""
            CREATE SECURITY POLICY {PolicyName}
                {Predicates(firstSchema, firstTable)}
            WITH (STATE = ON, SCHEMABINDING = ON);
            """);
    }

    /// <summary>Yeni bir tenant tablosunu politikaya ekler. Her ITenantEntity tablosu için çağrılır.</summary>
    public static void AddTable(MigrationBuilder migrationBuilder, string schema, string table) =>
        migrationBuilder.Sql($"""
            ALTER SECURITY POLICY {PolicyName}
                {Predicates(schema, table)};
            """);

    public static void RemoveTable(MigrationBuilder migrationBuilder, string schema, string table) =>
        migrationBuilder.Sql($"""
            ALTER SECURITY POLICY {PolicyName}
                DROP FILTER PREDICATE ON {Name(schema, table)},
                DROP BLOCK PREDICATE ON {Name(schema, table)} AFTER INSERT,
                DROP BLOCK PREDICATE ON {Name(schema, table)} AFTER UPDATE;
            """);

    public static void DropPolicy(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"DROP SECURITY POLICY IF EXISTS {PolicyName};");
        migrationBuilder.Sql($"DROP FUNCTION IF EXISTS {PredicateFunction};");
    }

    private static string Predicates(string schema, string table) => $"""
        ADD FILTER PREDICATE {PredicateFunction}([TenantId]) ON {Name(schema, table)},
                ADD BLOCK PREDICATE {PredicateFunction}([TenantId]) ON {Name(schema, table)} AFTER INSERT,
                ADD BLOCK PREDICATE {PredicateFunction}([TenantId]) ON {Name(schema, table)} AFTER UPDATE
        """;

    private static string Name(string schema, string table) => $"[{schema}].[{table}]";
}
