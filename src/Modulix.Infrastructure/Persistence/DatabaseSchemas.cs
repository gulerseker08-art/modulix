namespace Modulix.Infrastructure.Persistence;

/// <summary>
/// Veritabanı şemaları. docker/sqlserver/init.sql ile aynı listedir; biri değişirse diğeri de değişir.
/// Her modül yalnızca kendi şemasına yazar.
/// </summary>
public static class DatabaseSchemas
{
    /// <summary>Platform geneli kayıtlar (tenant listesi, planlar, migration geçmişi). RLS dışındadır.</summary>
    public const string Platform = "platform";

    public const string Core = "core";
    public const string Meta = "meta";
    public const string Crm = "crm";
    public const string Logistics = "logistics";
    public const string Ai = "ai";

    public static IReadOnlyList<string> All { get; } = [Platform, Core, Meta, Crm, Logistics, Ai];
}
