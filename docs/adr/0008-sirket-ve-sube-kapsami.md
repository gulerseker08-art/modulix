# ADR-0008: Tenant içi şirket ve şube kapsamı

- **Durum:** Taslak, 25 Ekim 2026
- **Karar:** Tenant, bir veya daha fazla işletici şirket (`core.company`) barındırır. Her tenant tablosu bilinçli olarak "ortak" ya da "şirket kapsamlı" sınıflandırılır; şirket kapsamlı kayıt `CompanyId` taşır ve şirket sınırı, tenant sınırı gibi sunucuda, oturumdan belirlenir
- **Yol haritası:** Ç22 (minimum D1, tam D2); envanter bulgusu B4

## Problem

Bugün tenant tek bir işletici şirket gibi davranır (envanter B4). Gerçek müşterilerde bir tenant altında birden fazla tüzel kişi vardır (ör. A Lojistik A.Ş. ve A Depolama Ltd.). Bunlar aynı kullanıcıları, müşterileri ve ürün kataloğunu paylaşır, ama faturaları, cari hesapları ve kasaları ayrı tutulmalıdır.

Sorunlar:

- Tenant ile işletici şirket aynı kavram sayılıyor; "bizim şirketimiz" ile "müşteri firma" (`core.party`, ADR-0006) karışabiliyor.
- Muhasebe, İK ve Lojistik gelmeden şirket alanı eklenmezse, sonradan her tabloya `CompanyId` eklemek veri taşıma ve RLS değişikliği gerektirir.
- Ekrandaki "aktif şirket" seçimi yetki kazandırırsa tenant sınırının ikinci bir zayıf kopyası oluşur.

Yol haritasındaki K3 kararı gereği modüller arası yeni sözleşme ADR ister.

## Seçenekler

| Seçenek | Artı | Eksi |
| --- | --- | --- |
| Tenant içinde şirket + tablo başına kapsam sınıfı (önerilen) | Ortak ana veri paylaşılır; finans ayrışır; tenant RLS'i değişmez | Her yeni tabloda bilinçli seçim; kapsam filtresi uygulamada |
| Her şirket ayrı tenant | Mevcut izolasyon yeter; basit | Kullanıcı, müşteri ve katalog kopyalanır; şirketler arası rapor imkânsız; kullanıcı çoklu tenant geçişine mahkûm |
| Şirket yalnızca bir etiket alanı | Çok az iş | Güvenlik sınırı değildir; muhasebeci B'nin faturaları sızar |
| Şirket kapsamını da SQL Server RLS ile uygula | Atlanması zor | Tenant RLS'ine ikinci boyut, tüm tablolarda ve migrationlarda ek karmaşıklık; ilk sürümde gereksiz |

## Karar

### Kavramlar

| Kavram | Anlamı | Kayıt |
| --- | --- | --- |
| **Tenant** | Müşterimizin Modülix hesabı; veri ve faturalama sınırı | `platform.Tenants` |
| **İşletici şirket** | Tenant'ın kendi tüzel kişisi; belge keser, hesap tutar | `core.company` |
| **Taraf** | Dışarıdaki firma veya kişi (müşteri, tedarikçi, taşıyıcı) | `core.party` (ADR-0006) |
| **Şube / lokasyon** | Şirkete bağlı fiziksel veya idari birim | `core.branch` (şirkete bağlı) |
| **Departman / ekip** | İdari gruplama, kapsam sınırı değil | mevcut ekipler |

- Müşteri Taraf kaydı, işletici şirketle aynı varlık değildir. Tenant'ın kendi şirketi de Taraf olarak (ör. şirketler arası işlemde) görünebilir; bu, `core.company` üzerinde isteğe bağlı `PartyId` ile bağlanır, ama bağ ileriki pakettir (Ç22.4).
- Her tenant en az bir şirkete sahiptir. Mevcut tenantlar için migration, tenant adıyla bir **varsayılan şirket** oluşturur; tek şirketli tenant için arayüz değişmez, şirket seçimi gösterilmez.

### Kayıt kapsamı sınıfları

Her tenant tablosu şu üçünden birine konur. Sınıf, tablo envanterine (`TableInventoryTests`) ve `ITenantEntity` yanına bir işaret arayüzüyle eklenir.

| Sınıf | Anlam | Örnekler |
| --- | --- | --- |
| **Ortak** (`ICompanyShared`) | Tenant'ın bütün şirketleri görür | Taraf, kişi, ürün, birim, roller, özel alanlar |
| **Şirket kapsamlı** (`ICompanyScoped`) | `CompanyId` zorunlu; yalnızca erişimi olan şirketlerde görünür | Fatura, cari hareket, kasa, bordro, sevkiyat belgesi |
| **Şirket seçmeli** | `CompanyId` boş olabilir; boşsa ortak | Teklif, görev, dosya (kuralı modül belirler) |

- Sınıfsız yeni tenant tablosu mimari testi kırar (envanter kuralı genişler).
- Kapsam sınıfı sonradan değiştirilebilir ama veri taşıma gerektirir; bu yüzden ortak/şirket kapsamlı ayrımı tablo eklenirken yapılır.

### Kullanıcı bağı ve aktif şirket

- Üyelik, bir veya daha fazla şirket ve şube görevi taşır (`MembershipCompanies`). Rol izni "ne yapabilir"i, şirket bağı "hangi şirkette" sorusunu belirler; ikisi birlikte gerekir.
- **Erişilebilir şirketler** üyelikten hesaplanır. **Aktif şirket**, oturumun varsayılan çalışma kapsamıdır ve yalnızca erişilebilir şirketlerden biri olabilir.
- Aktif şirketi değiştirmek yetki kazandırmaz: sunucu her istekte seçilen şirketin erişilebilir listesinde olduğunu doğrular (tenant gibi, başlık veya parametreye güvenilmez; oturumdan belirlenir).
- Tenant yöneticisi bütün şirketlere erişir. "Tüm şirketler" görünümü yalnızca birden fazla şirkete erişimi olanlar içindir ve okuma amaçlıdır; yazma işlemi tek şirket seçilerek yapılır.
- Geçici vekâlet: başlangıç/bitiş tarihli şirket bağı. Süresi dolunca erişim kendiliğinden kapanır, denetim kaydı kalır.

### Uygulama noktaları

- **Kayıt boru hattı:** okuma sorgusu şirket kapsamlı tablolarda `CompanyId IN (erişilebilir şirketler)` süzgecini ekler; yazma, kaydın `CompanyId` değerinin erişilebilir olduğunu doğrular. Bu bir kapsam adımıdır (Y8a kayıt kapsamının yanında) ve atlanamaz.
- **Veritabanı:** ilk sürümde şirket süzgeci yalnızca uygulamadadır; tenant RLS'i değişmez. Şirket boyutunu RLS'e taşıma, ihtiyaç doğarsa sonraki karardır.
- **Uç noktalar:** `/api/records` ve modül uçları şirketi oturumdan alır. `CompanyId` gövdeden yalnızca oluştururken, erişilebilir bir değer olarak kabul edilir; sonra değişmez (taşıma ayrı komuttur).
- **Numaralama, para birimi, takvim, belge şablonu:** şirket başına ayarlanır. Bunlar Ç22'nin değil ilgili modüllerin işidir, ama ayar yeri şirkettir.

### Şirketler arası işlem

Karşılıklı belge, borç/alacak ve mutabakat ileriki pakettir (Ç22.4). Farklı şirketlerin hesap hareketi tek defterde karışmaz; otomatik konsolidasyon kapsam dışıdır.

### Denetim ve olaylar

- Şirket oluşturma, kapatma, kullanıcıya şirket verme/alma ve aktif şirket reddi denetim kaydına yazılır.
- Şirket kapsamlı kayıtların olay zarfı `CompanyId` taşır (ADR-0007). Aboneler yalnızca kendi kapsamına uyan olayı işler.

## Geçiş

1. `core.company` ve `core.branch` tabloları, varsayılan şirket migrationı, kapsam işaret arayüzleri, envanter testinin genişletilmesi (her tablo bir sınıfta).
2. Üyelik–şirket bağı, oturumda erişilebilir ve aktif şirket, her istekte doğrulama; şirket yönetim ekranı (yalnızca birden fazla şirket varsa görünür).
3. Kayıt boru hattında kapsam adımı ve olay zarfına `CompanyId`.
4. Mevcut CRM tabloları "ortak" olarak işaretlenir; CRM davranışı değişmez.
5. Sonraki: şube/lokasyon kullanımı, vekâlet, şirket başına ayarlar. Muhasebe ve İK geldiğinde tabloları "şirket kapsamlı" olarak açılır.

## Sonuçlar

- Tek şirketli tenantlar için görünür bir değişiklik olmaz; çok şirketli tenantlar ayrı tenant açmadan finans ayrımına kavuşur.
- Her yeni tenant tablosunda ek bir bilinçli karar gerekir; bunu mimari test zorlar.
- Şirket süzgeci veritabanında değil uygulamadadır: boru hattını atlayan kod şirket sınırını da atlar. Bu yüzden "doğrudan `DbContext`'e yazma yasağı" ve uç nokta koruma testleri şirket kapsamı için de bağlayıcıdır.
- Mevcut `ownerId` ve kayıt kapsamı (Y8a) değişmez; şirket kapsamı onların üstüne eklenir.

## Açık sorular (kabulden önce karar gerekir)

1. Aktif şirket oturumda mı (sunucu tarafı) yoksa her istekte özel bir başlıkla mı taşınsın? Öneri: oturumda tutulur, değişiklik ayrı bir uçla yapılır.
2. Şirket seçmeli sınıf ilk sürümde gerekli mi, yoksa yalnızca ortak ve şirket kapsamlı ile mi başlansın?
3. Kullanıcıya hiç şirket bağı verilmemişse varsayılan: hiçbir şirket (güvenli) mi, yoksa yalnızca ortak veri mi? Öneri: yalnızca ortak veri.
4. Şirket kapatılınca veri politikası: yalnızca okunur arşiv mi, silme mi? (B3 silme politikasıyla birlikte ele alınabilir.)
