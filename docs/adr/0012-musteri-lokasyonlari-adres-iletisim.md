# ADR-0012: Müşteri lokasyonları, adresler, iletişim noktaları ve firma–firma ilişkisi

- **Durum:** Kabul edildi ve uygulandı, 28 Ekim 2026
- **Karar:** Müşterinin lokasyonu, çoklu adresi, çoklu telefon/e-postası ve firmalar arası bağı CRM'e özgü ayrı kayıtlardır; eski tekil alanlar bu kayıtların aynasıdır, tek doğruluk kaynağı yapılandırılmış kayıtlardır

## Problem

Bir müşteri firmanın tek telefon, tek e-posta, tek şehir ve tek adres metni vardı. Gerçekte bir müşterinin merkezi, fabrikası, depoları, ayrı fatura ve teslimat adresleri olur; santral ile kişisel hatlar ayrıdır; bir grubun iştirakleri ayrı tüzel kişiliklerdir. Bunlar ya firma kaydının çoğaltılmasıyla (aynı vergi no, bozuk raporlar) ya da serbest metin notlarla tutuluyordu.

## Karar

### Lokasyon ile firma ayrımı

- **Aynı tüzel kişiliğin şubesi/deposu/fabrikası/teslimat noktası** `crm.Locations` kaydıdır; yeni `Party` (ve vergi no) açmaz. Tür: Merkez, Şube, Fabrika, Depo, Mağaza, Teslimat, Fatura, Diğer (`CrmModule.LocationTypes`); ad, kod (firma içinde tekil), aktiflik, isteğe bağlı yetkili kişi.
- **Ayrı tüzel kişilik ayrı firmadır** (kendi `Party`'si ve vergi no'suyla) ve `crm.CompanyRelations` ile bağlanır.
- Lokasyon **işletmecinin kendi şirket/şubesi (ADR-0008, `ICompanyScoped`) değildir**: tenant'ın kendi organizasyon yapısına dokunmaz, onun tablolarını kullanmaz.
- Çekirdeğe konmadı: diğer modüller için anlamı yoktur; Lojistik gibi bir modül kendi durak/depo kavramını kendi tablosunda tutar. Gerekirse sonraki bir ADR ortak "yer" kavramını çekirdeğe taşıyabilir.

### Adresler (`crm.Addresses`)

Yapılandırılmış: ülke (ISO alfa-2, varsayılan TR), şehir, ilçe, posta kodu, iki satır, tür (Tescilli, Fatura, Sevkiyat, Ofis, Diğer). Sahibi firma, isteğe bağlı olarak firmanın bir lokasyonudur. Sahip başına en fazla bir aktif ana adres (depo 422 `isPrimary`; filtreli benzersiz indeks `UX_Addresses_PrimaryPerOwner` eşzamanlıda 409).

### İletişim noktaları (`crm.ContactPoints`)

Telefon veya e-posta; tür (İş, Cep, Santral, Fatura, Destek, Kişisel, Diğer); ana, aktif, paylaşılan. Sahip **firma** (isteğe bağlı lokasyonla) ya da **kişi**dir; kişi noktası isteğe bağlı olarak bir **firma–kişi ilişkisine özeldir** (`companyContactId`, ADR-0011), böylece "Ayşe'nin Acme'deki e-postası" kişinin genel e-postasından ayrı durur. İki kayıt türü, iki izin kümesi:

| Kayıt türü | Uç nokta | İzin | Görünürlük |
|---|---|---|---|
| `crm.contact-point` (firma/lokasyon) | `/api/crm/contact-points` | `crm.companies.*` | firmanın veri kapsamı |
| `crm.person-contact-point` (kişi) | `/api/crm/person-contact-points` | `crm.contacts.*` | kişi görünürlüğü (ADR-0010) |

Veri bütünlüğü veritabanında da: `CK_ContactPoints_Owner` kontrol kısıtı (sahip firma ya da kişi), sahip+kanal başına tek aktif ana nokta için filtreli benzersiz indeksler.

### Normalizasyon: orijinal korunur, eşleştirme ayrı

- `Value` girildiği gibi saklanır ve gösterilir. `NormalizedValue` yalnızca arama/eşleştirme içindir: e-posta kırpılıp küçük harfe, telefon rakamlara ve `+` ön ekine çevrilir (`0532 123 45 67`, `532-123-4567`, `0090 532…` hepsi `+905321234567`). Telefon 7–15 rakam olmalıdır; geçersiz değer 422'dir. Adreste `NormalizedText` Türkçe karakterleri katlayarak ("Şişli" → "sisli") aranır.
- **Paylaşılan e-posta/santral kimlik değildir.** `NormalizedValue` benzersiz değildir; aynı e-posta iki kişide ya da iki firmada olabilir. Yalnızca **aynı sahipte** aynı değerin tekrarı engellenir. `isShared` işareti sonraki aşamadaki mükerrer önerisinde bu değerlerin eşleşme kanıtı sayılmaması içindir. Tek kesin eşleşme vergi numarasıdır (ADR-0006).
- **İletişim noktası pazarlama izni değildir.** Bu kayıtlarda izin/rıza alanı yoktur ve olmamalıdır; izin ayrı bir kavram ve ayrı bir kayıt olarak ele alınır. Bir adresin kayıtlı olması ona toplu e-posta gönderilebileceği anlamına gelmez (test sözleşmede böyle bir alanın olmadığını denetler).

### Firma–firma ilişkisi (`crm.CompanyRelations`)

Ana firma → bağlı firma; tür: İştirak, Grup şirketi, Diğer; başlangıç/bitiş tarihi (bitiş verilince `isActive=false`, kayıt silinmez). Korumalar: kendine bağ (422 ve veritabanı kısıtı `CK_CompanyRelations_NoSelf`), aynı çiftin ikinci aktif ilişkisi (422 ve `UX_CompanyRelations_ActivePair`), **döngü** (aktif ilişkilerde bağlı firmanın altından ana firmaya ulaşılıyorsa 422). İki ucu da çağıranın veri kapsamında olmalıdır; kapsam dışı uçlu ilişki hiç görünmez.

**Eşzamanlı döngü:** iki istek birlikte `A→B` ve `B→A` kurarsa her biri diğerini göremez. Her aktif ilişki yazımı, hiyerarşideki ilgili firmaların (ana firmanın üstleri, bağlı firmanın altları ve iki uç) sürümünü artırır; aynı firmalara dokunan ikinci yazım iyimser eşzamanlılıkla 409 alır. Bedeli: bir grubun ilişkisi değişince o gruptaki firma formları yeniden yüklenmelidir (409). Kabul ettik; ayrı bir kilit tablosundan daha basittir ve ek şema gerektirmez.

### Eski API uyumu (iki doğruluk kaynağı yok)

`phone`, `email`, `city`, `address` (firma) ve `phone`, `email` (kişi) alanları API'de durur ama artık **ana kaydın aynasıdır**:

- **Yazma (eski → yeni):** değer değiştiyse firma/kişi düzeyindeki aktif ana noktayı/adresi günceller, yoksa açar (firma telefonu "Santral", paylaşılan olarak); boşaltılırsa ana kayıt pasifleşir. Değişmeyen değeri yeniden göndermek (ekranın tüm kaydı göndermesi) hiçbir kaydı değiştirmez ve çoğaltmaz. Serbest metin telefon eski istemcileri bozmasın diye reddedilmez, gevşek normalleştirilir.
- **Okuma (yeni → eski):** ana nokta/adres kaydedilir, pasifleşir veya silinirse eski alan buna eşitlenir (ana yoksa boş). Ana olmayan nokta eski alanı etkilemez. Lokasyon ve ilişkiye özel kayıtlar eski alanı etkilemez.
- Eski alanı yazmak ana noktayı kendi kaydının denetimine yazmaz (denetim firma/kişi kaydındadır); yeni uçlardan yapılan değişiklikler kendi kaydında denetlenir. Eski alanın kısıtı (`Party.Phone` 30 karakter) ana telefon değerine de uygulanır.

### Migration ve geri alma

`CustomerDirectory` migration'ı dört tabloyu ve RLS politikasını ekler; mevcut dolu `Party.Phone/Email/City/Address` ve `Person.Phone/Email` alanlarını ana kayıtlara kopyalar (orijinal metin `Value`/`Line1`'de, eski alanlar olduğu gibi kalır). Backfill sonunda kayıp kontrolü vardır: dolu bir eski alan için ana kayıt oluşmadıysa `THROW` ile migration geri alınır. Adresin alt alanları (ilçe, posta kodu) eski serbest metinden ayrıştırılmaz; `Line1` eski metni taşır, kullanıcı sonradan böler. Backfill'in SQL'i tohum verili bir tenant veritabanında gerçek migration ile denendi.

## Sonuçlar

- Lokasyon, adres, iletişim ve firma ilişkisi CRM'in kapsamındadır; başka modül CRM projesine bağlanmaz.
- Silme: lokasyonun adresleri/noktaları onunla gider; lokasyonu veya firma ilişkisi olan firma silinmez (409), diğer firmaların adres/noktaları firmayla gider.
- Tüm kayıtlar kayıt boru hattından geçer (izin, kapsam, doğrulama, sürüm, audit, outbox).

## Bilinen sınırlar / kalan iş

- Eski alan üzerinden yapılan ana nokta/adres değişikliği ana kaydın kendi denetim satırını yazmaz.
- Kişi adresi yoktur (adres firma ve lokasyon içindir).
- Firma ve kişi listelerinin araması ana kayıtların yanında tüm iletişim noktalarında ve adreslerde de çalışır (4. aşama); `CrmLookups` seçim kutuları hâlâ ilk 200 kaydı yükler, o yüzden ilişki/lokasyon seçim kutularında arama destekli seçici gerekir.
- Birleştirme (5. aşama) bu kayıtları hedef firmaya taşıyacaktır; `isShared` ve normalleştirilmiş değerler mükerrer önerisinde kullanılacaktır.

## Arayüz (4. aşama)

- **Firma detayı:** özet, genel bilgiler, "Adres ve iletişim" (firma düzeyi adresler ve telefon/e-postalar), "Lokasyonlar" (her lokasyonun kendi adres ve iletişimi açılır), "İlişkili firmalar" (ana/bağlı, sonlandırma), "Kişiler ve ilişki rolleri" (yeni kişiyi doğrudan oluşturup bağlama dahil), aktiviteler, fırsatlar ve değişiklik geçmişi.
- **Kişi detayı** (`/crm/kisiler/{id}`): temel bilgi, aktif ve geçmiş firma ilişkileri (rol, unvan, departman, tarihler), kişinin genel iletişimi ve her aktif ilişkiye özel iletişimi, erişilebilen aktiviteler ve fırsatlar (sunucu tarafında kapsamlı, sayfalı) ve değişiklik geçmişi.
- **Ortak alan uyarısı:** firma ve kişi düzenleme ekranları, ad/vergi/telefon/e-posta gibi ortak alanların bu kaydı kullanan diğer modüllere de yansıdığını ve eski alanların ana kaydı güncellediğini belirtir.
- **Sunucu tarafı arama/süzgeç:** firma `search` (ad, vergi no, şehir, etiket + iletişim noktaları + adresler, normalleştirilmiş), `city` (eski alan ve aktif adresler), `kind`; kişi `search` (ad + tüm iletişim noktaları), `role` (aktif ilişkideki ilişki rolü), `withoutCompany`, `ownerId=me`.
- **Kişi → firma oluşturma:** kişi API'si firmayla birlikte ilişkiyi tek istekte açar; departman/rol/ana irtibat ayrıntıları ikinci bir istekle yazılır. İkinci istek başarısız olursa kişi ve ilişki kayıtlı kalır, ekran uyarır (atomik değildir; bilinçli sınır).
