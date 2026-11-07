// Lookup bileşeni (ADR-0016) için küçük yardımcılar. Blazor olay başına preventDefault verebilir, tuş başına veremez;
// liste açıkken yön tuşları, Enter ve Esc varsayılanını burada engelleriz (Enter formu göndermesin, yön tuşu imleci oynatmasın).
window.modulixLookup = {
    guard: function (input) {
        if (!input || input.__lookupGuard) { return; }
        input.__lookupGuard = true;
        input.addEventListener('keydown', function (event) {
            if (input.getAttribute('aria-expanded') === 'true'
                && ['ArrowDown', 'ArrowUp', 'Enter', 'Escape'].indexOf(event.key) >= 0) {
                event.preventDefault();
            }
        });
    },
    // Girdinin kendisine değil dış kaba gönderilir: bileşenin arama işleyicisi çalışmaz, olay forma kabarcıklanır.
    changed: function (input) {
        var host = input && input.closest ? input.closest('.lookup, .tag-input') : null;
        if (host) { host.dispatchEvent(new Event('input', { bubbles: true })); }
    },
    // Etiket girişinde Enter formu göndermez, etiketi ekler.
    guardEnter: function (input) {
        if (!input) { return; }
        input.addEventListener('keydown', function (event) {
            if (event.key === 'Enter') { event.preventDefault(); }
        });
    },
    reveal: function (id) {
        var option = id ? document.getElementById(id) : null;
        if (option && option.scrollIntoView) { option.scrollIntoView({ block: 'nearest' }); }
    }
};
