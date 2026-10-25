namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// SirketVeSube migration'ının veri adımı (ADR-0008). Şirketi olmayan her tenant için tenant adıyla varsayılan şirket açar.
/// Tablo Row-Level Security altında olduğu için her tenant'ın oturum değeri sırayla yazılır; politika kapatılmaz.
/// Tekrar çalıştırmak güvenlidir.
/// </summary>
public static class DefaultCompanyUpgradeSql
{
    public const string AllTenants = $"""
        DECLARE @TenantId uniqueidentifier;
        DECLARE @TenantName nvarchar(200);
        DECLARE tenant_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT [Id], LEFT([Name], 200) FROM [platform].[Tenants];
        OPEN tenant_cursor;
        FETCH NEXT FROM tenant_cursor INTO @TenantId, @TenantName;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = @TenantId;
            IF NOT EXISTS (SELECT 1 FROM [core].[Companies] WHERE [TenantId] = @TenantId)
                INSERT INTO [core].[Companies] ([Id], [TenantId], [Name], [IsDefault], [CreatedAt])
                VALUES (NEWID(), @TenantId, @TenantName, 1, SYSDATETIMEOFFSET());
            FETCH NEXT FROM tenant_cursor INTO @TenantId, @TenantName;
        END
        CLOSE tenant_cursor;
        DEALLOCATE tenant_cursor;
        EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = NULL;
        """;
}
