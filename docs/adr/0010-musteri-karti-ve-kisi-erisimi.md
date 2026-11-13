# ADR-0010: Müşteri kartı, özetler ve kişi erişim politikası

- **Durum:** Kabul edildi (1. aşama uygulandı), 27 Ekim 2026
- **Karar:** Firma özetleri sunucuda erişilebilir tüm kayıtlar üzerinden hesaplanır; CRM kişisinin görünürlüğü bağlı olduğu firmanın (firmasızsa kendi sorumlusunun) kapsamına göre belirlenir; genel ortak kayıt uçları (`core.party`, `core.person`) bu kısıtı dolanamaz

## Problem

ADR-0006 ve ADR-0008 sonrası "Firma ve kişi yönetimi" sürecinde şu doğruluk ve erişim açıkları vardı:

- Firma detayındaki özetler (açık/kazanılan fırsat sayısı, tutar toplamı, açık/geciken aktivite) istemcide ilk 200 kayıttan hesaplanıyordu; 200'ü aşınca sessizce yanlış sonuç veriyordu. Tutarlar para birimine bakılmadan toplanabiliyordu ve erişimi olmayan veri "0" gibi görünüyordu.
- `crm.contact` sorumlu alanı taşımıyordu: `crm.contacts.read` izni olan herkes kapsamı (Own/Team) ne olursa olsun tenant'taki bütün kişileri görüyordu.
- `core.person` ve `core.party` genel uçları, CRM kapsamı dışındaki kayıtların telefon, e-posta, adres gibi alanlarını tam olarak veriyordu (ADR-0006: ortak kayıt tenant genelinde bulunabilir; ama bulunabilirlik tam profil demek değildi).
- `Party.Kind` ve `Party.TaxOffice` yalnızca genel uçtan düzenlenebiliyordu.

## Karar

### Özetler sunucuda

`GET /api/crm/companies/{id}/summary` özet sayılarını **kayıt boru hattı üzerinden** hesaplar (`IRecordPipeline.QueryAsync`; izin, veri kapsamı ve alan güvenliği çağıranınkidir). Sayılar `TotalCount`'tan, tutar toplamları sayfa sayfa gezilerek para birimi bazında hesaplanır. Kategori süzgeci (`stageCategory=open|won|lost`) fırsat deposuna eklendi; kapalı aşama kümesi firmanın seçim listesinden gelir (Y9b).

- Farklı para birimleri **hiçbir zaman** toplanıp tek tutar yapılmaz; `CurrencyTotal` listesi döner.
- Okuma izni olmayan bölüm `null` döner (sıfır değil). Tutar alanı hassas (Y8b) ve çağıranın izni yoksa toplamlar `null`'dır; sayılar yine verilir.
- Firma çağıranın kapsamında değilse özet de 404'tür.
- Firma detayındaki bağlı listeler (kişi, fırsat, aktivite) sunucuda sayfalanır (20'şer); ilk-200 kısıtı kalktı.

### Kişi erişim politikası

| Durum | Kişi kime görünür |
| --- | --- |
| Firmaya bağlı | Bağlı olduğu firmanın erişim kapsamındakilere (firmanın `ownerId`'si kapsamda) |
| Firmasız | Kişinin açık sorumlusu (`crm.contact.ownerId`) kapsamdakilere |
| Tüm kayıtlar kapsamı | Herkes |

- `Contact` artık `IOwnedEntity`'dir (`OwnerId`). Boş bırakılırsa kaydı oluşturan atanır. Eski istemci sorumluyu göndermezse mevcut sorumlu korunur.
- Kural hem listede (sorguda, sayfalama doğru kalır) hem tek kayıtta uygulanır. Bunun için `IRecordStore`'a iki kanca eklendi: `UsesOwnerScope` ve `IsInOwnerScopeAsync`. Varsayılan davranış (sorumlu alanına bakmak) değişmedi; yalnızca kişi deposu kendi kuralını verir.
- Kişiyi bir firmaya bağlamak için çağıranın firma okuma izni ve firmanın veri kapsamı gerekir; erişilemeyen firma "bulunamadı" (422, `companyId`) döner. Firma değişmediyse denetlenmez.
- Devir (ADR-0004): firmasız kişiler devir listesine **eklenmedi**. Eklenseydi devri yapan her yöneticiden kişi yazma izni de istenecek ve yalnızca firma/fırsat/aktivite yetkisi olan yönetici kullanıcı devre dışı bırakamayacaktı. Ayrılan kullanıcının firmasız kişileri "tüm kayıtlar" kapsamında görünür kalır ve sorumlusu elle değiştirilir (kalan iş: devirde izin eksikliğini ayrı raporlayan bir "kişi" kalemi).

### Genel ortak kayıt uçları

`ISharedRecordVisibility` (Contracts) sözleşmesi: modül, çağıranın kendi uzantısını göremediği ortak kayıtları bildirir. `SharedRecordStore` bu kayıtları yalnızca **asgari kimlikle** verir (taraf: tür, ünvan, vergi no; kişi: ad, soyad), diğer alanları boşaltır; güncelleme ve silme "bulunamadı" döner. Seçim kutuları ve vergi no eşleşmesi çalışmaya devam eder (ADR-0006).

- CRM'in uygulaması: çağıranın `crm.companies.read` / `crm.contacts.read` izni varsa ve veri kapsamı "tüm kayıtlar" değilse, kapsamı dışındaki firma/kişi kısıtlıdır. CRM okuma izni olmayan çağıranın erişimi başka modülden geldiği için CRM kısıt bildirmez. Birden fazla modül varsa en kısıtlısı geçerlidir.
- Yönetici ve "tüm kayıtlar" kapsamı kısıtlanmaz.
- **Bilinen sınır:** `search` ve `email` süzgeçleri kısıtlı kayıtta da çalışır (sonuç yine asgari kimlikle döner). Bir e-posta/telefon değerini önceden bilen kişi kaydın varlığını sınayabilir; kapatılması arama alanlarının kapsamla birlikte sorgulanmasını gerektirir (kalan iş).

### Modül erişim bilgisi

`IRecordAccess` (Contracts): modüller çağıranın veri kapsamını ve kayıt türü iznini boru hattıyla aynı kaynaktan öğrenir. İzin servisi kayıt deposu listesine bağlı olduğundan tembel çözülür (döngüsel bağımlılık).

### Kind ve TaxOffice

CRM API'si `kind` (Organization/Individual) ve `taxOffice` alanlarını ortak taraf üzerinden okur ve yazar; CRM tablosunda yinelenmez. Vergi numarası biçimi (tüzel 10, gerçek kişi 11 hane) yalnızca numara ya da tür değiştiğinde denetlenir; eskiden girilmiş serbest biçimli numara başka alan düzenlenirken hata vermez. Eski istemciler bu alanları göndermez: boş gönderilen `kind` ve `taxOffice` mevcut değeri korur (CRM'den vergi dairesi temizlenemez; genel uçtan temizlenir).

## Çekirdek mi CRM mi?

| Kavram | Yeri | Gerekçe |
| --- | --- | --- |
| Erişim bilgisi (`IRecordAccess`), ortak kayıt görünürlüğü (`ISharedRecordVisibility`) | Core.Contracts | Modüller birbirini bilmeden aynı kuralı paylaşır |
| Kişi sorumlusu, firma özetleri | CRM | Satış sürecine özgü |
| Kind, TaxOffice | Party (çekirdek) | Tüzel kişiliğin kendisinin bilgisi; her modülde aynı |

## Planlanan sonraki aşamalar (henüz uygulanmadı)

Müşteri lokasyonları (firma–kişi ilişkisi [ADR-0011](0011-firma-kisi-iliskisi.md) ile uygulandı), çoklu iletişim/adres, kişi detay ekranı, mükerrer önerileri ve güvenli birleştirme bu ADR'nin devamı olarak ayrı ADR/ekle bölümleriyle gelecektir. Bu belge yalnızca uygulanan kararları kapsar.

## Sonuçlar

- `crm.contacts.read` tek başına tüm kişileri görmek anlamına gelmez; Own/Team kapsamlı roller yalnızca kapsamlarındaki kişileri görür. Mevcut kişilerin sorumlusu migration'da oluşturan kullanıcıdan doldurulur (audit kaydı yoksa boş kalır ve yalnızca "tüm kayıtlar" kapsamı görür).
- Ortak kayıt uçlarında iletişim alanlarını görmek, o kaydı CRM'de görebilmeye bağlandı.

## Güncel durum notu (13 Kasım 2026)

Yukarıdaki "Planlanan sonraki aşamalar" listesi yazıldığı günün durumudur. O tarihten sonra ayrı ADR'lerle uygulananlar: firma–kişi
ilişkisi ve lokasyonlar ([ADR-0011](0011-firma-kisi-iliskisi.md)), çoklu iletişim/adres, kişi ayrıntı ekranı, mükerrer önerileri ve
güvenli birleştirme (`MergeEndpoints`; firma ve kişi birleştirme ekranları). Bu ADR'nin kararları değişmedi; yalnızca "henüz
uygulanmadı" ifadesi artık geçerli değildir. Veritabanı modeli için güncel karar [ADR-0020](0020-tek-veritabanina-donus.md)'dir.
