# ADR-0016: Merkezi lookup altyapısı (ilişkisel alan seçimi)

- **Durum:** Kabul edildi; 29 Ekim 2026'da uygulandı. Uygulanmayanlar "Yapılmadı / sınırlar" başlığında.
- **Karar:** Kullanıcının seçtiği N→1 referans alanları tek bir ortak yapıdan geçer: modül başına **sabit tanım** (`IModule.Lookups`), firma başına **tek yönetilebilir ayar** (`Aranabilir`), **sunucu tarafı** arama/çözümleme (kayıt boru hattı üzerinden) ve tek bir arayüz bileşeni (`LookupPicker`). Yeni bir tenant ya da veri mimarisi getirilmedi: ADR-0009 (tenant başına veritabanı) aynen geçerli, çelişen bir durum bulunmadı.

## Mevcut durum ve eksik analizi (uygulamadan önce)

| Konu | Durum | Kanıt |
| --- | --- | --- |
| Sunucuda arama, sayfalama, erişim kapsamı | **Mevcut** | `GET /api/crm/{kaynak}?search=&page=&pageSize=` kayıt boru hattından geçer (`CrmRecordStore.QueryAsync`: izin, sorumlu/şirket kapsamı, hassas alan); sınır `RecordQuery.MaxPageSize = 200`, `MaxSearchLength = 100` |
| Formlardaki seçim kutuları | **Eksik** | Her form `<select>` kullanıyordu; veri `CrmLookups.LoadAsync` ile **ilk 200** firma/kişi/fırsat olarak istemciye indiriliyordu (`pageSize=200`). 200'ü aşan veride seçim imkânsız, arama yok, düzenlemede mevcut seçim ilk 200'de değilse boş görünüyordu |
| Aranabilirliğin ayarı | **Eksik** | Kavram yoktu |
| Bağımlı alan (firma → kişi/fırsat) | **Kısmen** | Yalnızca istemcide, yüklenen listeyi süzüyordu (`Lookups.Contacts.Where(c => c.CompanyId == …)`); sunucu kaydı doğrulamıyordu. Sadece birincil firma bakıyordu, aktif kişi-firma ilişkisi (ADR-0011) yoktu |
| Kayıtta ilişki görünürlüğü | **Kısmen** | Kişi→firma, Lead→firma/kişi, Aktivite→firma/kişi/fırsat/Lead, Lokasyon→kişi, Firma ilişkisi, Kişi-firma ilişkisi doğruluyordu (varlık + okuma izni + kapsam, yalnızca değişimde). **Fırsat→firma/kişi yalnızca varlığı** denetliyordu: kapsam dışı firma kimliğiyle fırsat açılabiliyordu |
| Kayıtta ilişkililik (kişi, seçili firmanın kişisi mi) | **Eksik** | Hiçbir kayıt türünde sunucu doğrulaması yoktu |
| Sorumlu alanı | **Mevcut** | `ITenantMembers.IsActiveMemberAsync` ve atama kapsamı (`Field.Owner`, Y8) sunucuda doğrulanıyordu; seçim kutusu `?assignable=true` üyeleri düz liste olarak getiriyordu |

## İlişki envanteri ve sınıflandırma

N→1 alanlar (lookup'a dönüştürüldü). "Zorunlu" sunucu tanımındaki durumdur.

| Lookup anahtarı | Alan → hedef | Zorunlu | Kullanıldığı ekranlar | Durum |
| --- | --- | --- | --- | --- |
| `crm.contact.company` | Kişi.companyId → Firma | Hayır | Kişi formu (oluştur/düzenle; kişi detayı ve listesi aynı bileşen) | Dönüştürüldü |
| `crm.opportunity.company` | Fırsat.companyId → Firma | Evet | Fırsat formu; firma detayında sabit (gizli) | Dönüştürüldü |
| `crm.opportunity.contact` | Fırsat.contactId → Kişi (**bağımlı**: firma) | Hayır | Fırsat formu | Dönüştürüldü |
| `crm.lead.company` / `crm.lead.contact` | Lead.companyId/contactId → mevcut Firma/Kişi (bağımlı) | Hayır | Lead formu, Lead dönüştürme paneli | Dönüştürüldü |
| `crm.activity.company` / `.contact` / `.opportunity` | Aktivite → Firma / Kişi / Fırsat (kişi ve fırsat **bağımlı**: firma) | Hayır | Aktivite formu (firma detayında firma sabit) | Dönüştürüldü |
| `crm.location.contact` | Lokasyon.contactId → Kişi (bağımlı: firma) | Hayır | Firma detayı → Lokasyonlar | Dönüştürüldü |
| `crm.company-contact.contact` | Kişi-firma ilişkisi.contactId → Kişi | Evet | Firma detayı → Kişiler | Dönüştürüldü |
| `crm.company-relation.child` | Firma ilişkisi → diğer Firma | Evet | Firma detayı → İlişkili firmalar | Dönüştürüldü |
| `crm.{company,contact,opportunity,lead,activity}.owner` | ownerId → kuruluş üyesi | Hayır | Tüm kayıt formları, "Yeniden ata" (aktivite), "Ata" (Lead) | Dönüştürüldü |

**Lookup yapılmayanlar (gerekçeli):**

- Sistem alanları: `sourceLeadId`, `convertedCompanyId/ContactId/OpportunityId/By`, `createdBy`, `previousActivityId`, `TenantId`, audit alanları. Kullanıcı seçmez, iş akışı yazar (`AsSystem`).
- `Aktivite.leadId`: yalnızca Lead ekranından, sabit bağlamla yazılır; kullanıcı seçmez.
- Aktivite ilişkileri (`ActivityLinks`, polimorfik) ve katılımcılar: çoktan çoğa/polimorfik; mevcut ilişki yönetimi korundu. Hedef türe göre ayrı arama gerektirir; bu çalışmada kapsam dışı.
- Kişi↔firma çoktan çoğa ilişkisinin kendisi (`CompanyContacts`): ilişki satırı olarak yönetilir; yalnızca satırın `contactId` seçimi lookup oldu.
- Seçenek listeleri (aşama, durum, kaynak, aktivite türü, öncelik, ilişki rolü): sabit/düzenlenebilir **liste** alanlarıdır (Y9b), kayıt referansı değil; lookup'a ve yeni tabloya dönüştürülmedi.
- 1→N koleksiyonlar (firmanın kişileri, fırsatları, lokasyonları): ilişkili kayıt listesi olarak kaldı.
- Liste filtreleri (kişi/firma süzgeçleri) ve genel arama: ayrı özellikler; değişmedi.

## Tanımlar ve ayarlar

- **Tanım (kodda, değişmez):** `LookupDefinition` (`Modulix.Core.Contracts/Modules/LookupDefinition.cs`): sabit anahtar `modül.kaynak.alan`, kaynak entity + alan, hedef entity (ya da üye), başlık, etiket alanları, ikincil alanlar, arama alanlarının açıklaması, varsayılan aranabilirlik, en az karakter (2), sayfa boyutu (20, üst sınır 50), bağımlılık (üst alan + hedef filtre adı), ayrıntı yolu. `ModuleCatalog` anahtar önekini, tekrarı ve sınırları doğrular.
- **Ayar (veritabanında, yönetilebilir):** yalnızca `Searchable` (açık/kapalı/varsayılan). Tablo `meta.LookupSettings` (tenant veritabanı, RLS, `(TenantId, LookupKey)` benzersiz). Satır yoksa ya da değer boşsa tanımın varsayılanı geçerli; **ayar yokken formlar çalışır**. "Varsayılana dön" satırı silmez, değeri boşaltır ve sürümü artırır (silme-yeniden-oluşturma ile sürüm geri sarmasın diye).
- **Öncelik:** firma ayarı > tanım varsayılanı. Güvenlik/ilişki kuralları (izin, kapsam, bağımlılık, sınırlar) tanımdadır; ayarla kaldırılamaz.
- **Üç kavram ayrı:** *lookup içinde aranabilir* (bu ADR), *liste filtresinde kullanılabilir* (CRM liste süzgeçleri) ve *global aramaya dahil* (kapsam dışı) birbirine bağlı değildir.
- **Yönetim API:** `GET /api/lookups` (her üye; formlar davranışı buradan öğrenir), `PUT /api/customization/lookups/{anahtar}` (`core.customization.manage`; gövde `{ searchable: true|false|null, version }`). Ham SQL, tablo adı, serbest sorgu ya da uç nokta tanımı yönetilemez; tanımsız anahtar 404.
- **Eşzamanlılık:** sürüm + EF eşzamanlılık belirteci; eski sürüm 409 (ekran satırda "yenileyip tekrar deneyin" der), sessiz ezme yok.
- **Denetim:** `core.lookup-setting` kayıt türü: kim, ne zaman, alan (`kaynak · başlık`), eski/yeni `Aranabilir` ve ayar kaynağı (Varsayılan / Firma ayarı). Değişmeyen değer yazılmaz.

## Arama ve çözümleme (sunucu)

- `GET /api/lookups/{anahtar}/search?q=&page=&pageSize=&parentId=` ve `POST /api/lookups/{anahtar}/resolve`. Tenant yalnızca oturumdan gelir; istemci tenant/veritabanı bilgisi gönderemez.
- Kayıt hedefleri `IRecordPipeline.QueryAsync/GetAsync` üzerinden okunur: **arama, sayfalama, toplam ve kimlikten çözümleme aynı** izin, veri kapsamı (Role/Team/sorumlu), şirket kapsamı ve hassas alan kurallarını kullanır. Platform yöneticisi tenant üyesi değilse tenant verisine erişemez (oturumdaki aktif tenant olmadan uç nokta çalışmaz).
- **Aranabilir kapalı:** `q` dolu istek 422 (`search` alanı). Boş `q` ile sayfalı gezinme ve kimlikten çözümleme çalışır. **Aranabilir açık:** `q` en az 2, en çok 100 karakter; boşluklar tekilleştirilir.
- Üye lookup'ı: kişinin kayıt kapsamındaki etkin üyeler (atama kuralı); ada göre arama `tr-TR` kültüründe büyük/küçük harf ve aksan duyarsızdır; sıralama ad + kimlik.
- **Sınırlar:** sayfa boyutu en çok 50 (istenen büyük değer kırpılır), çözümleme en çok 50 kimlik; sıralama kararlıdır (hedef deposunun sırası, ardından `Id`). Yanıt yalnızca `id`, etiket ve ikincil bilgi taşır; ikincil alanlar hassas alan kuralından geçmiş değerlerdir.
- **Erişilemeyen kayıt:** silinmiş, kapsam dışı ya da izni kalkmış kayıt aynı yanıtı alır (`unavailable`, etiket yok); ad ve varlık sızmaz. Arayüz "Erişilemeyen kayıt" der, seçimi temizlemeye izin verir.
- **Ayrıntıya bağlantı** yalnızca hedef türü okuyabilen kullanıcıya gösterilir (`canView`).
- **Önbellek yoktur**: ayar ve yetki değişikliği bir sonraki istekte geçerlidir; bayat sonuç riski yok.
- **Günlük:** arama metni günlüğe yazılmaz.

### Türkçe karakter ve normalizasyon (ölçülen davranış)

Kayıt aramaları `SQL_Latin1_General_CP1_CI_AS` kolasyonunda `LIKE` ile çalışır. Test edildi (`LookupTests`):

- Büyük/küçük harf duyarsızdır: `ç/Ç`, `ğ/Ğ`, `ş/Ş`, `ö/Ö`, `ü/Ü` eşleşir; baştaki/sondaki ve tekrarlı boşluk normalleştirilir.
- **Eşleşmeyenler (bilinen sınır):** noktasız `ı` ile büyük `I`, noktalı `İ` ile küçük `i` (`ÇAĞRI` ≠ `çağrı`, `inşaat` ≠ `İnşaat`). Veritabanı kolasyonunu ya da arama sütunlarını (normalleştirilmiş sütun) değiştirmek bu çalışmanın kapsamı dışında bırakıldı; çözüm ADR-0012'deki adres gibi bir normalleştirilmiş arama sütunudur. Üye araması (uygulama içinde `tr-TR`) bu çiftleri doğru eşler.

## Bağımlı lookup ve kayıt doğrulaması

- Üst alan seçilmediyse alt lookup **süzülmez** (kullanıcının görebildiği tüm kayıtlar, sayfalı) ve ipucu gösterir; üst alan seçilince hedef `relatedCompanyId` (kişi: birincil firma **veya aktif kişi-firma ilişkisi**) / `companyId` (fırsat) filtresiyle **sunucuda** süzülür.
- Üst alan değişince alt seçim istemcide temizlenir ve nedeni söylenir (`role=status`); sessiz saklanmaz.
- **Kayıtta yeniden doğrulama** (`LookupGuard`, istemci süzgecine güvenilmez): bağlanan firma/kişi çağıranın görebildiği kayıt olmalı (okuma izni + kapsam; aksi "bulunamadı", var olmayanla aynı yanıt); kişi seçili firmayla ilişkili olmalı (Fırsat, Lead, Aktivite, Lokasyon); aktivitenin fırsatı seçili firmanın olmalı. Fırsat.firma/kişi için görünürlük denetimi bu çalışmayla **eklendi** (eksikti).
- **Pasif/değişmeyen bağ politikası:** değeri değişmeyen bağ yeniden gönderildiğinde denetlenmez; böylece sonradan görünmez ya da ilişkisiz kalmış eski bir bağ, ilgisiz bir alan düzenlenirken engel olmaz. Değişen veya yeni bağ her zaman doğrulanır. İş akışı yazımları (Lead dönüşümü) mevcut kapıyla ayrıdır.
- Hedef kayıt silinirken mevcut domain kuralları korunur; cascade eklenmedi. CRM hedeflerinde (firma, kişi, fırsat) "pasif" kavramı yoktur (firma durumu bir liste değeridir, seçimi engellemez); üyelerde yalnızca etkin üyeler yeni seçimde sunulur, mevcut seçili pasif üye "Erişilemeyen kayıt" görünür.

## Arayüz

- `LookupPicker` (combobox/listbox; ARIA: `aria-expanded`, `aria-controls`, `aria-activedescendant`, `role="status"` duyuruları): tekli seçim, zorunlu/isteğe bağlı (temizle ×), salt okunur/devre dışı, mevcut seçimi kimlikten yükler, aynı isimliyi ikincil bilgiyle ayırır, 300 ms debounce, her istek sıra numarası alır (**geç gelen eski yanıt yok sayılır**), sonuç yok / yükleniyor / hata + "Tekrar dene", "Daha fazla göster" ile sayfalı gezinme, yön tuşları + Enter + Esc, mobilde alt tabaka listesi, yetkiliye "Aç" bağlantısı.
- Aranabilir kapalıysa metin girişi **salt okunur** olur (yazılamaz), tıklama/ok tuşu sayfalı listeyi açar.
- "Alan ve Lookup Ayarları" (`/yonetim/alan-ve-lookup-ayarlari`): modül/kayıt türü grupları, alan ve hedef, anahtar, "ne değişir" açıklaması, süzme, toplu kaydet, "değişiklikleri at", "varsayılana dön", satır bazında çakışma/hata, yükleme ve değişiklik-yok durumları.

## Yapılmadı / sınırlar

- **Aranacak hedef alanların yönetici tarafından seçilmesi:** yapılmadı. Aranan alanlar kayıt deposunda sabit ve indeks/normalleştirme kararlarına bağlıdır; ekranda yalnızca açıklaması gösterilir. Güvenli bir tasarımı (izinli alan listesi) ayrı iş.
- **İş Birimi (BusinessUnit) → Kullanıcı bağımlılığı:** modüllere açık bir İş Birimi sözleşmesi yok (ADR-0015 ile aynı); yapılmadı. Üye lookup'ı kişinin kayıt kapsamını (Rol/Ekip/sorumlu) kullanır.
- "Yeni kayıt oluştur" seçeneği: mevcut formlarda yok; eklenmedi.
- Ek indeks: eklenmedi. Aramalar alt dize (`LIKE '%…%'`) olduğundan b-tree indeksi yardım etmez; sıralama ve kapsam mevcut indekslere (`Party(TenantId, Name)`, `Person(TenantId, LastName, FirstName)`, `(TenantId, OwnerId)`) dayanır. Büyük veride ölçüm yapılmadı; gerekirse normalleştirilmiş sütun + indeks.
- Liste sayfalarındaki firma/kişi adı çözümlemesi (`CrmLookups.CompanyName`) hâlâ ilk 200 kaydı kullanır; 200'ü aşan veride ad "—" görünür. Seçim kutuları bu veriye artık bağlı değildir; liste adı çözümlemesi ayrı bir iş olarak kaldı.
- Lead dönüştürmede "mevcut kişi seçili firmayla ilişkili mi" sunucu doğrulaması eklenmedi (dönüştürme kendi iş akışı kapısındadır); kayıt formundaki Lead alanı doğrulanır.
- Firma ilişkisi lookup'ında kendisini seçme artık listeden süzülmüyor; sunucu "Bir firma kendisine bağlanamaz" der.

## Devreye alma

1. `dotnet run --project src/Modulix.Api -- migrate-tenants`: her tenant veritabanına `meta.LookupSettings` (RLS ile) eklenir. Veri dönüşümü yok; mevcut ilişkiler ve kayıtlar değişmez, varsayılan "aranabilir açık" davranışı eskiyle uyumludur.
2. Yönetici gerekirse Yönetim → Alan ve Lookup Ayarları'ndan aramayı kapatır.
3. Geri dönüş: migration `Down` politikayı ve tabloyu kaldırır; formlar ayarsız çalışmaya devam eder.

## Güncel durum notu (13 Kasım 2026)

Lookup altyapısı bütün modüllerde kullanılıyor (CRM, Sözleşme, Sipariş, Destek, Pazarlama, Depo, Filo, Lojistik); son eklenenler depo
parti/hareket seçim kutularıdır ([ADR-0032](0032-depo-ve-gumruklu-antrepo-modulu.md) dilim 6b). "Yapılmadı / sınırlar" listesi hâlâ
geçerlidir: aranacak alanların yönetici seçimi yok; iş birimine göre kullanıcı lookup'ı için modüllere açık bir iş birimi sözleşmesi
yok (departman hiyerarşisi [ADR-0026](0026-departman-hiyerarsisi-ve-departman-bazli-gorunurluk.md) Core içindedir; öncelik listesi A-12).
