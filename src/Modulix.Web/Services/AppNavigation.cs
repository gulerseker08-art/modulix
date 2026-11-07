namespace Modulix.Web.Services;

/// <summary>Panelde bir başlık altındaki ana uygulamalar; <paramref name="Group"/> boşsa başlıksız kümedir.</summary>
public sealed record AppRootGroup(AppGroup? Group, IReadOnlyList<AppDefinition> Apps);

/// <summary>Sol menüdeki bir bağlantı: ekran tanımı, (ev sahibinin menüsünde değişmişse) görünen adı ve bağlantı adresi.</summary>
public sealed record MenuEntry(AppDefinition App, string Name, string? GroupId, int Order, string? Href = null)
{
    /// <summary>Tıklanınca gidilen adres: ev sahibinin verdiği özel adres, yoksa ekranın route'u.</summary>
    public string? Link => Href ?? App.Route;
}

/// <summary>Sol menüdeki bölüm: başlık (boşsa başlıksız küme) ve bağlantıları.</summary>
public sealed record MenuSection(AppGroup? Group, IReadOnlyList<MenuEntry> Entries);

/// <summary>Bir sayfanın menü bağlamı: hangi uygulamanın menüsü gösterilir, hangi ekran aktiftir.</summary>
public sealed record MenuContext(AppDefinition Root, AppDefinition Active, IReadOnlyList<MenuSection> Sections);

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
    /// Ana uygulamanın sol menü bağlantıları: kendi görünür alt uygulamaları ile ona ev sahipliği yapılan
    /// uygulamaların (<see cref="AppDefinition.HostId"/>) görünür ve yerleşimi tanımlı (<see cref="AppDefinition.Hosted"/>) ekranları.
    /// Ev sahipliği yalnızca menü yerleşimidir; her ekran kendi modül ve izin denetimiyle süzülür.
    /// </summary>
    public IReadOnlyList<MenuEntry> MenuEntries(string rootId)
    {
        // MenuParentId dolu ekranlar (ör. teklif onayları) ayrı satır olmaz; üst satırın altında sayılır.
        List<MenuEntry> entries = Children(rootId)
            .Where(a => a.MenuParentId is null)
            .Select(a => new MenuEntry(a, a.Name, a.Group, a.Order))
            .ToList();

        foreach (AppDefinition hosted in AppCatalog.All.Where(a => a.ParentId is null && a.HostId == rootId && IsVisible(a)))
        {
            foreach (AppDefinition child in Children(hosted.Id).Where(c => c.Hosted is { ParentEntryId: null }))
            {
                HostPlacement place = child.Hosted!;
                entries.Add(new MenuEntry(child, place.Name ?? child.Name, place.Group, place.Order, place.Route));
            }
        }

        return entries;
    }

    /// <summary>
    /// Görünür ana uygulamalar. Erişilebilir ekranı ya da kendi giriş ekranı olmayan boş modül listelenmez; menüsü
    /// görünür bir ev sahibinin (ör. CRM) içinde gösterilen uygulama ayrı listelenmez.
    /// </summary>
    public IReadOnlyList<AppDefinition> Roots() =>
        AppCatalog.All
            .Where(a => a.ParentId is null && IsVisible(a) && (a.Route is not null || MenuEntries(a.Id).Count > 0) && HostOf(a) is null)
            .OrderBy(a => a.Order)
            .ToList();

    /// <summary>Uygulamanın ekranlarını menüsünde gösteren ve şu an görünür olan ev sahibi; yoksa <c>null</c>.</summary>
    public AppDefinition? HostOf(AppDefinition root)
    {
        if (root.HostId is null || Find(root.HostId) is not { } host)
        {
            return null;
        }

        return IsVisible(host) && MenuEntries(host.Id).Count > 0 ? host : null;
    }

    /// <summary>Panele tıklanınca açılan ilk ekran: kendi route'u, yoksa menüsündeki ilk bağlantı.</summary>
    public string? EntryRoute(AppDefinition root) =>
        root.Route ?? Sections(MenuEntries(root.Id)).SelectMany(s => s.Entries).FirstOrDefault()?.Link;

    /// <summary>
    /// Verilen (görünür, sıralı) uygulamaları grup sırasına göre kümeler; kümeler içinde sıra korunur. Hiç uygulaması
    /// olmayan grup yer almaz; grubu tanımsız uygulamalar sonda başlıksız (<c>Group == null</c>) kümede toplanır.
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

    /// <summary>Menü bağlantılarını bölümlere ayırır: bölümler grup sırasında, bağlantılar bölüm içinde sıralı; boş bölüm yer almaz.</summary>
    public static IReadOnlyList<MenuSection> Sections(IReadOnlyList<MenuEntry> entries)
    {
        List<MenuSection> result = [];
        foreach (AppGroup group in AppCatalog.Groups.OrderBy(g => g.Order))
        {
            List<MenuEntry> inGroup = entries.Where(e => e.GroupId == group.Id).OrderBy(e => e.Order).ToList();
            if (inGroup.Count > 0)
            {
                result.Add(new MenuSection(group, inGroup));
            }
        }

        List<MenuEntry> ungrouped = entries.Where(e => AppCatalog.Groups.All(g => g.Id != e.GroupId)).OrderBy(e => e.Order).ToList();
        if (ungrouped.Count > 0)
        {
            result.Add(new MenuSection(null, ungrouped));
        }

        return result;
    }

    public AppDefinition? Find(string id) => AppCatalog.All.FirstOrDefault(a => a.Id == id);

    /// <summary>Kullanıcının açabildiği modül ayar ekranları (Yönetim girişindeki bağlantılar); modülü kapalı ya da izni olmayan yer almaz.</summary>
    public IReadOnlyList<AppDefinition> ModuleSettings() =>
        AppCatalog.All
            .Where(a => a.ModuleKey is not null && a.Route == AppCatalog.ModuleSettingsRoute(a.ModuleKey) && IsVisible(a))
            .ToList();

    /// <summary>
    /// Sayfanın menü bağlamı: aktif ekran, menüsü gösterilecek uygulama (ev sahibi görünürse o) ve bölümleri. Aktif ekran
    /// ya da uygulaması görünmüyorsa <c>null</c>. Aktif ekran, tanımın tamamından bulunur; yetkisiz sayfaya doğrudan gidilse de
    /// bağlam doğrudur (sayfa erişimini yine AuthGate/sunucu denetler), ama o uygulamanın menüsü kullanıcıya görünmüyorsa gösterilmez.
    /// </summary>
    public MenuContext? Context(string relativePath)
    {
        AppDefinition? active = Resolve(relativePath);
        AppDefinition? root = active is null ? null : RootOf(active);
        if (active is null || root is null)
        {
            return null;
        }

        AppDefinition menuRoot = HostOf(root) ?? root;
        if (Roots().All(r => r.Id != menuRoot.Id))
        {
            return null;
        }

        // Ayrı satırı olmayan ekran (ör. teklif onayları) aktifken üst satırı işaretlenir.
        string? parentId = active.MenuParentId ?? (menuRoot.Id != root.Id ? active.Hosted?.ParentEntryId : null);
        if (parentId is not null && Find(parentId) is { } parent)
        {
            active = parent;
        }

        return new MenuContext(menuRoot, active, Sections(MenuEntries(menuRoot.Id)));
    }

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
