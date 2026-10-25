namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// Seçim alanlarının (Y9b) sayıdan metin anahtara geçişi; migration çalıştırır. Satırlar RLS ile korunduğu için güncelleme
/// her tenant'ın oturum değeriyle ayrı yapılır. Tablo, kolon ve değerler sabittir (kullanıcı girdisi değildir).
/// </summary>
public static class ChoiceKeySql
{
    /// <summary>Kolondaki her değeri eşine çevirir (ör. "3" → "Proposal"); listede olmayan değer <paramref name="otherwise"/> olur (verilmezse kalır).</summary>
    public static string Map(string schema, string table, string column, IReadOnlyList<(string From, string To)> values, string? otherwise = null)
    {
        if (!new[] { schema, table, column }.Concat(values.SelectMany(v => new[] { v.From, v.To })).Concat(otherwise is null ? [] : [otherwise])
                .All(name => name.Length > 0 && name.All(char.IsAsciiLetterOrDigit)))
        {
            throw new ArgumentException("Geçersiz ad veya değer.");
        }

        var cases = string.Join(" ", values.Select(v => $"WHEN N'{v.From}' THEN N'{v.To}'"));
        var update = $"UPDATE [{schema}].[{table}] SET [{column}] = CASE [{column}] {cases} ELSE {(otherwise is null ? $"[{column}]" : $"N'{otherwise}'")} END "
            + $"WHERE [TenantId] = @TenantId AND [{column}] IS NOT NULL;";

        return $"""
            DECLARE @TenantId uniqueidentifier;
            DECLARE tenant_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT [Id] FROM [platform].[Tenants];
            OPEN tenant_cursor;
            FETCH NEXT FROM tenant_cursor INTO @TenantId;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = @TenantId;
                {update}
                FETCH NEXT FROM tenant_cursor INTO @TenantId;
            END;
            CLOSE tenant_cursor;
            DEALLOCATE tenant_cursor;
            EXEC sys.sp_set_session_context @key = N'{TenantIsolationSql.SessionKey}', @value = NULL;
            """;
    }
}
