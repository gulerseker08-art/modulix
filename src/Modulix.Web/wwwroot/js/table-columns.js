// Liste tablolarında sütun göster/gizle (ColumnPicker). Seçim tarayıcıda (localStorage) sayfa yoluna göre saklanır;
// tablo yeniden çizildiğinde (sayfa değişimi, süzgeç) gizli sütunlar MutationObserver ile yeniden uygulanır.
window.modulixTable = (function () {
    const observers = new Map();

    function load(key) {
        try { return JSON.parse(localStorage.getItem(key) || '[]'); } catch (e) { return []; }
    }

    function save(key, hidden) {
        try { localStorage.setItem(key, JSON.stringify(hidden)); } catch (e) { /* özel pencere vb.: kalıcı olmaz */ }
    }

    function label(th) {
        return (th.textContent || '').replace(/\s+/g, ' ').trim();
    }

    function apply(selector, hidden) {
        const table = document.querySelector(selector);
        if (!table) { return; }
        table.querySelectorAll('tr').forEach(function (row) {
            Array.prototype.forEach.call(row.children, function (cell, index) {
                if (cell.colSpan > 1) { return; }
                cell.classList.toggle('col-hidden', hidden.indexOf(index) >= 0);
            });
        });
    }

    return {
        // Sayfa açıldığında çağrılır: kayıtlı gizli sütunları uygular ve tablo değişince yeniden uygular.
        restore: function (selector, key) {
            this.release(key);
            const run = function () { apply(selector, load(key)); };
            const observer = new MutationObserver(run);
            observer.observe(document.body, { childList: true, subtree: true });
            observers.set(key, observer);
            run();
        },

        release: function (key) {
            const observer = observers.get(key);
            if (observer) { observer.disconnect(); observers.delete(key); }
        },

        // Menüde gösterilecek sütunlar: etiketi dolu başlıklar; ilk sütun (kayıt adı) hep görünür.
        columns: function (selector, key) {
            const table = document.querySelector(selector);
            if (!table) { return []; }
            const hidden = load(key);
            const head = table.querySelector('thead tr');
            if (!head) { return []; }
            const result = [];
            Array.prototype.forEach.call(head.children, function (th, index) {
                const text = label(th);
                if (text) {
                    result.push({ index: index, label: text, visible: hidden.indexOf(index) < 0, locked: index === 0 });
                }
            });
            return result;
        },

        set: function (selector, key, index, visible) {
            const hidden = load(key).filter(function (i) { return i !== index; });
            if (!visible) { hidden.push(index); }
            save(key, hidden);
            apply(selector, hidden);
        },

        reset: function (selector, key) {
            save(key, []);
            apply(selector, []);
        }
    };
})();
