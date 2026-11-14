# ADR-0042 — İnsan Kaynakları modülü: epic analizi, ekranlar, iş akışları ve dilim planı

Durum: **Kabul edildi, 13 Kasım 2026** (dilim 1 bu kabulle teslim edilir). Bölüm 8'deki mevzuat maddeleri uzman doğrulaması ister; doğrulanmamış kural zorunlu üretim kuralı yapılmaz (ADR-0032 ilkesi). Numara: ADR-0041 başka bir oturumda kullanıldığı için 0042 alındı; birleştirmede çakışırsa yeniden numaralandırılır.

İlgili: yol haritası M4.1–M4.8 ve D3 ("İK temel"), ADR-0006 (ortak firma/kişi), ADR-0008 (şirket kapsamı), ADR-0016 (lookup), ADR-0021 (form düzeni), ADR-0026 (departman = erişim ağacı), ADR-0027 (kural ve onay altyapısı, Ç6a), ADR-0028 (lisans), ADR-0038 K2–K3 (sürücü = kişi uzantısı; süreli belge Core yeteneği), ADR-0040 (dosya ve ekler).

## 1. Bağlam

Pazardaki İK paketleri (örnek olarak incelenen bir reklam; ürün adı ve görseli kullanılmaz, yalnızca işlev kapsamı alınır) on bir başlığı tek platformda toplar: **İşe giriş ve çıkış (onboarding/offboarding), Zimmet takibi, Özlük yönetimi, İzin yönetimi, Masraf yönetimi, Blog, Anket, Duyurular, Organizasyon şeması, Rehber, Galeri.** Modulix'in yol haritası İK'yı D3'te "temel" olarak (M4.1–M4.3, M4.5, M4.8), bordroyu D4'te öngörür. `AppCatalog.Planned` İK'yı "Yakında" kartı olarak gösteriyordu; modül, entity ve ekran yoktu.

Bu ADR on bir başlığı (epic) analiz eder, her biri için ekranları ve iş akışlarını çıkarır, Modulix kurallarına (tek veritabanı + RLS, kayıt boru hattı, ortak kişi, metadata form, sektörsüz Core) göre nereye ait olduğuna karar verir ve dilimlere böler.

## 2. Epic analizi

| # | Epic | Özü | Modulix'teki karşılığı | Yeni mi? |
|---|------|-----|------------------------|----------|
| E1 | Özlük yönetimi | Çalışanın kimlik, iletişim, görev, istihdam ve özlük dosyası | `hr.employee` = `core.Person` uzantısı (K2) + hassas alanlar (K4) + Core süreli belge (kategori `employee`) | Yeni (dilim 1) |
| E2 | Rehber | Çalışanların aranabilir iletişim kartları | `hr.employee` okuması; özlük alanları hassas olduğu için rehber izni bunları görmez | Yeni (dilim 1) |
| E3 | Organizasyon şeması | Yönetici bağına göre ağaç | `managerEmployeeId` bağı; döngü sunucuda engellenir | Yeni (dilim 1) |
| E4 | İzin yönetimi | Tür, hak ediş, bakiye, talep, onay, takvim | `hr.leave-type`, `hr.leave-balance`, `hr.leave-request`; onay ADR-0027 Ç6a onay servisiyle | Dilim 2 |
| E5 | Masraf yönetimi | Fiş/fatura ile masraf beyanı, onay, ödeme durumu | `hr.expense-claim` + satırlar; belge ADR-0040 dosya; onay Ç6a; ödeme finans işi (ADR-0037 sınırı) | Dilim 3 |
| E6 | Zimmet takibi | Demirbaşın çalışana teslimi ve iadesi, tutanak | `hr.asset` (demirbaş) + `hr.asset-assignment` (teslim/iade hareketi); tutanak çıktısı | Dilim 4 |
| E7 | İşe giriş ve çıkış | Görev listesi şablonu, sorumlu, son tarih; çıkışta zimmet iadesi ve hesap kapatma | `hr.checklist-template` + `hr.employee-checklist`; çıkış listesi açık zimmet varsa kapanmaz | Dilim 5 |
| E8 | Duyurular | Kuruma/departmana duyuru, okundu bilgisi | Kurum içi iletişim (K7): `hr.announcement` + okundu kaydı; Core bildirim altyapısı | Dilim 6 |
| E9 | Anket | Çalışan memnuniyeti / nabız anketi | Pazarlama anket motoru yeniden kullanılır mı, ayrı mı: K8 | Dilim 7 |
| E10 | Blog | Kurum içi yazılar | Kurum içi iletişim: `hr.post` (yazı + yayın durumu) | Dilim 8 |
| E11 | Galeri | Etkinlik fotoğraf albümleri | Kurum içi iletişim: `hr.album` + ADR-0040 dosyaları | Dilim 8 |

Kapsam dışı (bu ADR'de yok): bordro ve SGK/vergi bildirimleri (M4.6–M4.7, D4), puantaj/vardiya (M4.4), aday ve işe alım (M4.8 ikinci yarısı), performans ve hedef yönetimi, eğitim planlama.

## 3. Kararlar

**K1 — Ayrı modül: `Modulix.Modules.Hr`** (anahtar ve şema `hr`, yalnızca `Core.Contracts`'a başvurur). Müşteri İK'yı CRM'siz satın alabilir; modül başka modülün tablosuna dokunmaz. Arayüzde "İnsan Kaynakları" uygulaması Yönetim grubundadır; "Yakında" kartı kaldırıldı.

**K2 — Çalışan tek kimliktir.** Çalışan ayrı kişi tablosu değildir; `core.Person` kaydının İK uzantısıdır (`hr.Employees.PersonId`, `UNIQUE(TenantId, PersonId)`), ADR-0038 K2'deki sürücü kalıbıyla aynıdır: aynı kişi hem sürücü hem çalışan olabilir, ad/iş e-postası/iş telefonu tek yerdedir. Kullanıcı hesabı (`UserId`) isteğe bağlıdır: hesabı olmayan çalışan ve çalışan olmayan kullanıcı desteklenir (M4.1). Hesap bağlıysa izin, masraf, zimmet onayı gibi self servis işlemlerde "ben" bu karttır.

**K3 — Departman İK seçim listesidir, erişim ağacı değildir.** `hr.department` (Y9b) çalışanın organizasyon birimidir. ADR-0026'nın `BusinessUnit` ağacı kayıt **görünürlüğünü** belirler ve Core içindedir (`Core.Contracts`'ta sözleşmesi yok). İki kavram karıştırılmaz; ihtiyaç doğarsa İK departmanı → BusinessUnit eşlemesi Core sözleşmesiyle (ad taslak `IBusinessUnitDirectory`) sonra kurulur. Organizasyon şeması departmandan değil yönetici bağından çizilir.

**K4 — Özlük bilgisi hassas alandır.** T.C. kimlik no, doğum tarihi, ev adresi, acil durum kişisi ve telefonu, IBAN `IModule.SensitiveFields` ile `hr.employees.personal` iznine bağlıdır: rehberi gören herkes çalışanları görür ama özlük bilgisini göremez, yazamaz, süzemez; denetim kaydında değer gizlenir. Sağlık ve ücret bilgisi hiçbir dilimde rehber izniyle açılmaz (M4.5). KVKK açık rıza ve saklama süresi bölüm 8'de.

**K5 — Belgeler Core süreli belgesidir.** İş sözleşmesi, sağlık raporu, diploma, sertifika, İSG eğitimi, çalışma izni `core.credential` kaydıdır (yeni kategori `employee`; ADR-0038 K3). Bitiş uyarısı mevcut `DateRelative` kuralıyla verilir. Belgenin zorunluluğu (ör. "İSG eğitimi olmadan sahaya çıkamaz") İK'nın kuralıdır, Core'un değil.

**K6 — Onay akışları genel onay servisindendir.** İzin talebi ve masraf beyanı ADR-0027 Ç6a onay sürecine bağlanabilen kayıt türleridir (`IModule.ApprovalTargets`; masrafta tutar + para birimi korunur). Varsayılan onaylayan çalışanın yöneticisidir; çok adımlı süreç (yönetici → İK → finans) firma tarafından tanımlanır. Onaylanmadan izin bakiyeden düşmez, masraf "ödenecek"e geçmez. Onay kararı modülün komutuna girdi olur, modülün doğrulamasının yerine geçmez (bakiye yetersizse onay da reddedilir).

**K7 — Kurum içi iletişim İK modülündedir.** Duyuru, blog ve galeri sektörsüzdür ve İK paketinin parçası olarak satılır; ayrı bir "intranet" modülü bugün açılmaz (ADR-0028 lisans tanımı yeterli). Hedef kitle departman/çalışma yeri/herkes olarak çalışan kartından seçilir. Ayrı modül ihtiyacı (İK almadan duyuru) doğarsa kayıt türleri taşınabilir; şema adı `hr` kalır.

**K8 — Anket motoru tekrar yazılmaz.** Pazarlama modülünün anket portalı (`SurveyPortal`) müşteri anketidir; çalışan anketi kimlik doğrulamalı ve isteğe bağlı anonimdir. Dilim 7'de Pazarlama motorunun Core'a taşınabilir parçaları (soru tipi, yanıt toplama) incelenir; taşınamıyorsa İK'da küçük bir anket kaydı açılır. Karar dilim 7'ye ertelendi. **Dilim 7 kararı:** repodaki tek "anket" Destek modülünün talep memnuniyeti puanıdır (tek 1–5 puan, belirteçli bağlantı); soru tipi ve yanıt toplama motoru yoktur, Core'a taşınacak parça çıkmadı. İK'da küçük bir anket kaydı açıldı (bölüm 15).

**K9 — Silme yoktur.** Çalışan kartı, izin talebi, masraf beyanı, zimmet hareketi silinmez: iptal edilir ya da durumu değişir (yasal saklama). Tenant dışa aktarma ve silme (`tenant-export`/`tenant-delete`) bütün `hr` tablolarını kapsar.

**K10 — Mevcut platform yetenekleri aynen kullanılır.** İzinler, rol şablonları, `AuditedEntities`, `CustomizableEntities`, `ChoiceLists`, `Lookups`, `Forms` (çok sekmeli), `Automation`, `CredentialOwners`, `SensitiveFields`; ekranlar grid şablonu (`ViewTabs`, `SortHeader`, `ColumnPicker`, kişisel görünüm) ve tasarım tokenları. Kodda sektör koşulu yoktur.

## 4. Veri modeli

Hepsi `TenantId` taşır ve RLS'e `TenantIsolationSql.AddTable` ile eklenir. Kapsam: dilim 1'de çalışan kartı `ICompanyShared`; işveren şirket bağı (M4.1 "işveren şirket") ve izin/masraf/zimmetin `ICompanyScoped` olup olmayacağı şirket süzgecinin uçtan uca testiyle birlikte dilim 2'de netleşir.

| Şema.Tablo | Dilim | İçerik | Benzersizlik / not |
|---|---|---|---|
| `hr.Employees` | 1 | kişi, sicil no, kullanıcı, unvan, departman, yönetici, çalışma şekli, çalışma yeri, giriş/ayrılış, durum, özlük alanları | `(TenantId, PersonId)`, `(TenantId, EmployeeNumber)`, `(TenantId, UserId)`, `(TenantId, NationalId)` süzgeçli; yönetici FK `(TenantId, Id)` |
| `hr.LeaveTypes` | 2 | ad, ücretli mi, yıllık hak mı, yarım gün, belge zorunlu mu | tür seçim listesi + kural |
| `hr.LeaveBalances` | 2 | çalışan, tür, dönem, hak, kullanılan, devreden | `(TenantId, EmployeeId, TypeId, Period)`; satır sürümüyle yarış engeli (M4.3) |
| `hr.LeaveRequests` | 2 | çalışan, tür, başlangıç/bitiş, yarım gün, gün sayısı, açıklama, durum | çakışan onaylı talep yok |
| `hr.ExpenseClaims` / `hr.ExpenseLines` | 3 | beyan (dönem, toplam, para birimi, durum) ve satır (tarih, tür, tutar, KDV, belge) | onaylı beyan değişmez |
| `hr.Assets` / `hr.AssetAssignments` | 4 | demirbaş (tür, seri no, durum) ve teslim/iade hareketi | bir demirbaş aynı anda tek çalışanda |
| `hr.ChecklistTemplates` / `hr.EmployeeChecklists` (+ görevler) | 5 | işe giriş/çıkış şablonu ve çalışana açılan liste | çıkış listesi açık zimmetle kapanmaz |
| `hr.Announcements` (+ okundu) | 6 | başlık, metin, hedef kitle, yayın aralığı, sabitlenmiş | okundu `(TenantId, AnnouncementId, EmployeeId)` |
| `hr.Posts`, `hr.Albums` | 8 | blog yazısı; albüm + dosyalar | dosyalar ADR-0040 |

## 5. Ekranlar ve iş akışları

### Dilim 1 (bu teslim)

| Ekran | Yol | Kim | Ne yapar |
|---|---|---|---|
| Çalışanlar | `/ik/calisanlar` | `hr.employees.read` (+ `write`) | Grid şablonu: görünümler Çalışanlar (ayrılmamış) / İzinde / Ayrılanlar / Tüm kartlar + kişisel görünüm; arama (ad, soyad, e-posta, telefon, sicil no, unvan); Filtrele (departman, çalışma şekli); Sütunlar; sıralanabilir başlıklar; "⋯" menüsü; "Yeni çalışan" |
| Çalışan kartı | (Çalışanlar içinde) | aynı; özlük sekmesi `hr.employees.personal` | Sekmeli form (metadata): Genel bilgiler (kişi, sicil, işe giriş, durum, hesap; unvan, departman, yönetici, çalışma yeri) · İstihdam (çalışma şekli, ayrılış) · Özlük bilgileri (T.C. kimlik, doğum, IBAN, adres, acil durum) · Notlar ve ek bilgiler; özet kartında kıdem; altında "Özlük belgeleri" paneli (süreli belge) |
| Rehber | `/ik/rehber` | `hr.employees.read` | Ayrılmamış çalışanların kartları (avatar, ad, unvan · departman, e-posta ve telefon bağlantısı, izinde rozeti); arama ve departman süzgeci |
| Organizasyon şeması | `/ik/organizasyon-semasi` | `hr.employees.read` | Yönetici bağından ağaç; departman seçilince o departmandakiler vurgulanır (ağaç daraltılmaz) |

İş akışları:

1. **İşe alım kaydı:** İK uzmanı "Yeni çalışan" → "Yeni kişi" (ad, soyad, iş telefonu, iş e-postası; ortak kişi kaydı açılır) ya da "Kayıtlı kişi" (ör. CRM'de zaten kişi olan) → işe giriş tarihi, unvan, departman, yönetici → Kaydet. Sunucu: kişi başına tek kart, sicil/kullanıcı/T.C. kimlik benzersizliği, T.C. kimlik ve IBAN denetimi, yönetici döngüsü. Denetim kaydı + `hr.employee.created` olayı.
2. **Özlük dosyası:** İzinli kullanıcı özlük sekmesini doldurur, karttaki panelden belge ekler (iş sözleşmesi, sağlık raporu...); bitiş tarihi olan belge için firma "bitişe 30 gün kala bildir" kuralı yazabilir.
3. **Görev değişikliği:** Unvan/departman/yönetici güncellenir; değişiklik denetim kaydında önce/sonra ile görünür (tarihli görev geçmişi M4.2 dilim 2+).
4. **Ayrılış:** Durum "Ayrıldı" + ayrılış tarihi (ileri tarih "planlanan çıkış" olarak girilebilir); kart silinmez, rehber ve şemadan düşer, "Ayrılanlar" görünümünde kalır. (Dilim 5'te çıkış kontrol listesi bu adımı başlatır.)

### Sonraki dilimlerin ekranları ve akışları (tasarım)

- **İzin (dilim 2):** "İzinlerim" (self servis: bakiye kartları, yeni talep, geçmiş), "İzin talepleri" (yönetici/İK grid: Onay bekleyen / Onaylanan / Reddedilen), "İzin takvimi" (ekip/departman ay görünümü), "İzin türleri ve haklar" (ayar). Akış: çalışan talep açar → gün sayısı tatil/hafta sonu ve yarım gün kuralıyla sunucuda hesaplanır → çakışma ve bakiye denetimi → onaya gider (varsayılan yönetici, Ç6a) → onaylanınca bakiye aynı işlemde düşer, çalışan durumu izin günlerinde "İzinde" görünür → iptal onaydan önce serbest, sonra yeni onay ister.
- **Masraf (dilim 3):** "Masraflarım" (beyan + satırlar, fiş fotoğrafı), "Masraf onayları", "Ödenecek masraflar" (finans). Akış: taslak → gönder (belgesiz satır uyarı/engel tenant ayarı) → yönetici onayı → finans onayı → "Ödendi" işareti (ödeme kaydı muhasebe modülünde, ADR-0037).
- **Zimmet (dilim 4):** "Demirbaşlar" (grid: Depoda / Zimmetli / Arızalı / Hurda), çalışan kartında "Zimmetler" paneli. Akış: teslim (çalışan ekranda teslim aldığını onaylar, tutanak PDF) → iade (durum notu) → çıkışta açık zimmet listesi.
- **İşe giriş/çıkış (dilim 5):** "Kontrol listesi şablonları" (ayar; görev, sorumlu rol, gün ofseti), çalışan kartında "Giriş/Çıkış süreci" paneli, "Görevlerim". Akış: işe giriş tarihi girilince şablondan liste açılır (kural) → görevler sorumluya düşer → tamamlanır; çıkışta zimmet iadesi ve hesap kapatma (kullanıcı devre dışı + kayıt devri ADR-0004) görevleri.
- **Duyurular (dilim 6):** "Duyurular" (akış görünümü, sabitlenmiş üstte), yönetim grid'i; hedef kitle, yayın aralığı, okundu oranı.
- **Anket (dilim 7):** K8 kararına göre.
- **Blog ve galeri (dilim 8):** yazı listesi/okuma, albüm ızgarası; dosya altyapısı ADR-0040.

## 6. Dilimler

Her dilim dört testle teslim edilir (mutlu yol, doğrulama, tenant izolasyonu, yetki).

| Dilim | İçerik | Bağımlılık |
|---|---|---|
| 0 | Bu ADR | — |
| 1 | Modül, çalışan kartı (özlük, hassas alanlar, özlük belgeleri), rehber, organizasyon şeması | — |
| 2 | İzin türleri, hak/bakiye, talep, onay (**yapıldı**, bölüm 10); takvim, Ç6a bağlantısı, işveren şirket bağı (2b) | 1, Ç6a (var) |
| 3 | Masraf beyanı, belge, onay, ödeme durumu (**yapıldı**, bölüm 11); Ç6a tutar onayı (3b) | 1, ADR-0040 (var), Ç6a |
| 4 | Demirbaş ve zimmet, tutanak çıktısı (**yapıldı**, bölüm 12) | 1 |
| 5 | İşe giriş/çıkış kontrol listeleri (**yapıldı**, bölüm 13) | 1, 4 |
| 6 | Duyurular (**yapıldı**, bölüm 14) | 1 |
| 7 | Anket (K8; **yapıldı**, bölüm 15) | 1 |
| 8 | Blog ve galeri (**yapıldı**, bölüm 16) | 1, ADR-0040 |

## 7. Riskler

- Çalışan kartının kişi kaydına bağlı olması, kişi birleştirmede (ADR-0013) çalışan kartının taşınmasını gerektirir; bugün kişi kartında kullanılıyorsa silme 409 döner, birleştirme dilim 2'de denenmeli.
- İK departman listesi ile ADR-0026 departman ağacı kullanıcı gözünde karışabilir; ekranlarda İK listesi "Departman", erişim ağacı Yönetim'de kalır.
- Organizasyon şeması istemcide en çok 2000 çalışanla çizilir; büyük kurumda sunucu tarafı ağaç ucu gerekir.
- `hr` tabloları bugün şirket kapsamı taşımıyor; çok şirketli grupta İK verisinin şirket bazında ayrılması dilim 2 kararıdır.

## 8. Doğrulanmadı (mevzuat)

- **KVKK:** özlük verisinin işleme şartı, açık rıza gereken alanlar (sağlık, adli sicil), saklama süreleri ve imha; aydınlatma metni.
- **4857 sayılı İş Kanunu:** yıllık izin hak ediş basamakları (kıdeme göre gün), yaş istisnaları, bölünerek kullanım, ücretsiz/mazeret izinleri; resmî tatil ve hafta tatili sayımı.
- **Özlük dosyasında zorunlu belgeler** ve İSG eğitim geçerlilik süreleri.
- **Masraf:** fiş/fatura türleri, KDV indirimi ve harcırah sınırları (muhasebe/vergi).
- Tablo, arayüz ve izin adları dilim 2+ için taslaktır.

## 9. Dilim 1: modül, çalışan kartı, rehber, organizasyon şeması (13 Kasım 2026)

- **Modül:** `Modulix.Modules.Hr` (anahtar ve şema `hr`, yalnızca `Core.Contracts`; mimari referans tablosu güncellendi), izinler `hr.employees.read/write/personal`, rol şablonları "İK uzmanı" (üçü) ve "Çalışan (rehber)" (okuma). Demo firmasında (acme) açılır.
- **Çalışan (`hr.Employees`):** bölüm 4'teki alanlar; kişi alanları (`firstName`, `lastName`, `fullName`, `email`, `phone`) kişi kaydından toplu yüklenen salt okunur alanlardır (yönetici lookup etiketi `fullName`). Doğrulamalar: kişi başına tek kart; sicil no, kullanıcı (etkin üye), T.C. kimlik no benzersiz; T.C. kimlik denetim haneleri; IBAN TR + mod 97; "Ayrıldı"da ayrılış tarihi zorunlu; ayrılış ≥ işe giriş; doğum < işe giriş; yönetici kendisi ya da astı olamaz (zincir izlenir); silme 409. Süzgeç `current=true` ayrılmamışları verir. Migration `HrEmployees`: tablo + `TenantIsolationSql.AddTable` + şema yetkisi; `init.sql` güncellendi.
- **Özlük belgeleri:** Core süreli belgeye `employee` kategorisi ve altı hazır tür eklendi; çalışan `CredentialOwners` ile sahip türüdür.
- **Arayüz:** "İnsan Kaynakları" uygulaması (Yönetim grubu; "Yakında" kartı kaldırıldı): Çalışanlar (grid şablonu + sekmeli kart + özlük belgeleri paneli), Rehber (`profile-cards`), Organizasyon şeması (yeni `org-tree` sınıfı; `docs/design-system/component-catalog.md` önce güncellendi). TR/EN/AZ menü anahtarları.

**Bilerek yapılmadı:** izin, masraf, zimmet, giriş/çıkış, iletişim dilimleri; tarihli görev geçmişi (M4.2); işveren şirket bağı; içe aktarma tanımı (ADR-0033; ayrı küçük iş); İK departmanı → BusinessUnit eşlemesi.

### Test sonuçları (Dilim 1)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 17/17 geçti |
| `dotnet test tests/Modulix.ArchitectureTests` | 73/73 geçti |
| `dotnet test tests/Modulix.Core.Tests` | 940/940 geçti |
| `dotnet test tests/Modulix.Modules.Fleet.Tests`, `Modulix.Modules.Logistics.Tests` | 14/14, 21/21 geçti |
| `HrEmployeeTests` (entegrasyon, 4 test) | Derleniyor; bu ortamda SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır) |
| `Modulix.Web.Tests` (menü testleri güncellendi) | Bu ortamın SDK'sı (Ubuntu 10.0.115) mevcut Razor dosyalarında derleme hatası verdiği için **çalıştırılamadı**; yeni Razor dosyaları hatasız derleniyor (CI 10.0.100 kullanır) |

## 10. Dilim 2: izin yönetimi (13 Kasım 2026)

Bölüm 5'teki izin akışı şu kararlarla uygulandı; K6'nın genel onay süreci (Ç6a) bağlantısı **dilim 2b**'ye kaldı (aşağıda).

- **İzin türü** `hr.leave-type` (Y9b seçim listesi): kategoriler `annual` (yıllık izin, haktan düşer), `excuse` (ücretli; mazeret, evlilik, ölüm, babalık, doğum, idari), `sick` (rapor), `unpaid`. Kod yalnızca kategoriye bakar; talep, açıldığı andaki kategoriyi saklar. Türlerin yasal süreleri koda yazılmadı (bölüm 8).
- **İzin hakkı** `hr.LeaveBalances`: çalışan + yıl tekil; hak ve devreden gün İK tarafından girilir (kıdeme göre hak ediş hesabı mevzuat doğrulaması bekler); kullanılan gün yalnızca iş akışınca yazılır. Onay, hakkın satırını sürümüyle aynı işlemde günceller: eşzamanlı iki onay bakiyeyi aşamaz (biri 409, M4.3).
- **Resmî tatil** `hr.Holidays`: tarih tekil, yarım gün (arife) desteklenir; tarihler koda yazılmaz.
- **Gün hesabı:** başlangıç ve bitiş dahil, cumartesi–pazar düşülür, tatil listesi düşülür; yarım günlük talep 0,5. Talep tek takvim yılında kalır (yıl sonunu aşan izin ikiye bölünür). Cumartesinin iş günü sayılması tenant ayarı olarak sonra eklenebilir (bölüm 8).
- **İzin talebi** `hr.LeaveRequests`: çakışan bekleyen/onaylı talep yok; yıllık izinde kalan hak, bekleyen yıllık taleplerle birlikte yetmeli. "İzin yönetimi" izni olmayan kullanıcı yalnızca kendi kartına talep açar. Talep anında çalışanın yöneticisinin kullanıcısı onaylayan olarak yazılır ve **değişmez** (M4.2: yönetici değişikliği geçmiş onay merciini yeniden yazmaz); yöneticisi ya da yöneticinin kullanıcısı yoksa yalnızca İK karar verir.
- **İş akışı:** durum yalnızca `/api/hr/leave-requests/{id}/actions` komutlarıyla değişir (Lojistik K7 kalıbı: iş akışı kapısı + kayıt boru hattı, audit, outbox). Onay/ret: bekleyen talep, `hr.leaves.approve` + (onaylayan ya da `hr.leaves.manage`); kimse kendi talebine karar veremez; ret gerekçelidir. İptal: bekleyen talebi sahibi ya da İK; onaylı talebi yalnızca İK (yıllık izin hakka geri eklenir). Talep değiştirilmez ve silinmez.
- **Görünürlük:** veri kapsamı (Y8) alanı `employeeUserId`; "kendi" kapsamındaki kullanıcı kendi taleplerini ve hakkını, ayrıca onaylayanı olduğu talepleri görür. Alan adı bilerek `ownerId` değildir: Core'un atama e-postası `ownerId` değişiminde "kayıt size atandı" iletisi gönderir.
- **Rol şablonları:** "Çalışan (rehber)" artık kendi izinlerini görür ve talep açar (kapsam "kendi"); yeni "Yönetici (izin onayı)" (kapsam "kendi" + onay); "İK uzmanı" hepsi.
- **Arayüz:** İzinlerim (bu yılın hak kartları, taleplerim, yeni talep; gün sayısı ve kalan hak önizlemeli), İzin talepleri (grid şablonu; görünümler Onayımı bekleyenler / Onay bekleyen / Onaylanan / Reddedilen / Tüm talepler; talep kartında eylemler), İzin hakları (yıl sekmeli grid + sekmeli form), Resmî tatiller (ayar ekranı). Yeni CSS sınıfı yok.
- Migration `HrLeaves`: üç tablo + `TenantIsolationSql.AddTable`.

**Dilim 2b'ye kalanlar:** firmanın Ç6a onay sürecine bağlanması (`ApprovalTargets`, çok adımlı yönetici → İK), izin takvimi (ekip/departman ay görünümü), onaylanan izinde çalışanın durumunun "İzinde" görünmesi, onaylanan/reddedilen talep bildirimi (Core bildirim), yıl sonu devreden gün aktarımı, cumartesi ayarı, işveren şirket bağı.

### Test sonuçları (Dilim 2)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 26/26 geçti (gün hesabı 7 durum, tür kategorileri, rol kapsamları, sabitler) |
| `dotnet test tests/Modulix.ArchitectureTests` | 73/73 geçti |
| `dotnet test tests/Modulix.Core.Tests` | 940/940 geçti |
| `HrLeaveTests` (entegrasyon, 4 test: mutlu yol, doğrulama, tenant izolasyonu, yetki) | Derleniyor; yerelde SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır) |
| `Modulix.Web.Tests` (menü testi güncellendi) | Yerel SDK nedeniyle **çalıştırılamadı**; yeni Razor dosyaları hatasız derleniyor |

## 11. Dilim 3: masraf yönetimi (13 Kasım 2026)

Bölüm 5'teki masraf akışı izin dilimindeki kalıpla (iş akışı kapısı, veri kapsamı + onaylayan) uygulandı.

- **Masraf beyanı** `hr.ExpenseClaims`: çalışan, başlık, para birimi (beyan başına tek para birimi; satırlar onda), açıklama; toplam ve satır sayısı satırlardan aynı işlemde türetilir (sürüm alanıyla). Durumlar: taslak → onaya gönderildi → onaylandı → ödendi; gönderilmiş beyan **düzeltmeye iade** edilebilir (taslağa döner, gerekçeli) ya da reddedilir (gerekçeli); taslak ve gönderilmiş beyan iptal edilir. Beyan silinmez.
- **Masraf satırı** `hr.ExpenseLines`: tarih (ileri olamaz), tür (Y9b `hr.expense-type`; kategori ulaşım/konaklama/yemek/diğer), açıklama, KDV dahil tutar, isteğe bağlı KDV (tutarı aşamaz). KDV indirimi, harcırah ve belge türü kuralları vergi doğrulaması beklediği için koda yazılmadı (bölüm 8). Fiş ve fatura satıra ADR-0040 dosyası olarak eklenir; dosya yetkisi satırın güncelleme iznidir (çalışanın kendi satırı).
- **Onaylayan** gönderim anında çalışanın yöneticisinin kullanıcısıdır; iade sonrası yeniden gönderimde yeniden belirlenir. Kimse kendi beyanına karar veremez.
- **Ödeme** finans işidir (ADR-0037): burada yalnızca "ödendi" işareti, işaretleyen, zaman ve ödeme referansı tutulur; muhasebe fişi ve kasa/banka hareketi üretilmez. Görev ayrılığı: kimse kendi beyanının ödemesini işaretleyemez; "Finans (masraf ödemesi)" şablonu karar veremez.
- **Görünürlük:** veri kapsamı alanı `employeeUserId`; onaylayan beyanı ve satırlarını "kendi" kapsamıyla da görür.
- **Arayüz:** Masraflarım (beyanlarım, yeni beyan, beyan kartı: satırlar, satır başına fiş/fatura, onaya gönder) ve Masraf beyanları (grid; görünümler Onayımı bekleyenler / Onay bekleyen / Ödenecek / Ödenen / Reddedilen / Tüm beyanlar). Yeni CSS sınıfı yok.
- Migration `HrExpenses`: iki tablo + `TenantIsolationSql.AddTable`.

**Dilim 3b'ye kalanlar:** Ç6a tutar eşiğine bağlı çok adımlı onay (ör. 5.000 TRY üstü İK/finans), beyanın Excel/PDF çıktısı, faturalanabilir kalem/muhasebe köprüsü (ADR-0037), kart/avans mahsubu, fiş zorunluluğu ayarı.

### Test sonuçları (Dilim 3)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 27/27 geçti |
| `dotnet test tests/Modulix.ArchitectureTests` | 73/73 geçti |
| `dotnet test tests/Modulix.Core.Tests` | 964/964 geçti |
| `HrExpenseTests` (entegrasyon, 4 test) | Derleniyor; yerelde SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır) |
| `Modulix.Web.Tests` (menü testi güncellendi) | Yerel SDK nedeniyle **çalıştırılamadı**; yeni Razor dosyaları hatasız derleniyor |

## 12. Dilim 4: zimmet takibi (13 Kasım 2026)

- **Demirbaş** `hr.Assets`: envanter numarası (tenant içinde tekil, büyük harf), ad, kategori (Y9b `hr.asset-category`; kategori bilişim / giriş ve kimlik / mobilya / ekipman), seri no, marka, model, alış tarihi, tutar ve para birimi, durum, not. Durum elle depoda / bakımda / hurda / kayıp arasında değişir; "zimmetli" ve şu anki zimmetli çalışan yalnızca zimmetten türetilir (zimmetteki demirbaşın durumu elle değişmez). Araçlar Filo modülündedir (ADR-0038), burada tutulmaz. Zimmet geçmişi olan demirbaş silinmez, hurdaya ayrılır.
- **Zimmet** `hr.AssetAssignments`: demirbaş, çalışan, teslim tarihi, beklenen iade, teslimdeki durum. Açılınca demirbaş aynı işlemde "zimmetli" olur (sürüm alanıyla); bir demirbaşın aynı anda tek açık zimmeti olur (süzgeçli tekil indeks veritabanında da korur). Zimmet değiştirilmez ve silinmez.
- **İş akışı:** çalışan teslim aldığını ekrandan onaylar (yalnızca kendisi, bir kez); demirbaş sorumlusu iade alır (iadedeki durum) ya da kayıp olarak kapatır (açıklama zorunlu). Demirbaş aynı işlemde depoya ya da kayba döner.
- **Tutanak:** `GET /api/hr/asset-assignments/{id}/document` zimmet tutanağını PDF olarak üretir (QuestPDF, teklif ve sözleşme PDF'leriyle aynı altyapı); veriler kayıt boru hattından, çağıranın görebildiği kadarıyla okunur. Tutanak metninin yasal biçimi doğrulanmadı (bölüm 8); firmaya özel metin sonra şablonla gelebilir.
- **Görünürlük:** zimmette veri kapsamı alanı `employeeUserId`, demirbaşta şu anki zimmetli kullanıcı (`currentUserId`): "kendi" kapsamındaki çalışan yalnızca kendi zimmetlerini ve üzerindeki demirbaşları görür; depodaki demirbaşları göremez.
- **Rol şablonları:** "Çalışan" zimmet okumayı, "İK uzmanı" okuma ve yönetimi, yeni "Demirbaş sorumlusu" yalnızca demirbaş ve zimmeti (özlük, izin, masraf verisi yok) alır.
- **Arayüz:** Demirbaşlar (grid; görünümler Tüm / Depoda / Zimmetli / Bakımda / Hurda ve kayıp; kartta zimmet geçmişi, Zimmetle, eylemler, tutanak), Zimmetlerim (üzerimdeki ve geçmiş zimmetler, teslim onayı, tutanak), çalışan kartında "Zimmetler" paneli. Yeni CSS sınıfı yok.
- Migration `HrAssets`: iki tablo + `TenantIsolationSql.AddTable`.

**Sonraya kalanlar:** işe giriş/çıkış listesinde açık zimmet engeli (dilim 5), toplu demirbaş içe aktarma (ADR-0033), bakım geçmişi ve garanti bitiş uyarısı, beklenen iade tarihi geçen zimmet bildirimi (ADR-0027 kuralı yazılabilir: `expectedReturnOn` tarih alanı beyan edildi), tutanağın e-imzası.

### Test sonuçları (Dilim 4)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 28/28 geçti |
| `dotnet test tests/Modulix.ArchitectureTests` | 73/73 geçti |
| `dotnet test tests/Modulix.Core.Tests` | 964/964 geçti |
| `HrAssetTests` (entegrasyon, 4 test; PDF dahil) | Derleniyor; yerelde SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır) |
| `Modulix.Web.Tests` (menü testi güncellendi) | Yerel SDK nedeniyle **çalıştırılamadı**; yeni Razor dosyaları hatasız derleniyor |

## 13. Dilim 5: işe giriş ve çıkış (13 Kasım 2026)

- **Şablon** `hr.ChecklistTemplates` (ad tür içinde tekil; tür işe giriş/işten çıkış, yalnızca oluştururken; etkin) ve **adımları** `hr.ChecklistTemplateItems` (sıra, başlık, açıklama, sorumlu: çalışan / yönetici / İK, başlangıca göre gün farkı, görev türü: elle / zimmet iadesi). "Zimmet iadesi" yalnızca çıkış şablonunda olur. Şablon değişikliği başlatılmış süreçleri etkilemez; kullanılmış şablon silinmez, pasife alınır.
- **Süreç** `hr.EmployeeChecklists` yalnızca başlatma komutuyla açılır; şablonun adımları aynı işlemde **görevlere** `hr.ChecklistTasks` dönüşür (kayıt boru hattından; her biri audit ve olay üretir). Son tarih = başlangıç günü + gün farkı; sorumlu kullanıcı o anda çözülür (çalışanın ve yöneticisinin kullanıcısı; İK görevi "süreç yönetimi" izni olan herkese açık). Ayrılmış çalışana işe giriş başlatılmaz; aynı türde açık süreç tekildir.
- **İlerleme:** görev kapandıkça sürecin ilerlemesi aynı işlemde güncellenir; bütün görevler tamamlanınca ya da atlanınca süreç tamamlanır, biri yeniden açılınca açılır; iptal edilmiş süreç ve görevleri değişmez.
- **Açık zimmet kuralı (dilim 4 bağı):** çıkış sürecindeki "zimmet iadesi" görevi çalışanın açık zimmeti varken tamamlanamaz (409, neden ekranda); bu yüzden açık zimmetle çıkış süreci kapanmaz. Modül içi okumadır (aynı modülün tablosu).
- **Görünürlük:** veri kapsamı alanı `employeeUserId`; ek olarak kullanıcıya atanmış görevler ve onların süreçleri görünür (yönetici astının sürecini "kendi" kapsamıyla görür).
- **Arayüz:** İşe giriş ve çıkış (süreç başlat, görünümler, süreç kartında görevler ve iptal), Süreç şablonları (şablon + adımlar, sekmeli formlar), Görevlerim (bana atanan açık görevler, İK'ya ayrıca İK görevleri; gecikenler işaretli). Yeni CSS sınıfı yok.
- Migration `HrChecklists`: dört tablo + `TenantIsolationSql.AddTable`.

**Bilerek yapılmadı:** işe giriş tarihi girilince/çalışan "Ayrıldı" yapılınca sürecin kendiliğinden başlaması (ADR-0027 kuralıyla bağlanabilir; "Süreç başlat" aksiyonu sonra), görev atandığında bildirim, kullanıcı hesabının çıkışta kapatılması (Core üyelik işlemi; görev olarak elle), görev başına dosya.

### Test sonuçları (Dilim 5)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 29/29 geçti |
| `dotnet test tests/Modulix.ArchitectureTests` | 73/73 geçti |
| `dotnet test tests/Modulix.Core.Tests` | 964/964 geçti |
| `HrChecklistTests` (entegrasyon, 4 test) | Derleniyor; yerelde SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır) |
| `Modulix.Web.Tests` (menü testi güncellendi) | Yerel SDK nedeniyle **çalıştırılamadı**; yeni Razor dosyaları hatasız derleniyor |

## 14. Dilim 6: duyurular (13 Kasım 2026)

- **Duyuru** `hr.Announcements` (başlık, düz metin gövde en çok 8000 karakter, hedef departman, sabitlenmiş, yayında, yayın ve bitiş günü, yazan) ve **okundu kaydı** `hr.AnnouncementReads` (duyuru + kullanıcı tekil; `UNIQUE(TenantId, AnnouncementId, UserId)`). İki yeni izin: `hr.announcements.read` (çalışan ve yönetici rol şablonlarında) ve `hr.announcements.manage` (İK uzmanında).
- **Hedefleme sunucuda:** "duyuru yönetimi" izni olmayan okur yalnızca yayında, yayın günü gelmiş, süresi dolmamış ve hedef departmanı boş ya da çalışan kartındaki departmanı olan duyuruları görür; diğerleri liste ve tekil okumada "bulunamadı"dır (sorgu düzeyinde, `HrRecordStore.RestrictAsync`). Yönetici bütün duyuruları görür.
- **Okundu:** `POST /api/hr/announcements/{id}/read` duyuruyu görebilen kullanıcı için okundu kaydını kayıt boru hattından yazar (tekrar okumada yeni kayıt yazmaz) ve duyurunun güncel halini döner. Okundu kaydı doğrudan yazılamaz ve değişmez (409); listesini (kim okudu) yalnızca yönetici görür. Duyuru silinince okundu kayıtları aynı işlemde silinir.
- **Doğrulama:** başlık ve metin zorunlu; bitiş günü başlangıçtan önce olamaz; yayın günü verilmezse bugündür; hedef departman seçim listesinden.
- **Arayüz:** Duyurular (akış: sabitlenmişler üstte, okunmamışlar "Yeni" rozetli, açınca okundu yazılır; metin `.preline` ile düz metin) ve Duyuru yönetimi (Yayında / Taslak / Süresi geçen / Tümü görünümleri, okuyan sayısı, sekmeli form, okuyanlar listesi). Yeni CSS sınıfı yok.
- Migration `HrAnnouncements`: iki tablo + `TenantIsolationSql.AddTable`.

**Bilerek yapılmadı:** zengin metin/HTML gövde ve görsel (dilim 8'deki dosya altyapısıyla), duyuru yayınlanınca e-posta/anlık bildirim (ADR-0027 kuralıyla bağlanabilir), yorum ve beğeni, birden çok departmana ya da kişiye hedefleme, ana sayfa kutucuğu.

### Test sonuçları (Dilim 6)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 29/29 geçti |
| `dotnet test tests/Modulix.ArchitectureTests` | 73/73 geçti |
| `HrAnnouncementTests` (entegrasyon, 4 test) | Derleniyor; yerelde SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır) |
| `Modulix.Web.Tests` (menü testi güncellendi) | Yerel SDK nedeniyle **çalıştırılamadı**; yeni Razor dosyaları hatasız derleniyor |

## 15. Dilim 7: anketler (13 Kasım 2026)

- **Anket** `hr.Surveys` (başlık, açıklama, durum taslak / yanıta açık / kapalı, kimliği gizli, hedef departman, kapanış günü, hazırlayan), **soru** `hr.SurveyQuestions` (sıra, metin, tür: 1–5 puan / tek seçim / serbest metin, seçenekler satır başına, zorunlu), **katılım** `hr.SurveyResponses` (anket + kullanıcı tekil; `UNIQUE(TenantId, SurveyId, RespondentUserId)`) ve **cevap** `hr.SurveyAnswers`. İzinler: `hr.surveys.respond` (çalışan ve yönetici şablonları), `hr.surveys.manage` (İK uzmanı), `hr.surveys.answers` (tek tek cevaplar ve denetim kaydındaki cevap değerleri; **hiçbir rol şablonunda yok**).
- **Durum komutla değişir:** `POST /api/hr/surveys/{id}/actions` (`open`: sorusu olmalı, kapanış günü geçmemiş olmalı; `close`: yalnızca açık anket; kapalı anket yeniden açılabilir). Soru yalnızca taslakta eklenir, değişir, silinir (yanıt toplanırken soru değişirse sonuç karşılaştırılamaz). Yanıtı olan anket silinmez, kapatılır; taslak anket sorularıyla silinir.
- **Yanıt:** `POST /api/hr/surveys/{id}/responses` anket yanıta açıksa, kapanış günü geçmemişse ve kullanıcı hedef kitledeyse (departmanı boş ya da çalışan kartındaki departmanı) kabul edilir. Zorunlu soru, puan 1–5, seçeneğin listede olması ve metin uzunluğu denetlenir (alan adı `answers.{soruId}`, 422); tekrar yanıt 409. Katılım ve cevaplar kayıt boru hattından **tek işlemde** yazılır. Okur (yönetim izni yok) yalnızca yanıt verebileceği anketleri ve sorularını görür; diğerleri 404. Yönetici bütün anketleri görür ama yanıt için o da hedef kitlede olmalıdır.
- **Sonuç:** `GET /api/hr/surveys/{id}/results` (yalnızca yönetim izni): soru başına cevap sayısı, puan ortalaması ve 1–5 dağılımı, seçenek dağılımı, metin cevapları (yanıtlayan bilgisi olmadan, alfabetik).
- **Kimliği gizli anket (sınırıyla birlikte):** seçim yalnızca oluştururken yapılır. Sonuçlarda ve kayıt uçlarında yanıtlayan gösterilmez (katılım listesinde `respondentUserId` boş), cevaplar tek tek listelenmez (`hr.surveys.answers` izniyle de), sonuç en az **3 yanıt** gelince gösterilir. **Sınır:** platform kuralı gereği her yazma denetim kaydına yazan kullanıcıyla düşer (Y5: her önemli yazma denetim kaydına yazılır); bu yüzden cevap kaydı ile kullanıcı denetim kaydında ilişkilidir. Cevap değerleri denetim kaydında yalnızca `hr.surveys.answers` izniyle görünür, ama tenant'ın sistem yöneticisi rolü bütün izinlere sahiptir. Yani "kimliği gizli", İK yöneticisinden ve sonuç ekranından gizlidir; tenant yöneticisinden ve veritabanından tam anonimlik **değildir**. Arayüz metni de buna göre yazıldı ("sonuçlarda kimin ne yanıtladığı gösterilmez"; "anonim" denmez). Tam anonimlik (yazanı kaydetmeyen yazma) Core'da kayıt boru hattının sistem kimliğiyle yazabilmesini gerektirir; ayrı bir karar konusudur. **Güncelleme (bölüm 24):** tam anonimlik eklendi; arayüz artık "anonim" der.
- **Arayüz:** Anketler (`/ik/anketler`: açık anketler, "Yanıt bekliyor/Yanıtlandı", yanıt formu) ve Anket yönetimi (`/ik/anket-yonetimi`: Taslak / Yanıta açık / Kapalı / Tümü görünümleri, sıralanabilir başlıklar, sekmeli anket ve soru formları, yanıta aç/kapat eylemleri, sonuç özeti). Yeni CSS sınıfı yok.
- Migration `HrSurveys`: dört tablo + `TenantIsolationSql.AddTable`. `Field.Flag` yardımcısına `createOnly` eklendi.

**Bilerek yapılmadı:** tam anonim yazma (yukarıda), çoklu seçim ve matris soru, koşullu soru, anket şablonları ve tekrarlayan nabız anketi, hatırlatma e-postası, sonuçların dışa aktarımı ve departman kırılımı (kimliği gizli ankette küçük grupları açığa çıkarır; eşik kuralıyla birlikte tasarlanmalı).

### Test sonuçları (Dilim 7)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 30/30 geçti |
| `dotnet test tests/Modulix.ArchitectureTests` | 73/73 geçti |
| `dotnet test tests/Modulix.Core.Tests` | 971/971 geçti |
| `HrSurveyTests` (entegrasyon, 4 test) | Derleniyor; yerelde SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır) |
| `Modulix.Web.Tests` (menü testi güncellendi) | Yerel SDK nedeniyle **çalıştırılamadı**; yeni Razor dosyaları hatasız derleniyor |

## 16. Dilim 8: blog ve galeri (13 Kasım 2026)

- **Blog yazısı** `hr.BlogPosts` (başlık, özet, düz metin gövde en çok 20.000 karakter, yayında, ilk yayın anı, yazan) ve **fotoğraf albümü** `hr.PhotoAlbums` (başlık, açıklama, etkinlik günü, yayında, ilk yayın anı, yazan). İzinler: `hr.blog.read`, `hr.gallery.read` (çalışan ve yönetici şablonları), `hr.blog.manage`, `hr.gallery.manage` (İK uzmanı).
- **Yazar modeli:** yazıyı ve albümü yönetim izni olan iletişim/İK ekibi yazar. Her çalışanın kendi yazısını yazması bilerek yapılmadı: ADR-0040'ta dosya yükleme kayıt *türünün* güncelleme iznine bağlıdır; yazar başına yazma izni verilseydi bir yazar başkasının yazısına dosya ekleyebilirdi. Bu, ADR-0040'ın kayıt düzeyinde dosya yetkisi dilimiyle birlikte açılır.
- **Görünürlük sunucuda:** yönetim izni olmayan okur yalnızca yayındaki yazı ve albümleri görür; taslak 404'tür ve dosya erişimi bağlı kaydın erişimi olduğundan taslağın ekleri ve fotoğrafları da 404'tür. Yayın anı ilk yayında yazılır; taslağa alınıp yeniden yayınlansa değişmez.
- **Fotoğraflar ve ekler ADR-0040 dosyalarıdır** (yeni tablo yok): albüm başına en çok 20 dosya, PNG/JPEG/GIF (ve diğer izinli türler), 10 MB. İndirme her zaman `attachment` olarak kalır; galeri görüntüyü oturumla alıp sayfada `data:` adresiyle gösterir (dosya adresi tarayıcıda açılmaz, CSP değişmedi). Görüntü olmayan dosya indirme bağlantısıyla listelenir.
- **Arayüz:** Blog (`/ik/blog`: yayındaki yazılar, devamını oku, ekler), Galeri (`/ik/galeri`: albüm kartları, fotoğraf ızgarası), Blog yönetimi ve Galeri yönetimi (Tümü / Yayında / Taslak görünümleri, sıralanabilir başlıklar, sekmeli formlar, dosya paneli). Yeni CSS sınıfı yok (`profile-cards` + `img-fluid`); `AppIcon` setine Lucide `image` ikonu eklendi.
- Migration `HrBlogAndGallery`: iki tablo + `TenantIsolationSql.AddTable`.

**Bilerek yapılmadı:** yorum ve beğeni, kategori/etiket, zengin metin, albüm kapağı ve küçük resim (sunucuda küçültme yok; albüm açılınca fotoğraflar tam boyutuyla yüklenir), albüm başına 20'den fazla fotoğraf, kaydı silinen yazı/albümün dosyalarının temizliği (ADR-0040 dilim 3; galeri yönetimi albüm silinmeden önce fotoğrafların silinmesini ister, bu kural yalnızca arayüzdedir).

### Test sonuçları (Dilim 8)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 31/31 geçti |
| `dotnet test tests/Modulix.ArchitectureTests` | 73/73 geçti |
| `dotnet test tests/Modulix.Core.Tests` | 971/971 geçti |
| `HrBlogGalleryTests` (entegrasyon, 4 test) | Derleniyor; yerelde SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır) |
| `Modulix.Web.Tests` (menü testi güncellendi) | Yerel SDK nedeniyle **çalıştırılamadı**; yeni Razor dosyaları hatasız derleniyor |

## 17. Liste şablonu borcu kapatıldı (13 Kasım 2026)

Dilim 5–8'de grid şablonunun bir kısmıyla açılan altı İK listesi tam şablona taşındı: İşe giriş ve çıkış, Süreç şablonları, Duyuru yönetimi, Anket yönetimi, Blog yönetimi, Galeri yönetimi. Hepsinde artık `ViewTabs` + "+ Görünüm" ile kişisel görünüm (`PersonalViewBar`, sütunlar, sıralama ve görünüm süzgeci birlikte saklanır), `GridHeader` ile sunucu `sort` anahtarlarına bağlı sıralanabilir başlıklar, `GridColumnPicker` (özel alanlar da sütun olarak seçilebilir), `RowMenu` ve `Pager` (sayfa boyutu seçimiyle) vardır. İşe giriş ve çıkış listesine tür süzgeci (`FilterToggle`) eklendi; Süreç şablonları arama, sayfalama ve "Pasif" görünümü kazandı. Sunucu ve API değişmedi; yeni CSS sınıfı yok. Okur ekranları (Duyurular, Anketler, Blog, Galeri, Görevlerim) liste değil akış/kart ekranıdır ve şablonun dışındadır.

## 18. Sonraya kalanlar, öncelik 1: otomasyon ve bildirim (14 Kasım 2026)

Dilimlerin "bilerek yapılmadı" listelerinden kalanlar öncelik sırasına konuldu: (1) otomasyon ve bildirim, (2) çalışan kartı genişletmeleri (içe aktarma, görev geçmişi, iş birimi eşlemesi), (3) iletişim ek özellikleri, (4) Core kararı gerektirenler (tam anonim yazma, kayıt düzeyinde dosya yetkisi, silinen kaydın dosya temizliği). Bu bölüm (1)'dir; yeni tablo ve migration yoktur, her şey ADR-0027 kural altyapısının mevcut sözleşmesiyle yapıldı.

- **Beyan taşındı:** İK'nın kural yeteneği `HrModule` içinden `Automation/HrAutomation.cs`'ye alındı (CRM kalıbı). Tarih sütunlarının tek kaynağı bu sınıftır.
- **Tarama kaynağı** `HrAutomationRecordSource`: çalışan (işe giriş, ayrılış), izin talebi (başlangıç), zimmet (beklenen iade), süreç görevi (son tarih) için tarihe bağlı tetikleyiciler artık çalışır; zamanlanmış tarama yalnızca açık kayıtları kapsar. Önceden tarih alanları beyan ediliyor ama taranamıyordu.
- **Bildirime bağlanabilir kullanıcı alanları:** çalışanın hesabı, izin ve masrafta talep eden ve onaylayan, zimmetli, görevin sorumlusu ve çalışanı Core'un bildirim alıcısı lookup'ıyla beyan edildi. Böylece Core'un "Bildirim gönder" aksiyonunun "Kime"si bu alanlara bağlanır; alıcı kaydı kendi yetkisiyle göremiyorsa bildirim gitmez (Core kuralı).
- **Aksiyon `hr.checklist.start`** ("İşe giriş/çıkış süreci başlat", izinler `hr.checklists.read` + `hr.checklists.manage`; kural kimliği yalnızca beyan edilen izinleri taşıdığından şablonu ve açık süreci okumak için okuma izni de beyan edilir): hedef çalışana süreç başlatır; elle başlatmayla aynı servis ve kurallar (`IChecklistWorkflow`). Şablon verilmezse türün ada göre ilk etkin şablonu; başlangıç günü işe giriş/ayrılış tarihine bağlanabilir, boşsa tenant'ın bugünü. Aynı türde açık süreç varsa adım atlanır (kural yeniden tetiklense de tek süreç). Dry-run desteklenir.
- **Yedi başlangıç şablonu** (kullanılınca taslak kural; yayın firmanın kararı): görev atanınca sorumluya bildir, görevin son gününe 1 gün kala hatırlat, izin talebi açılınca onaylayana bildir, masraf beyanı onaya gönderilince onaylayana bildir, zimmet iade tarihi geçince zimmetliye bildir, çalışan eklenince işe giriş süreci başlat, çalışan ayrılınca işten çıkış süreci başlat.

**Bilerek yapılmadı:** duyuru ve anket için toplu bildirim/e-posta (Core'un "Bildirim gönder"i tek alıcılıdır; hedef kitleye toplu gönderim Core'da yeni bir aksiyon ister), kullanıcı hesabının çıkışta otomatik kapatılması (Core üyelik işlemi; bilinçli olarak elle), e-posta kanalı (bildirim kutusu yeterli görüldü; e-posta aksiyonu Core kararıdır).

### Test sonuçları (bölüm 18)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 37/37 geçti (gerçek katalogla beyan, tarih alanı ↔ tarama kaynağı uyumu, bildirim alanları, aksiyon izni, şablon bağlamaları) |
| `dotnet test tests/Modulix.ArchitectureTests` | 78/78 geçti |
| `HrAutomationTests` (entegrasyon, 4 test: bütün şablonlar gerçek yayın doğrulamasından geçer; görev bildirimi + son gün hatırlatması + tenant izolasyonu; ayrılınca tek işten çıkış süreci; izinsiz kural yöneticisi şablonu kullanamaz) | Derleniyor; yerelde SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır) |

## 19. Sonraya kalanlar, öncelik 2a: içe aktarma (14 Kasım 2026)

Öncelik 2'nin (çalışan kartı genişletmeleri) ilk parçası: ADR-0033 içe aktarma tanımları. Yeni tablo ve migration yoktur; motor, ekran ("Yeni içe aktarma"), işlem günlüğü ve firmanın içe aktarma tasarımı mevcut Core altyapısıdır.

- **Demirbaş** (`hr.asset`, izin `hr.assets.manage`): envanter no ve ad zorunlu; kategori, seri no, marka, model, alış tarihi ve bedeli, para birimi, notlar. Envanter no kesin eşleşmedir (kayıtlı ya da dosyada tekrar eden satır hatası). Durum içe aktarılmaz: yeni demirbaş depoda başlar, zimmet iş akışıyla verilir.
- **İzin hakkı** (`hr.leave-balance`, izin `hr.leaves.manage`): geçiş için açılış bakiyeleri. Çalışan sicil numarasıyla bağlanır (`hr.employee` kayıt deposuna `employeeNumber` süzgeci eklendi); hak ve devreden gün; aynı çalışan ve yıl için ikinci hak depo kuralıyla reddedilir.
- **Resmî tatil** (`hr.holiday`, izin `hr.leaves.manage`): tarih, ad, yarım gün; tarih kesin eşleşmedir.
- **Tam sayı alanı:** içe aktarma sayıları ondalık verdiğinden İK'nın tam sayı alanı küsuratsız ondalık sayıyı da kabul eder (ör. yıl 2026); küsuratlı değer hâlâ hatadır.

**Çalışan kartı içe aktarılmadı (karar gerekiyor):** çalışan kartı Core'un kişi kaydına (`core.person`) bağlıdır ve kişiler bugün toplu içe aktarılamaz. İçe aktarma tanımı modülün kendi kayıt türü olmak zorunda (katalog kuralı), kişi kaydının yazma izni ise kişiyi kullanan modüllerden gelir; bu yüzden ne İK kişi içe aktarabilir ne de Core tek bir izinle tanımlayabilir. Seçenekler: (a) Core'a kişi içe aktarma için ayrı bir izin (ör. `core.people.import`) ve tanım; önce kişiler, sonra çalışan kartları (kişi e-postayla bağlanır), (b) motora "bağlı kaydı yoksa oluştur" desteği. İkisi de Core değişikliğidir; öncelik 4 ile birlikte karara bağlanmalıdır.

### Test sonuçları (bölüm 19)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 42/42 geçti (gerçek katalogla tanımlar, eşleşme kuralları, sicil no bağlantısı, ondalık → tam sayı) |
| `dotnet test tests/Modulix.ArchitectureTests` | 78/78 geçti |
| `dotnet test tests/Modulix.Core.Tests` | 971/971 geçti |
| `HrImportTests` (entegrasyon, 3 test: demirbaş + izin hakkı + tatil; tekrar, küsuratlı yıl ve bilinmeyen sicil no satır hatası; başka firmanın sicil numarası çözülmez ve izinsiz kullanıcı demirbaş türünü görmez) | Derleniyor; yerelde SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır) |

## 20. Sonraya kalanlar, öncelik 2b: görev geçmişi (14 Kasım 2026)

Yol haritası M4.2 (tarihli görev geçmişi). Çalışanın unvan, departman, yönetici ve çalışma şekli bir başlangıç gününden itibaren geçerli **dönemler** olarak tutulur.

- **Tablo** `hr.EmployeePositions` (çalışan, başlangıç, bitiş, unvan, departman, yönetici, çalışma şekli, değiştiren); migration `HrEmployeePositions` + `TenantIsolationSql.AddTable`. Kayıt türü `hr.employee-position` yalnızca okunur (oluşturma, değiştirme ve silme 409); görünürlüğü çalışan kartınınkidir (`hr.employees.read`).
- **Satırları çalışan kartı yazar**, kartla aynı işlemde (sözleşme geçmişi `ContractEvent` ile aynı kalıp; değişikliğin kendisi kartın denetim kaydındadır): kart açılınca işe giriş gününden ilk dönem; bu dört alandan biri değişince önceki dönem kapanır ve yenisi **geçerlilik gününden** açılır; ayrılınca son dönem ayrılış günüyle kapanır, yeniden çalışınca açılır. İlgisiz değişiklik (ör. not) dönem açmaz.
- **Geçerlilik günü** kartta saklanmayan bir giriş alanıdır (`positionEffectiveDate`, form düzeninde "Görev ve organizasyon" bölümünde; yalnızca kayıtlı kartta görünür). Boşsa bugün; önceki dönemin başından sonra ve ayrılış tarihinden önce olmalıdır (422). Bir kapsamda birden çok kart yazılabileceği için (içe aktarma) her yazmadan sonra sıfırlanır.
- **Eski kartlar:** bu bölümden önce açılmış kartın geçmişi yoktur; ilk görev değişikliğinde kartın önceki değerleri işe giriş gününden başlayan dönem olarak yazılır. Migration'da geriye dönük doldurma yapılmadı (RLS altında migration kullanıcısıyla toplu yazma ayrı bir karardır).
- **Arayüz:** çalışan kartında "Görev geçmişi" paneli (dönem, unvan, departman, yönetici, çalışma şekli; en yeni üstte; `LoadingState`/`EmptyState`). Yeni CSS sınıfı yok.

**Bilerek yapılmadı:** geçmiş dönemin sonradan düzeltilmesi (dönemler kayıt değişikliğinden türer; yanlış dönem yeni bir değişiklikle düzeltilir), maaş/ücret geçmişi (bordro D4), departman → iş birimi eşlemesi (öncelik 2c).

### Test sonuçları (bölüm 20)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 42/42 geçti |
| `dotnet test tests/Modulix.ArchitectureTests` | 83/83 geçti |
| `HrEmployeePositionTests` (entegrasyon, 3 test: dönem açma/kapama, ayrılış ve yeniden başlama; geçerlilik günü kuralları ve geçmişe doğrudan yazma 409; tenant izolasyonu, izinsiz kullanıcı ve salt okur) | Derleniyor; yerelde SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır) |
| `Modulix.Web` derlemesi | Yeni bileşen ve editör değişikliği hatasız derleniyor; ekran görüntüsü alınamadı (yerelde uygulama çalışmıyor) |

## 21. Çalışan kartı içe aktarma: seçenek (a) (14 Kasım 2026)

Bölüm 19'daki karar (a) olarak verildi (kullanıcı kararı, 14 Kasım 2026): Core'a kişi içe aktarması için ayrı bir izin ve tanım; çalışan kartı kişiye e-postayla bağlanır.

- **Core:** yeni izin `core.people.import` ("İçe aktarma" grubu; Yönetici rolü bütün izinleri taşıdığından onda vardır) ve `core.person` içe aktarma tanımı (ad, soyad zorunlu; e-posta, telefon, unvan). Aynı e-postalı kişi **olası mükerrerdir** (uyarı; kişiler otomatik birleştirilmez, ADR-0013). Kişinin yazma izni kişiyi kullanan modüllerden geldiği için (`SharedRecordAccess`) içe aktarıcı ayrıca bunlardan birine (ör. `hr.employees.write`) sahip olmalıdır; kayıt boru hattı bunu her satırda denetler. Katalog kuralı (tanım modülün kendi kayıt türü, izin modülün kendi izni) değişmedi: tanım Core'da, izin Core'un.
- **İK:** `hr.employee` içe aktarma tanımı (`hr.employees.write`): kişi e-postası (zorunlu, `core.person` e-posta süzgeciyle), sicil no, işe giriş (zorunlu), unvan, departman, yönetici sicil no (`hr.employee` sicil no süzgeciyle), çalışma şekli, çalışma yeri, kullanıcı hesabı (e-posta), durum, ayrılış, özlük alanları (izni olmayan içe aktarıcıda dolu hücre satır hatasıdır), notlar. Sicil no ve kişi kesin eşleşmedir (dosyada tekrar eden satır hatası; kayıtlı kişinin ikinci kartı depo kuralıyla reddedilir). Görev geçmişinin ilk dönemi içe aktarmada da açılır (bölüm 20).
- **Yönetici bağlantısı:** satırlar sırayla yazılır ve bağlantı yazma anında çözülür; yönetici dosyada astından önce gelirse bulunur. Kuru çalışmada satırlar yazılmadığından aynı dosyadaki yöneticiye bağlantı çözülmez; "hepsi ya da hiç" kipi hatasız kuru çalışma istediği için bu kipte yöneticiler ayrı (önceki) bir dosyayla yüklenmelidir.
- Sıra: kişiler → çalışan kartları → izin hakları.

### Test sonuçları (bölüm 21)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 43/43 geçti (gerçek katalogla çalışan ve kişi tanımları) |
| `dotnet test tests/Modulix.Core.Tests` | 996/996 geçti |
| `dotnet test tests/Modulix.ArchitectureTests` | 91/91 geçti |
| `HrImportTests` (entegrasyon, +2 test: kişiler sonra kartlar, e-posta ve yönetici bağlantısı, görev geçmişi; bilinmeyen e-posta, aynı kişiye ikinci kart, olası mükerrer kişi uyarısı, kişi içe aktarma izni, başka firmanın kişisi) | Derleniyor; yerelde SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır) |

## 22. Departmanlar Core'a bağlandı (öncelik 2c, 14 Kasım 2026)

Karar (kullanıcı, 14 Kasım 2026: "Core'a bağla"): İK'nın departmanı ayrı bir seçim listesi (`hr.department`) değil, Core'un departman ağacıdır (ADR-0026). Tek kaynak Yönetim › Departmanlar'dır; İK departman açmaz, adlandırmaz.

- **Core sözleşmesi:** `IBusinessUnitDirectory` (`Core.Contracts.Tenancy`): etkin tenant'ın departmanları (ağaç yolu adı ve kimlikleri, derinlik, etkin mi) ve kullanıcıların departmanı (etkin/pasif üyeliğin bağlı olduğu birim). Uygulaması Infrastructure'da, istek başına önbellekli. İK Core tablolarına doğrudan erişmez; yalnızca bu arayüzü kullanır.
- **Çalışan kartı:** `businessUnitId` (Core departmanı), okunurken `businessUnitName` (ağaç yolu). Hesabı **bağlı** çalışanın departmanı kullanıcının Core departmanıdır: kart kaydedilirken oradan alınır, farklı bir değer gönderilirse alan hatası (departman değişikliği kayıt görünürlüğünü etkilediği için Core'un "Departmanı değiştir" akışıyla yapılır; ADR-0026 dilim 4). `businessUnitFromAccount` arayüze bunu söyler (seçici kapalı). Hesabı olmayan çalışanda departman ağaçtan seçilir; bulunmayan departman ve yeni seçimde kapalı departman reddedilir. Görev geçmişi (bölüm 20) dönem değişikliğini Core departmanına göre yapar ve `businessUnitName`'i gösterir. Liste süzgeci `businessUnitId`; departman adı İK tablosunda olmadığından o sütuna göre sunucuda sıralanmaz (sütun sıralanamaz).
- **Duyuru ve anket hedefi:** `audienceBusinessUnitId` (okunurken `audienceName`). Okurun departmanı kullanıcının Core departmanıdır (üyelikte departman yoksa kartındaki); hedef departman okurun departman yolundaysa içerik ona yöneliktir, yani üst departmana hedeflenen içerik alt departmanlardakilere de görünür. Hedef seçimi eski İK hedefini temizler; hedef değiştirilmedikçe editör alanı göndermez (eski hedef korunur).
- **Geçiş (geriye uyumlu, kesintisiz):** migration `HrCoreDepartments` yalnızca boş kolon ekler (`Employees.BusinessUnitId` + `(TenantId, BusinessUnitId)` indeksi, `EmployeePositions.BusinessUnitId`, `Announcements/Surveys.AudienceBusinessUnitId`); yeni tablo yok. Eski `DepartmentKey` / `AudienceDepartmentKey` kolonları ve `hr.department` listesi durur: eşlenmemiş eski hedef, okurun kartındaki eski departmana göre çalışmaya devam eder. **Eşleme aracı** (Çalışanlar ekranında, `hr.employees.write`): `GET /api/hr/department-mapping` eski değerin adını Core'da aynı adlı (ya da aynı ağaç yollu) tek etkin departmanla eşler ve henüz Core departmanı olmayan çalışan/duyuru/anket sayılarını gösterir; eşleşmeyen ya da belirsiz değer listelenir (Core'da departman açılıp yeniden denenir). `POST` eşleşenleri kayıt boru hattıyla yazar (yetki, denetim kaydı, olay): hesabı olmayan karta eşleşen departman, hesabı bağlı karta kullanıcının departmanı; duyuru/anket hedefleri yalnızca onları yönetme izni olan uygulayınca.
- **İçe aktarma:** çalışan kartı "Departman" sütunu Core departmanının adı ya da ağaç yoludur (yolun başı yazılmayabilir: "Satış › İstanbul"; ayraç "›", ">" ya da "/"); bulunmayan ya da birden çok departmana uyan ad satır hatasıdır.
- **Arayüz:** ortak `HrBusinessUnitSelect` seçicisi (seçenek ağaç yolu; kapalı departman yalnızca kayıtta seçiliyse "(kapalı)" ekiyle) çalışan formunda, Çalışanlar ve Rehber süzgecinde, organizasyon şemasında (vurgu alt departmanları kapsar), duyuru ve anket editöründe. Eşleme paneli yalnızca bekleyen kayıt varken görünür (`btn-tonal`, toast). Yeni CSS sınıfı ya da token yok; organizasyon şemasındaki düz boş metin `EmptyState`'e çevrildi.

**Sonraki adımlar (bilerek bu bölümde yapılmadı):**

- **22b:** Core, kullanıcının departmanı değişince bir olay yayınlamıyor (bugün yalnızca denetim kaydı). Hesabı bağlı kart bu yüzden kullanıcı taşındıktan sonra ilk kaydında (ya da eşleme aracıyla) yeni departmanı alır; okurun duyuru/anket hedefi ise her istekte üyelikten okunduğu için hemen doğrudur. Core'a "üyelik departmanı değişti" olayı eklenip İK kartı ve görev geçmişi (değişiklik gününden) onu dinleyecek.
- **22c:** bütün firmalar eşlemeyi uyguladıktan sonra `DepartmentKey`/`AudienceDepartmentKey` kolonlarının ve `hr.department` listesinin kaldırılması (ayrı, geri alınamaz migration; önce kullanım sayısı ölçülür).

### Test sonuçları (bölüm 22)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 57/57 geçti (+14: departmanı adla bulma: kendi adı, ağaç yolu, yolun sonu, Türkçe harf, belirsiz ad, kapalı departman, üst departman kapsamı) |
| `dotnet test tests/Modulix.Core.Tests` | 996/996 geçti |
| `dotnet test tests/Modulix.ArchitectureTests` | 91/91 geçti |
| `dotnet ef migrations has-pending-model-changes` | Bekleyen model değişikliği yok |
| `HrDepartmentTests` (entegrasyon, 4 test: seçici, hesabı olmayan/bağlı kart, kullanıcı taşınınca kart ve görev geçmişi, süzgeç; eski değerlerin adla eşlenmesi ve eşlenmemiş eski duyurunun görünmeye devam etmesi; bulunmayan/kapalı/çakışan departman; tenant izolasyonu ve yetki); ayrıca `HrAnnouncementTests` ve `HrSurveyTests` güncellendi (hedef Core departmanı, üst departman kapsamı, bulunmayan ve kapalı departman), `HrImportTests` (departman adı/ağaç yolu, bulunmayan departman) | Derleniyor; yerelde SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır) |
| `Modulix.Web` derlemesi | Yeni bileşenler ve ekran değişiklikleri hatasız derleniyor (bilinen 210 hatalık taban dışında hata yok); ekran görüntüsü alınamadı (yerelde uygulama çalışmıyor) |

## 23. Sonraya kalanlar, öncelik 3: iletişim ek özellikleri (14 Kasım 2026)

Kullanıcı kararı (14 Kasım 2026): dört ek özelliğin hepsi, ayrı ve küçük adımlarla: (a) duyuru kutucuğu ve ekler, (b) ankette çoklu seçim ve sonuçların dışa aktarımı, (c) çoklu hedefleme, (d) yorum ve beğeni.

### 23a. Ana sayfa duyuru kutucuğu ve duyuru ekleri

- **Ekler:** duyuruya görsel ve belge eklenir (ADR-0040 `/api/files`, `entityKey=hr.announcement`). Sunucu değişikliği gerekmedi: dosyanın görünürlüğü duyurunun görünürlüğüdür (hedef departman, yayın aralığı; göremeyene 404), eklemek ve silmek duyuru güncelleme iznini (`hr.announcements.manage`) ister. Duyuru yönetiminde ekler düzenlenir, duyuru akışında açılan duyurunun ekleri listelenir.
- **Okunmamış süzgeci:** `unread=true` kullanıcının okumadığı duyurular (kullanıcı başına; başka kullanıcının okuması etkilemez).
- **Ana sayfa kutucuğu** (`AnnouncementsTile`): duyuru okuma izni olan herkese, CRM açık olmasa da; son 5 yayındaki duyuru (sabitlenmişler üstte), "Sabitlenmiş"/"Yeni" rozeti ve okunmamış sayısı; akışa bağlantı. Metinler çeviri sözlüğündedir (`home.announcements.*`, tr/en/az; ADR-0034). Yeni CSS sınıfı yok (aktivite ajandasının `today-panel` sınıfları).
- **Bilerek yapılmadı:** silinen duyurunun dosyalarının temizliği (öncelik 4, ADR-0040 dilim 3); duyuru metninde satır içi görsel (ekler ayrı listelenir).

### 23b. Ankette çoklu seçim ve sonuçların dışa aktarımı

- **Çoklu seçim** (`multiChoice`): seçenekler tek seçimle aynı kuralla girilir (2–20 farklı seçenek). Yanıtta `choices` listesi gönderilir; seçenekler soruda olmalı, bir seçenek bir kez seçilir, zorunlu soruda en az biri seçilir. Seçilen her seçenek **ayrı bir cevap satırıdır** (`SurveyAnswer.Choice`): kolon genişlemedi, migration yok, sonuçta seçenek başına sayım doğal olarak çalışır. Sorunun "cevaplayan" sayısı artık yanıt (kişi) sayısıdır; oran, cevaplayanların o seçeneği işaretleyen payıdır (çoklu seçimde toplamı %100'ü geçebilir).
- **Dışa aktarma:** `GET /api/hr/surveys/{id}/results/export` sonuç ekranıyla **aynı özeti** CSV verir (soru, tür, cevaplayan, ortalama, seçenek/puan dağılımı, metin cevapları). İzin ve kimliği gizli ankette eşik sonuçla aynıdır (eşik altında 409). Yanıtlayan bilgisi ve ham yanıt satırı **yoktur**: ham satırlar kimliği gizli ankette cevap kombinasyonuyla kişiyi açığa çıkarabilirdi. Excel'in Türkçe ayarına göre UTF-8 BOM, ";" ayraç, virgüllü ondalık; "=", "+", "-", "@" ile başlayan hücre kesme işaretiyle başlar (formül enjeksiyonu; denetim kaydı dışa aktarımıyla aynı kural).
- **Bilerek yapılmadı:** departman kırılımı (kimliği gizli ankette küçük grupları açığa çıkarır; eşik kuralıyla ayrıca tasarlanmalı), matris ve koşullu soru, Excel (.xlsx) biçimi.

### 23c. Birden çok departmana ve kişiye hedefleme

- **Model:** yeni tenant tablosu `hr.AudienceTargets` (içerik kimliği; ya Core departmanı ya kullanıcı, CHECK kısıtıyla yalnızca biri; RLS `TenantIsolationSql.AddTable`, migration `HrAudienceTargets`). Satırları duyuru ve anket kayıt depoları içerikle aynı işlemde yazar (görev geçmişi kalıbı); içerik silinince hedefleri de silinir. Duyuru ve anket aynı kodu kullanır (`IAudienceContent`, `AudienceFields<T>`, `HrAudience.Visible`).
- **Kural (birleşim):** hedefi olmayan içerik herkese yöneliktir; hedefi olan içeriği, hedeflerden birine uyan okur görür: departman hedefi okurun departmanı ya da üst departmanıysa (alt departmanlar dahil), kişi hedefi okurun kendisiyse. Bölüm 22'den kalan tek departman kolonu ve eşlenmemiş eski İK departmanı birer hedef sayılmaya devam eder.
- **Alanlar:** `audienceBusinessUnitIds` (en çok 50) ve `audienceUserIds` (en çok 200) kimlik listesidir; gönderilen tür tamamen değiştirilir, gönderilmeyen tür kalır. Yeni seçilen departman ağaçta olmalı ve etkin olmalı (kapalı departmanda kalan eski hedef korunur), yeni seçilen kişi kuruluşun etkin üyesi olmalı (başka firmanın kullanıcısı reddedilir); tekrar ve biçimsiz değer alan hatasıdır. Tek departman `audienceBusinessUnitId` uyumluluk için tek elemanlı liste gibi yazılır; okunurken yalnızca hedef tam olarak tek departmansa doludur. Departmanlar seçilince eski İK departmanı ve tek departman kolonu temizlenir (kolondaki hedef satıra taşınır).
- **Arayüz:** duyuru ve anket formunda "Hedef departmanlar" ve "Hedef kişiler" alanları (`HrAudiencePicker`: seçilenler listelenir, tek tek kaldırılır). Yeni CSS sınıfı yok.
- **Bilerek yapılmadı:** çalışma yerine göre hedefleme (çalışma yeri serbest metindir), hedef kitle önizlemesi (kaç kişiye ulaşır), hariç tutma ("şu departman hariç").

### 23d. Duyuru ve blog yazısına yorum ve beğeni

- **Model:** iki yeni tenant tablosu: `hr.ContentComments` (içerik, yazar, metin ≤ 2000, gizli mi, gizleyen) ve `hr.ContentLikes` (içerik, kullanıcı; içerik başına kullanıcı tekil). RLS `TenantIsolationSql.AddTable`, migration `HrContentComments`.
- **Kayıt türleri:** kayıt boru hattı işlem başına tek izin denetlediği için içerik türü başına ayrı kayıt türü vardır: `hr.announcement-comment` / `hr.announcement-like` (duyuru okuma izni) ve `hr.blog-comment` / `hr.blog-like` (blog okuma izni). Tablolar ve depo kodu ortaktır (`ContentCommentRecordStore<T>`, `ContentLikeRecordStore<T>`).
- **Görünürlük:** yorum ve beğeni, içeriğin görünürlüğünü izler. İçerik deposunun okur kuralı kullanılır (`ForReaderAsync`: duyuruda yayın aralığı ve hedef kitle, blogda yayın). Görülmeyen içeriğe yorum ve beğeni 404'tür, yorumları da listede yoktur.
- **Yazma:** yalnızca `IContentInteractions` ile ve kayıt boru hattından yapılır (yetki, denetim kaydı, olay); genel uçtan yazma, değiştirme ve silme 409'dur.
  - Yorumu ve beğeniyi içeriği görebilen kullanıcı yazar.
  - Yorumunu yazarı siler.
  - İçeriği yöneten (`hr.announcements.manage` / `hr.blog.manage`) yorumu gizler, yeniden gösterir ya da siler. Gizli yorumun metni okura gönderilmez ve gizli yorum sayılmaz.
  - Yorum metni değiştirilmez.
  - İçerik silinince yorumları ve beğenileri de silinir.
- **Arayüz:** duyuru akışında ve blogda açılan içerikte `ContentDiscussion` bileşeni yer alır: beğen düğmesi (`btn-tonal`, `aria-pressed`) ve sayı, yorum listesi (`LoadingState`/`EmptyState`), yorum formu, yazar ve yönetici eylemleri, toast. Yeni CSS sınıfı yoktur.
- **Bilerek yapılmadı:**
  - yoruma yanıt (iç içe yorum)
  - yorumu düzenleme
  - yorum ve beğeni bildirimi (yazara)
  - beğenenlerin adlarının listesi (arayüzde)
  - galeri albümüne yorum
  - içeriğin yorumlara kapatılması

## 24. Tam anonim anket (öncelik 4a, 14 Kasım 2026)

Karar (kullanıcı, 14 Kasım 2026: "kullanıcısız oy pusulası"): kimliği gizli ankette cevap, yanıtlayana sistemin hiçbir yerinde bağlanmaz. Bu, "her önemli yazma denetim kaydına yazılır" kuralının (Y5) **bilinçli ve tek istisnasıdır**: yazmanın olduğu denetlenir, kimin ve neyin yazdığı denetlenmez.

Bölüm 15'teki sınır üç bağdan geliyordu; üçü de koparıldı:

1. **Katılım satırı:** cevap katılım satırına (`SurveyResponse`, kullanıcıyı tutar) bağlıydı. Artık kimliği gizli ankette cevapta `ResponseId` boştur. Bir yanıt formunun cevapları, başka hiçbir yerde saklanmayan rastgele bir pusula kimliğiyle (`BallotId`) bir arada durur; cevaplayan sayısı buna göre sayılır. Kimliği açık ankette pusula = katılım. Katılım (kim yanıtladı) tutulmaya devam eder, çünkü tekrar yanıt böyle engellenir.
2. **Denetim kaydı:** her cevap yazımı kullanıcı, değer ve istek iz kimliğiyle denetime düşüyordu. Core'a **anonim yazma** eklendi:
   - `RecordCommand.Anonymous` yalnızca `IRecordStore.AllowsAnonymousWrites` diyen depoda kabul edilir; diğerlerinde 409. Bugün yalnızca `hr.survey-answer` bunu der.
   - Depo kuralı anonim cevabı yalnızca kimliği gizli ankete izin verir; kimliği açık anketin cevabı katılıma bağlanmak zorundadır.
   - Yetki ve doğrulama her zamanki gibi işler.
   - Denetim satırı kullanıcısız (ekranda "Sistem"), kayıt kimliksiz, değersiz (`{}`) ve iz kimliksizdir.
   - Olay yayımlanmaz.
3. **Zaman ve sıralı kimlik:** satır kimlikleri zamana göre sıralı (v7) ve zaman damgaları saniyeliydi; yazma anı katılımla eşleştirilebiliyordu.
   - Anonim cevap satırının kimliği rastgeledir (v4), zamanı anketin açıldığı gündür.
   - Anonim denetim satırının kimliği rastgele, zamanı yalnızca gündür.
   - Böylece "o gün tek kişi yanıtladı" durumunda bile denetim satırından cevaba gidilemez: satır kayıt kimliği taşımaz, cevabın zamanı da yanıt gününü değil anketin açıldığı günü gösterir.

**Mevcut veri:** migration `HrAnonymousSurveyAnswers` kimliği gizli anketlerin önceki cevaplarını katılımdan koparır (yanıt başına yeni rastgele pusula). Ancak bölüm 24'ten önce yazılmış denetim satırları (kullanıcı ve değer) **değişmez**, çünkü denetim kaydı değiştirilmez. Bu yüzden eski cevaplar tam anonim **değildir**. Tam anonimlik bu sürümden sonra verilen cevaplar içindir.

**Kalan sınırlar (bilerek):**
- Katılım ve anonim cevap aynı veritabanı işleminde yazılır. SQL Server işlem günlüğüne doğrudan erişen bir veritabanı yöneticisi satırları işlem sırasıyla ilişkilendirebilir.
- Aynı gün kaç anonim cevap satırı yazıldığı, o günün denetim satırı sayısından görülebilir. Bu yalnızca sayıdır: değer de kayıt kimliği de yoktur.
- Kimliği gizli ankette sonuç eşiği (en az 3 yanıt), ham satır dışa aktarılmaması ve departman kırılımı olmaması korunur. Az yanıtta cevap birleşiminden kişi tahmini bu kurallarla önlenir.

**Arayüz:** form ve yanıt ekranı artık "anonim" der ("cevaplarınız kimliğinizle hiçbir yerde ilişkilendirilmez; yalnızca anketi yanıtladığınız kaydedilir").

### Test sonuçları (bölüm 24)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Core.Tests` (boru hattı) | 1018/1018 geçti. +2 test: anonim oluşturmada denetim satırı kişisiz, kayıtsız, değersiz, iz kimliksiz ve olay yok; izin vermeyen depoda 409; anonim yazmada da yetki ve doğrulama işler. |
| `dotnet test tests/Modulix.ArchitectureTests`, `tests/Modulix.Modules.Hr.Tests` | 91/91 ve 57/57 geçti |
| `dotnet ef migrations has-pending-model-changes` | Değişiklik yok |
| `HrSurveyTests` (+1 entegrasyon testi) | Veritabanında cevapta katılım yok, pusula başka yerde yok, kimlik v4, zaman açılış günü. Cevabın denetim satırı anonim; cevap kimliği, pusula ve metin denetim kaydının hiçbir yerinde yok; olay yok. Katılım tutulur, tekrar yanıt 409. Kimliği açık ankette bağ ve kişisel denetim sürer. Derleniyor; yerelde SQL Server olmadığı için **CI çalıştırır**. |

## 25. Kayıt düzeyinde dosya izni (öncelik 4b, 14 Kasım 2026)

Karar (kullanıcı, 14 Kasım 2026: "Core + masraf düzeltmesi"; blog yazar modeli sonraya).

**Bulunan açık:** ADR-0040'ta dosya ekleme ve silme kaydı okuyabilmeye ve kayıt **türünün** güncelleme iznine bağlıydı. Masraf satırının kendisi ise yalnızca taslak beyanda ve beyanın sahibi ya da İK tarafından değişir. Fiş ve fatura bu kuralı atlatıyordu:
- talep izni de olan onaylayan yönetici, başkasının gönderilmiş beyanından fiş silebiliyor ya da ekleyebiliyordu;
- çalışan, onaylanmış hatta ödenmiş beyanının belgesini değiştirebiliyordu.

**Çözüm:**
- **Core:** kayıt düzeyinde yan yazma kuralı zaten vardı (`IRecordWriteGuard`, süreli belge için) ve dosya deposu tür izninden sonra ona soruyordu; yeni kod gerekmedi. Kuralın anlamı belgelendi (ADR-0040 K2, sözleşme yorumu): kaydın güncellemesini durum ya da kişiyle sınırlayan her depo dosya kuralını da vermelidir.
- **İK:**
  - `HrRecordStore` bu kancayı her İK deposu için uygular (`FileWriteDeniedAsync`; varsayılan kural yok, davranış değişmez).
  - Masraf beyanı ve satırı satırın kuralını verir: yalnızca taslak beyanda, sahibi ya da İK. Gönderilmiş, onaylanmış ya da ödenmiş beyanın belgesi kimse tarafından eklenemez ya da silinemez (403).
  - Okuma ve indirme kaydın görünürlüğünü izlemeye devam eder; onaylayan fişleri görür.
  - Arayüz zaten yalnızca taslakta ve sahibine yükleme gösteriyordu.
- **Diğer İK ekleri** (duyuru, blog, albüm, özlük belgesi): yazma zaten yönetim iznine bağlı ya da Core süreli belge kuralıyla korunuyor; değişiklik yok.

**Kapsam dışı (bilerek):**
- Blog yazar modeli (çalışanın kendi yazısını yazıp dosya eklemesi). Ayrı izin ve yayın akışı gerektiren bir ürün kararıdır.
- Lojistik sefer masrafı ve teslim kanıtında aynı açık (onaylı masrafın belgesi değişebiliyor); ayrı iş olarak önerildi.

### Test sonuçları (bölüm 25)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.ArchitectureTests` | 94/94 geçti |
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 57/57 geçti |
| `HrExpenseTests` (+1 entegrasyon testi) | Kapsadıkları: taslakta sahibi ve İK ekler; gönderimden sonra sahibi, İK ve onaylayan ekleyemez ve silemez (403); onaylayan okur ve indirir; izinsiz kullanıcı ve başka tenant ulaşamaz. Derleniyor; yerelde SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır). |
