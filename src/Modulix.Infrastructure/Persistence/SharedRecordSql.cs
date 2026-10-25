namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// CRM firma ve kişilerinin ortak kayıtlara (ADR-0006) taşınması; migration çalıştırır. Satırlar RLS ile korunduğu için
/// her tenant'ın oturum değeriyle ayrı çalışır. Taraf ve kişinin kimliği firmanın ve kişinin kimliğiyle aynıdır; böylece
/// kişinin çalıştığı taraf, CRM'deki firmasının kimliğidir.
/// </summary>
public static class SharedRecordSql
{
    /// <summary>Firmalar → core.Parties, kişiler → core.People; CRM satırları ortak kayda bağlanır.</summary>
    public static string MoveCrmToShared => ForEachTenant("""
        INSERT INTO [core].[Parties] ([Id], [TenantId], [Kind], [Name], [TaxNumber], [Phone], [Email], [Website], [City], [Address], [CreatedAt], [UpdatedAt], [Version])
        SELECT [Id], [TenantId], N'Organization', [Name], [TaxNumber], [Phone], [Email], [Website], [City], [Address], [CreatedAt], [UpdatedAt], 1
        FROM [crm].[Companies] WHERE [TenantId] = @TenantId;
        UPDATE [crm].[Companies] SET [PartyId] = [Id] WHERE [TenantId] = @TenantId;
        INSERT INTO [core].[People] ([Id], [TenantId], [FirstName], [LastName], [Email], [Phone], [Title], [PartyId], [CreatedAt], [UpdatedAt], [Version])
        SELECT [Id], [TenantId], [FirstName], [LastName], [Email], [Phone], [Title], [CompanyId], [CreatedAt], [UpdatedAt], 1
        FROM [crm].[Contacts] WHERE [TenantId] = @TenantId;
        UPDATE [crm].[Contacts] SET [PersonId] = [Id] WHERE [TenantId] = @TenantId;
        """);

    /// <summary>Geri alma: ortak kaydın alanları CRM satırlarına kopyalanır.</summary>
    public static string MoveSharedToCrm => ForEachTenant("""
        UPDATE c SET [Name] = p.[Name], [TaxNumber] = p.[TaxNumber], [Phone] = p.[Phone], [Email] = p.[Email],
            [Website] = p.[Website], [City] = p.[City], [Address] = p.[Address]
        FROM [crm].[Companies] c JOIN [core].[Parties] p ON p.[TenantId] = c.[TenantId] AND p.[Id] = c.[PartyId]
        WHERE c.[TenantId] = @TenantId;
        UPDATE c SET [FirstName] = p.[FirstName], [LastName] = p.[LastName], [Email] = p.[Email], [Phone] = p.[Phone], [Title] = p.[Title]
        FROM [crm].[Contacts] c JOIN [core].[People] p ON p.[TenantId] = c.[TenantId] AND p.[Id] = c.[PersonId]
        WHERE c.[TenantId] = @TenantId;
        """);

    private static string ForEachTenant(string statements) => $"""
        DECLARE @TenantId uniqueidentifier;
        DECLARE tenant_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT [Id] FROM [platform].[Tenants];
        OPEN tenant_cursor;
        FETCH NEXT FROM tenant_cursor INTO @TenantId;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = @TenantId;
            {statements}
            FETCH NEXT FROM tenant_cursor INTO @TenantId;
        END;
        CLOSE tenant_cursor;
        DEALLOCATE tenant_cursor;
        EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = NULL;
        """;
}
