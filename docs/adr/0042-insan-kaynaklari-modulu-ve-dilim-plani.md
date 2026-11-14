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
- **Kimliği gizli anket (sınırıyla birlikte):** seçim yalnızca oluştururken yapılır. Sonuçlarda ve kayıt uçlarında yanıtlayan gösterilmez (katılım listesinde `respondentUserId` boş), cevaplar tek tek listelenmez (`hr.surveys.answers` izniyle de), sonuç en az **3 yanıt** gelince gösterilir. **Sınır:** platform kuralı gereği her yazma denetim kaydına yazan kullanıcıyla düşer (Y5: her önemli yazma denetim kaydına yazılır); bu yüzden cevap kaydı ile kullanıcı denetim kaydında ilişkilidir. Cevap değerleri denetim kaydında yalnızca `hr.surveys.answers` izniyle görünür, ama tenant'ın sistem yöneticisi rolü bütün izinlere sahiptir. Yani "kimliği gizli", İK yöneticisinden ve sonuç ekranından gizlidir; tenant yöneticisinden ve veritabanından tam anonimlik **değildir**. Arayüz metni de buna göre yazıldı ("sonuçlarda kimin ne yanıtladığı gösterilmez"; "anonim" denmez). Tam anonimlik (yazanı kaydetmeyen yazma) Core'da kayıt boru hattının sistem kimliğiyle yazabilmesini gerektirir; ayrı bir karar konusudur.
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
- **Aksiyon `hr.checklist.start`** ("İşe giriş/çıkış süreci başlat", izin `hr.checklists.manage`): hedef çalışana süreç başlatır; elle başlatmayla aynı servis ve kurallar (`IChecklistWorkflow`). Şablon verilmezse türün ada göre ilk etkin şablonu; başlangıç günü işe giriş/ayrılış tarihine bağlanabilir, boşsa tenant'ın bugünü. Aynı türde açık süreç varsa adım atlanır (kural yeniden tetiklense de tek süreç). Dry-run desteklenir.
- **Yedi başlangıç şablonu** (kullanılınca taslak kural; yayın firmanın kararı): görev atanınca sorumluya bildir, görevin son gününe 1 gün kala hatırlat, izin talebi açılınca onaylayana bildir, masraf beyanı onaya gönderilince onaylayana bildir, zimmet iade tarihi geçince zimmetliye bildir, çalışan eklenince işe giriş süreci başlat, çalışan ayrılınca işten çıkış süreci başlat.

**Bilerek yapılmadı:** duyuru ve anket için toplu bildirim/e-posta (Core'un "Bildirim gönder"i tek alıcılıdır; hedef kitleye toplu gönderim Core'da yeni bir aksiyon ister), kullanıcı hesabının çıkışta otomatik kapatılması (Core üyelik işlemi; bilinçli olarak elle), e-posta kanalı (bildirim kutusu yeterli görüldü; e-posta aksiyonu Core kararıdır).

### Test sonuçları (bölüm 18)

| Komut | Sonuç |
|---|---|
| `dotnet test tests/Modulix.Modules.Hr.Tests` | 37/37 geçti (gerçek katalogla beyan, tarih alanı ↔ tarama kaynağı uyumu, bildirim alanları, aksiyon izni, şablon bağlamaları) |
| `dotnet test tests/Modulix.ArchitectureTests` | 78/78 geçti |
| `HrAutomationTests` (entegrasyon, 4 test: bütün şablonlar gerçek yayın doğrulamasından geçer; görev bildirimi + son gün hatırlatması + tenant izolasyonu; ayrılınca tek işten çıkış süreci; izinsiz kural yöneticisi şablonu kullanamaz) | Derleniyor; yerelde SQL Server olmadığı için **çalıştırılamadı** (CI çalıştırır) |
