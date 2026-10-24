namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// Yeni bir izni, belirli bir izni zaten veren rollere ekleyen veri adımı (ör. Y8b: fırsatları görenler tutarı da görmeye
/// devam eder). Sistem rolü tüm izinleri zaten verir, dokunulmaz. Tablolar Row-Level Security altında olduğu için her
/// tenant'ın oturum değeri sırayla yazılır (<see cref="SystemRoleUpgradeSql"/> gibi). Tekrar çalıştırmak güvenlidir.
/// </summary>
public static class PermissionGrantSql
{
    /// <summary>Tüm tenant'lar; migration çalıştırır. İzin anahtarları sabittir (kullanıcı girdisi değildir).</summary>
    public static string GrantWhereGranted(string permission, string whereGranted)
    {
        if (!Core.Modules.AccessKeys.IsValidPermissionKey(permission) || !Core.Modules.AccessKeys.IsValidPermissionKey(whereGranted))
        {
            throw new ArgumentException("Geçersiz izin anahtarı.");
        }

        return $"""
            DECLARE @TenantId uniqueidentifier;
            DECLARE tenant_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT [Id] FROM [platform].[Tenants];
            OPEN tenant_cursor;
            FETCH NEXT FROM tenant_cursor INTO @TenantId;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = @TenantId;
                UPDATE r SET [Permissions] = (
                    SELECT N'[' + STRING_AGG(CAST(N'"' + STRING_ESCAPE(p.[Key], 'json') + N'"' AS nvarchar(max)), N',')
                        WITHIN GROUP (ORDER BY p.[Key] COLLATE Latin1_General_BIN2) + N']'
                    FROM (
                        SELECT j.[value] AS [Key] FROM OPENJSON(r.[Permissions]) AS j
                        UNION
                        SELECT N'{permission}'
                    ) AS p)
                FROM [core].[Roles] AS r
                WHERE r.[TenantId] = @TenantId
                    AND r.[IsSystem] = 0
                    AND EXISTS (SELECT 1 FROM OPENJSON(r.[Permissions]) AS j WHERE j.[value] = N'{whereGranted}')
                    AND NOT EXISTS (SELECT 1 FROM OPENJSON(r.[Permissions]) AS j WHERE j.[value] = N'{permission}');
                FETCH NEXT FROM tenant_cursor INTO @TenantId;
            END
            CLOSE tenant_cursor;
            DEALLOCATE tenant_cursor;
            EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = NULL;
            """;
    }
}
