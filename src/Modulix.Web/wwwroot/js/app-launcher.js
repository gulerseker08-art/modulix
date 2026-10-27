// Uygulama panelinin açıkken dış tıklama, Escape ve klavyeyle dışarı çıkma davranışları.
let cleanup = null;

function attach(dotNetRef, panel, button) {
    detach();

    const inside = target => panel.contains(target) || button.contains(target);
    // Odak sayfadaki başka bir denetime geçmediyse (gövdede ya da panelde kaldıysa) düğmeye dönülür.
    const focusIsFree = () => document.activeElement === document.body || panel.contains(document.activeElement);

    const onPointerDown = e => {
        if (!inside(e.target)) {
            dotNetRef.invokeMethodAsync('Dismiss', false);
        }
    };
    const onFocusIn = e => {
        if (!inside(e.target)) {
            dotNetRef.invokeMethodAsync('Dismiss', false);
        }
    };
    const onKeyDown = e => {
        if (e.key === 'Escape') {
            e.preventDefault();
            dotNetRef.invokeMethodAsync('Dismiss', focusIsFree());
        }
    };

    document.addEventListener('pointerdown', onPointerDown, true);
    document.addEventListener('focusin', onFocusIn);
    document.addEventListener('keydown', onKeyDown);
    cleanup = () => {
        document.removeEventListener('pointerdown', onPointerDown, true);
        document.removeEventListener('focusin', onFocusIn);
        document.removeEventListener('keydown', onKeyDown);
    };
}

function detach() {
    if (cleanup) {
        cleanup();
        cleanup = null;
    }
}

window.modulixAppLauncher = { attach, detach };
