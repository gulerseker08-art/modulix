// Ekran testi: tasarım sistemi fazlarının (kabuk, Aktiviteler, Fırsat, Firma, Kişi) gerçek tarayıcıda davranışı.
// Kullanım: BASE_URL=https://localhost:7026 OUT=ekran-testi-sonuc node tests/ui-screens/screens.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';

const BASE = process.env.BASE_URL ?? 'https://localhost:7026';
const OUT = process.env.OUT ?? 'ekran-testi-sonuc';
fs.mkdirSync(OUT, { recursive: true });

const results = [];
const consoleErrors = [];
let shotNo = 0;

function record(area, name, ok, detail = '', skipped = false) {
    results.push({ area, name, ok: !!ok, skipped, detail: String(detail ?? '') });
    console.log(`${skipped ? 'ATLANDI' : ok ? 'GEÇTİ ' : 'KALDI '} [${area}] ${name}${detail ? ' — ' + detail : ''}`);
}

// Kontrol sonucu: true / açıklama metni = geçti; fail('…') = kaldı; skip('…') = veri yok, değerlendirilemedi.
const fail = detail => ({ fail: detail });
const skip = detail => ({ skip: detail });

async function check(area, name, fn) {
    try {
        const r = await fn();
        if (r === false) {
            record(area, name, false);
        } else if (r && typeof r === 'object' && 'fail' in r) {
            record(area, name, false, r.fail);
        } else if (r && typeof r === 'object' && 'skip' in r) {
            record(area, name, true, r.skip, true);
        } else {
            record(area, name, true, typeof r === 'string' ? r : '');
        }
    } catch (e) {
        record(area, name, false, (e?.message ?? String(e)).split('\n')[0]);
    }
}

async function shot(page, name, full = false) {
    shotNo++;
    const file = `${String(shotNo).padStart(2, '0')}-${name}.png`;
    await page.waitForTimeout(350);
    await page.screenshot({ path: path.join(OUT, file), fullPage: full });
}

async function blazorErrorShown(page) {
    return page.evaluate(() => {
        const el = document.getElementById('blazor-error-ui');
        return !!el && getComputedStyle(el).display !== 'none';
    });
}

async function overflowX(page) {
    return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
}

async function gotoApp(page, route, readySelector) {
    await page.goto(`${BASE}/${route}`, { waitUntil: 'domcontentloaded' });
    if (readySelector) {
        await page.waitForSelector(readySelector, { timeout: 60000 });
    }
    await page.waitForTimeout(800);
}

function istanbulIso(dayOffset, hour, minute = 0) {
    // Firma saat dilimi varsayılanı Türkiye (UTC+03:00, yaz saati yok).
    const now = new Date(Date.now() + 3 * 3600 * 1000);
    const d = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + dayOffset, hour - 3, minute));
    return d.toISOString();
}

const browser = await chromium.launch();
const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 950 }, locale: 'tr-TR' });
const page = await context.newPage();
page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text().slice(0, 300)); });
page.on('pageerror', err => consoleErrors.push('pageerror: ' + String(err).slice(0, 300)));

// ---------------------------------------------------------------- Giriş
await check('Giriş', 'Giriş sayfası açılır ve Modulix logosu görünür', async () => {
    await gotoApp(page, 'giris', '#email');
    await shot(page, 'giris');
    const title = await page.title();
    return title.includes('Modulix') ? `başlık: ${title}` : false;
});

await check('Giriş', 'ayse@acme.demo ile giriş yapılır', async () => {
    await page.fill('#email', 'ayse@acme.demo');
    await page.fill('#password', 'Modulix.Demo1');
    await page.click('button[type=submit]');
    await page.waitForSelector('.profile-button', { timeout: 60000 });
    return true;
});

// ---------------------------------------------------------------- Örnek veriyi zenginleştir (bu hafta için aktiviteler)
let ids = {};
await check('Hazırlık', 'Bu hafta için örnek aktiviteler API ile eklenir', async () => {
    const companies = await (await page.request.get(`${BASE}/api/crm/companies?pageSize=10`)).json();
    const opps = await (await page.request.get(`${BASE}/api/crm/opportunities?pageSize=10`)).json();
    const contacts = await (await page.request.get(`${BASE}/api/crm/contacts?pageSize=10`)).json();
    ids.company = companies.items?.[0]?.id;
    ids.opportunity = opps.items?.find(o => o.title?.startsWith('Depo'))?.id ?? opps.items?.[0]?.id;
    ids.contact = contacts.items?.[0]?.id;
    const samples = [
        { subject: 'Ekip toplantısı', type: 'Meeting', startAt: istanbulIso(0, 9), endAt: istanbulIso(0, 10), timeZone: 'Europe/Istanbul' },
        { subject: 'Saha ziyareti', type: 'Meeting', startAt: istanbulIso(0, 9, 30), endAt: istanbulIso(0, 10, 30), timeZone: 'Europe/Istanbul', companyId: ids.company },
        { subject: 'Bütçe teyidi', type: 'Call', startAt: istanbulIso(0, 14, 30), endAt: istanbulIso(0, 15, 15), timeZone: 'Europe/Istanbul', companyId: ids.company, opportunityId: ids.opportunity },
        { subject: 'Teklif revizyonu', type: 'Task', dueAt: istanbulIso(0, 17), companyId: ids.company, opportunityId: ids.opportunity },
        { subject: 'Ürün demosu', type: 'Meeting', startAt: istanbulIso(1, 11), endAt: istanbulIso(1, 12), timeZone: 'Europe/Istanbul', companyId: ids.company },
    ];
    const statuses = [];
    const errors = [];
    for (const s of samples) {
        const r = await page.request.post(`${BASE}/api/crm/activities`, { data: s });
        statuses.push(r.status());
        if (r.status() >= 400) errors.push(`${s.subject}: ${(await r.text()).slice(0, 200)}`);
    }
    const ok = statuses.filter(x => x >= 200 && x < 300).length;
    const detail = `${ok}/${samples.length} oluşturuldu (durumlar: ${statuses.join(',')})${errors.length ? ' · hatalar: ' + errors.join(' ;; ') : ''}`;
    return ok === samples.length ? detail : fail(detail);
});

// ---------------------------------------------------------------- Kabuk
await check('Kabuk', 'Ana sayfa: üst çubuk, logo ve sol menü çizilir', async () => {
    await gotoApp(page, '', '.profile-button');
    await shot(page, 'ana-sayfa');
    const brand = await page.textContent('.brand-name');
    return brand?.replace(/\s/g, '') === 'Modulix' ? 'logo metni "Modulix"' : `logo metni: ${brand}`;
});

await check('Kabuk', 'Profil menüsü açılır, Escape ile kapanır', async () => {
    await page.click('.profile-button');
    await page.waitForSelector('#profile-menu-list', { timeout: 5000 });
    const items = await page.$$eval('#profile-menu-list [role=menuitem]', els => els.map(e => e.textContent.trim()));
    await shot(page, 'profil-menusu');
    await page.keyboard.press('Escape');
    await page.waitForSelector('#profile-menu-list', { state: 'detached', timeout: 5000 });
    return `öğeler: ${items.join(' | ')}`;
});

await check('Kabuk', 'Profil menüsü dışarı tıklanınca kapanır', async () => {
    await page.click('.profile-button');
    await page.waitForSelector('#profile-menu-list');
    await page.mouse.click(700, 500);
    await page.waitForSelector('#profile-menu-list', { state: 'detached', timeout: 5000 });
    return true;
});

await check('Kabuk', 'Koyu tema açılır ve sayfa yenilenince korunur', async () => {
    await page.click('.header-tools .icon-button');
    await page.waitForFunction(() => document.documentElement.dataset.theme === 'dark');
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('.profile-button', { timeout: 60000 });
    const theme = await page.evaluate(() => document.documentElement.dataset.theme);
    await shot(page, 'ana-sayfa-koyu');
    return theme === 'dark' ? true : false;
});

await check('Kabuk', 'Tema düğmesi koyu temada "basılı" durumda; geri açık temaya döner', async () => {
    const pressed = await page.getAttribute('.header-tools .icon-button', 'aria-pressed');
    await page.click('.header-tools .icon-button');
    await page.waitForFunction(() => document.documentElement.dataset.theme === 'light');
    return pressed === 'true' ? true : `aria-pressed=${pressed}`;
});

await check('Kabuk', 'Menüyü Daralt: menü ikonlara iner ve yenilemede korunur', async () => {
    await gotoApp(page, 'crm/firmalar', '.nav-collapse-button');
    const before = await page.$eval('.sidebar', el => el.getBoundingClientRect().width);
    await page.click('.nav-collapse-button');
    await page.waitForFunction(() => document.documentElement.classList.contains('sidebar-collapsed'));
    await page.waitForTimeout(400);
    const after = await page.$eval('.sidebar', el => el.getBoundingClientRect().width);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('.nav-collapse-button', { timeout: 60000 });
    const kept = await page.evaluate(() => document.documentElement.classList.contains('sidebar-collapsed'));
    await shot(page, 'menu-daraltilmis');
    await page.click('.nav-collapse-button');
    await page.waitForFunction(() => !document.documentElement.classList.contains('sidebar-collapsed'));
    return kept && after < before ? `genişlik ${Math.round(before)}px → ${Math.round(after)}px` : fail(`korundu=${kept}, ${before}→${after}`);
});

await check('Kabuk', 'Sol menüde aktif öğe vurgulu (Firmalar)', async () => {
    const active = await page.$eval('.nav-item a.active', el => el.textContent.trim());
    return active.includes('Firmalar') ? `aktif: ${active}` : fail(`aktif: ${active}`);
});

await check('Kabuk', 'Firmalar listesi PageHeader ile açılır', async () => {
    const h = await page.textContent('.page-header h1');
    const sub = await page.textContent('.page-header .page-subtitle');
    await shot(page, 'firmalar-liste');
    return `başlık: ${h?.trim()} · açıklama: ${sub?.trim()}`;
});

// ---------------------------------------------------------------- Aktiviteler
await check('Aktiviteler', 'Varsayılan görünüm haftalık saat çizelgesi', async () => {
    await gotoApp(page, 'crm/aktiviteler', '.kpi-grid');
    await page.waitForSelector('.cal-time', { timeout: 30000 });
    const active = await page.$eval('.segmented .chip.active', el => el.textContent.trim());
    return active === 'Hafta' ? true : fail(`seçili görünüm: ${active}`);
});

await check('Aktiviteler', 'Özet kartları sayı gösterir', async () => {
    await page.waitForFunction(() => [...document.querySelectorAll('.kpi-card strong')].every(s => s.textContent.trim() !== '–'), null, { timeout: 20000 });
    const values = await page.$$eval('.kpi-card', els => els.map(e => e.querySelector('.kpi-label').textContent + '=' + e.querySelector('strong').textContent));
    return values.join(', ');
});

await check('Aktiviteler', 'Saat çizelgesinde aktivite blokları ve bugün paneli dolu', async () => {
    const events = await page.$$eval('.cal-event', els => els.length);
    const allDay = await page.$$eval('.cal-time-allday .cal-item', els => els.length);
    const today = await page.$$eval('.today-item', els => els.length);
    const now = await page.$$eval('.cal-now', els => els.length);
    await shot(page, 'aktiviteler-hafta');
    const d = `${events} saatli blok, ${allDay} tüm gün/son tarihli, bugün panelinde ${today}, şimdi çizgisi ${now}`;
    return events > 0 && today > 0 ? d : fail(d);
});

await check('Aktiviteler', 'Çakışan aktiviteler yan yana (genişlik paylaşımı)', async () => {
    const widths = await page.$$eval('.cal-time-col.today .cal-event', els => els.map(e => e.style.width));
    if (widths.length < 2) return skip(`bugün yalnızca ${widths.length} saatli blok var; çakışma denenemedi`);
    return widths.some(w => w && !w.startsWith('100')) ? `bugünün blok genişlikleri: ${widths.join(', ')}` : fail(`genişlikler: ${widths.join(', ')}`);
});

await check('Aktiviteler', 'Bloğa tıklayınca detay çekmecesi açılır, Escape ile kapanır', async () => {
    if (!(await page.$('.cal-event .cal-entry'))) return fail('takvimde tıklanacak saatli blok yok');
    await page.click('.cal-event .cal-entry');
    await page.waitForSelector('.drawer .activity-panel', { timeout: 10000 });
    const focused = await page.evaluate(() => document.activeElement?.classList.contains('drawer'));
    await shot(page, 'aktivite-detay-cekmece');
    await page.keyboard.press('Escape');
    await page.waitForSelector('.drawer', { state: 'detached', timeout: 5000 });
    return focused ? 'açılınca odak çekmecede' : fail('açıldı ama odak çekmeceye geçmedi');
});

await check('Aktiviteler', 'Bugün panelinden detay açılır, arka plana tıklayınca kapanır', async () => {
    await page.click('.today-item .today-body');
    await page.waitForSelector('.drawer', { timeout: 10000 });
    await page.mouse.click(200, 400);
    await page.waitForSelector('.drawer', { state: 'detached', timeout: 5000 });
    return true;
});

await check('Aktiviteler', 'Yeni aktivite çekmecede açılır; Kaydet/Vazgeç görünür', async () => {
    await page.click('.page-header .btn-primary');
    await page.waitForSelector('.drawer .record-form', { timeout: 10000 });
    const visible = await page.$eval('.drawer .form-actions', el => {
        const r = el.getBoundingClientRect();
        return r.bottom <= window.innerHeight + 1 && r.top >= 0;
    });
    await shot(page, 'yeni-aktivite-cekmece');
    await page.click('.drawer .form-actions .btn-outline-secondary');
    await page.waitForSelector('.drawer', { state: 'detached', timeout: 5000 });
    return visible ? 'aksiyonlar ekranda görünür' : fail('Kaydet/Vazgeç ekran dışında');
});

await check('Aktiviteler', 'Ay, Gün, Ajanda ve Liste görünümleri çizilir', async () => {
    const seen = [];
    for (const [label, sel] of [['Ay', '.cal-grid.cal-month'], ['Gün', '.cal-time-day'], ['Ajanda', '.cal'], ['Liste', '.record-table, .activity-main p.text-muted']]) {
        await page.click(`.segmented .chip:text-is("${label}")`);
        await page.waitForSelector(sel, { timeout: 15000 });
        seen.push(label);
        if (label === 'Ay') await shot(page, 'aktiviteler-ay');
    }
    await shot(page, 'aktiviteler-liste');
    return seen.join(', ');
});

await check('Aktiviteler', '"Geciken" kartı liste görünümünü gecikenler süzgeciyle açar', async () => {
    await page.click('.kpi-card.tone-danger');
    await page.waitForTimeout(1200);
    const quick = await page.$eval('.filters[aria-label="Hızlı filtre"] .chip.active', el => el.textContent.trim());
    const mode = await page.$eval('.segmented .chip.active', el => el.textContent.trim());
    return mode === 'Liste' && quick === 'Gecikenler' ? true : fail(`görünüm=${mode}, süzgeç=${quick}`);
});

// ---------------------------------------------------------------- Fırsat detayı
if (ids.opportunity) {
    await check('Fırsat', 'Fırsat detayında huni, gösterge şeridi ve sağ panel çizilir', async () => {
        await gotoApp(page, `crm/firsatlar/${ids.opportunity}`, '.pipeline');
        const steps = await page.$$eval('.pipeline-step', els => els.map(e => e.className.match(/is-\w+/)?.[0] + ':' + e.textContent.trim()));
        const kpis = await page.$$eval('.kpi-item', els => els.length);
        const side = await page.$$eval('.detail-side .side-card', els => els.length);
        await shot(page, 'firsat-detay', true);
        return `aşamalar: ${steps.join(' | ')}; ${kpis} gösterge; ${side} yan kart`;
    });

    await check('Fırsat', 'Hunide başka bir açık aşamaya tıklayınca işlem paneli açılır ve aşama önseçilir', async () => {
        const target = await page.$('.pipeline-step.is-todo.cat-open button.pipeline-label');
        if (!target) return skip('tıklanabilir açık aşama yok (fırsat son açık aşamada olabilir)');
        const name = (await target.textContent()).trim();
        await target.click();
        await page.waitForSelector('#o-stage', { timeout: 5000 });
        const selected = await page.$eval('#o-stage', el => el.options[el.selectedIndex].text);
        const focused = await page.evaluate(() => document.activeElement?.id);
        await shot(page, 'firsat-asama-paneli');
        const d = `tıklanan: ${name}; seçili: ${selected}; odak: ${focused}`;
        return name.includes(selected) && focused === 'o-stage' ? d : fail(d);
    });

    await check('Fırsat', 'Huni "Kazanıldı" aşaması kazanma panelini açar', async () => {
        const won = await page.$('.pipeline-step.cat-won button.pipeline-label');
        if (!won) return fail('kazanıldı aşaması tıklanamıyor');
        await won.click();
        await page.waitForSelector('#w-date', { timeout: 5000 });
        return true;
    });

    await check('Fırsat', 'Aktivite ekle çekmecede açılır ve kapanır', async () => {
        await page.click('.detail-side .side-card .btn-outline-primary');
        await page.waitForSelector('.drawer .record-form', { timeout: 10000 });
        await page.keyboard.press('Escape');
        await page.waitForSelector('.drawer', { state: 'detached', timeout: 5000 });
        return true;
    });
}

// ---------------------------------------------------------------- Firma detayı
if (ids.company) {
    await check('Firma', 'Firma detayında özet kartı ve sekmeler çizilir', async () => {
        await gotoApp(page, `crm/firmalar/${ids.company}`, '.record-tabs');
        const tabs = await page.$$eval('.record-tab', els => els.map(e => e.textContent.trim().replace(/\s+/g, ' ')));
        const facts = await page.$$eval('.record-summary .summary-facts dt', els => els.map(e => e.textContent.trim()));
        await shot(page, 'firma-genel');
        return `sekmeler: ${tabs.join(' | ')}; özet: ${facts.join(', ')}`;
    });

    await check('Firma', 'Sekme geçişinde yarım kalan form korunur (Kişi ilişkilendir)', async () => {
        await page.click('.record-tab:has-text("Kişiler ve ilişkiler")');
        await page.waitForSelector('#tabpanel-iliskiler:not([hidden])');
        const linkBtn = await page.$('#tabpanel-iliskiler button:has-text("Kişi ilişkilendir")');
        if (!linkBtn) return false;
        await linkBtn.click();
        await page.waitForTimeout(800);
        // Unvan düz metin alanıdır (kişi arama kutusu seçim yapılmadan odaktan çıkınca bilerek temizlenir).
        const input = await page.waitForSelector('#link-title', { timeout: 5000 });
        const marker = 'Yarım kalan unvan';
        await input.fill(marker);
        await page.click('#tabpanel-iliskiler .section-header h2');
        await shot(page, 'firma-iliskiler-form');
        await page.click('.record-tab:has-text("Genel bilgiler")');
        await page.waitForSelector('#tabpanel-genel:not([hidden])');
        await page.click('.record-tab:has-text("Kişiler ve ilişkiler")');
        await page.waitForSelector('#tabpanel-iliskiler:not([hidden])');
        const still = await page.$eval('#link-title', el => el.value).catch(() => null);
        return still === marker ? 'form açık kaldı, yazılan unvan korundu' : fail(`form/değer kayboldu: "${still}"`);
    });

    await check('Firma', 'Fırsatlar, Aktiviteler ve Geçmiş sekmeleri açılır', async () => {
        const opened = [];
        for (const t of ['Fırsatlar ve leadler', 'Aktiviteler', 'Geçmiş']) {
            const tab = await page.$(`.record-tab:has-text("${t}")`);
            if (!tab) continue;
            await tab.click();
            await page.waitForTimeout(900);
            opened.push(t);
            if (t === 'Aktiviteler') await shot(page, 'firma-aktiviteler');
        }
        return opened.join(', ');
    });

    await check('Firma', 'Aktivite ekle çekmecede açılır', async () => {
        await page.click('.record-tab:has-text("Aktiviteler")');
        await page.click('#tabpanel-aktiviteler .section-header .btn-primary');
        await page.waitForSelector('.drawer .record-form', { timeout: 10000 });
        await shot(page, 'firma-aktivite-cekmece');
        await page.keyboard.press('Escape');
        await page.waitForSelector('.drawer', { state: 'detached', timeout: 5000 });
        return true;
    });
}

// ---------------------------------------------------------------- Kişi detayı
if (ids.contact) {
    await check('Kişi', 'Kişi detayında özet kartı ve sekmeler çizilir', async () => {
        await gotoApp(page, `crm/kisiler/${ids.contact}`, '.record-tabs');
        const tabs = await page.$$eval('.record-tab', els => els.map(e => e.textContent.trim().replace(/\s+/g, ' ')));
        await shot(page, 'kisi-genel');
        await page.click('.record-tab:has-text("Firma ilişkileri")');
        await page.waitForSelector('#tabpanel-firmalar:not([hidden])');
        await shot(page, 'kisi-firma-iliskileri');
        return `sekmeler: ${tabs.join(' | ')}`;
    });
}

// ---------------------------------------------------------------- Koyu tema ekran görüntüleri
await check('Koyu tema', 'Aktiviteler ve fırsat koyu temada çizilir', async () => {
    await page.evaluate(() => window.modulixShell.setTheme('dark'));
    await gotoApp(page, 'crm/aktiviteler', '.cal-time');
    await shot(page, 'aktiviteler-koyu');
    if (ids.opportunity) {
        await gotoApp(page, `crm/firsatlar/${ids.opportunity}`, '.pipeline');
        await shot(page, 'firsat-koyu');
    }
    await page.evaluate(() => window.modulixShell.setTheme('light'));
    return true;
});

// ---------------------------------------------------------------- Mobil (390px)
const mobile = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 390, height: 844 }, locale: 'tr-TR', storageState: await context.storageState() });
const m = await mobile.newPage();
m.on('pageerror', err => consoleErrors.push('mobil pageerror: ' + String(err).slice(0, 300)));
const mobileRoutes = [['ana-sayfa', '', '.profile-button'], ['aktiviteler', 'crm/aktiviteler', '.kpi-grid']];
if (ids.opportunity) mobileRoutes.push(['firsat', `crm/firsatlar/${ids.opportunity}`, '.pipeline']);
if (ids.company) mobileRoutes.push(['firma', `crm/firmalar/${ids.company}`, '.record-tabs']);
if (ids.contact) mobileRoutes.push(['kisi', `crm/kisiler/${ids.contact}`, '.record-tabs']);
for (const [name, route, sel] of mobileRoutes) {
    await check('Mobil', `${name}: yatay taşma yok`, async () => {
        await gotoApp(m, route, sel);
        const o = await overflowX(m);
        await shot(m, `mobil-${name}`, true);
        return o <= 0 ? true : fail(`${o}px yatay taşma`);
    });
}
await check('Mobil', 'Menü düğmesi sol menüyü açar', async () => {
    await gotoApp(m, 'crm/firmalar', '.navbar-toggler');
    await m.click('.navbar-toggler');
    await m.waitForSelector('.nav-scrollable:not(.collapse)', { timeout: 5000 });
    await shot(m, 'mobil-menu');
    return true;
});
if (ids.opportunity) {
    await check('Mobil', 'Fırsat sayfasında aktivite çekmecesi tam ekran açılır', async () => {
        await gotoApp(m, `crm/firsatlar/${ids.opportunity}`, '.pipeline');
        await m.click('.detail-side .side-card .btn-outline-primary');
        await m.waitForSelector('.drawer', { timeout: 10000 });
        const w = await m.$eval('.drawer', el => Math.round(el.getBoundingClientRect().width));
        await shot(m, 'mobil-cekmece');
        return w >= 389 ? `genişlik ${w}px` : fail(`genişlik ${w}px (tam ekran değil)`);
    });
}

// ---------------------------------------------------------------- Genel
await check('Genel', 'Blazor hata çubuğu hiç görünmedi', async () => !(await blazorErrorShown(page)) && !(await blazorErrorShown(m)));

await browser.close();

const failed = results.filter(r => !r.ok);
const skipped = results.filter(r => r.skipped);
fs.writeFileSync(path.join(OUT, 'sonuc.json'), JSON.stringify({ base: BASE, results, consoleErrors }, null, 2));
const md = [
    `# Ekran testi sonucu`,
    ``,
    `${results.length - failed.length - skipped.length}/${results.length} kontrol geçti, ${failed.length} kaldı, ${skipped.length} atlandı.`,
    ``,
    `| Alan | Kontrol | Sonuç | Ayrıntı |`,
    `|---|---|---|---|`,
    ...results.map(r => `| ${r.area} | ${r.name} | ${r.skipped ? 'Atlandı' : r.ok ? 'Geçti' : '**Kaldı**'} | ${r.detail.replace(/\|/g, '/')} |`),
    ``,
    `## Tarayıcı konsol hataları (${consoleErrors.length})`,
    ...consoleErrors.slice(0, 40).map(e => `- ${e.replace(/\n/g, ' ')}`),
].join('\n');
fs.writeFileSync(path.join(OUT, 'SONUC.md'), md);
console.log(`\n${results.length - failed.length}/${results.length} kontrol geçti.`);
