# ADR-0041 — Firma alt profili (sektör profili altında kullanıcı tanımlı paketler)

Durum: **taslak** (12 Kasım 2026, tartışma için; kabul edilmedi). Bu belge tek başına hiçbir şeyi uygulamaz. Numara geçicidir: açık bir PR aynı numarayı almış olabilir, birleştirmeden önce kontrol et.

İlgili: ADR-0029 (sektör/faaliyet modeli, alan kuralı katmanları), ADR-0021 (form düzeni), ADR-0016 (lookup), ADR-0038 (Lojistik), `docs/sektor-faaliyet-analizi.md`.

## 1. Bağlam

Lojistik firmaları tek tip değildir: pilot (Makaroğlu) karayolu taşımacılığı, gümrüklü antrepo ve denizyolu taşımacılığı yapar; başka bir firma yalnızca kurye ya da soğuk zincir yapabilir. Bugün bu farkı karşılayan üç ayrı parça var ve birbirini tanımıyor:

- **Sektör profili** (`SectorProfiles.cs`, ekran "Sektör profili"): kodda tanımlı, tek seçimli, yalnızca ekleyen bir başlangıç paketi (alan ve liste değeri). Firma kendi paketini tanımlayamaz.
- **Faaliyetlerim** (`/yonetim/faaliyetler`, ADR-0029): platformun yönettiği faaliyet ağacından çoklu seçim. Ayrı sayfadır, Sektör profili ekranından anılmaz.
- **Alan kuralı** (ADR-0029 K7/K8): ürün standardı → sektör paketi → tenant uyarlaması katmanları. Tenant uyarlaması tenant başına **tek kayıttır** (`TenantRuleAdaptation`).

Ürün sahibinin kararları (12 Kasım 2026 görüşmesi):
1. İş modeli (ne yapıyoruz) ve taşıma özellikleri (nasıl taşıyoruz) ayrı eksendir ve ikisi de **tamamen dinamiktir**; kodda sabit liste olmaz.
2. Kırılım gerçek davranışı da değiştirir (alan, liste değeri, kural).
3. Profil kartının içinde bir **alt profil tablosu** olur; firma istediği kadar alt profil ve alt profil başına istediği kadar seçenek ekler.
4. Seçenek alan, liste değeri **ve alan kuralını** kapsar (ilk dilimde kural ertelenmez).
5. Tek seviye: alt profilin altında alt profil yoktur.
6. Alt profil bir faaliyet düğümüne bağlanabilir ama zorunlu değildir.
7. Eski "Lojistik firması" kartı "hazır başlangıç" olarak kalır; altına faaliyet seçimi eklenir.

## 2. Kararlar (öneri)

**K1 — Alt profil firma kaydıdır.** `core.SubProfiles` (`ITenantEntity` + `ICompanyShared`, RLS altında): kod (tenant içinde benzersiz, sabit), ad, açıklama, isteğe bağlı `ActivityNodeId` (platform kataloğuna başvuru; kayıt yoksa ya da pasifse alt profil yine çalışır), durum (taslak, etkin, pasif), iyimser kilit. Alt profil başka alt profile bağlanmaz. Silinmez, pasife alınır; tenant dışa aktarma ve silme kapsar.

**K2 — Seçenek üç türdür, alt profile bağlı satırlardır.** Alan (özel alan tanımı: kayıt türü, ad, tür, zorunlu, listede göster, seçenekler), liste değeri (liste, ad, kategori) ve kural (`FieldRule`). Alan ve liste değeri biçimi `ProfileField`/`ProfileListValue` ile aynıdır; böylece `SectorProfileRules.Plan` doğrulaması yeniden kullanılır, ikinci bir kural dili yazılmaz.

**K3 — Etkinleştirme.** Alt profil etkinleştirilince alanlar ve liste değerleri mevcut özel alan/liste altyapısına **yalnızca ekleyerek** uygulanır (aynı ad atlanır, arşivlenen alan geri getirilmez; `SectorProfileRules.Plan` kuralı). Bu sayede form, içe aktarma, rapor ve otomasyon yeni bir yol gerektirmez. Seçenek sonradan eklenirse etkin alt profil yalnızca yeni seçeneği uygular.

**K4 — Kurallar yeni bir katman olur.** Etkin alt profillerin kuralları birleşip `RuleLayerMerger`'a ayrı bir katman (`RuleLayerKind.SubProfile`) olarak girer: ürün standardı → sektör paketi → **alt profil** → tenant uyarlaması. Böylece tenant uyarlaması hâlâ en üsttür ve alt profil kuralını geçersiz kılabilir. Kural anahtarı alt profil koduyla önek alır (`sp.<kod>.<ad>`); farklı alt profillerde aynı alana çelişen koşulsuz kural gelirse mevcut çelişki denetimi çakışma raporu verir ve etkinleştirmeyi engeller. Kilitli (ürün/platform) kural alt profilce geçersiz kılınamaz ve kapatılamaz.

**K5 — Pasife alma.** Kurallar katmandan çıkar. Alanlar ve liste değerleri **kalır** (veri korunur, alan firmanın olur; kaldırmak isteyen Özel alanlar sayfasından arşivler). Bu, "yalnızca ekler, silmez" ilkesiyle uyumludur ve sessiz veri gizlemeyi önler. Alternatif (alanları alt profille birlikte arşivlemek) ürün sahibince reddedildi (12.11.2026).

**K6 — Yetki ve denetim.** Alt profil yönetimi `core.customization.manage` iznine bağlıdır. Her yazma ve etkinleştirme denetim kaydına yazılır. Kural doğrulaması mevcut `FieldRuleChecker` ile yapılır (tenant'ın özel alanları dahil alan kümesi, en çok 500 kural). Tenant, güvenlik kurallarını kaldıramaz. Alan kuralı yetkiyi değiştirmez; sunucu yetkisi görünürlükten bağımsızdır (ADR-0029 K7).

**K7 — Sınırlar (karar, 12.11.2026).** Şimdilik sınır yoktur ama **sınır kodu kalır**: `CustomFieldService` içindeki `MaxPerEntity` (30) ve `MaxOptions` (100) denetimleri silinmez; değerleri sabit olmaktan çıkıp ayara bağlanır ve varsayılan "sınırsız"dır. Sınır gerektiğinde tek ayar değiştirilir, kod yazılmaz. Aynı ayar mantığı alt profil sayısına ve alt profil başına seçenek sayısına da uygulanır (varsayılan sınırsız). Ayar platform ya da tenant düzeyinde olabilir; düzey ilk dilimde seçilir. Risk: sınırsız alan form ve liste maliyetini büyütür; ölçüm ilk dilimde yapılır, sınır gerekirse ayardan konur.

**K8 — Ekran.** "Sektör profili" sayfası üç bölümlüdür: (1) hazır başlangıç kartları (seçilince ilgili alt profilleri ve faaliyet düğümlerini **işaretli getirir**; kart kendi başına bir şey uygulamaz), (2) alt profil tablosu (`ViewTabs` değil `record-table`; ad, bağlı faaliyet, seçenek sayısı, durum, `RowMenu`) ve alt profil açılınca seçenek tablosu, (3) faaliyet seçimi (bugünkü `Faaliyetlerim` ağacı aynı sayfaya taşınır; eski rota yönlendirir). Önizleme tek yerdedir: eklenecek alan/değer, etkinleşecek kural, eksik modül/yetenek. Tasarım kuralları `docs/design-system/` uyarınca; yeni sınıf gerekirse önce belge güncellenir.

**K9 — Başlangıç alt profilleri.** Lojistik kartı pilot için "Kara taşımacılığı", "Gümrüklü antrepo" ve "Deniz taşımacılığı" alt profillerini getirir. Bugünkü kartın üç alanı (güzergâh, araç tipi, yük tipi) "Kara taşımacılığı" alt profiline taşınır. Önceden uygulanmış firmalar bozulmaz (alanlar zaten vardır, aynı ad atlanır). İçerik ürün önerisidir, doğrulanmamıştır.

Uygulama notu (dilim 1, 13.11.2026): tablolar `meta.SubProfiles` / `meta.SubProfileOptions`; servis `ISubProfileService`; uçlar `/api/customization/sub-profiles`; arayüz Sektör profili sayfasındaki "Alt profiller" bölümü. K7 ayar düzeyi **uygulama yapılandırması** seçildi (`Customization:Limits`, platform geneli, varsayılan sınırsız); firma bazında sınır ve ekran yoktur. K3'e iki somutlaştırma eklendi: (1) **uygulanmış seçenek değişmez ve kaldırılmaz** (alan artık firmanındır; düzeltme Özel alanlar/Listeler sayfasındandır), uygulanmamış seçenek düzenlenir ve kaldırılır; (2) durum geçişleri yalnızca Taslak→Etkin, Etkin→Pasif, Pasif→Etkin'dir (etkin alt profilin alanları firmaya eklendiğinden taslağa dönüş yoktur). Kural seçeneği, başlangıç alt profilleri ve eski kartın bağlanması sonraki dilimlerdedir.

Uygulama notu (dilim 3, 13.11.2026): **hazır alt profiller** (K9) kodda tanımlıdır (`SubProfileStarters`; Kara taşımacılığı, Gümrüklü antrepo, Deniz taşımacılığı) ve açılışta modül kataloğuna göre doğrulanır. K8'den **bilinçli sapma:** sektör profili kartları alt profil ve faaliyet işaretlemez, olduğu gibi kalır ("hazır başlangıç": alan ve liste değeri ekler); kartı alt profile bağlamak mevcut davranışı ve platformun firma açılışını (`CreateTenantRequest.Profile`) değiştireceği için ayrı bir kararla bırakıldı. Bunun yerine "Alt profiller" bölümünde **Hazır alt profiller** galerisi vardır: tek tıkla firmanın *taslak* alt profili olarak eklenir (`GET /starters`, `POST /starters/{key}`), seçenekler firma etkinleştirince uygulanır; gereken modül (seçeneklerin hedeflerinden türer) kapalıysa eklenemez, bağlı faaliyet katalogda yoksa ya da pasifse bağlanmadan eklenir. Kara taşımacılığı, "Lojistik firması" kartının üç fırsat alanını taşır; kart ayrıca olduğu gibi kalır, aynı adlı alan atlandığı için çift alan oluşmaz. Faaliyetlerim ağacı Sektör profili sayfasına taşındı (`ActivitySelectionSection`); `/yonetim/faaliyetler` yeni yere yönlenir, ayrı menü ve Yönetim kartı kalktı. İçerik ürün önerisidir, bir işletmeyle doğrulanmamıştır; mevzuata bağlı alan ya da kural içermez.

Uygulama notu (dilim 4, 13.11.2026) — **karar ürün sahibinin onayı alınmadan önerimle verildi** (soru kutusu reddedildi; geri alınabilir, dilim ayrı PR'dadır): taşıma özellikleri ("nasıl yapıyoruz" ekseni) **firma tanımlı ve alt profile bağlıdır**; ADR-0029'daki platform kataloğu (`ServiceFeatures`, `ActivityNodeFeatures`, `TenantActivityFeatureValues`) yapılmadı. Gerekçe: firma kendi alt profilini zaten ekliyor (K1) ve ekseni "tamamen dinamik" istendi; platform kataloğu üç tablo ve platform ekranı ister, firma kendi özelliğini ekleyemez. `meta.SubProfileFeatures` (grup + değer, alt profil başına tekrarsız, büyük/küçük harf fark etmez; RLS, `ICompanyShared`); `POST /{id}/features`, `DELETE /{id}/features/{featureId}`; özellikler hem listede hem ayrıntıda gelir. İlk sürümde **yalnızca etiket ve süzgeçtir**: hiçbir alan, kural ya da modül uygulamaz, bu yüzden taslak/etkin/pasif her durumda eklenip kaldırılır (uygulanmış seçenek gibi kilitlenmez). Arayüz: alt profil tablosunda "Hizmet özellikleri" sütunu ve özelliğe göre süzgeç, ayrıntıda özellik tablosu ve ekleme formu (mevcut özellik adları önerilir), hazır alt profil kartlarında özet. Hazır alt profiller özellikle gelir (Kara: Taşıma türü Komple/Parsiyel, Kapsam Yurt içi/Uluslararası; Deniz: Taşıma türü Tam konteyner/Parsiyel konteyner/Dökme, Kapsam Uluslararası; Antrepo: yok); içerik öneridir, doğrulanmadı. Özelliğin **davranışa dönüşmesi** (kural koşulu, alan göster/gizle) dilim 2'nin kural katmanına bağlıdır; faaliyet düğümüne bağ da yoktur. Açık: özellik adlarının firmalar arası standardı (serbest metin olduğundan "Taşıma türü" ile "Taşıma tipi" ayrı sayılır) — raporlama gerekirse ortak ad katalogu ayrı karardır.

## 3. Dilimler (öneri)

1. **Veri modeli ve alan/liste değeri.** Tablolar + RLS, `ISubProfileService`, uçlar, tablo ekranı, etkinleştirme önizlemesi ve uygulama. Dört test: mutlu yol, doğrulama, tenant izolasyonu, yetki.
2. **Kural katmanı.** K4; `RuleLayerKind` sırası, `TenantFieldRulesService.BuildLayersAsync`, çakışma testleri, N+1 ve derinlik sınırı testi.
3. **Sayfa birleştirme ve başlangıç alt profilleri.** K8, K9; açık/koyu tema ekran görüntüsü.
4. **Taşıma özellikleri kataloğu** (komple/parsiyel, yurt içi/uluslararası, sıcaklık kontrollü vb.) ve düğüm/alt profil bağı; ayrı karardır, bu belgenin kapsamı dışındadır.

## 4. Riskler

- Alan kuralı yeni bir değerlendirme yoludur; alt profil sayısı arttıkça birleşim maliyeti büyür (önbellek anahtarı: etkin alt profil sürümleri).
- Tenant yazarlı `Block`/`RequireApproval` kuralı kendi kullanıcılarını kilitleyebilir; mevcut uyarlamadaki izin verilen etkiler aynen geçerlidir, ayrı sınır koymadan önce kodda doğrulanmalıdır.
- Aynı özel alanı iki alt profil tanımlarsa ad çakışması sessiz atlanır; önizlemede "zaten var" olarak gösterilmelidir.
- ADR-0029 açık soru 1 ("tenant ağaç düğümü ekleyebilsin mi") bu belgeyle kısmen cevaplanır: firma düğüm değil **alt profil** ekler; katalog hâlâ platformundur.

## 5. Reddedilen seçenekler

- Her kombinasyon (kara + antrepo, kara + deniz…) için ayrı profil kartı: dinamik ilkeye aykırı, kombinasyon patlaması.
- Alt profil için ayrı alan/liste altyapısı: form, içe aktarma, rapor ve otomasyonun hepsi ikinci kez yazılırdı.
- Çok seviyeli alt profil: derinlik gerekirse faaliyet ağacı zaten çok seviyelidir.
- Kuralı yalnızca tenant uyarlaması katmanına koymak: tek kayıt olduğundan alt profil bazında aç/kapa yapılamaz.

## 6. Açık sorular

1. ~~Pasife alınınca alanlar~~ **Karar (12.11.2026):** alanlar ve liste değerleri kalır, yalnızca kurallar katmandan çıkar (K5). Alanın kaynağı (alt profil kimliği) özel alan tablosunda saklanmaz.
2. ~~30 alan sınırı~~ **Karar:** şimdilik sınırsız, sınır kodu kalır ve ayara bağlanır (K7).
3. ~~Katman sırası~~ **Karar:** `RuleLayerKind`'e `SubProfile` eklenir ve sıra `ProductStandard < SectorPackage < SubProfile < TenantAdaptation < Scope` olur. `RuleLayerMerger` katmanları bu sayıya göre sıralar, üstteki alttakini geçersiz kılar. Katman türü `RulePackage.Layer` sütununda **metin** olarak saklanır (`HasConversion<string>`), sayı olarak değil; bu yüzden sayıları kaydırmak veritabanını ve migration'ı etkilemez. `Scope` ve `TenantAdaptation` yalnızca kodda ve testlerde adıyla geçer. Sıra bir testle sabitlenir.
4. ~~Şirkete özel mi?~~ **Karar:** hayır, tenant genelinde (`ICompanyShared`); şirket bazında alt profil yoktur. Not: "global" bu belgede firma geneli diye anlaşıldı. Platformun bütün firmalara dağıttığı bir alt profil kataloğu ayrı bir karardır ve bu belgede yoktur.
5. Alt profil başlangıç içeriğini platform mu yayınlasın (paket), firma mı kopyalasın? Öneri: ilk dilimde kodda tanımlı başlangıç, sonra platform paketi.

## 7. Doğrulanmadı

- Pilotun gerçek işleyişi: yalnızca karayolu, gümrüklü antrepo ve denizyolu söylendi; alt profillerin içeriği (hangi alan, hangi liste, hangi kural) pilotla gözden geçirilmedi.
- Mevzuata bağlı hiçbir kural bu belgeden zorunlu üretim kuralı yapılmaz (ADR-0032 ilkesi).
- Tenant uyarlaması servisinde (`TenantFieldRulesService`) etkiye göre bir kısıt görmedim, yani firma `Block` ve `RequireApproval` dahil dokuz etkiyi de yazabiliyor görünüyor; doğrulayıcı ve çalışma zamanı yolu ayrıca okunmadı.
