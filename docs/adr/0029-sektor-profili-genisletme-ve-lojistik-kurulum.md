# ADR-0029 — Sektör/faaliyet modeli ve modül profili

Durum: **taslak** (3 Kasım 2026, tartışma için; kabul edilmedi). Bu belge tek başına hiçbir şeyi uygulamaz. Kod kanıtı ve ayrıntılı tablolar `docs/sektor-faaliyet-analizi.md`'dedir; "doğrulanmadı" olanlar oradaki bölüm 10'da ve burada bölüm 8'dedir.

Bu belge önceki taslağın yerini alır: önceki taslak tek profil anahtarı üstüne rol/form ekleyen bir genişletmeydi; kullanıcının tarifi (sektör → faaliyet grubu → alt faaliyet → özellik, çoklu seçim, sektör altyapısının CRM'e özel olmaması) bunu aştığı için model yeniden kuruldu.

İlgili: ADR-0002/0020 (tek veritabanı), ADR-0021 (form düzeni), ADR-0027 (kural altyapısı), ADR-0028 (kayıt türüne göre kapsam) ve ADR-0028 (platform lisans; numara çakışması var, iki belge de 0028'i taşıyor). Faaliyet seçiminin modül açması lisans çözümleyicisinden geçmelidir; lisans ADR'si kabul edilene kadar seçim hiçbir modülü kendiliğinden açmaz.

Uygulama notu (Dilim A tabloları): katalog tabloları `platform` şemasında ve RLS dışıdır, tenant seçimi `core.ActivitySelections` RLS altındadır. Düğüm taşıma bu aşamada yoktur: kullanım sayısı tenant sınırı yüzünden platformdan okunamadığı için (K3) taşıma etkisi sonraki dilimde, platform tarafı sayaçla çözülecektir.

Uygulama notu (Dilim C, saf Core): `Modulix.Core/FieldRules` alan kuralı modelini (`FieldRule`, dokuz etki), doğrulayıcıyı, değerlendiriciyi ve katman birleşimini (`RuleLayerMerger`) içerir; koşul dili ADR-0027'nin `ConditionNode`/`ConditionEvaluator`'ıdır, ADR-0027 değişmedi. Form çizimi ve yönetim ekranı henüz yoktur. Kalıcılık (Dilim C devamı): `platform.RulePackages`/`RulePackageVersions` (RLS dışı; paket başına en çok bir taslak ve bir yayın, filtreli benzersiz dizinle) ve `core.RuleAdaptations` (RLS altında, tenant başına tek kayıt). Etkin kural = ürün standardı + tenant'ın seçtiği etkin faaliyetlerin ve üst düğümlerinin sektör paketleri + uyarlama; pasif faaliyetin paketi uygulanmaz. Yayın etki önizlemesi tenant sayısı vermez (kullanım sayısı tenant sınırı yüzünden platformdan okunamaz); yalnızca kural farkı ve çakışmaları gösterir. Kural alanı olarak bugün yalnızca Lojistik'in beyan ettiği alanlar kullanılabilir (CRM kural alanı beyan etmiyor). Kayıt boru hattı (Dilim C bağlama): `FieldRuleGate` oluşturma ve güncellemede yazılacak son hale karşı kuralları sunucuda uygular (zorunlu, Doğrula, Engelle, onay gerekir = reddet, salt okunur, uyarı engellemez); okuma ve silmede çağrılmaz. Kural koşulu, değerlendiren kimliğin okuyamadığı alanı görmez (alan yetkisi). Bugün kural alanı beyan eden kayıt deposu olmadığından (Lojistik deposuz, CRM beyansız) uç uca etki yoktur. Uygulanmayanlar: lookup filtresi etkisi ve kapsam (şirket/şube/tesis) katmanının verisi. Kilitli kural alanı (`Locked`) K8'deki "tenant uyarlaması güvenlik kurallarını kaldıramaz" kararının karşılığıdır.

## 1. Bağlam ve girdiler

Tek ortak CRM vardır; sektör kopyaları yoktur. Tenant'ın sektör/faaliyet seçimleri alanları, formları, seçenekleri, şablonları ve kuralları şekillendirir. Altyapı CRM'e özel olmaz (lojistik, depo ve sonraki modüller de kullanır). Veritabanı topolojisi değişmez (tek paylaşımlı DB, RLS + EF filtre; ADR-0002, ADR-0020). Pilot (Makaroğlu) için sabit kodlu davranış yoktur.

Karar verilmiş girdiler: lojistik profilinde CRM isteğe bağlıdır; sürücü saha ekranı ve offline ayrı epic'tir (Ç16), bu işin kapsamında değildir.

## 2. Mevcut durum özeti

Tenant'ta tek `ProfileKey` vardır; profil yalnızca modül, özel alan ve liste değeri ekler. Liste kategorisi düz ve kodda sabittir, ağaç için yetmez. Kural altyapısı (ADR-0027) otomasyondur, yürütmesi yoktur ve göster/gizle/zorunlu aksiyonu yoktur. Form düzeni statik görünürlük ve zorunluluk taşır, koşul taşımaz. Firma sektörü (`Company.Industry`) serbest metindir ve müşterinin sektörüdür. Ayrıntı: analiz belgesi bölüm 1.

## 3. Kararlar

**K1 — Sınıflandırma ağacı.** Sektör, faaliyet grubu ve alt faaliyet tek ağaçta (`ActivityNode`, `Kind` ile) tutulur; seviye sayısı kodda sınırlı değildir. Kod sabittir, ad değişebilir, referans `Id`/`Code` iledir. Döngü ve kendine bağlanma engellenir. Kullanımdaki düğüm silinmez, pasife alınır. Taşıma etki raporu üretir; seçimli düğüm taşınmaz.

**K2 — Ayrı kavramlar.** Hizmet özelliği, yetenek, modül ve tenant seçimi ağaçtan ayrı tutulur. Özellikler düğümlere çoktan çoğa bağlanır (tekrar tanım yok); karşılıklı dışlama `ExclusionGroup` ile. Yetenek bağımlılık grafiği sınıflandırma ağacından ayrıdır.

**K3 — Katalog global, seçim tenant'ta.** Katalog `platform` şemasında, `TenantId` olmadan ve platform yöneticisi tarafından yönetilir. Tenant seçimi `core` şemasında `ITenantEntity` + `ICompanyShared` ve RLS altındadır (mimari testler tenant varlığının platform şemasında durmasını yasaklar). Platform yöneticisi seçim atayacaksa `SectorProfileService` kalıbıyla tenant bağlamında yazar; iş kaydı okuma yetkisi doğurmaz.

**K4 — Seçim semantiği.** Üst başlığı seçmek alt faaliyetleri etkinleştirmez. Açıkça seçilenler saklanır; gerekli üst bağlam türetilir. Eksik bağımlılıklar önizlemede gösterilir. Boş seçim = mevcut davranış (eski tenant'lar değişmez).

**K5 — Katalog ≠ hazır modül.** Düğüm `ModuleReadiness` taşır; faaliyetin katalogda bulunması operasyon modülünün hazır olduğu anlamına gelmez.

**K6 — Tesis özellikleri.** Antrepo sınıfı gibi bilgiler ülkeye bağlı tesis özelliğidir; firma türüne veya tenant geneli tek alana dönüştürülmez. Mevzuat kuralları doğrulanmadan zorunlu kural yapılmaz.

**K7 — Alan kuralı katmanı.** Göster/gizle/salt okunur/koşullu zorunlu/doğrula/varsayılan öner/lookup filtrele aksiyonları için, ADR-0027'nin koşul modelini (`ConditionNode`, evaluator) yeniden kullanan **ayrı bir alan kuralı** katmanı eklenir. Otomasyon yürütmesine bağlanmaz; ikinci bir koşul dili tanımlanmaz. UI, API ve import aynı sunucu değerlendiricisini çağırır; gizli alan değeri silinmez; sunucu yetkisi görünürlükten bağımsızdır. Serbest SQL/kod yoktur. Bu karar ADR-0027'ye dokunur ve orada teyit edilmelidir.

**K8 — Sürümlü paket ve birleşim.** Sektör paketi sürümlüdür (taslak → doğrulama → etki önizlemesi → yayın, atomik). Katman sırası: ürün standardı → sektör paketi → tenant uyarlaması → (ileride) şirket/şube/tesis → kullanıcı tercihi. Çakışma raporlanır ve yayını engeller; tenant uyarlaması güvenlik kurallarını kaldıramaz. Paket güncellemesi tenant uyarlamasını ezmez. Geri alma ayar geri alımıdır, veri migrationı ayrıdır.

**K9 — Firma sektörü ve ilişki rolü.** `Company.Industry` müşteri firmanın sektörü olarak kalır; tenant sektörüyle ilişkisi yoktur. Party'ye ilişki rolü kataloğu (müşteri, tedarikçi, taşıyıcı…) eklenir; ilişki durumu ve ticari koşul ayrıdır. Müşteri sektörünün ağaçtan seçilmesi isteğe bağlıdır, geriye uyumlu ayrı dilimdir.

**K10 — Sahiplik.** Firma/kişi kimliği, adres, temel tesis kimliği, sektör/faaliyet kataloğu ve yapılandırma Core'dadır. Lead, fırsat, aşama, aktivite, kazanma/kaybetme ve satış raporu CRM'dedir. Teklif/sözleşme/sipariş Core'a taşınmaz; mevcut `IAcceptedQuoteSource` sınırı korunur. Adres ve ürün/hizmet tanımının Core'a taşınması ayrı karardır (analiz bölüm 2).

## 4. Dilimler

A Core tanımları ve servisler → B yönetim ekranları → C paket birleşimi ve alan kuralı → D lojistik başlangıç paketi → E firma ilişki rolü ve hizmet profili → F lead/fırsat/teklif entegrasyonu → G yayın, etki analizi, migration ve testler. Ayrıntı ve bağımlılıklar analiz belgesi bölüm 11.

## 5. Riskler

- Alan kuralı yeni bir değerlendirme yoludur: maliyet sınırı (koşul derinliği, ilişki sorgusu yok) ve N+1 testi gerekir.
- Çoklu seçim ve paket sürümü önbelleği: etkin profil sürüm numarasıyla anahtarlanmalı, yayında artmalı.
- Katalog platformda global olduğu için tenant'a özel faaliyet gerekirse tenant uyarlaması katmanı gerekir.
- Mevcut profil (`ProfileKey`) ile yeni seçim bir süre birlikte yaşar; çakışmayı tanımlayan geçiş kuralı yazılmalıdır.

## 6. Reddedilen seçenekler

- Faaliyet ağacını seçim listesi (ChoiceList) olarak tutmak: kategori düz ve kodda sabit.
- Tüm sektör/faaliyet/özellik/yetenek/izni tek listede birleştirmek.
- Her sektör için ayrı CRM veya ayrı yönetim paneli.
- Kuralı otomasyon aksiyonu olarak yazmak: yürütme yok, kritik doğrulama otomasyona bırakılmıyor.
- Katalogu tenant şemasında tutmak: her tenant'a kopya ve sürüm sapması.

## 7. Açık sorular

1. Ağaç düğümlerini tenant da ekleyebilsin mi (tenant uyarlaması), yoksa yalnızca platform mu?
2. Adres ve ürün/hizmet tanımı Core'a ne zaman taşınsın?
3. Alan kuralı ADR-0027'nin içine mi yazılsın, ayrı ADR mi?
4. Başlangıç lojistik sınıflandırması pilotla doğrulanacak mı?

## 8. Doğrulanmadı

- Satır numaralarının bir kısmı, Blazor tarafı ve `CrmForms.cs` içeriği okunmadı.
- Merkezi önbellek ve platform yöneticisi sınır testi bulunamadı (kapsamlı aranmadı).
- Pilot işleyişi ve mevzuat zorunlulukları doğrulanmadı.
- Hiçbir test çalıştırılmadı.
