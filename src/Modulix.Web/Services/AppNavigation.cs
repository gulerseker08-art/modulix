namespace Modulix.Web.Services;

/// <summary>Panelde bir başlık altındaki ana uygulamalar; <paramref name="Group"/> boşsa başlıksız kümedir.</summary>
public sealed record AppRootGroup(AppGroup? Group, IReadOnlyList<AppDefinition> Apps);

/// <summary>
/// <see cref="AppCatalog"/> tanımlarını oturuma göre süzer: tenant'ın açık modülü, platform yöneticiliği ve kullanıcının
/// izinleri birlikte değerlendirilir. Sonuç önbelleğe alınmaz; tenant değişince oturum yeniden okunur ve liste
/// yeniden hesaplanır, önceki tenant'ın menüsü kalmaz.
/// </summary>
public sealed class AppNavigation(SessionState session)
{
    public bool IsVisible(AppDefinition app)
    {
        if (app.PlatformOnly && !session.IsPlatformAdministrator)
        {
            return false;
        }

        if (app.ModuleKey is not null && !session.IsModuleEnabled(app.ModuleKey))
        {
            return false;
        }

        if (app.Permission is not null && !session.Can(app.Permission))
        {
            return false;
        }

        return app.AnyPermission is null || session.CanAny(app.AnyPermission);
    }

    /// <summary>Kullanıcının görebildiği alt uygulamalar, sıralı.</summary>
    public IReadOnlyList<AppDefinition> Children(string parentId) =>
        AppCatalog.All
            .Where(a => a.ParentId == parentId && IsVisible(a))
            .OrderBy(a => a.Order)
            .ToList();

    /// <summary>
    /// Görünür ana uygulamalar. Erişilebilir alt uygulaması ya da kendi giriş ekranı olmayan boş modül listelenmez.
    /// </summary>
    public IReadOnlyList<AppDefinition> Roots() =>
        AppCatalog.All
            .Where(a => a.ParentId is null && IsVisible(a) && (a.Route is not null || Children(a.Id).Count > 0))
            .OrderBy(a => a.Order)
            .ToList();

    /// <summary>
    /// Verilen (görünür, sıralı) uygulamaları grup sırasına göre kümeler; kümeler içinde sıra korunur. Hiç uygulaması
    /// olmayan grup yer almaz; grubu tanımsız uygulamalar sonda başlıksız (<c>Group == null</c>) kümede toplanır.
    /// Ana uygulamalar da (<see cref="Roots"/>) bir uygulamanın alt uygulamaları da (<see cref="Children"/>) bu yolla gruplanır.
    /// </summary>
    public IReadOnlyList<AppRootGroup> Grouped(IReadOnlyList<AppDefinition> listed)
    {
        List<AppRootGroup> result = [];
        foreach (AppGroup group in AppCatalog.Groups.OrderBy(g => g.Order))
        {
            List<AppDefinition> apps = listed.Where(a => a.Group == group.Id).ToList();
            if (apps.Count > 0)
            {
                result.Add(new AppRootGroup(group, apps));
            }
        }

        List<AppDefinition> ungrouped = listed.Where(a => AppCatalog.Groups.All(g => g.Id != a.Group)).ToList();
        if (ungrouped.Count > 0)
        {
            result.Add(new AppRootGroup(null, ungrouped));
        }

        return result;
    }

    public AppDefinition? Find(string id) => AppCatalog.All.FirstOrDefault(a => a.Id == id);

    /// <summary>
    /// Route'a göre aktif alt uygulama (en uzun eşleşen) ve ana uygulaması; hiçbiri değilse <c>null</c>. Tanımın tamamına
    /// bakar, böylece yetkisiz sayfaya doğrudan gidilse de bağlam doğru bulunur (sayfa erişimini yine AuthGate/sunucu denetler).
    /// </summary>
    public static AppDefinition? Resolve(string relativePath)
    {
        string path = Normalize(relativePath);
        AppDefinition? best = null;
        foreach (AppDefinition app in AppCatalog.All)
        {
            if (app.Route is null)
            {
                continue;
            }

            bool matches = app.ExactMatch
                ? path == app.Route
                : path == app.Route || path.StartsWith(app.Route + "/", StringComparison.Ordinal);
            if (matches && (best is null || app.Route.Length > best.Route!.Length))
            {
                best = app;
            }
        }

        return best;
    }

    /// <summary>Alt uygulamanın (ya da ana uygulamanın kendisinin) ana uygulaması.</summary>
    public AppDefinition? RootOf(AppDefinition app) => app.ParentId is null ? app : Find(app.ParentId);

    private static string Normalize(string relativePath)
    {
        int cut = relativePath.IndexOfAny(['?', '#']);
        string path = cut >= 0 ? relativePath[..cut] : relativePath;
        return path.Trim('/').ToLowerInvariant();
    }
}
