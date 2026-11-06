namespace Modulix.Web.Services;

/// <summary>
/// <see cref="AppCatalog"/> adlarının etkin dildeki karşılığı (ADR-0034). Katalogdaki <c>Name</c> Türkçe kaynak metindir ve
/// sözlük yüklenmemişse ya da anahtar yoksa o gösterilir; çeviri anahtarları kimlikten türer
/// (<c>nav.app.{id}</c>, ev sahibi menüsündeki ad için <c>nav.app.{id}.menu</c>, bölüm başlığı için <c>nav.group.{id}</c>).
/// Bir uygulamaya ya da bölüme ad eklenince üç sözlüğe de anahtar eklenir (birim testi kapsamı denetler).
/// </summary>
public static class AppNames
{
    public static string KeyOf(AppDefinition app) => $"nav.app.{app.Id}";

    public static string MenuKeyOf(AppDefinition app) => $"nav.app.{app.Id}.menu";

    public static string KeyOf(AppGroup group) => $"nav.group.{group.Id}";

    public static string KeyOf(PlannedApp planned) => $"nav.app.{planned.Id}";

    public static string Of(AppDefinition app) => Texts.GetOr(KeyOf(app), app.Name);

    public static string Of(AppGroup group) => Texts.GetOr(KeyOf(group), group.Name);

    public static string Of(PlannedApp planned) => Texts.GetOr(KeyOf(planned), planned.Name);

    /// <summary>Ekranın ev sahibinin menüsündeki adı: ev sahibi ayrı ad verdiyse o, yoksa ekranın kendi adı.</summary>
    public static string MenuNameOf(AppDefinition app) =>
        app.Hosted?.Name is { } hostedName ? Texts.GetOr(MenuKeyOf(app), hostedName) : Of(app);
}
