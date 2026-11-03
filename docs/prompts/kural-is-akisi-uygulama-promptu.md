# Modulix — Ortak Kural ve İş Akışı Altyapısı

## Claude Code için analiz ve uygulama promptu (v2)

Modulix repository'sinde kıdemli .NET mimarı ve iş analisti olarak çalış. Önce `CLAUDE.md`'yi, sonra aşağıdaki gereksinimleri oku. Ardından mevcut kodu analiz et, eksikleri belirle, tasarımı ADR olarak yaz ve **dilim dilim** çalışır şekilde uygula. Yalnızca öneri veya rapor üretip bırakma.

Bu prompt tek seferde bitirilecek bir iş değildir. Bölüm 12'deki dilimlerden **yalnızca Dilim 0 ve Dilim 1'i** bu oturumda tamamla; her dilimin sonunda neyin yapıldığını ve neyin kaldığını özetle. Sonraki dilimler için ayrı oturum açılır.

### 1. Amaç ve değişmez sınırlar

Modulix; çok tenantlı, modüler bir iş uygulamaları platformudur. İlk modül CRM'dir; bir tenant CRM kullanmadan başka bir modülle başlayabilir.

Hedef: Ücretli AI API'si veya LLM gerektirmeden kural tabanlı kararlar, süreç otomasyonları, puanlama, bildirim, onay ve zamanlanmış takipler.

**Bu kapsamda yapma:** ücretli AI servisi, yerel model, GPU, embedding, vektör veritabanı, LLM çağrısı. `Modulix.AI` projesine dokunma.

**Ama AI'a açık tasarla.** Kural motorunun aksiyon kaydı, ileride AI ajanlarının da çağırabileceği tek tip araç sözleşmesi olmalı (ayrı bir "AI araç" sistemi kurulmayacak). Bunun için aksiyon sözleşmesi şunları taşır: ad, açıklama, parametre şeması, gereken izin, gereken modül/lisans, yan etki sınıfı (okuma / taslak / yazma), dry-run desteği ve idempotency anahtarı. Çağıran kimlik (kullanıcı, otomasyon servis kimliği, ileride AI) sözleşmenin dışında bir bağlam olarak gelir. Bu ADR'ye yazılır; AI kodu yazılmaz.

Diğer sınırlar:
- Kuralları AI tahmini olarak sunma. Her kararın nedenini kullanıcıya açıkla. Şablondan üretilen metni "AI üretimi" diye etiketleme.
- Dış servis gerektirmeyen uygulama içi bildirimleri çalışır sun. E-posta/SMS gibi kanalları ancak mevcut `IEmailQueue` ve izin altyapısı varsa bağla; ücretli kanal satın alma, zorunlu hale getirme.
- Harici kullanım ücreti olmasa da CPU, veritabanı, depolama ve bakım maliyeti olduğunu dokümante et.
- **Veritabanı modeli:** tek paylaşımlı veritabanı `Modulix` (ADR-0002, ADR-0020; tenant başına DB yok). ADR-0009 (tenant başına veritabanı) bu kararla geçersizdir. Bunu koddan doğrula ve koruyarak geliştir; belgeler çelişiyorsa çelişkiyi kanıtlarıyla belirt. Tenant başına DB'ye dönüş yapma.
- **Frontend Blazor WebAssembly'dir (`Modulix.Web`, yalnızca `Modulix.Shared`'a bağlı). Flutter projesi kurma.**
- Dinamik alanlar, Form Designer (ADR-0021), lookup (ADR-0016), rol, departman (ADR-0026), kullanıcı, lisans ve modül sözleşmeleriyle bütünleş.
- Mevcut çalışan özellikleri yeniden yazma; var olan servisleri genişlet. Yeni framework veya altyapı eklemeden önce mevcut kabiliyetleri incele (outbox/olay altyapısı ADR-0007, modüllerdeki `*Scheduler` sınıfları, `SlaScheduler`, kayıt boru hattı, `IEmailQueue`).
- Üretime dağıtım, gerçek müşterilere mesaj gönderme ve gerçek verilerde toplu otomasyon çalıştırma kapsam dışıdır. Yerel/test ortamında doğrula; yeni kuralları **pasif/taslak** teslim et.

### 2. Önce mevcut durumu kanıtlarıyla incele (Dilim 0)

`CLAUDE.md`, `README.md` (ilgili bölümler), `docs/adr/`, `docs/yol-haritasi.md`, `docs/envanter.md`, solution/proje dosyaları ve ilgili kaynak kodu incele. Şunları dosya ve sembol referanslarıyla çıkar:

1. Core ve modül bağımlılıkları, `IModule` kaydı, lisans/modül erişim kontrolü.
2. Tenant bağlamı, EF query filter + RLS, rol/departman/kullanıcı kapsamı ve alan yetkileri.
3. Dinamik alan/Form metadata, alan veri tipleri, lookup ve ilişkiler.
4. Olay, outbox, arka plan worker, mevcut scheduler'lar (Contracts, Orders, Support, Marketing), retry ve bildirim altyapısı.
5. Mevcut onay, SLA (`SlaScheduler`), görev/aktivite, şablon, audit, puanlama ve otomasyon özellikleri. **Çalışma takvimi ve tatil altyapısı var mı, açıkça doğrula.**
6. Blazor tarafında menü, liste, form, filtre, yetkilendirme ve tema bileşenleri (`docs/design-system`).
7. Test projeleri, CI (`.github/workflows/ci.yml`), migration ve dağıtım yaklaşımı.

Mevcut / kısmen mevcut / eksik / çakışan olarak bir boşluk analizi hazırla. İncelemediğin bileşeni eksik ilan etme. Hangi mevcut parçanın yeniden kullanılacağını belirt. Çıktıyı `docs/adr/0027-kural-ve-is-akisi-altyapisi.md` olarak yaz (taslak durumunda). ADR şunları içersin: boşluk analizi, Core–modül sınırı, aksiyon/araç sözleşmesi, veri modeli, çalıştırma ve tekilleştirme tasarımı, dilim planı, AI'a açık bırakılan noktalar.

Veri kaybı veya kapsamı değiştiren bir belirsizlik varsa yalnızca ilgili işi durdur, diğer işleri sürdür.

### 3. Core ve modül sınırları

Core; kural tanımı ve çalıştırma, iş akışı durumu, zamanlayıcı entegrasyonu, onay altyapısı, şablon motoru, puanlama, audit ve genel yönetim ekranlarını içersin. Core sektöre özel iş mantığı içermez; Core doğrudan CRM entity veya servislerine bağımlı olmasın (mimari testler bunu denetler).

Modüller yalnızca `Modulix.Core.Contracts` üzerinden kayıt olur. Her modül; desteklediği olayları, okunabilir alanları, koşul operatörlerini, aksiyonları, parametre şemalarını, gereken izinleri ve lisans koşullarını `IModule` kaydında beyan edebilsin. Aksiyonlar mevcut uygulama servislerinden ve kayıt boru hattından geçer; doğrudan veri güncellemesiyle iş kuralları atlanmaz. Yeni proje eklersen `ProjectReferenceRulesTests.AllowedReferences` tablosunu önce güncelle.

### 4. Kural motoru

Her kural için ad, açıklama, tenant, modül, kapsam, hedef varlık, tetikleyici, koşullar, aksiyonlar, öncelik, etkinlik tarihleri, durum (taslak/aktif/pasif) ve değişmez sürüm bilgisi tut. Yeni tablolar `ITenantEntity` + `ICompanyShared`/`ICompanyScoped` uygular ve migration'da `TenantIsolationSql.AddTable` ile RLS'e eklenir.

Tetikleyiciler:
- Kayıt oluşturma, belirli alanların değişmesi, durum geçişi.
- Zamanlanmış tarama, belirli tarihten önce/sonra, hareketsizlik süresi.
- Domain event ve yetkili kullanıcının manuel çalıştırması.

Koşullar:
- İç içe AND/OR grupları; eşit, farklı, büyük/küçük, aralık, boş/dolu, içerir, listede bulunur, tarih karşılaştırmaları.
- Veri tipine göre izinli operatörler (enum, para, sayı, boolean, tarih, lookup).
- Lookup karşılaştırmasında görünen metin yerine kalıcı kimlik.
- İlişkili kayıtlar için sınırlı exists/count/aggregate; sınırsız ilişki gezintisi ve N+1 yok.
- Eski/yeni değer ayrımı, null davranışı, tenant saat dilimi ve tarih hassasiyeti açıkça tanımlı.
- Serbest SQL, C#, script veya çalıştırılabilir kod girişi yok; tipli ve doğrulanan koşul modeli.

Aksiyonlar: görev oluşturma, sorumlu atama, uygulama içi bildirim, izinli alan güncelleme, onay başlatma, puan hesaplama, iş akışı başlatma, kayıtlı modül aksiyonunu çağırma.

Aynı alanı değiştiren birden çok kural için sıralama, çakışma politikası, öncelik ve stop-processing davranışını tanımla. Sonsuz döngüyü correlation/causation bilgisi, tekrar politikası ve azami zincir derinliğiyle engelle.

### 5. Kalıcı iş akışları ve onaylar

İş akışı tanımı ile çalışan örneği ayır. Başlat, koşullu dallan, aksiyon çalıştır, görev/onay bekle, tarihe kadar bekle, tamamla, iptal et ve hata durumlarını destekle. İlk sürümde sağlam sıralı akış ve koşullu dallanma yeterlidir; görsel BPMN altyapısı kurma.

- Beklemeleri bellekte veya HTTP isteğinde tutma; uygulama yeniden başlayınca kaldığı yerden devam etsin.
- Çalışan örnek, başlatıldığı tanım sürümüne bağlı kalsın.
- Onay/ret, gerekçe, süre aşımı, eskalasyon, vekalet ve kendini onaylamayı engelleme.
- Onaylanan içerik değişirse eski onayı geçersiz kılma politikasını uygula (onay hangi kayıt sürümü ve değerler için verildiyse onu kapsar).
- Modül/lisans kapanınca yeni çalıştırmayı engelle; bekleyen örnekleri tanımlı biçimde duraklat, yöneticiyi bilgilendir.
- Başarılı önceki aksiyonları geri alınmış gibi gösterme. Kısmi başarı, telafi edilebilirlik ve güvenli yeniden çalıştırma açık olsun.

### 6. Güvenilir çalıştırma ve yetki

- Tenant kimliğini istemci parametresinden belirleme; doğrulanmış bağlamdan çöz. Worker ve olay tüketiminde de zorunlu doğrula.
- Kural yönetme, yayınlama, çalıştırma, log görme ve onaylama ayrı izinler olsun (`automation.*` anahtarları, `.RequirePermission`).
- Kuralı yayınlayan kullanıcı ile çalıştırma kimliği ayrı olsun. Otomasyon için sınırlı yetkili servis kimliği kullan; çalıştırma anında izin, kapsam, departman, kayıt sahipliği, alan güvenliği ve lisans kontrolü hem sorguda hem aksiyonda uygulansın.
- Tenantlar arası lookup, şablon, kuyruk, log veya önbellek erişimi engellensin.
- Mevcut outbox yaklaşımını kullan. En az bir kez teslim edilen olaylarda aynı iş etkisi iki kez oluşmasın. Tekilleştirme anahtarı: tenant + olay/planlı çalıştırma kimliği + kural sürümü + hedef kayıt + aksiyon adımı; meşru tekrarları engelleme.
- Retry, artan bekleme, azami deneme, başarısız işler kuyruğu, yetkili manuel tekrar.
- Birden çok worker için sahiplenme/lease, eşzamanlılık ve transaction sınırları; yeniden başlama ve yarıda kalan işlem senaryoları.
- Harici kanalda sağlayıcı tekilleştirmesi yoksa tam bir kez teslim garantisi verme; belirsiz sonuçları görünür kıl.
- Kural kapatılınca kuyruktaki işlere ve yürüyen adımlara ne olacağını tanımla; tüm otomasyonlar için acil durdurma sun.
- Her yönetim işlemi ve her otomasyon etkisi denetim kaydına yazılır (kaynak: otomasyon).

### 7. Zaman, puanlama ve şablonlar

Tarihleri UTC sakla; tenant saat diliminde göster. Çalışma takvimi/tatil altyapısı Dilim 0'da yoksa, SLA hesabı için gerekçeli en küçük takvim altyapısını kur veya Dilim 4'e ertele ve ADR'de belirt. Mevcut `SlaScheduler` davranışını bozma; genişlet. SLA duraklatma, yeniden açılma, öncelik değişimi desteklenir. Zamanlayıcı kesintisinden sonra kaçırılan çalışmaların telafi politikasını ve bildirim tekrar sıklığını belirle.

Lead uygunluğu, fırsat riski, müşteri hareketsizliği ve partner performansı için yönetilebilir kriter/ağırlık altyapısı kur. Puan aralığı, eksik veri, üst/alt sınır, sürüm ve yeniden hesaplama açık olsun; kullanıcı bileşenleri görebilsin. Puanı kazanma olasılığı olarak sunma. Ağırlıklı satış tahmininde tutar × tanımlı aşama olasılığı yöntemini açıklayarak kullan; farklı para birimlerini doğrudan toplama.

Görev listesi, bildirim, e-posta taslağı ve belge şablonlarını metadata üzerinden yönet. Değişkenleri doğrula, yetkisiz alanları engelle, HTML'e giren kullanıcı girdisini `WebUtility.HtmlEncode` ile kodla, eksik değişken davranışını belirle, önizleme sun.

### 8. İlk CRM otomasyonları

Gün/sınır değerleri tenant tarafından değiştirilebilir, **taslak** şablonlar olarak geliştir. Eksik modül varsa sahte başarılı entegrasyon üretme; bağımlılığı ve etkinleştirme koşulunu göster.

| Şablon | Koşul | Aksiyon |
|---|---|---|
| Fırsat takibi | 7 gündür ilgili aktivite yok | Sorumluya tek takip görevi |
| Teklif süresi | Geçerlilik bitimine 3 gün kaldı | Uygulama içi bildirim |
| İndirim onayı | İndirim yetki sınırını aşıyor | Mevcut onay sürecine gönder |
| Sözleşme yenileme | Bitişe 30 gün kaldı | Yenileme akışı başlat |
| SLA uyarısı | Kullanılan SLA süresi %80 | Ekip liderine bildirim |
| Mükerrer kontrolü | Aynı normalize vergi kimliği | Mevcut duplicate kontrolünü uygula |
| Satış devri | Teklif kabul edildi | Yetkili/idempotent sipariş servisini çağır |
| Müşteri başlangıcı | Uygun sipariş/hizmet başlangıcı | Şablondan onboarding görevleri |
| Hareketsiz müşteri | 90 gündür tanımlı işlem yok | Mevcut segment servisine kriter sun |
| Lead atama | Kaynak/bölge/ürün eşleşiyor | İlgili ekibe kurallı atama |
| Eksik bilgi | Metadata'ya göre bilgi eksik | Tamamlama önerisi/görevi |
| Fırsat riski | Kapanış geçmiş ve takip yok | Gerekçeli risk puanı |

**Önemli:** Bu tablodaki bazı işler (sözleşme yenileme, satış devri, SLA, segment) ilgili modüllerde zaten zamanlayıcı veya servis olarak çalışıyor olabilir. Her şablondan önce mevcut davranışı bul; çift tetikleme yaratma, mevcut zamanlayıcıyı kural motoruna taşıma kararını ADR'de gerekçelendir.

Veri bütünlüğü gerektiren engelleri (kritik mükerrerlik, onaysız indirim) yalnızca asenkron otomasyona bırakma; kayıt işleminin uygulama doğrulamasında da çalışsın.

Lojistik için belge eksikliği, bakım tarihi ve kapasite eşiği yeteneklerinin aynı sözleşmeyle eklenebildiğini, `Modulix.Modules.Logistics` içinde minimal bir yetenek kaydı veya mimari testle kanıtla; tam lojistik geliştirme bu görevin dışında.

### 9. Ekranlar (Blazor, mevcut tema ve bileşenlerle)

Türkçe, teknik olmayan kullanıcıya uygun ekranlar. Her ekranda yükleniyor, boş, hata, yetkisiz, pasif modül ve doğrulama durumlarını tasarla; masaüstü ve dar ekranda kullanılabilir olsun. Kayıt formu düzeni gerekiyorsa ADR-0021 metadata modelini kullan, alan yerleşimini Razor'a sabit yazma.

1. Otomasyon Merkezi: modül/durum/tetikleyici filtreleri, arama, son çalışma ve hata göstergesi.
2. Kural editörü: genel bilgi → tetikleyici → koşullar → aksiyonlar → test → yayınlama; dinamik alan seçici, iç içe koşullar, lookup araması, tipli parametreler, okunabilir kural özeti.
3. İş akışı editörü (adım listesi tabanlı; diyagram şart değil).
4. Otomasyon şablonları: açık modüllere göre seçme, çoğaltma, uyarlama.
5. Çalışma geçmişi: sonuç, süre, kural sürümü, koşul açıklaması, aksiyonlar, hata, yetkili yeniden deneme.
6. Onay kutusu.
7. Puanlama ayarları ve kayıt üzerinde puan açıklaması.
8. Zamanlama/çalışma takvimi ayarları ve operasyon durumu.
9. İlgili kayıtta otomasyon geçmişi: neden görev açıldı, hangi kural neyi değiştirdi.

Test/önizleme **hiçbir yan etki üretmesin**. Yetkili örnek kayıtlarla hangi koşulların geçtiğini ve hangi aksiyonların planlandığını göster. Bir alan veya lookup silinir/pasifleşirse ilgili kuralı sessizce bozma; bağımlılığı göster ve yayınlamayı engelle.

### 10. Performans ve işletim

- Normal kayıt işlemlerinde yalnızca zorunlu doğrulamalar senkron; uzun süreçler dayanıklı kuyruğa.
- Her istekte bütün kuralları tarama; tenant, modül, varlık, olay ve değişen alanlardan aday kural seç. Kural önbelleğini sürüm bazlı geçersiz kıl.
- Zamanlanmış taramalar indeksli, sayfalı, artımlı ve iptal edilebilir olsun.
- Tenant bazlı eşzamanlılık, iş sayısı, tarama boyutu, zincir derinliği ve süre limitleri; yoğun tenant diğerlerini durdurmasın.
- Çalışma ve audit kayıtları için saklama/silme politikası ve hassas veri maskeleme.
- Kuyruk bekleme süresi, işlem süresi/p95, başarı/hata/tekrar oranı, tekilleştirilen işler, tenant başına yük ölçülsün. Bu ölçümler ileride AI kullanım ölçümü (kota ve fiyatlama) için de aynı kullanım defterine bağlanabilecek biçimde tasarlansın; defteri bu görevde AI için doldurma.
- Önce mevcut donanım ve veritabanıyla baz ölçüm yap, temsilî veri ve eşzamanlılıkla karşılaştır. Ölçmediğin gecikme veya kapasiteyi garanti olarak yazma.
- Yeni ücretli altyapı zorunlu tutma; mevcut scheduler/worker yeterliyse kullan.

### 11. Kabul testleri

CLAUDE.md'deki dört test kuralı her özellik için geçerlidir: mutlu yol, hata/doğrulama, tenant izolasyonu, yetki. Ek olarak, kapsanan dilime ait olanları yaz:

- AND/OR, null, tip, lookup, tarih ve eski/yeni değer koşulları.
- Tenantlar arası sızıntı; yetkisiz alan/aksiyon engeli.
- Aynı olayın tekrar teslimi ve eşzamanlı iki worker altında tek iş etkisi.
- Günlük taramada aynı dönem için tekrar görev açılmaması; sonraki meşru dönemin engellenmemesi.
- Onay sırasında kayıt değişikliği, yetki kaybı, modül/lisans kapanması.
- Restart'ta bekleyen akışın korunması; retry sonrası kısmi başarı.
- Döngüsel tetikleme sınırı, acil durdurma, önizlemenin yan etkisizliği.
- Hafta sonu/tatil, saat dilimi, SLA duraklatma, kaçırılan zamanlama.
- Silinen metadata bağımlılığı, sürüm değişimi, çalışan örneğin eski sürümü koruması.
- Kabul edilen tekliften siparişin bir kez oluşması.
- CRM kurulu olmadan Core'un derlenmesi ve başka bir modülün yetenek kaydı (mimari test).
- Büyük kayıt kümesinde sayfalama, indeks kullanımı ve tenant yük sınırları.

Migration'lar geriye uyumlu ve kesintisiz olsun (tek veritabanı bütün tenant'ları etkiler); `dotnet ef database update` kullanma. Dağıtım ve geri dönüş adımlarını belgele. Çalıştır: `dotnet build`, mimari testler, Core/CRM/ilgili birim testleri, veritabanı varsa entegrasyon testleri. **Veritabanı yoksa entegrasyon testlerini çalıştıramadığını açıkça söyle.** Gerçek harici alıcıya test mesajı gönderme.

### 12. Dilimler ve teslim

Her dilim kendi başına çalışan bir dikey kesittir; küçük, incelenebilir PR'lar halinde ilerle (değişiklikler GitHub'da, doğrudan `main`'e PR; merge'i kullanıcı yapar).

| Dilim | İçerik | Bu oturum |
|---|---|---|
| 0 | İnceleme, boşluk analizi, ADR-0027 taslağı, mevcut altyapıyı kullanma kararı | Evet |
| 1 | Core sözleşmeleri, tipli koşul modeli, aksiyon/araç sözleşmesi, veri modeli ve migration, **yan etkisiz değerlendirme (dry-run)**, yetki ve audit | Evet |
| 2 | Güvenilir yürütme: olay/zamanlayıcı bağlantısı, tekilleştirme, lease, retry, acil durdurma, tenant limitleri | Hayır |
| 3 | CRM aksiyonları ve ilk şablonlar (pasif); demo: hareketsiz fırsat → kural → tek görev → kayıt geçmişi | Hayır |
| 4 | Kalıcı akışlar, onaylar, çalışma takvimi/SLA genişletme, puanlama, şablon motoru | Hayır |
| 5 | Blazor yönetim ve kayıt ekranları | Hayır |
| 6 | Performans ölçümü, işletim belgesi, yol haritası güncellemesi | Hayır |

Her dilimin sonunda şunları sun:
- Mevcut durum ve yapılan değişiklikler; dosya/sınıf referansları.
- Çalıştırılan test komutları ve sonuçları; çalıştırılamayanlar ve nedeni.
- Bilinen sınırlamalar, kalan işler, migration ve geri dönüş adımları.
- Sonraki dilim için bağımlılıklar.

Son dilimde ek olarak: Core–CRM sınırı özeti, uçtan uca demo, çalışan ve pasif/bağımlı şablonlar, ölçülen performans ve **"AI API'si veya yeni ücretli servis gerekti mi?" sorusunun açık cevabı** (beklenen: hayır).

Yol haritasında yalnızca uygulanıp doğrulanan işleri tamamlandı işaretle. Yapmadığın ekran, test veya entegrasyonu tamamlanmış gösterme. README'de belgelenen bir davranışı değiştirirsen README'yi de güncelle.

Şimdi Dilim 0'dan başla.
