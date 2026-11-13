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

**K8 — Anket motoru tekrar yazılmaz.** Pazarlama modülünün anket portalı (`SurveyPortal`) müşteri anketidir; çalışan anketi kimlik doğrulamalı ve isteğe bağlı anonimdir. Dilim 7'de Pazarlama motorunun Core'a taşınabilir parçaları (soru tipi, yanıt toplama) incelenir; taşınamıyorsa İK'da küçük bir anket kaydı açılır. Karar dilim 7'ye ertelendi.

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
| 2 | İzin türleri, hak/bakiye, talep, onay, takvim; işveren şirket bağı | 1, Ç6a (var), tatil takvimi kararı |
| 3 | Masraf beyanı, belge, onay, ödeme durumu | 1, ADR-0040 (var), Ç6a |
| 4 | Demirbaş ve zimmet, tutanak çıktısı | 1 |
| 5 | İşe giriş/çıkış kontrol listeleri | 1, 4 |
| 6 | Duyurular | 1 |
| 7 | Anket (K8) | 1 |
| 8 | Blog ve galeri | 1, ADR-0040 |

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
