# ADR-0011: Firma–kişi ilişkisi

- **Durum:** Kabul edildi ve uygulandı, 27 Ekim 2026
- **Karar:** Kişinin firmayla bağı `crm.CompanyContacts` ilişki kaydıdır ve tek doğruluk kaynağıdır; eski `Contact.CompanyId` bu kayıtlardan türetilen "birincil firma"dır

## Problem

`Contact.CompanyId` tek firma ilişkisiydi. Aynı kişi iki firmada çalışıyorsa ya kişi çoğaltılıyor ya da bir firma kaybediliyordu. Firmaya özgü unvan, departman, satın alma/finans gibi ilişki rolü, ana irtibat ve ilişkinin başlangıç/bitişi tutulamıyordu; kişi işten ayrılınca ya kişi silinip geçmiş kayboluyor ya da yanlış firmaya bağlı kalıyordu.

## Karar

### Model (CRM alanı)

İlişki firma ve kişiyi birbirine bağlayan CRM'e özgü bir uzantıdır (satış sürecindeki iş rolü); çekirdeğe konmadı çünkü diğer modüller için anlamı yoktur. Alanlar: firma, kişi, departman, firmadaki unvan, ilişki rolleri (çoklu), ana irtibat, karar verici, başlangıç, bitiş, aktiflik. Kayıt türü `crm.company-contact`; izinler kişi izinleridir (`crm.contacts.read/write`). Şirket kapsamı: `ICompanyShared`.

- **İlişki rolü güvenlik rolü değildir:** Satın alma, Finans, Teknik, Yönetim, Diğer sabit anahtarlardır (`CrmModule.RelationshipRoles`); bilinmeyen anahtar 422'dir.
- **Unvan ve iletişim:** ilişkideki `jobTitle` yalnızca o firma içindir; `Person.Title` kişinin genel unvanı olarak kalır ve ilişki düzenlenirken değişmez. (Firmaya özgü iletişim bilgisi 3. aşamada gelir.)
- **Biten ilişki silinmez:** `endDate` doldurulur, `isActive=false` olur (aktiflik bitiş tarihinden türetilir), ana irtibat işareti kalkar. Kişi, aktiviteler ve fırsatlar korunur.
- Firma ve kişi yalnızca oluştururken verilir. **İkisine de erişim gerekir:** firma için okuma izni ve veri kapsamı, kişi için ADR-0010 görünürlüğü; erişilemeyen uç "bulunamadı" (422) döner.

### Ana irtibat tekilliği

Kapsam: **firma başına en fazla bir aktif ana irtibat**; aynı kişi farklı firmalarda ana irtibat olabilir. Denetim iki katmandadır: depo açık hata verir (422 `isPrimary`, değiştirmek için önce eskisinin işareti kaldırılır — otomatik devir yapılmaz, böylece her değişiklik kendi kaydının denetimine düşer) ve veritabanı filtreli benzersiz indeksle (`UX_CompanyContacts_PrimaryPerCompany`, `IsPrimary=1 AND IsActive=1`) eşzamanlı isteklerde ikincisini 409 ile reddeder. Aynı firma–kişi çiftinin tek aktif ilişkisi vardır (`UX_CompanyContacts_ActivePair`); geçmiş ilişkiler çoklanabilir.

### Eski alanın anlamı (iki doğruluk kaynağı yok)

`Contact.CompanyId` artık yalnızca türetilmiş birincil firmadır: **en eski başlayan aktif ilişkinin firması**. Yazan tek yer depolardır:

- İlişki oluşturulur/güncellenir/biter → kişinin `CompanyId`'si ve ortak kişinin `PartyId`'si yeniden hesaplanır (kişinin sürümü artar).
- Eski API ile `companyId` yazmak: değer değişmediyse hiçbir şey yapılmaz (ekran kaydı yeniden gönderse de çok firmalı kişinin ilişkileri bozulmaz); değiştiyse önceki birincil firmayla ilişki biter, yeni firmayla aktif ilişki yoksa açılır (unvan `Person.Title`'dan), sonra birincil firma yeniden türetilir. Çok firmalı kişide yanıttaki `companyId`, gönderilenden farklı olabilir (en eski aktif ilişki).
- Kişi silinirse ilişkileri de silinir (bilinçli işlem); ilişkiyi sonlandırmak geçmişi korur.

### Görünürlük (ADR-0010'un devamı)

- İlişki, **firmasının** veri kapsamına göre görünür; kişinin diğer firmalarla ilişkileri bu firmayı göremeyene gösterilmez.
- Kişi: aktif ilişkisi varsa bağlı firmalardan biri kapsamdaysa görünür; aktif ilişkisi yoksa (firmasız ya da ilişkisi bitmiş) kendi sorumlusuna göre. Genel `core.person` uçlarının kısıtı da bu kuralı kullanır.

### Veri taşıma

Migration her `Contact.CompanyId` için aktif bir ilişki oluşturur: ilişki kimliği = kişinin kimliği (eski kimlikler korunur), başlangıç = kişinin oluşturulma günü, unvan = ortak kişideki unvan, ana irtibat işaretsiz (tahmin edilmez). `Contact.CompanyId` ve `Person.PartyId` olduğu gibi kalır. Migration, firması olup ilişkisi oluşmayan kişi bulursa hata verip geri alınır (veri kaybı kontrolü). RLS politikası backfill deyimleri boyunca kapatılıp açılır.

## Kapsam dışı / kalan

Lokasyona bağlı ilişki, firmaya özgü iletişim bilgisi, kişi detay ekranı, mükerrer kişi birleştirmede ilişkilerin taşınması sonraki aşamalardadır. `ContactDto.companyId` birincil firmanın kimliğini, çağıranın göremediği bir firma olsa bile, verir (adı vermez).
