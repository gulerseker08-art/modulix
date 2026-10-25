namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// UyelikSirketBagi migration'ının veri adımı (ADR-0008, adım 2). Mevcut her etkin üyeliğe tenant'ının varsayılan
/// şirketini verir; böylece adım 3'te şirket kapsamlı tablolar gelince tek şirketli tenant'larda kimse erişim
/// kaybetmez (ADR-0008 geçiş md.4). Yöneticiler zaten bütün şirketlere erişir; bağ yalnızca kayıt bütünlüğü içindir.
/// Tablo Row-Level Security altında olduğu için her tenant'ın oturum değeri sırayla yazılır. Tekrar çalıştırmak güvenlidir.
/// </summary>
public static class MembershipCompanyBackfillSql
{
    /// <summary><see cref="Modulix.Core.Platform.MembershipStatus.Active"/> değeridir (sabit string ilişkilendirmesi için metin).</summary>
    private const string ActiveMembership = "1";

    public const string AllTenants = $"""
        DECLARE @TenantId uniqueidentifier;
        DECLARE @CompanyId uniqueidentifier;
        DECLARE tenant_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT [Id] FROM [platform].[Tenants];
        OPEN tenant_cursor;
        FETCH NEXT FROM tenant_cursor INTO @TenantId;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = @TenantId;
            SET @CompanyId = (SELECT TOP 1 [Id] FROM [core].[Companies] WHERE [TenantId] = @TenantId AND [IsDefault] = 1);
            IF @CompanyId IS NOT NULL
                INSERT INTO [core].[MembershipCompanies] ([Id], [TenantId], [MembershipId], [CompanyId], [CreatedAt])
                SELECT NEWID(), @TenantId, m.[Id], @CompanyId, SYSDATETIMEOFFSET()
                FROM [platform].[Memberships] m
                WHERE m.[TenantId] = @TenantId
                      AND m.[Status] = {ActiveMembership}
                      AND NOT EXISTS (
                          SELECT 1 FROM [core].[MembershipCompanies] mc
                          WHERE mc.[MembershipId] = m.[Id] AND mc.[CompanyId] = @CompanyId);
            FETCH NEXT FROM tenant_cursor INTO @TenantId;
        END
        CLOSE tenant_cursor;
        DEALLOCATE tenant_cursor;
        EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = NULL;
        """;
}
