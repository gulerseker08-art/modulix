# Bileşen kataloğu

Durum: **Var** = kullanıma hazır, **Stil** = ayrı Razor bileşeni yok, ortak CSS sınıfıyla standart, **Yok** = henüz geliştirilmedi (bkz. `migration-report.md`).

## Kabuk ve gezinme

| Bileşen | Durum | Nerede / nasıl |
|---|---|---|
| AppShell | Var | `Layout/MainLayout.razor`: sol menü + üst çubuk + `main#main-content`; "İçeriğe geç" bağlantısı |
| AppHeader | Var | MainLayout üst çubuğu: tenant logosu, şirket seçici, tema düğmesi, profil menüsü |
| Sidebar | Var | `Layout/NavMenu.razor`: aktif modülün sayfaları (AppCatalog + izin süzgeci), aktif öğede vurgu çizgisi, Menüyü Daralt |
| ModuleSwitcher | Var | `Components/AppLauncher.razor` (logoya tıklanınca "Tüm Uygulamalar" paneli) |
| BrandLogo | Var | `Components/BrandLogo.razor` — `Size`, `ShowTagline` |
| ThemeToggle | Var | `Components/ThemeToggle.razor` |
| PageHeader | Var | `Components/PageHeader.razor` — `Title`, `Icon`, `Subtitle`, aksiyonlar `ChildContent` |
| Breadcrumb | Stil | `<ol class="breadcrumb-trail">`; bugün detay sayfaları `back-link` kullanır |
| Global arama | Yok | Sunucuda tenant/yetki sınırlı genel arama uç noktası yok |
| NotificationCenter | Yok | Bildirim altyapısı yok |

## Temel

| Bileşen | Durum | Nerede / nasıl |
|---|---|---|
| Button | Stil | `btn btn-primary` (birincil), `btn-outline-secondary` (ikincil/İptal), `btn-outline-danger` (yıkıcı), `btn-sm` |
| IconButton | Stil | `<button class="icon-button" aria-label="…">` + `AppIcon` |
| Badge / StatusBadge | Stil + Var | `badge` + `stage-*`, `status-*`, `user-*` tonları; `TenantStateBadge`, `UserStatusBadge` |
| Card / SectionCard | Stil | `record-form`, `detail-section`, `detail-grid`, `chart-card`, `tile` |
| SummaryCard / KPIStatCard | Stil | `summary` + `summary-item`, `stat-grid` + `stat-card` (+ `stat-icon`); tıklanabilir KPI: `kpi-grid` + `button.stat-card.kpi-card.tone-*` |
| Tabs / segmented | Stil | `filters` + `chip` (`aria-pressed`), `segmented` |
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
| Dinamik (özel) alanlar | Var | `CustomFieldInputs.razor` — aynı form sınıflarıyla çizilir |
| FieldError | Var | Alanın altında doğrulama iletisi |
| FileUpload, RichTextEditor, MultiLookup | Yok | |

## Veri

| Bileşen | Durum | Not |
|---|---|---|
| DataGrid | Stil | `table record-table` + `Pager` + `filters`; telefonda kart listesi. Sunucu taraflı sayfalama/süzme mevcut uç noktalardan |
| Kolon gizleme/sıralama/yeniden boyutlandırma, kayıtlı görünüm, toplu işlem | Kısmen/Yok | Toplu işlem yalnız Aktiviteler'de |
| Satış hunisi | Var | `SalesPipeline.razor` — `Stages`, `Current`, `Status`, `CanChangeStage`, `CanClose`, `OnChoose` |
| Takvim | Var | `ActivityCalendar.razor`: ay ızgarası, hafta/gün saat çizelgesi (`cal-time-*`, çakışanlar yan yana, şimdiki zaman çizgisi), ajanda; renk `FamilyOf` → `fam-*` |
| Günün ajandası | Var | `TodayActivities.razor` |
| Grafikler | Var | `BarChart.razor`, `DonutChart.razor` (`--chart-c*`) |
