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
| Global arama | Var | `Components/GlobalSearch.razor`: üst çubukta "Arama yapın..." kutusu; 2+ karakterde 300ms gecikmeyle firma, kişi, fırsat, lead ve teklif liste uç noktalarını (`?search=&pageSize=5`) yalnızca izin olan türler için paralel çağırır; sonuçlar gruplu açılır panelde (ok tuşları, Enter, Escape, dışarı tıklama). Ayrı genel arama uç noktası yoktur; tenant, yetki ve kapsam süzgeçleri sunucudaki liste uç noktalarındadır |
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
