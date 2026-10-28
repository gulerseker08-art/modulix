# ADR-0013: Mükerrer önerisi, mevcut ortak kaydı seçme ve güvenli birleştirme

- **Durum:** Kabul edildi ve uygulandı, 28 Ekim 2026
- **Karar:** Eşleşme önerilir, otomatik birleştirilmez. Vergi numarası tek kesin eşleşmedir; ad, e-posta ve telefon yalnızca olası eşleşmedir. Birleştirme ayrı izinli, önizlemeli, sürüm denetimli ve tek işlemli bir kayıt boru hattı komutudur; kaynak kaldırılır, eski kimlik hedefe izlenir

## Problem

Aynı firma ya da kişi iki kez açılabiliyordu (farklı yazılmış ünvan, farklı modülde açılmış taraf). Vergi numarası çakışması zaten reddediliyordu, ama kullanıcı mevcut kaydı görmeden "zaten kayıtlı" hatası alıyordu; vergi numarasız firmalarda ve kişilerde hiçbir uyarı yoktu. Oluşmuş mükerrerleri temizlemenin yolu da yoktu: biri silinirse fırsat, aktivite, ilişki ve iletişim kayıtları da gidiyor ya da bağlı kayıtlar yüzünden silinemiyordu.

## Karar

### Eşleşme: kesin ve olası ayrı

- **Kesin eşleşme yalnızca vergi numarasıdır** (ADR-0006). Aynı numarayla yeni firma açılırken mevcut ortak taraf kullanılır, yeni taraf açılmaz.
- **Olası eşleşme:** ünvan benzerliği (büyük/küçük harf, aksan, noktalama ve "A.Ş./Ltd./Anonim Şirketi" gibi hukuki ekler yok sayılır), aynı e-posta, aynı telefon; kişide ayrıca aynı ad ve soyad. Olası eşleşme **hiçbir zaman otomatik birleştirilmez ya da bağlanmaz**; yalnızca gösterilir.
- **Paylaşılan değer kanıt değildir** (ADR-0012): `isShared` işaretli santral/ortak e-posta noktaları eşleşme sayılmaz. Farklı kişilerin aynı e-postayı/telefonu paylaşması serbest kalır.
- Öneri uçları (`GET /api/crm/companies|contacts/duplicates`) kayıt boru hattından geçer: çağıran yalnızca kapsamındaki CRM kayıtlarını görür, düzenlenen kaydın kendisi (`excludeId`) önerilmez. Kapsam dışında ya da başka bir modülde (CRM uzantısı olmayan) kayıtlı kesin eşleşme için yalnızca `sharedId` ve asgari kimlik (ünvan) döner; ayrıntılar görünmez.

### Mevcut ortak kaydı seçerek CRM uzantısı açma

Tipli istekler `partyId` (firma) ve `personId` (kişi) taşır (ADR-0006'dan beri depolarda vardı; bu aşamada arayüz ve testle tamamlandı). Yeni CRM kaydı seçilen ortak kayda bağlanır, yeni taraf/kişi açılmaz; mevcut kayda bağlanırken boş gönderilen ortak alanlar kayıttaki değeri silmez. Sunucu seçimi her seferinde yeniden denetler: kayıt yoksa ya da zaten CRM uzantısı varsa 422 (öneride görünmesi izin vermek anlamına gelmez). Kullanıcının yeni firmayı "kullan" demesi için öneri satırında "Bu kaydı kullan" düğmesi vardır; yalnızca CRM kaydı olmayan (başka modülde açılmış) kesin eşleşmelerde görünür.

### Birleştirme

**Kimler:** ayrı izin `crm.companies.merge` / `crm.contacts.merge`; yazma izni yetmez, hazır hiçbir role verilmez (yönetici dışında rolle açıkça atanır). Kaynak ve hedef çağıranın veri kapsamında olmalıdır; birini göremeyen "bulunamadı" (404) alır, kayıtların varlığı belli edilmez. Başka tenant'ın kaydı 404'tür. Görme yetkisi olmayan (hassas) alanı bulunan kayıt türü birleştirilemez (`PermissionDenied`).

**Akış:** `POST …/{targetId}/merge/preview` (yazmaz: alan farkları, etki listesi, engeller) → `POST …/{targetId}/merge`. İstek her iki kaydın sürümünü ister; ekranın gördüğünden farklıysa 409 ve hiçbir şey yazılmaz. Önizleme güncel sürümleri döndürür.

**Atomiklik:** kaynağın bağlı kayıtlarının taşınması, kaynağın (ve ortak kaydının) kaldırılması, birleştirme izi, hedefin sürüm artışı, iki kayıt için audit ve olaylar **tek `SaveChanges`'tadır**. Depo hiçbir şey kaydetmez; boru hattı kaydeder. Engel, sürüm uyuşmazlığı, benzersizlik/yabancı anahtar çakışması ya da yetki hatasında işlem tümüyle geri alınır (kısmi birleştirme yoktur).

**Alan seçimi:** `fieldChoices` alan → `source`/`target`; verilmeyen alan hedeften kalır. Önizleme, hedef boşken kaynağı önerir (`suggested`). Bilinmeyen alan ya da geçersiz değer 422'dir. Özel alanlar: hedefte olmayan anahtar kaynaktan gelir, ikisinde de varsa hedef kalır, açık `custom.anahtar = source` seçimi kaynağı yazar. Kişide notlar kaybolmasın diye hedef notun altına kaynağınki eklenir (kaynak seçilmişse yalnızca kaynağınki). Vergi numarası hedefe geçerken kaynağınki aynı işlemde boşaltılır (benzersizlik).

**Firma birleştirmesinde taşınanlar** (kaynak → hedef):

| Kayıt | Kural |
| --- | --- |
| Firma–kişi ilişkisi | Aynı kişinin iki aktif ilişkisi tek kalır (roller birleşir, karar verici/başlangıç korunur, ilişkiye özel iletişim noktaları ikize geçer); diğerleri taşınır. Firmada tek ana irtibat: hedefinki kalır. Kişilerin birincil firması (ADR-0011) yeniden türetilir |
| Lokasyon | Taşınır. Aynı kod iki firmada da varsa **engel** (birleştirme reddedilir) |
| Adres, iletişim noktası | Taşınır; aynı değer (aynı tür/kanal) tek kalır. Ana kayıt varsayılan olarak hedefinki; eski alan (`phone`, `email`, `city`, `address`) "source" seçilirse kaynağın ana kaydı öne geçer |
| Firma–firma ilişkisi | Taşınır; kendine dönen ve yinelenen aktif ilişki kaldırılır; sonuçta aktif ilişkilerde **döngü** oluşuyorsa engel |
| Fırsat, aktivite | `companyId` hedefe geçer |
| Ortak taraf (Party) | Kaynağınki kaldırılır; tarafa başka modüllerin başvuruları aşağıdaki sözleşmeyle taşınır |

Tüzel kişi ile gerçek kişi firması birleştirilmez (engel). **Kişi birleştirmesinde** ilişkiler, iletişim noktaları (aynı kanal/değer/ilişkide tek kalır), fırsat/aktivite/lokasyon yetkilisi başvuruları hedef kişiye geçer; ortak kişi kaydı kaldırılır.

**Ana kayıt işaretini çevirmeden içerik takası.** Kaynağın ana adresi/telefonu öne geçecekse iki satırın "ana" işareti çevrilmez, **içerikleri takas edilir** (hedefin ana satırı kaynağın değerini alır, taşınan satır hedefin eski değeriyle ikincil olur). Sebep: tek ana kayıt indeksi filtrelidir ve EF'in güncelleme sırası iki satırın bayrağını çevirirken geçici olarak iki ana satır üretip indeksi ihlal edebilir (gerçekte yaşandı); içerik takası hiçbir sırada iki ana satır oluşturmaz.

### Birleştirme izi ve tekrar koruması

`crm.MergedRecords` (tenant tablosu, RLS'li): `EntityKey`, `SourceId`, `TargetId`, kim, ne zaman. `(TenantId, EntityKey, SourceId)` benzersizdir. Böylece:

- **Eski kimlik hedefe çözülür:** `GET …/merged/{oldId}`; arayüz firma/kişi sayfası eski kimlikle açılırsa hedefe yönlendirir. Hedefi göremeyen çağıran 404 alır (eski kimliğin varlığı da belli edilmez).
- **Tekrarlanan istek** sessizce başarılı olmaz: kaynak yoksa ve iz varsa 409 "zaten birleştirilmiş (hedef …)".
- **Eşzamanlı** iki birleştirme (aynı kaynak, iki hedef) sürüm denetimi ve izin benzersiz indeksiyle yarışır; yalnızca biri geçer, ötekisi 404/409/422 alır (test).

### Audit ve olay

Hedef için `AuditAction.Merged` (değişen alanlar + `mergedFrom`) ve olay `RecordUpdated`; kaynak için `AuditAction.Merged` (son hali + `mergedInto`) ve **yeni olay türü `RecordMerged`** (sürüm 1). Kaynak kaydı silinmiş gibi görünmez: olayı eski kimliğe başvuran modüller `mergedInto` ile kendi başvurularını yenileyebilir.

### Başka modüllerin başvuruları: genişletilmiş başvuru sözleşmesi

`SharedRecordReference` (ADR-0006) başvuruyu yalnızca saymak (silme koruması) içindi. Birleştirme için: başvuran kayıt deposu `IReferenceMover.RepointAsync(field, fromId, toId)` uygularsa başvuru yeni kayda taşınır (kaydetmeden; aynı işlemde). Uygulamıyorsa ve başvuru varsa birleştirme **engellenir** ("N sevkiyat bu kayda başvuruyor ve taşınamıyor"); eski kayda bakar kalan başvuru oluşmaz. Birleştiren modülün kendi başvuruları (`crm.*`) deposunca taşınır; yalnızca diğer modüllerinki bu mekanizmaya girer. Çekirdek bunu `ISharedReferenceMerger` ile yapar, kayıt türlerini ve tablolarını bilmez. Bugün tek taşıyıcı `core.person.partyId`'dir (kişinin çalıştığı taraf); Lojistik henüz ortak kayda başvuran bir alan bildirmiyor, o zaman `IReferenceMover` uygulamak onun sorumluluğundadır.

### Çekirdek mi CRM mi

Birleştirme **sözleşmesi** çekirdekte (`IMergeableRecordStore`, `MergeRequest`, `RecordOperation.Merge`, boru hattı adımları): izin/kapsam/sürüm/audit/olay her modül için aynı olmalı ve bir modül başkasının tablosuna dokunmamalı. Taşınacak kayıtların **kuralları ve iz tablosu CRM'de** (firma/kişi uzantısı, lokasyon, adres, ilişki CRM kavramlarıdır; ADR-0012). Başka modüller CRM projesine bağlanmaz.

## Migration ve geri alma

`MergedRecords` migration'ı tek yeni tablo ve RLS politikasını ekler; mevcut veriye dokunmaz. Geri alma tabloyu kaldırır (izler kaybolur, birleştirmenin kendisi geri alınmaz).

## Sonuçlar

- Mükerrer kayıt oluşmadan önce görünür; oluşmuş olanlar kayıp olmadan birleştirilir.
- Birleştirme **geri alınamaz**: kaynak kaydın son hali audit'te kalır, ama kayıt yeniden oluşturulmaz. Arayüz bunu açıkça onaylatır.
- Taşınan alt kayıtlar (fırsat, aktivite, adres, ilişki...) tek tek audit satırı yazmaz; etki sayıları API yanıtındadır, hedefin audit satırında `mergedFrom` ve alan farkları vardır.

## Bilinen sınırlar / kalan iş

- Mükerrer önerisi yalnızca CRM kayıtlarını ve (vergi numarası için) ortak taraf kaydını arar; kişi için ortak kişi (CRM'siz) araması yoktur, o yüzden arayüzde kişi için "Bu kaydı kullan" yoktur (API'de `personId` ile mümkün).
- Ünvan benzerliği basit bir ön-ek/eşitlik kuralıdır (bulanık/yazım hatası eşleşmesi yok); olası eşleşmeler kaçabilir, yanlış pozitifler kullanıcı kararına bırakılır. Arama, ünvandaki en uzun sözcükle yapılır.
- Çözülemeyen başvuru engeli, depo `IReferenceMover` uygulamadığı durumda boru hattı birim testleriyle doğrulandı; gerçek bir ikinci modülün başvuru bildirdiği uçtan uca senaryo yok (Lojistik henüz bildirmiyor).
- Lokasyon kodu çakışması ve döngü kullanıcıya engel olarak gösterilir ama arayüzde çözüm aracı yoktur (kullanıcı kodu yeniden adlandırır / ilişkiyi kaldırır, sonra tekrar dener).
- Birleştirmenin geri alınması ve toplu (arka plan) birleştirme yoktur.
- Arayüz (öneri paneli, birleştirme sihirbazı) derlenir ve API'si entegrasyon testlerinden geçer; tarayıcıda elle denenmedi.
