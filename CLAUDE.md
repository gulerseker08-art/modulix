# CLAUDE.md

Modulix: modüler, multi-tenant kurumsal iş yönetim platformu. .NET 10 (ASP.NET Core minimal API + Blazor WebAssembly), EF Core, SQL Server 2025 (Row-Level Security). Kod, yorumlar, commit mesajları ve kullanıcıya dönük metinler **Türkçe**.

Veritabanı modeli (ADR-0002, ADR-0020): **tek paylaşımlı veritabanı** `Modulix`; bütün tenant'lar aynı veritabanındadır, tenant başına ayrı DB yoktur. Platform tabloları (`platform` şeması: kullanıcılar/kimlik, tenant kayıtları, üyelikler, modül erişimleri, oturumlar, e-posta kuyruğu) globaldir ve RLS dışındadır; iş tabloları `TenantId` taşır ve iki katmanla korunur (EF query filter + SQL Server Row-Level Security). Tek `ModulixDbContext` (kimlik ve platform tabloları dahil); `MembershipId` taşıyan tenant tabloları `platform.Memberships`'e bileşik yabancı anahtarla bağlıdır. İşletim: `docs/tenant-isletim.md`.

Ayrıntılı davranış ve uç nokta belgeleri `README.md`'de, mimari kararlar `docs/adr/` altında. Gerekmedikçe README'nin tamamını okuma; ilgili bölümü ara.

## Komutlar

```bash
dotnet build
dotnet test tests/Modulix.ArchitectureTests      # veritabanı gerekmez, hızlı
dotnet test tests/Modulix.Core.Tests             # birim
dotnet test tests/Modulix.Modules.Crm.Tests      # birim
dotnet test tests/Modulix.Modules.Contracts.Tests # birim (sözleşme kuralları)
dotnet test tests/Modulix.IntegrationTests       # SQL Server + Mailpit gerekir
dotnet run --project src/Modulix.Api             # API + arayüz; /health
dotnet run --project src/Modulix.Api -- migrate  # bekleyen migration'lar (tek zincir); db-up.ps1 bunu çağırır
dotnet run --project src/Modulix.Api -- tenant-export <slug> [--dir <dizin>]   # tenant verisini TenantId ile JSON olarak dışa aktarır
dotnet run --project src/Modulix.Api -- tenant-delete <slug> --confirm <slug>  # tenant verisini siler (geri alınamaz; önce dışa aktar)
```

- Yerel veritabanı: `docker compose up -d --wait sqlserver mailpit && docker compose run --rm db-init` (şifreler `.env`'de; Windows'ta `scripts/db-up.ps1`). Port 14333.
- Migration (tek zincir): `dotnet ef migrations add <Ad> --project src/Modulix.Infrastructure --startup-project src/Modulix.Api` (`Persistence/Migrations`). `dotnet ef database update` kullanma. Tek veritabanı bütün tenant'ları etkilediği için migration'lar geriye uyumlu ve kesintisiz olmalı. Yeni tenant tablosu RLS politikasına da eklenir (`TenantIsolationSql.AddTable`).
- CI (`.github/workflows/ci.yml`) Release modunda derler, migration uygular, mimari + Core + CRM + entegrasyon testlerini çalıştırır. CI'da yüksek/kritik açıklı NuGet paketi derlemeyi durdurur.
- Değişiklikten sonra en az `dotnet build` ve mimari testleri çalıştır; veritabanı yoksa entegrasyon testlerini çalıştıramadığını açıkça söyle.

## Mimari kurallar (testlerle denetlenir)

- Proje referansları `tests/Modulix.ArchitectureTests` (`ProjectReferenceRulesTests.AllowedReferences`) ile sabit. Modüller (`Modulix.Modules.*`, `Modulix.AI`) yalnızca `Modulix.Core.Contracts`'a başvurur; `Modulix.Web` yalnızca `Modulix.Shared`'a. Yeni proje eklemeden önce kural tablosunu güncelle.
- Her modül tek bir `IModule` uygulamasıyla kaydolur (`Program.cs` → `ModulixModules.Create()`); izin anahtarları modül anahtarıyla başlar (`crm.companies.read`).
- Tenant'a ait her entity `ITenantEntity` uygular; tablo şeması = modül anahtarı.  Her tenant entity ayrıca `ICompanyShared` ya da `ICompanyScoped` uygular (ADR-0008; yoksa `TableInventoryTests` kırılır). Her yeni tenant tablosu için migration'da `TenantIsolationSql.AddTable` ekle (yoksa RLS testi kırılır).
- Kayıt işlemleri kayıt boru hattından geçer (izin, kapsam, hassas alan, özel alan, audit, outbox). Uç noktada boru hattını atlayıp doğrudan `DbContext`'e yazma.
- Tenant istemciden gelen başlık/parametreden değil, oturumdan belirlenir ve her istekte doğrulanır.
- Firmanın erişime açık olup olmadığı `TenantStatusRules.Operational` ile sorulur (Active ve Trial); sorgularda `Status == TenantStatus.Active` yazma (ADR-0028).
- Uç noktalar `.RequirePermission(...)` / `.RequireModule(...)` ile korunur. Yeni yönetim işlemleri denetim kaydına yazılmalı.
- Destek erişimi (`MembershipStatus.Support`) "etkin üye" değildir: üye sayan, kayıt sahibi seçen ya da bildirim gönderen yeni sorgular `Status == Active` kullanmaya devam eder, destek üyeliğini dahil etmez (ADR-0028).
- Platform uçlarının (`/api/platform`) rolü `PlatformAccessRules` tablosunda tanımlanır (okuma her platform rolüne açık, yazma tablodaki rol; tabloda olmayan yazma ucu yalnızca Owner'a). Yeni platform yazma ucu eklenince tabloya da yaz, yoksa test kırılır (ADR-0028).
- Lisans süresi bitince modül önce ek sürede çalışır, sonra salt okunur olur (`IModuleLicense`, ADR-0028); salt okunurda `RequireModule` altındaki POST/PUT/DELETE 403 döner. Yazmayan POST uçlarının yolu `/preview` ile biter; oturumsuz ve arka plan yollarında modül kontrolü `IModuleLicense.AllowsAsync`/`IsWritableAsync` ile yapılır, `IsModuleEnabledAsync` ile değil.
- E-posta `IEmailQueue.Enqueue` ile kuyruğa yazılır; HTML'e giren kullanıcı girdisi `WebUtility.HtmlEncode` ile kodlanır.
- Yeni sektör modülü için CRM'i örnek al (`src/Modulix.Modules.Crm`: `Domain`, `Persistence`, `Records`).
- Kayıt formlarının düzeni metadata'dan gelir (ADR-0021): modül `IModule.Forms` ile varsayılan sekme/bölüm/alan yerleşimini tanımlar, firma Form tasarımı ekranından özelleştirir, editör `LayoutForm` ile çizer. Yeni formda alan yerleşimini Razor'a sabit yazma; tanıma alan ekle ve editörde o anahtar için girdiyi ver.

## Proje kuralları (ürün ve ekip kararları)

- Ürün: Türkiye pazarı için modüler, yapay zekâ destekli, çok sektörlü B2B SaaS. Modüller CRM (ilk), Lojistik, Antrepo, İK ve sonrası; hepsi ortak Core üzerinde çalışır, müşteri herhangi bir modülle başlayabilir.
- Modüler monolit. Modüller birbirinin tablolarına doğrudan erişmez; iletişim yalnızca public servis arayüzleri, API ve domain event'ler üzerinden olur.
- Core sektöre özel iş mantığı içermez; sektöre özel davranış metadata (sektör profilleri) ile tanımlanır. Kodda `if sektor == "lojistik"` gibi koşullar yasaktır.
- Ortak ana veri (Firma, Kişi, Adres) Core'a aittir; bir kez girilir, tüm modüller kullanır.
- Benzersizlik kuralları `TenantId` ile birlikte tanımlanır (ör. `UNIQUE(TenantId, VergiNo)`). Ham SQL'de de tenant süzgeci zorunludur. Bir tenant'ın tüm verisi `TenantId` ile eksiksiz dışa aktarılabilir ve silinebilir olmalıdır.
- On-prem'e hazırlık: tek imaj, müşteriye özel kod yok, her şey yapılandırmayla.
- Yetkilendirme modeli User – Membership – Company – BusinessUnit – Team – Role – Permission; kayıt kapsamı kendi / takım / birim / şirket / tümü; alan seviyesi yetki desteklenir. Yetki her zaman sunucuda ve sorgu seviyesinde uygulanır; yalnızca arayüzde gizlemek yetkilendirme değildir.
- Yapay zekâ yalnızca tenant ve kullanıcı yetkisi sınırları içinde, modül servisleri üzerinden çalışır; tüm AI işlemleri denetim kaydına yazılır.
- Her önemli yazma işlemi denetim kaydına yazılır; veri değişikliği ve olay yayını aynı transaction içinde yapılır (outbox).
- Gizli bilgiler (şifre, API anahtarı) koda veya repoya yazılmaz.
- Arayüz metinleri için TR/EN çeviri altyapısı **ayrı bir iş kalemidir** (henüz yok); bugün metinler Türkçe'dir.
- Her özellik dört testle teslim edilir: mutlu yol, hata/doğrulama, tenant izolasyonu (A tenant'ı B'nin verisine erişemez), yetki (yetkisiz kullanıcı işlemi yapamaz veya veriyi göremez).
- Küçük, incelenebilir adımlarla ilerle; her adımın sonunda neyin yapıldığını ve neyin kaldığını özetle. Bu kurallara aykırı bir şey gerekiyorsa uygulamadan önce açıkça belirt.

## Arayüz tasarım kuralları (seçenek 4 "Canlı & Enerjik", 3.11.2026 kararı; bütün modüllerde geçerli)

Yeni ve dokunulan her ekran (CRM ve sonraki bütün modüller) bu kurallara uyar; ayrıntı `docs/design-system/` altındadır. Bu kurallara aykırı bir şey gerekiyorsa uygulamadan önce açıkça belirt.

- **Yazı tipi Manrope** (yerel woff2, `--mx-font`); ekranda başka yazı tipi tanımlanmaz. Renk, köşe, gölge ve degrade yalnızca `--mx-*` tokenlarından gelir; bileşene sabit renk ya da ekrana özel CSS yazılmaz, mevcut sınıflar kullanılır. Beyaz metin üstünde kontrast en az 4.5:1 (AA); açık ve koyu tema birlikte tanımlanır.
- **Düğmeler (form/düğme yenilemesi seçenek C, 6.11.2026):** düğme ve giriş yüksekliği `--mx-control-height` (36 px; dokunmatikte 44 px). Birincil `btn btn-primary` (marka degradesi + parlama; ekranda en çok bir tane, sağda ve en sonda), ikincil/İptal `btn-outline-secondary` (dolgulu, çerçevesiz), üçüncül `btn-tonal`, araç çubuğu `btn-ghost`, yıkıcı `btn-outline-danger`, yalnızca ikonlu `btn-icon`. Parlama gölgesi başka öğeye eklenmez.
- **Liste ekranları (grid), ŞABLON (5.11.2026 kararı; Firmalar `Crm/Companies.razor` örnektir, bütün gridler bu şablona taşınır):** sayfa başlığının altında görünümler `ViewTabs` sekmeleri (ikonlu; "Benim …", "Tüm …", durum sekmeleri, ileride kişisel görünümler + "+ Görünüm"), altında araç çubuğu aramayla başlar. Başlıklar `SortHeader` ile **tıklanınca sıralanır** (`ColumnIcon` ile sütun türü ikonlu; sunucuda `sort` anahtarı şart), son sütun `RowMenu` ("⋯") satır menüsüdür. Kullanıcı sütunları ekleyip çıkarabilir (`ColumnPicker`) ve kişisel görünüm kaydedebilmelidir (kişisel görünüm kaydı henüz yok; dilim 2). Aşağıdaki `ViewSelector` açılır seçicisi yalnızca `ViewTabs` sığmayan (çok sayıda görünüm) listelerde kullanılır. Ayrıntı: `docs/design-system/component-catalog.md`.
- **Liste ekranları (grid) ayrıntısı:** `record-table` (kartın içinde, cümle yazımlı ikonlu başlık, `data-label` ile telefonda kart listesi; satır yüksekliği 44 px ve ince başlık bandı ortak CSS'ten gelir, ekrana özel yükseklik yazılmaz), durumlar hap biçimli `badge` (tonlar tokenlardan). Araç çubuğu (`list-toolbar`): görünüm seçimi `ViewTabs` ya da (sığmazsa) sol başta `ViewSelector` ("Görünüm: … ▾"), arama, `FilterToggle` ("Filtrele": select süzgeçlerini açıp kapatır, etkin süzgeç sayısı rozetli; süzgeci olmayan listede yoktur), `ColumnPicker` ("Sütunlar": sütun göster/gizle, tarayıcıda saklanır) ve birincil "Yeni …" düğmesi (birincil eylem sayfa başlığında değil araç çubuğundadır). Sayfalama `Pager`: "1–8 / 24 kayıt" + sayfa numaraları (+ `PageSizeChoice` ile "25 / sayfa"; liste sorgusuna `&pageSize={ListPaging.Size}` eklenir). Sıralanabilir sütun başlığı `SortHeader` (listenin sunucudaki `sort` anahtarlarıyla), sorumlu hücresi `OwnerCell` (avatar + ad). Satır eylemleri (`td.actions` içindeki `btn-outline-*`) ortak CSS ile yumuşak (tonal) görünür. Birbirini dışlayan görünümler ve kayıtlı görünümler `ViewSelector` ile sunulur (`ViewOption` grupları; "Tümü" ilk öğe); çip satırıyla görünüm uydurulmaz. Çip (`chip`) yalnızca açılıp kapanan süzgeçler içindir (ör. "Yalnızca benimkiler").
- **Formlar sekmelidir:** her kayıt formu `LayoutForm`/`TabBar` ile sekmeli kurulur (alan yerleşimi ADR-0021 uyarınca metadata'dan; Razor'a sabit yazılmaz). Sekmeler bölüm kartlarını (`FormSection`) gruplar; etkin sekmede degrade çizgi, dolu sekmelerde sayaç rozeti. Tek sekmeli form da sekme çubuğuyla çizilir (`LayoutForm` her zaman çizer); modül varsayılan form düzenleri çok sekmeli tanımlanır (`FormSectionDefinition.Tab`; birim testle denetlenir). Metadata dışı kayıt formları `TabBar` + `TabPanel` ile (tek bölümlüler `FormTabs` sarmalayıcısıyla) kurulur; hatalı alan içeren sekme `TabItem.Errors` ile işaretlenir ve `TabState.RevealFirstFailing` ile açılır. Sekmeler arasında girilen veri kaybolmaz. Ayar ekranları ve sayfa içi küçük eylem formları bu kuralın dışındadır.
- **Terim:** arayüzde "Lead" yazılmaz, Türkçe adı **"Müşteri adayı"** kullanılır (menü, başlık, düğme, ileti, izin adı). Kodda, API yolunda ve URL'de (`lead`, `crm/leadler`) teknik ad kalır.
- Yeni ekranın ekran görüntüsü açık ve koyu temada kontrol edilir; yeni sınıf ya da token gerekiyorsa önce `docs/design-system/` güncellenir.

## Kod stili

- `.editorconfig`: file-scoped namespace, primary constructor, `var` yerine açık tip, Allman süslü parantez, 4 boşluk.
- Nullable açık; null uyarıları derleme hatasıdır.
- Çevredeki kodun yorum yoğunluğuna ve adlandırmasına uy. README'de belgelenen bir davranışı değiştirirsen README'yi de güncelle.
