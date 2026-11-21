# Öncelikli işler (ADR taraması, 7 Kasım 2026)

`docs/adr/` altındaki 37 kararın "yapılmadı / kalan / açık karar" bölümleri tek tek okundu, kodla karşılaştırıldı ve
tek bir sıraya kondu. Sıralama ölçütleri: (1) kaç başka işi açtığı, (2) CRM pilotunun (yol haritası D1: Ç4 + Ç7 + Ç5a)
kritik yolunda olup olmadığı, (3) ürün, ticari ya da mevzuat kararı beklemeden yapılabilir olması, (4) küçük ve
incelenebilir adımlarla teslim edilebilmesi. Yalnızca kodda ve testte doğrulananlar "yapıldı" sayılır; bu belge
tek başına hiçbir şeyi uygulamaz.

## A. Karar beklemeden yapılabilir (sırayla)

| # | İş | ADR | Neden bu sırada | Büyüklük |
|---|----|-----|-----------------|----------|
| 1 | ~~**Kişisel görünüm kalanları:** `AutomationRules`, `ApprovalProcesses`, `ApprovalCenter` ekranlarında eski `ColumnPicker` → `GridViewState`/`GridColumnPicker`/`PersonalViewBar`; Aktiviteler'in tarayıcıda sakladığı görünümler sunucuya (`core.GridViews`); `contracts.ContractSavedViews` tablosunun ortak tabloya taşınıp kaldırılması; eski `ColumnPicker` ve `table-columns.js`'in silinmesi~~ **Yapıldı** (7 Kasım 2026, [ADR-0036](adr/0036-kisisel-liste-gorunumleri.md); 11 Kasım'da kodda doğrulandı: eski `ColumnPicker`, `table-columns.js` ve `ContractSavedView` kalmadı) | 0036, 0015, 0022 | Kabul edilmiş tek ADR'nin açık kalan son parçası; küçük; CRM menü kararındaki "Aktiviteler hâlâ chip kullanıyor" borcunu da kapatır | K–O |
| 2 | ~~**Küçük otomasyon/lisans kalanları:** `tenant-delete` platform denetim kaydı; yalnızca `core.automation-logs.read` izni olanlar için menü girdisi; otomasyon kural listesinde sunucu `sort`~~ **Yapıldı** (11 Kasım 2026'da kodda doğrulandı: `TenantDataTransfer` platform denetim kaydı, `AppCatalog` "Çalışma geçmişi" girdisi, `AutomationEndpoints` `sort` doğrulaması) | 0027, 0028 | Birkaç dosya; teknik borç, karar gerektirmez | K |
| 3 | **İş olayı yayın sözleşmesi (Ç0.4):** ~~ortak sözleşme + CRM'den ilk olaylar~~ **Yapıldı** (8 Kasım 2026, [ADR-0039](adr/0039-modul-is-olaylari.md)): `IModule.BusinessEvents`, `IBusinessEventPublisher`, CRM `crm.opportunity.won/lost`, `crm.lead.converted`; `DomainEvent` tetikleyicisi yayınlanıyor. Kalan: diğer modül komutları (teklif kabul, sevkiyat teslimi, ADR-0038 dilim 6) ve webhook | 0007, 0027 (2b-3), 0038 (dilim 6) | İki ADR'yi bloke eden ön koşul kalktı | O |
| 4 | **Dosya ve ekler (Ç4):** ~~dilim 1: ADR, depolama adaptörü + metadata + yetkili indirme~~ **Yapıldı** (11 Kasım 2026, [ADR-0040](adr/0040-dosya-ve-ekler.md); imzalı URL portal dilimine ertelendi). Kalan: dilim 1b `AttachmentPanel` + ilk ekran, 2 tarama, 3 yaşam döngüsü, 4 imzalı bağlantı/portal, 5 mobil, mevcut modül eklerinin taşınması | 0022, 0023, 0024, 0032, 0033, 0038 | En çok işi açan ön koşul (sözleşme/sipariş/destek ekleri, antrepo vekâlet belgesi, içe aktarma kuyruğu, lojistik teslim kanıtı); D1 pilot şartı | B |
| 5 | ~~**Bildirim kutusu (Ç5a) + onayda eskalasyon ve vekâlet:** ADR-0027 dilim 4b~~ **Yapıldı** (7 Kasım 2026; 12 Kasım'da kodda doğrulandı: `core.Notifications`, `NotificationBell`, `ApprovalEscalationWorker`, `ApprovalDelegation`, ADR-0027 "Dilim 4b") | 0027, 0015, 0017, 0019, 0024 | D1 pilot şartı; aktivite/fırsat/teklif/destek bildirimleri hep bunu bekliyor | B |
| 6 | **İçe aktarma dilim 4:** ~~XLSX~~ **XLSX yapıldı** (12 Kasım 2026, dilim 4a; ADR-0033'te MiniExcel sapması yazıldı). Kalan: geri alma, aynı dosya uyarısı, kayıtlı sütun eşlemesi, hepsi-ya-da-hiç | 0033 | D1 pilot şartı (Ç7); Excel taşıma pilotun ön koşulu | O–B |
| 7 | ~~**Otomasyon dilim 4c:** kayda bağlı onay ("onaya gönder" aksiyonu) ve kalıcı akış~~ **Yapıldı** (4c, 4c-2 ... 4c-5; ADR-0027 uygulama bölümleri) | 0027 | 4a (genel onay) main'de; 4b'den bağımsız yapılabilir | B |
| 8 | ~~**Depo/antrepo 6b:** Partiler, Hareketler, Bakiyeler ekranları (arka uç 6a main'de)~~ **Yapıldı** (13 Kasım 2026; ADR-0032 "Dilim 6b") | 0032 | Arka ucu var, ekranı yok; mevzuat kararı istemez | O |
| 9 | ~~**Eskimiş ADR metinleri:**~~ **Yapıldı** (13 Kasım 2026, PR #305; tarihli durum notları) — 0010, 0014, 0015, 0016, 0017, 0019'daki "yapılmadı" listeleri artık kodda olan şeyleri (teklif entegrasyonu, satış tahmini, departman, kampanya, sipariş/sözleşme devri) hâlâ "engelli" yazıyor; 0017/0019'daki "tenant başına veritabanı" atıfları; ~~0026 çift numara~~ **Yapıldı** (9 Kasım 2026: raporlama → [ADR-0035](adr/0035-raporlama-ve-satis-tahmini.md)) | belge | Yanlış yönlendiriyor; kod değişikliği yok | K |
| 10 | **Arayüz dili aşama 4:** modül ekranlarının metin göçü (Core, CRM, Destek, Sözleşme, Sipariş, Pazarlama, Raporlar) | 0034 | Mekanik ama çok dosya; `az`/`en` çevirisini kimin doğrulayacağı açık | B |
| 11 | **Raporlama kalanları:** ekip/departman kırılımı (Core.Contracts ekip sözleşmesi), xlsx dışa aktarma, dağıtım listesi, lead sütun daraltma | 0035 | Pilotu kesmiyor; ekip sözleşmesi 0026 departman ADR'sine bağlı | O |
| 12 | **Artık engelli olmayan CRM bağları:** Lead/Fırsat'a `CampaignId`, `OwningBusinessUnitId`, ürün kataloğu bağı (`productInterest`), indirim onayının genel onay servisine bağlanması, BusinessUnit'e göre kullanıcı lookup'ı | 0014, 0015, 0016, 0017, 0018 | Bağımlılıkları artık main'de; her biri migration ister, ürün önceliği sorulmalı | O–B |

## A2. Güncel sıra (13 Kasım 2026 taraması)

A tablosundaki kalanlar kodla yeniden karşılaştırıldı; aynı anda çalışan başka oturumların alanı (Lojistik dilim 8 masraf/yakıt, ADR-0041
firma alt profili, sektör bilgisi tablosu, AI erişimi menüsü) bu sıraya alınmadı, çakışmasın diye dokunulmadı. Karar gerektiren yerde
önerilen seçenekle ilerlenir ve seçenek ilgili ADR'ye "Sonra değerlendirilecek" notu olarak yazılır.

| Sıra | İş | Durum / gerekçe |
|------|----|-----------------|
| 1 | Depo/antrepo 6b ekranları (A-8) | Yapıldı (13 Kasım): arka ucu hazırdı, karar istemiyordu, pilotun depo tarafını açar |
| 2 | Eskimiş ADR metinleri (A-9) | Yapıldı (13 Kasım, PR #305): 0010, 0014–0017, 0019 sonuna tarihli güncel durum notu |
| 3 | İçe aktarma dilim 4 kalanları (A-6) | 4b aynı dosya uyarısı (#306), 4c kayıtlı sütun eşlemesi (#307), 4d geri alma (#312) yapıldı ve 4e "hepsi ya da hiç" yapıldı; dilim 4 tamam |
| 4 | Arayüz dili aşama 4 kalan dilimleri (A-10): 8b teklif ayrıntısı → 18 | Mekanik; `az` doğrulaması hâlâ açık (taslak sayılır) |
| 5 | Raporlama kalanları (A-11) ve CRM bağları (A-12) | xlsx dışa aktarma, dağıtım listesi ve kart dışa aktarma yapıldı (ADR-0035; 7–9 Kasım). `CampaignId` bağı yapıldı (14 Kasım; müşteri adayı + fırsat, ADR-0025 "CRM kampanya bağı"; kampanya ayrıntısında "Bağlı kayıtlar" sekmesi eklendi). Kalan A-11: ekip/departman kırılımı (ekip sözleşmesi + çift sayım kararı), lead sütun daraltma (önce ölçüm). Kalan A-12: `OwningBusinessUnitId`, ürün kataloğu bağı (`productInterest`), indirim onayının genel onay servisine bağlanması, birime göre kullanıcı lookup'ı; her biri ürün önceliği ister |
| — | Ç4 dosya dilim 1b (A-4) | `AttachmentPanel` ADR-0038 dilim 2a ile geldi; kalan 2 tarama, 3 yaşam döngüsü, 4 portal bağlantısı dış sağlayıcı/ürün kararı bekliyor |
| — | Ç0.4 kalanları (A-3) | Sevkiyat teslimi olayı ADR-0038 dilim 6 ile geldi; teklif kabul olayı `crm.quote.accepted` yapıldı (#311); kalan: webhook (dış sağlayıcı kararı) |

### Sonra değerlendirilecek notlar

- **Antrepo 6b:** partide beyanname satırı seçimi, bakiyede stok kartı/birim sütunu, toplu (barkod) hareket ekranı; ekranlar tarayıcıda açık/koyu temada gözle doğrulanmadı (C listesine eklendi).
- **Depo seçim kutuları:** hareketin lokasyon seçimi tesise bağlı değil (hareketin tesisi yok); çok tesisli müşteride uzun liste olursa tesis süzgeci eklenmeli.
- **CRM kampanya bağı:** müşteri adayı/fırsat listesinde kampanya sütunu ve süzgeç seçicisi, kampanya raporuna elle bağlanan kayıtların girmesi (atıf modeli kararı), içe aktarma ve rapor veri kümesinde kampanya sütunu; formdaki alan tarayıcıda gözle doğrulanmadı.

## A3. Güncel sıra: önce Core, sonra CRM (15 Kasım 2026)

Kaynaklar: ADR-0046 ve ADR-0049 durum satırları, `core-mimari-degerlendirme-2026-11.md`, `crm-eksik-surecler-epicleri.md`. Açık PR'lar
(#417 müşteri adayı skorlama dilim 1, #416 çeviri 18b) sıraya alındı ama dokunulmadı.

**Core (karar beklemeden yapılabilir, sırayla)**

| Sıra | İş | Kaynak | Durum |
|------|----|--------|-------|
| C1 | Kalan modül işlerinin tek zamanlayıcıya taşınması: Destek → `QuoteExpiry` → `ContractJobs` → `MarketingSend` (iş başına bir PR) | ADR-0046 dilim 4, E3 | Yapıldı (15-16 Kasım): Destek, `QuoteExpiry`, `ContractJobs`, `MarketingSend` (kuyruk moduyla) |
| C2 | Orta modüllerin uç noktaları modüle (Sipariş, Destek, Marketing) | ADR-0046 dilim 5, H3 | Yapıldı (16 Kasım): Sipariş, Pazarlama, Destek; ortak `ResourceEndpoints`, `IRecordExportAudit`, `CsvText`, `IModulePublicEndpoints`, `IPublicTenantScopes` |
| C3 | Büyük modüllerin uç noktaları modüle (Sözleşme, CRM; CRM birden çok PR) | ADR-0046 dilim 6, H3 | Yapıldı (16 Kasım): Sözleşme ve CRM'in bütün uç noktaları modülde; `Api/Crm` ve `Api/Contracts` klasörleri kalktı |
| C4 | Customs'ın Antrepo'ya taşınması ve Core hazır listelerindeki sektör sözlüğü | ADR-0046 dilim 7, H1, H2 | Customs yapıldı (17 Kasım): sözleşmeler `Modulix.Modules.Warehouse.Contracts`'ta, uygulama ve tenant uçları modülde, tablolar başvuru tablosu (`IPlatformReferenceEntity`); `Api`'de modül klasörü kalmadı. Sektör sözlüğü de yapıldı (17 Kasım): `IModule.ChoiceValueContributions`; araç belgeleri Filo'ya, sürücü belgeleri Lojistik'e, antrepo yer türü Depo'ya taşındı |
| C5 | Seriyi firma yapılandırır (önek, basamak, sıfırlama; yönetim ekranı) | ADR-0049 dilim 2 | Yapıldı: dilim 2a (API) ve 2b (Yönetim > Belge numaraları); 2c (şirket başına seri) kullanan belge olmadığı için ertelendi |
| C6 | Modül bağımlılık beyanı (`IModule` + lisans atamada denetim) | H7 | Yapıldı (17 Kasım): ADR-0051, dilim 1 (beyan, katalog doğrulaması, mimari test) ve dilim 2 (hak yazılırken zorunlu bağımlılık denetimi) yapıldı (16 Kasım); dilim 3a (platform: bağımlılık bilgisi, eksik bağımlılık uyarısı, "bağımlılığı eksik firmalar" raporu) ve dilim 3b (firma yöneticisine Yönetim ana sayfasında bilgi) |
| C7 | Kapanış: `Program.cs`'te modüle özel satır kalmadığını denetleyen mimari test | ADR-0046 dilim 8 | Yapıldı (17 Kasım): `HostProgramRulesTests`; modül listesi ve host köprüleri `Api/ModulixModules.cs`'te |

Core'da karar bekleyenler: ADR-0049 dilim 3 (numara biçimi) ve 5 (kur kaynağı), entegrasyon katmanı (E1), belge çıktısı (E2), portal kimliği (E4),
saklama politikası (E5), yapay zekâ çekirdeği (E6, ADR-0047).

**CRM (Core sırasından sonra; karar beklemeyenler)**

| Sıra | İş | Kaynak | Durum |
|------|----|--------|-------|
| R1 | Müşteri adayı skorlama dilim 2–4 (davranış puanı, azalma/eşik olayı, puan açıklaması) | CRM epik E1 | Yapıldı: dilim 1-4 teslim (ADR-0050; PR #417, #441, #449, #462 ve puan açıklaması) |
| R2 | Canlı panolar ve pano bileşenleri (A-11 ekip/departman kırılımı ile) | CRM epik E2 | Sürüyor: [ADR-0054](adr/0054-kullanici-panolari.md) dilim 1 (model, servis, uç noktalar, ekran; 17 Kasım) ve dilim 2 (paylaşım: özel / seçilen kullanıcılar / firma, kişiye özel varsayılan pano; 17 Kasım); dilim 3 aralıklı yenileme yapıldı (17 Kasım; iş olayıyla yenileme karar bekler), dilim 4a rol/ekip/iş birimi hedefli paylaşım yapıldı (17 Kasım; rapor ve pano, alt birimler dahil), dilim 4b departman/ekip kırılımı yapıldı (18 Kasım; tek birincil üyelik) |
| R3 | Birleşik 360° müşteri zaman çizelgesi | CRM epik E9, [ADR-0053](adr/0053-musteri-zaman-cizelgesi.md) | Dilim 1 (kaynak sözleşmesi, CRM kaynakları, sekme) 17 Kasım'da; Sipariş, Sözleşme ve Destek kaynakları yapıldı (21 Kasım); kalan: Pazarlama kaynağı, süzgeç ve özet şeridi |
| R4 | Çalışma alanı rengi ve rol bazlı menü | CRM epik E8 | Önce tasarım sisteminde "firmanın değiştirebileceği tokenlar" |
| R5 | Müşteri yolculukları + görsel süreç tasarımcısı (ortak akış modeli) | CRM epik E4, E6 | `fake` sağlayıcıyla |
| R6 | Kod yazmadan özel tablolar | CRM epik E5 | Önce ADR |

CRM'de karar bekleyenler: A-12 bağları (ürün önceliği), çok kanallı gönderim/İYS (E3), yapay zekâ asistanı (E7), kampanya atıf modeli.

## B. Karar bekleyen (kod yazılmaz)

- **Ürün kararı:** ADR-0029 sektör/faaliyet (kabul), ADR-0031 rentcar (kabul + pilot), ADR-0038 filo/lojistik (dilim 0 kararları: Ç0.4, Ç4 asgari, Ç8.1, ADR-0037 dilim 1), çoklu pipeline (0017), ürün varyantı (0018), kayıt bazlı `OwningUnitId` (0026), paylaşılan/takım görünümü (0036), otomatik lead atama kuralları (0014), yeniden yönlendirme ve SLA yeniden seçimi (0024), kampanya atıf modeli (0025).
- **Ticari karar:** ADR-0028 açık kararlar 1, 2, 5, 6, 7; on-prem imzalı lisans (7b); faturalama/ödeme sağlayıcısı (7c); depolama/e-posta/AI limitleri; Filo lisansı (0038).
- **Hukuk / mevzuat:** KVKK saklama ve anonimleştirme (0022, 0024, 0025); İYS entegrasyonu ve rıza metni (0025); bayi çalışanının müşteri verisini görmesi (0026); antrepo tescil no, antrepo tipleri, GTİP cetveli, 7100 rejim (0032); rentcar mevzuatı (0031); U-ETDS, yetki belgeleri, e-İrsaliye (0038).
- **Dış sağlayıcı seçimi:** e-imza (0022), e-posta/SMS/WhatsApp ve webhook (0025), gelen e-posta IMAP (0024), Evrim/Solon adaptörü (0032).

## C. Doğrulanmadı (kod var, elle denenmedi)

Otomasyon 5a/5b/5c ekranları, raporlama ekranları (dilim 1–4), rejim profili platform ekranı, içe aktarma tasarım ekranı,
onay merkezi dolu liste ve pasif kartlar, depo Partiler/Stok hareketleri/Stok bakiyeleri ekranları: hiçbiri tarayıcıda açık ve koyu temada gözle doğrulanmadı. Tam entegrasyon
paketi CI'da temiz veritabanıyla koşuyor; yerelde yalnızca değişen alanın testleri çalıştırılıyor.
