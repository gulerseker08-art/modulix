# Bileşen kataloğu

Durum: **Var** = kullanıma hazır, **Stil** = ayrı Razor bileşeni yok, ortak CSS sınıfıyla standart, **Yok** = henüz geliştirilmedi (bkz. `migration-report.md`).

## Kabuk ve gezinme

| Bileşen | Durum | Nerede / nasıl |
|---|---|---|
| AppShell | Var | `Layout/MainLayout.razor`: sol menü + üst çubuk + `main#main-content`; "İçeriğe geç" bağlantısı |
| AppHeader | Var | MainLayout üst çubuğu: tenant logosu, ortada genel arama, şirket seçici, tema düğmesi, profil menüsü (avatar, ad, kuruluş) |
| Sidebar | Var | `Layout/NavMenu.razor`: aktif modülün sayfaları (AppCatalog + izin süzgeci), aktif öğede vurgu çizgisi, Menüyü Daralt |
| ModuleSwitcher | Var | `Components/AppLauncher.razor` (logoya tıklanınca "Tüm Uygulamalar" paneli; yalnızca mevcut, etkin ve erişilebilir uygulama kartları; kart uygulamanın ilk ekranına gider) |
| SideNav bölümleri | Var | `Layout/NavMenu.razor`: aktif uygulamanın ekranları `AppCatalog` gruplarına göre açılır/kapanır bölüm başlıkları (`nav-group`, ikon + ad + ok) ve girintili bağlantılar (`nav-sublink`, dolu nokta + açık zemin + `aria-current`); `AppGroup.Footer` bölümü (CRM Ayarları) altta sabit; boş bölüm çıkmaz. Sözleşmeler, Siparişler, Pazarlama CRM'in menüsünde `AppDefinition.Hosted` ile görünür (modül, izin ve route'ları değişmez) |
| Pasif uygulama kartı | Var | `AppLauncher` "Yakında" bölümü (`app-tile is-planned`): yol haritasındaki uygulama (`AppCatalog.Planned`); tıklanmaz, odak almaz, kesikli kenarlık ve ikincil metin rengi; kontrast AA |
| Breadcrumb | Var | `Components/Breadcrumb.razor` (`page-breadcrumb`): "CRM / Satış / Teklifler"; `AppCatalog` ve route'tan türer, sayfa başına elle yazılmaz |
| BrandLogo | Var | `Components/BrandLogo.razor` — `Size`, `ShowTagline` |
| ThemeToggle | Var | `Components/ThemeToggle.razor` |
| PageHeader | Var | `Components/PageHeader.razor` — `Title`, `Icon`, `Subtitle`, aksiyonlar `ChildContent` |
| Breadcrumb | Stil | `<ol class="breadcrumb-trail">`; bugün detay sayfaları `back-link` kullanır |
| Global arama | Var | `Components/GlobalSearch.razor`: üst çubukta "Arama yapın..." kutusu; 2+ karakterde 300ms gecikmeyle firma, kişi, fırsat, lead ve teklif liste uç noktalarını (`?search=&pageSize=5`) yalnızca izin olan türler için paralel çağırır; sonuçlar gruplu açılır panelde (ok tuşları, Enter, Escape, dışarı tıklama). Ayrı genel arama uç noktası yoktur; tenant, yetki ve kapsam süzgeçleri sunucudaki liste uç noktalarındadır **Komut paleti (13.11.2026):** `Ctrl+K` (macOS `⌘K`) kutuya odaklanır (`shell.js`; kutu boşken sağda `global-search-kbd` ipucu). Sonuçların ilk grubu **Ekranlar**dır: kullanıcının menüsünde görünen ekranlar (`AppNavigation`) adına göre istemcide, beklemeden eşleşir (`ScreenSearch`; büyük/küçük harf ve Türkçe işaret duyarsız: "firsat" → "Fırsatlar"); kayıt sonuçları gelince altına eklenir. Menüde gizli ekran burada da çıkmaz; yetki yine route ve sunucuda denetlenir. |
| NotificationCenter | Yok | Bildirim altyapısı yok |

## Temel

| Bileşen | Durum | Nerede / nasıl |
|---|---|---|
| FormTabs | Var | Tek bölümlü kayıt formunun gövdesini tek sekmeli çubukla sarar: `<FormTabs Title="Birim bilgileri" Label="Birim formu bölümleri">…form-grid…</FormTabs>`. Çok bölümlü formlar için TabBar + TabPanel. |
| SegmentedChoice | Var | İki-üç seçenekli alan için düğme grubu (gerçek radio): `<SegmentedChoice Id="…" Label="…" Options="…" @bind-Value="…" />`; sınıflar `choice-group`, `choice-option`. |
| TagInput | Var | Virgülle ayrılmış değeri haplarla gösteren etiket girişi; sınıflar `tag-input`, `tag-chip`. |
| UnsavedChanges | Var | Kaydedilmemiş değişiklik durumu ve sayfadan ayrılma onayı: `<UnsavedChanges IsDirty="() => dirty" />` (`form-toolbar-actions` içinde); kullanım `form-standards.md`. |
| TabBar + TabPanel | Var | Sekmeler: `<TabBar Tabs="…" State="tabs" />` + `<TabPanel State="tabs" Key="…">…</TabPanel>`; `TabItem(Key, Label, Count?, Errors)`; `Errors > 0` sekmede "!" rozeti gösterir, `TabState.RevealFirstFailing` hatalı ilk sekmeyi açar. Metadata formları `LayoutForm` ile aynısını otomatik yapar. |
| GridColumnPicker | Var | "Sütunlar" düğmesi + açılır menü: ekranın `GridViewState` sütunlarını göster/gizle ve sırala (sabit sütun hep görünür). Seçim kişisel görünümle sunucuda saklanır (`core.GridViews`, ADR-0036); tablo başlıkları `GridHeader`, hücreler `grid.VisibleColumns` ile çizilir, kaydetme şeridi `PersonalViewBar`. Eski DOM tabanlı `ColumnPicker` (tarayıcıda saklanan) kaldırıldı. |
| FilterToggle | Var | "Filtrele" düğmesi: `@bind-Open` + `Count` (etkin süzgeç sayısı rozeti). Sayfa süzgeç satırını `@if (filtersOpen || ActiveFilters > 0)` ile çizer. |
| Pager | Var | "1–8 / 24 kayıt" + sayfa numaraları (ilk, son, geçerli ±1; arası "…"), `PageChanged`. `PageSizeChoice="true"` "25 / sayfa" seçicisini açar: liste sorgusuna `&pageSize={ListPaging.Size}` eklenmiş olmalı (oturum boyunca ortak; 10/25/50/100). |
| SortHeader | Var | Sıralanabilir sütun başlığı (`<th>` içinde): `<SortHeader Label="Başlık" ColumnIcon="phone" Asc="title" Desc="-title" Value="@sort" Default="@DefaultSort" ValueChanged="SortAsync" />`. `ColumnIcon` sütun türü ikonudur (başlıkta metnin önünde). Tıklama artan → azalan → varsayılan; sıralama sunucudadır, yalnızca listenin `sort` anahtarları kullanılır (Firmalar, Sözleşmeler, Siparişler, Operasyon işleri, Destek talepleri). Sıralanamayan sütun başlığı `<span class="th-label"><AppIcon … /> Ad</span>` ile aynı görünür. |
| ViewTabs | Var | **Liste görünümlerinin varsayılan sunumu (sayfa başlığının altında sekmeler):** `<ViewTabs Items="…" Value="…" ValueChanged="…" />`; `Items` = `ViewOption(Key, Label, Group?, Icon?)`, `Icon` sekmede metnin önünde çizilir. Etkin sekmede marka çizgisi; çok sekme telefonda yatay kayar. `NewView` verilirse sonda "+ Görünüm" sekmesi (kişisel görünüm oluşturma) çıkar. Araç çubuğu aramayla başlar (`list-toolbar > .search:first-child`). Örnek: `Crm/Companies.razor`. |
| RowMenu | Var | Satır eylem menüsü "⋯": `<RowMenu Label="…">…</RowMenu>`; içine `<a class="view-menu-item">` / `<button class="view-menu-item">` konur (yetkisiz eylem hiç konmaz, menü boşsa düğme çizilmez). Tablo hücresi `td.row-menu-cell` (başlıkta `th.row-menu-cell`): dar sütun, solunda ayırıcı çizgi, telefonda kart satırında çizgisiz. Satır başına tek "Düzenle" düğmesi yerine kullanılır. |
| OwnerCell | Var | Liste hücresinde sorumlu: yuvarlak baş harf rozeti (renk addan türetilir, 5 token tonu) + ad; ad yoksa "—". `<OwnerCell Name="@Lookups.MemberName(x.OwnerId)" />` |
| ViewSelector | Var | Liste görünüm seçici: `<ViewSelector Items="…" Value="…" ValueChanged="…" />`: "Görünüm: Ad ▾" düğmesi + açılır menü (`role=menu`, `menuitemradio`, seçili öğede onay işareti, Esc ve dışarı tıklama kapatır). `Items` = `ViewOption(Key, Label, Group?)`; aynı `Group` başlık altında toplanır. `NewView` verilirse menü sonuna "+ Yeni görünüm" eylemi eklenir. Araç çubuğu: `<div class="list-toolbar">` içinde seçici + `form.search` + birincil eylem. Örnekler: `Leads`, `Contracts`, `Orders`, `OperationJobs`, `Tickets`. Görünümler yalnızca süzgeçtir; yetki sunucuda uygulanır. |
| Button | Stil | `btn btn-primary` (birincil: dolu turkuaz→mavi degrade `--mx-gradient-cta`, 700, hafif ışıma `--mx-shadow-cta`; hover'da bir ton koyulaşır ve 1 px yükselir; koyu temada açık degrade + koyu metin; 12.11.2026 görsel kararı), `btn-outline-secondary` (ikincil/İptal: dolgulu, çerçevesiz, 600), `btn-tonal` (üçüncül, pastel, 600; `btn-outline-primary` aynı görünümü paylaşır), `btn-ghost` (araç çubuğu: Filtrele, Sütunlar, 600), `btn-outline-danger` (yıkıcı, listede yumuşak kırmızı), `btn-danger` (yıkıcı, onay adımında düz kırmızı dolgu; `--mx-fill-danger`), `btn-sm` (30px), `btn-lg` (44px; yalnızca tek eylemli ekranlar), `btn-icon` (yalnızca ikon, kare; `aria-label` zorunlu). Kaydetme sırasında düğmeye `aria-busy="true"` verilir (tıklanmaz, genişlik sabit, hafif solar); pasif durum soluk değil tonludur. Dolgu düğmelerde odak halkası iki katmanlıdır. Yükseklik `--mx-control-height` (36px, dokunmatikte 44px) |
| IconButton | Stil | `<button class="icon-button" aria-label="…">` + `AppIcon` |
| Badge / StatusBadge | Stil + Var | `badge` + `stage-*`, `status-*`, `user-*` tonları; `TenantStateBadge`, `UserStatusBadge` |
| Card / SectionCard | Var | `Components/FormSection.razor` — ikon başlıklı bölüm kartı (`Title`, `Icon`, `Description`, `Actions`, `Class`); form ve detay bölümleri bununla kurulur. Eski stiller: `record-form`, `detail-section`, `detail-grid`, `chart-card`, `tile` |
| Kayıt düzeni + özet kartı | Stil | `record-layout` (solda `record-summary` > `summary-card`: `summary-identity`, `summary-facts`, `summary-numbers`, `summary-contact`; sağda `record-main`) — Firma ve Kişi detayı |
| Detay düzeni + sağ panel | Stil | `detail-layout` (`detail-main` + yapışkan `detail-side` > `side-card`) — Fırsat detayı |
| SummaryCard / KPIStatCard | Stil | `summary` + `summary-item`, `stat-grid` + `stat-card` (+ `stat-icon`); tıklanabilir KPI: `kpi-grid` + `button.stat-card.kpi-card.tone-*` |
| DashboardHero | Stil | `dash-hero` (+ `dash-hero-solo`: hedef yoksa tek sütun): pano özet bandı; `dash-hero-kicker`, `dash-hero-value`, `dash-hero-note`, `dash-hero-legend`, `dash-hero-stats`. Ekranda en çok bir tane. Örnek: `Crm/CrmDashboard.razor` |
| GoalMeter | Var | `<GoalMeter Realized="…" Weighted="…" Tempo="…" Label="…" />`: hedef göstergesi; yüzdeler hedefe oranla, tempo dikey işaret; yalnızca `dash-hero` içinde |
| DashKpi | Var | Pano KPI kutusu: `<DashKpi Label Icon Tone Value Detail Partial Trend Delta DeltaTrend />`; `Delta` önceki döneme göre değişimdir (`dash-delta` hapı: artı başarı tonu, eksi tehlike, sıfır nötr; işaret ve ok metinde de var, renk tek başına anlam taşımaz); `dash-kpis` ızgarasında (telefonda 2×2). `Trend` verilirse `Sparkline` çizer (telefonda gizli) |
| MyDayPanel | Var | CRM panosunun "Benim günüm" sekmesi (`dash-day`: solda öneriler + `TodayActivities`, sağda `dash-hero-compact` kişisel hedef, "Bu ay kapanabilecekler" ve `dash-week`). Öneri satırı `dash-steps` (`dash-kpi-icon` tonlu ikon, `dash-list-main`, `btn-tonal` eylem; telefonda eylem alta kayar), kapanış satırı `dash-closing-head` + `dash-closing-track` (olasılık çubuğu; metinde yüzde de yazılır). Kart başlığında ikon `h2.dash-card-title`. `dash-kpi-icon.tone-danger` gecikmiş iş, `tone-success` kabul edilen teklif içindir. Önerilerin kurallarla üretildiği kartta yazılır ("Otomatik öneri" rozeti, yapay zekâ değil) |
| RouteMap | Var | `<RouteMap Routes="…" />`: koyu hero zeminli güzergâh haritası (`dash-map`; Lojistik panosu, `Logistics/LogisticsDashboard.razor`). Sınırlar `wwwroot/maps/{tr,europe,world}.json` yerel dosyalarından gelir (CDN yok); şehir metni `LogisticsGazetteer` ile noktaya çevrilir, bulunamayan yer, ülkesi biliniyorsa ülkenin yaklaşık merkezine düşer ve içi boş kesikli halkayla "yaklaşık" işaretlenir (`map-dot.is-approx`); ülke de bilinmiyorsa haritada çizilmez ve altında sayılır. Güzergâh ülke kodunu taşır (`DashboardRouteDto.FromCountry/ToCountry`), başlıkta uluslararası açık sevkiyat sayısı (`International` parametresi) yazılır. Görünüm ülke kodlarından otomatik seçilir (hepsi `TR` ise il haritası, hepsi Avrupa ve Kafkasya kümesindeyse Avrupa, değilse Dünya) ya da `segmented` ile elle seçilir. Renk yalnızca `--mx-map-*` tokenlarından gelir (`map-land`, `map-land-hot`, `map-line`, `map-dot`, `map-label`, `map-route-0..3`; iki temada aynı, zemin koyudur). Güzergâh çizgisi akar ve ışır (`map-route`; ışıma yalnızca burada serbesttir), araç noktaları çizgi boyunca ilerler, şehirlerde nabız halkası vardır; `prefers-reduced-motion` altında animasyon durur. Sağdaki `dash-map-legend` güzergâhları listeler, üzerine gelinince/odaklanınca diğerleri sönükleşir. Renk anlamı tek başına taşımaz: sayı ve güzergâh adı metindedir. Ekranda en çok bir tane |
| Sparkline | Var | `<Sparkline Values="…" Label="…" />`: eksensiz küçük eğilim çizgisi (`--chart-c0`), son nokta işaretli |
| ColumnChart | Var | `<ColumnChart Items="…" Title="…" />`: dikey sütun grafik, tek renk, son sütun (güncel dönem) tam tonda, her sütunda değer etiketi |
| PairColumnChart | Var | `<PairColumnChart Items="…" Title="…" FirstName="Giriş" SecondName="Çıkış" />`: iki serili dikey sütun grafik (`pair-chart`, `--chart-c0` ve `--chart-c1`), üstte `pair-chart-legend` göstergesi; eksende ilk, orta ve son etiket; değerler ipucunda. Örnek: `Warehouse/WarehouseDashboard.razor` |
| DashCard | Stil | `dash-grid` + `dash-card` (+ `dash-card-head`): pano kutusu; `dash-stage` aşama satırları, `dash-list` kayıt listesi. Ek parçalar: `dash-totals` (kart altında küçük toplamlar), `stack-bar` (yığılmış dağılım çubuğu, `role="img"` + metin etiketi), `dash-swatch` (renk kutusu), `dash-meter` (satır içi doluluk ölçeri; değer yanında yüzde olarak yazılır). Renkler `chart-fill-0`…`chart-fill-5` (`--chart-c0`…`--chart-c5`) sınıflarıyla verilir |
| Tabs (kayıt sekmeleri) | Var | `Components/TabBar.razor` + `TabState` (`TabItem(Key, Label, Count)`): sekme ilk seçilişte çizilir, sonra gizli kalır (kaydedilmemiş form kaybolmaz); paneller `role="tabpanel"`, `hidden` |
| Segmented / chip | Stil | `filters` + `chip` (`aria-pressed`), `segmented` |
| Accordion | Stil | `sub-section` + `section-toggle` |
| Notice / Toast | Var | `Components/Notice.razor` (sayfa içi, `role="status"`/`alert`). Merkezi toast kuyruğu yok |
| ConfirmationDialog | Stil | `confirm` satır içi onay deseni |
| EmptyState / ErrorState / Skeleton | Kısmen | `app-state`, `alert`; ortak bileşen yok |
| Drawer | Var | `Components/Drawer.razor` — `Open`, `Label`, `OnClose`; Escape ve arka plan tıklaması kapatır, açılınca odak panele geçer, telefonda tam ekran. İçindeki `record-form`/`detail-section` çerçevesiz çizilir, `form-actions` alta yapışır |
| Modal | Yok | |
| Tooltip / DropdownMenu | Stil | `title` ipucu; profil menüsü `menu-list` deseni |

## Form

| Bileşen | Durum | Not |
|---|---|---|
| TextField, EmailField, PhoneField, URLField, NumberField, TextArea | Stil | `form-control` + uygun `type`/`inputmode` |
| CurrencyField, PercentageField | Stil | `form-control` + `inputmode="decimal"`; biçim `Services/Regional.cs` |
| DateField, DateTimeField, TimeField | Stil | `type="date" / "datetime-local" / "time"` |
| Select | Stil | `form-select`; seçim listeleri `CrmLookups.Choices` |
| SearchableLookup / RecordPicker | Var | `Components/LookupPicker.razor` + `js/lookup.js`: sunucu taraflı arama, debounce, klavye, temizleme, kayda gitme; aranabilir alanlar `LookupSettings` (metadata) |
| Checkbox / Switch / RadioGroup | Stil | `form-check`, `form-check-input` |
| UserPicker / TeamPicker | Kısmen | Kayıt sahibi/ekip seçimleri ilgili formlarda select olarak |
| AddressField | Var | `AddressesSection.razor` |
| Dinamik (özel) alanlar | Var | `CustomFieldInputs.razor` — aynı form sınıflarıyla çizilir; `Only` ile tek alan (form düzeni içinde) |
| Metadata'dan form (LayoutForm) | Var | `Components/LayoutForm.razor` — `Layout` (`FormLayoutDto`), `Field` şablonu, `Shows`, özel alanlar; sekme / bölüm / alan düzeni firmanın form düzeninden (ADR-0021). Yönetim: `Pages/Admin/FormDesigner.razor` |
| FieldError | Var | Alanın altında doğrulama iletisi |
| Alan açıklaması baloncuğu | Var | Giriş alanıyla aynı kapsayıcıdaki `form-text`, alanın üzerine gelince/odaklanınca alanın üstünde baloncuk olarak açılır (yalnızca CSS, `app.css`; ikon yok, formda yer kaplamaz). Hata metni (`form-text text-danger`) ve girişle aynı kapsayıcıda olmayan `form-text` satır içi kalır |
| FileUpload, RichTextEditor, MultiLookup | Yok | |

## Veri

| Bileşen | Durum | Not |
|---|---|---|
| DataGrid | Stil | `table record-table` (kart çerçeveli; başlık satırı dolgulu bant, cümle yazımı kalın başlık, ikonlu; son sütun `⋯` satır menüsü) + `ViewTabs` + `Pager`; telefonda kart listesi. Yoğunluk ortak CSS'tedir (6.11.2026 kararı): satır 44 px, ince başlık bandı (12 px, ikincil renk), 24 px avatar, ince rozet, kayıt bağlantısı alt çizgisiz yarı kalın; yeni grid `record-table` kullanır, ekrana özel yükseklik yazılmaz. Sunucu taraflı sayfalama/süzme/sıralama mevcut uç noktalardan. Şablon örneği: `Crm/Companies.razor` (3.11.2026 sonrası karar, tüm gridler bu şablona taşınır) |
| Kolon gizleme/sıralama/yeniden boyutlandırma, kayıtlı görünüm, toplu işlem | Kısmen/Yok | Toplu işlem yalnız Aktiviteler'de |
| Satış hunisi | Var | `SalesPipeline.razor` — `Stages`, `Current`, `Status`, `CanChangeStage`, `CanClose`, `OnChoose` |
| Takvim | Var | `ActivityCalendar.razor`: ay ızgarası, hafta/gün saat çizelgesi (`cal-time-*`, çakışanlar yan yana, şimdiki zaman çizgisi), ajanda; renk `FamilyOf` → `fam-*` |
| Günün ajandası | Var | `TodayActivities.razor` |
| Grafikler | Var | `BarChart.razor`, `DonutChart.razor` (`--chart-c*`) |
| Kişi kartları (rehber) | Var | `ul.profile-cards` + `li.profile-card`; kişi satırı `owner-cell` + `avatar avatar-tone-*` (baş harfler). Örnek: `Hr/Directory.razor` |
| Organizasyon ağacı | Var | `ul.org-tree` (rol `tree`, alt düğümler `ul[role=group]`): her düğüm `org-node` (avatar + ad + ikincil satır), alt düzeyler sol kenarda ince çizgiyle girintili; vurgulanan düğüm `org-node active` (marka tonu zemin). Renk, köşe ve çizgi yalnızca `--mx-*` tokenlarından. Örnek: `Hr/OrgChart.razor` (13.11.2026) |

## Pano bandı ve adım izi

- `hero-band`: pano ekranlarında `PageHeader` yerine kullanılır; solda `hero-band-text` (üst etiket, tek `h1`, tek cümle), sağda `hero-band-stats` içinde en çok dört `hero-stat`. Tıklanabilen kutu `button.hero-stat` olur. Band yalnızca pano ekranlarındadır; liste ve form ekranlarında `PageHeader` kalır. CRM panosu bu kuralın dışındadır: başlık ve dönem/sorumlu seçimi `PageHeader`'da kalır, altında `dash-hero` çizilir (7.11.2026 kararı).
- `step-dots` / `step-dot`: liste satırında adım ilerlemesi (`aria-label` ile metni verilir).
- `step-track` / `step-track-item`: ayrıntıda dikey adım izi; durum sınıfları `is-approved`, `is-rejected`, `is-active`, `is-cancelled`.

## Bildirim kutusu

- `NotificationBell`: üst çubukta zil; `notification-count` okunmamış sayısı, `notification-panel` açılır liste, `notification-item.is-unread` okunmamış satır. Bildirim metni kaydın hassas ayrıntısını taşımaz; bağlantı kaydı açar ve yetki orada denetlenir.

## Bekleme, boş durum ve geri bildirim (13.11.2026)

Üçü de ortak bileşendir; sayfaya düz "Yükleniyor…" ya da gri "… yok." metni yazılmaz.

| Bileşen | Kullanım |
|---|---|
| `LoadingState` | Veri gelene kadar içeriğin biçimini gösteren iskelet. `Kind`: `Lines` (ayrıntı, panel; varsayılan), `Table` (liste; `record-table` ile aynı çerçeve ve 44 px satır), `Cards` (özet kutuları); `Rows` satır sayısı. `role="status"` taşır, ekran okuyucuya yalnızca "Yükleniyor…" okunur. 150 ms gecikmeyle belirir (kısa yüklemede titremez); `prefers-reduced-motion` altında parıltı durur. Sınıflar: `loading-state`, `skeleton`, `skeleton-bar`, `skeleton-table`, `skeleton-row`, `skeleton-cards`. |
| `EmptyState` | Boş listede tablo yerine çizilir: ikon (`Icon`, varsayılan `inbox`), başlık (`Title`, varsayılan "Kayıt yok."), isteğe bağlı tek cümle (`Text`) ve eylem (`ChildContent`; ör. "Yeni …" düğmesi, yalnızca yazma yetkisi olana). Kart ya da panel içindeki küçük listede `Compact` (çerçevesiz, tek satır). Sınıflar: `empty-state`, `empty-state-compact`. |
| Toast (`ToastService` + `ToastHost`) | İşlem sonucu ("Kaydedildi.", "Silindi.") sağ altta kısa süreli bildirimle verilir; sayfa düzeni kaymaz. `Toasts.Success/Info/Error(metin)`; 4 sn (hata 8 sn) sonra kapanır, en çok dört bildirim durur. Kayıt editörleri `Saved`/`Deleted` geri çağrısından önce kendisi gösterir; sayfa ayrıca başarı kutusu çizmez. **Doğrulama hatası toast ile verilmez**: alanın yanında (`FieldError`) ve formun üstünde (`ErrorSummary`) kalır. `ToastHost` kabukta (`MainLayout`) bir kez çizilir; hata `role="alert"`, diğerleri `role="status"`. Sınıflar: `toast-stack`, `app-toast`, `app-toast-success/info/error` (Bootstrap `.toast` kullanılmaz). |

## Kayıt kartında satır eylemleri ve alt bölümler (13.11.2026)

- Kart içindeki liste satırlarında (`ul.item-list`: adres, telefon/e-posta) "Düzenle / Sil" metin bağlantısı yazılmaz; eylemler `RowMenu` ("⋯") içinde toplanır. Yıkıcı öğe `view-menu-item is-danger` (kırmızı metin + `trash` ikonu) ve menünün son öğesidir.
- Bölüm kartının (`FormSection` → `section-card-body`) içindeki alt bölüm (`sub-section`) ikinci bir kart çizmez: çerçeve ve gölge ortak CSS'te kalkar, kardeş alt bölümler ince çizgiyle ayrılır. Kart içine kart konmaz.

## Ana sayfa ve telefonda sekmeler (13.11.2026)

- Ana sayfa (`Pages/Home.razor`): `PageHeader` ("Hoş geldiniz, {ad}"), ilgili listeye bağlanan özet kutuları (`stat-grid`), solda satış hunisi (`BarChart`), sağda kullanıcının bugünkü işleri (`TodayActivities`). Aynı veri iki grafikle yinelenmez; özet kutuları zaten bağlantı olduğu için ayrıca kısayol kartı (`tiles`) çizilmez.
- `ViewTabs` çubuğu içerik panelini genişletmez (`contain: inline-size`): sekmeler sığmayınca çubuk kendi içinde yatay kayar. Telefonda sayfa yatay taşmamalıdır; yeni ekranın görsel denetiminde 390 px genişlikte `scrollWidth` kontrol edilir.

## Sıralama: sunucuda mı istemcide mi (14.11.2026)

- **Sayfalanan liste** sunucuda sıralanır: kayıt deposu `SortKeys` + `Order(source, sort)` tanımlar, başlık `SortHeader`/`GridHeader` ile bu anahtarları gönderir; bilinmeyen anahtar reddedilir. Her yeni `sort` anahtarı dört testle gelir (yön, geçersiz anahtar, tenant izolasyonu, yetki). Örnek: `MeasureUnitSortTests`, `SalesTargetSortTests`.
- **Tamamı yüklü küçük liste** (sayfalanmayan: platform planları, sektörler, onay bekleyenler) istemcide `ClientSort.Apply(liste, sort, sütunlar)` ile sıralanır; Türkçe sıra, boş değerler sonda, eşit satırlar yerinde kalır. Sayfalanan listede `ClientSort` kullanılmaz (yalnızca görünen sayfayı sıralar, yanıltır).
- **Günlükler** (denetim kaydı, otomasyon çalışma geçmişi, olay teslimleri) varsayılan olarak en yeni önce gelir ve sunucuda sıralanır: sorgu kaydı `SortKeys` taşır (`AuditQuery`, `AutomationRunQuery`, `EventDeliveryQuery`), uç nokta bilinmeyen anahtarı 422 ile reddeder. `SortHeader`'ın `Default` değeri varsayılan azalan anahtardır (ör. `-occurredAt`); başka tablodan çözülen ad sütunları da sıralanır (`user`, `rule`, `tenant`): ad sorguda çözülür; denetim kaydında adı ekranda gösterilmeyenler (sistem, firma üyesi olmayan platform yöneticisi) gerçek adlarıyla sıralanmaz, her iki yönde de sonda kalır.
- **Sıralanmayan listeler (bilinçli istisna):** durum raporu tabloları (platform sağlığı) ve kod sırasıyla gezilen kataloglar (GTİP) başlığa göre sıralanmaz.

## Silme onayı (14.11.2026)

- `ConfirmBox`: yıkıcı eylem ayrı adımda onaylanır. `Text` (soru), isteğe bağlı `ConfirmLabel` (varsayılan "Sil"), `Busy`, `Confirm`, `Cancel`. Kutu `alert alert-warning confirm` + `role="alertdialog"`; onay düğmesi `btn-danger`, yanında "Vazgeç". Açılınca odak "Vazgeç"tedir, Escape kapatır.
- Liste satırında (`ul.item-list`) menüdeki "Sil" satırın yerinde `ConfirmBox` açar; silme başarılıysa toast ("Silindi.") gösterilir. Onaysız tek tıkla silme yazılmaz. Örnek: `AddressesSection`, `ContactPointsSection`.

## Özet kutusunda dönem karşılaştırması (14.11.2026)

- Ana sayfa özet kutuları toplamın altında son 30 günde eklenen kayıt sayısını gösterir: `small.stat-trend` içinde `dash-delta` rozeti (işaretli sayı + ok ikonu; önceki 30 güne göre azaldıysa `is-down`, aynıysa `is-flat`) ve "son 30 günde yeni" etiketi. Yön yalnızca renkle anlatılmaz; ekran okuyucuya iki dönemin sayıları tam cümleyle okunur (`visually-hidden`).
- Sayılar liste uçlarının `createdSince` (dahil) ve `createdBefore` (hariç) süzgeçleriyle alınır (ISO 8601 an; firma, kişi, fırsat, aktivite). Kapsam ve yetki sunucudadır; yüklenemeyen kutuda karşılaştırma çizilmez. Hesap `PeriodChange` ile yapılır.

