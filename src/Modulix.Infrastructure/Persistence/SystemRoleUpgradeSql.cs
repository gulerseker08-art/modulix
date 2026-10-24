namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// SystemRole migration'ının veri adımı (Y1). Her tenant için:
/// <list type="number">
///   <item>Sistem rolü yoksa "Yönetici" adlı eski rol sistem rolüne çevrilir (izin listesi boşaltılır; sistem rolü tüm izinleri verir).</item>
///   <item>Hâlâ sistem rolü yoksa bir tane oluşturulur (üye atanmaz; tenant'ın yöneticisi yoksa korunacak kimse de yoktur).</item>
///   <item><c>core.roles.manage</c> izni olan rollere yeni yönetim izinleri eklenir; bugünkü yöneticiler yetki kaybetmez.</item>
/// </list>
/// Tablolar Row-Level Security altında olduğu için her tenant'ın oturum değeri sırayla yazılır; politika kapatılmaz.
/// Tekrar çalıştırmak güvenlidir.
/// </summary>
public static class SystemRoleUpgradeSql
{
    /// <summary>Tek tenant için; <c>@TenantId</c> değişkeni tanımlı ve oturum değeri o tenant'a ayarlı olmalıdır.</summary>
    public const string ForTenant = """
        IF NOT EXISTS (SELECT 1 FROM [core].[Roles] WHERE [TenantId] = @TenantId AND [IsSystem] = 1)
            UPDATE [core].[Roles] SET [IsSystem] = 1, [Permissions] = N'[]'
            WHERE [TenantId] = @TenantId AND [Name] = N'Yönetici';

        IF NOT EXISTS (SELECT 1 FROM [core].[Roles] WHERE [TenantId] = @TenantId AND [IsSystem] = 1)
            INSERT INTO [core].[Roles] ([Id], [TenantId], [Name], [Permissions], [CreatedAt], [IsSystem])
            VALUES (NEWID(), @TenantId, N'Yönetici', N'[]', SYSDATETIMEOFFSET(), 1);

        UPDATE r SET [Permissions] = (
            SELECT N'[' + STRING_AGG(CAST(N'"' + STRING_ESCAPE(p.[Key], 'json') + N'"' AS nvarchar(max)), N',')
                WITHIN GROUP (ORDER BY p.[Key] COLLATE Latin1_General_BIN2) + N']'
            FROM (
                SELECT j.[value] AS [Key] FROM OPENJSON(r.[Permissions]) AS j
                UNION
                SELECT n.[Key] FROM (VALUES (N'core.users.manage'), (N'core.settings.manage'), (N'core.audit.read')) AS n([Key])
            ) AS p)
        FROM [core].[Roles] AS r
        WHERE r.[TenantId] = @TenantId
            AND r.[IsSystem] = 0
            AND EXISTS (SELECT 1 FROM OPENJSON(r.[Permissions]) AS j WHERE j.[value] = N'core.roles.manage');
        """;

    /// <summary>Tüm tenant'lar; migration çalıştırır.</summary>
    public const string AllTenants = $"""
        DECLARE @TenantId uniqueidentifier;
        DECLARE tenant_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT [Id] FROM [platform].[Tenants];
        OPEN tenant_cursor;
        FETCH NEXT FROM tenant_cursor INTO @TenantId;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = @TenantId;
            {ForTenant}
            FETCH NEXT FROM tenant_cursor INTO @TenantId;
        END
        CLOSE tenant_cursor;
        DEALLOCATE tenant_cursor;
        EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = NULL;
        """;
}
