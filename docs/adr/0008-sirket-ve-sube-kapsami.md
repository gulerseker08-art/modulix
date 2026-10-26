# ADR-0008: Tenant içi şirket ve şube kapsamı

- **Durum:** Kabul edildi, 25 Ekim 2026 (geçiş adım 1-2 uygulandı)
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

## Kabulde verilen kararlar

- **Aktif şirket:** oturumda (sunucu tarafı) tutulur; değişiklik ayrı bir uçla yapılır, başlıkla taşınmaz.
- **Sınıflar:** ilk sürümde yalnızca ortak ve şirket kapsamlı vardır. "Şirket seçmeli" sınıf, ilk tablo gerektirdiğinde (teklif, görev, dosya) bu ADR'ye eklenir.
- **Şirket bağı olmayan kullanıcı:** yalnızca ortak veriyi görür.
- **Şirket kapatma:** silme politikasıyla (envanter B3) birlikte karara bağlanır; o zamana kadar şirket silinmez, varsayılan şirket hiç silinemez.

## Uygulama durumu

- Adım 1 yapıldı: `core.Companies` ve `core.Branches` (RLS'li), `ICompanyShared` / `ICompanyScoped`, mevcut bütün tenant tablolarının "ortak" işaretlenmesi, mevcut tenantlara tenant adıyla varsayılan şirket (migration `SirketVeSube`), yeni tenant açılışında varsayılan şirket, `TableInventoryTests` içinde "her tablo tam bir kapsam sınıfında" kapısı.
- Adım 2 yapıldı: `core.MembershipCompanies` (RLS'li, `ICompanyShared`) üyelik–şirket bağı ve mevcut etkin üyeliklere varsayılan şirketi veren geri dolum (migration `UyelikSirketBagi`). Aktif şirket oturumda `modulix:company_id` claim'inde tutulur; `CompanyContextMiddleware` her istekte erişilebilir şirketlere karşı doğrular, erişilemeyen veya eksik seçim varsayılan şirkete düşer. Kabulde verilen karar: yönetici (sistem rolü) bütün şirketlere erişir; yönetici olmayan yalnızca görevlendirildiği şirketleri görür. Aktif şirket `/api/auth/active-company` ile seçilir (başlıkla taşınmaz). Şirket yönetimi `core.companies.manage` izniyle `/api/companies` ve `/api/memberships/{id}/companies` uçlarında; şirket oluşturma/yeniden adlandırma ve şirket görevi değişikliği denetim kaydına yazılır (`core.company`, `core.membership`). Arayüzde şirket seçici (yalnızca >1 şirket) ve "Şirketler" yönetim ekranı.
- Adım 2'de ertelendi: "aktif şirket reddi" denetim kaydı yazılmadı (erişilemeyen şirket seçimi yalnızca 403 döner); `AuditAction` için uygun bir eylem değeri yok, ilgili eylem geldiğinde eklenecek. Şirket kapatma, silme politikasıyla (envanter B3) birlikte karara bağlı olduğundan adım 2 kapsamı dışında (şirket oluştur/adlandır/görev ver-al var).
- Adım 3 yapıldı: kayıt boru hattında şirket kapsam adımı ve olay zarfında `CompanyId`. Depo kayıt türünü `IRecordStore.CompanyField` ile şirket kapsamlı bildirir (null = ortak). Okumada boru hattı `RecordQuery.Companies`'i erişilebilir şirketlerle doldurur (yönetici ve şirket bağlamı olmayan arka plan/sistem → süzgeç yok, tenant geneli); tek kayıtta `InScopeAsync` şirketi de denetler (kapsam dışı kayıt "bulunamadı" görünür). Yazmada oluşturma `CompanyId` verilmemişse aktif şirkete düşer ve değerin erişilebilir olmasını ister; güncelleme `CompanyId`'yi değiştiremez (taşıma ayrı). Olay zarfı (`EventEnvelope`, `OutboxMessage`, migration `OlayZarfiSirket`) şirket kapsamlı kaydın `CompanyId`'sini taşır; ortak kayıtta boş. Henüz şirket kapsamlı tablo yok; mekanizma Core birim testleriyle doğrulandı, uçtan uca entegrasyon testi ilk şirket kapsamlı modülle (M3/M5) gelecek.
- Kalan: adım 4-5 (CRM tablolarının "ortak" teyidi zaten var; şube/vekâlet/şirket başına ayarlar; abonenin olayı şirket kapsamına göre süzmesi ilk şirket kapsamlı modülle birlikte).
