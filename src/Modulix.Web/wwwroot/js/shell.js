// Uygulama kabuğu tercihleri: tema (açık/koyu) ve sol menünün daraltılmış durumu.
// Bu dosya <head> içinde, Blazor yüklenmeden önce çalışır; böylece sayfa yanlış temayla yanıp sönmez.
// Tercihler kullanıcının tarayıcısında saklanır (sunucuda tema politikası yoktur; eklenirse öncelik ondadır).
(function () {
    const themeKey = 'modulix.theme';
    const sidebarKey = 'modulix.sidebarCollapsed';
    const root = document.documentElement;

    function read(key) {
        try {
            return window.localStorage.getItem(key);
        } catch {
            return null;
        }
    }

    function write(key, value) {
        try {
            window.localStorage.setItem(key, value);
        } catch {
            // Depolama kapalıysa tercih yalnızca bu oturumda geçerli olur.
        }
    }

    function applyTheme(theme) {
        const value = theme === 'dark' ? 'dark' : 'light';
        root.setAttribute('data-theme', value);
        root.setAttribute('data-bs-theme', value);
        return value;
    }

    function applySidebar(collapsed) {
        root.classList.toggle('sidebar-collapsed', collapsed);
        return collapsed;
    }

    // Varsayılan tema açıktır; kullanıcı açıkça koyu seçmediyse açık tema kullanılır.
    applyTheme(read(themeKey));
    applySidebar(read(sidebarKey) === 'true');

    window.modulixShell = {
        getTheme: () => root.getAttribute('data-theme') || 'light',
        setTheme: theme => {
            const value = applyTheme(theme);
            write(themeKey, value);
            return value;
        },
        toggleTheme: () => window.modulixShell.setTheme(window.modulixShell.getTheme() === 'dark' ? 'light' : 'dark'),
        isSidebarCollapsed: () => root.classList.contains('sidebar-collapsed'),
        setSidebarCollapsed: collapsed => {
            write(sidebarKey, collapsed ? 'true' : 'false');
            return applySidebar(collapsed);
        }
    };
})();
