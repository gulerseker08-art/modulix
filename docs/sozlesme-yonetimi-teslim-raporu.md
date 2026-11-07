# Sözleşme Yönetimi (EPIC 7) — Teslim Raporu

Tarih: 1 Kasım 2026. Kapsam: "EPIC 7 — Sözleşme Yönetimi" talimatı (şablon, sürüm, tarih, yenileme, onay, imza, taahhüt). Mimari karar ve dilim ayrıntıları [ADR-0022](adr/0022-sozlesme-yonetimi.md); uç nokta ve davranış özeti README "Sözleşme yönetimi" bölümünde; işletim notları [tenant-isletim.md](tenant-isletim.md).

Bu rapor yalnızca yapılan **ve doğrulanan** işleri "Tamamlandı" sayar. Koşulmayan test, denenmeyen ekran, kurulmayan özellik ve hedef modülü olmayan akış açıkça yazılıdır.

## 1. Başlangıçtaki eksikler

Analiz anında sözleşme varlığı, tablosu, uç noktası ve ekranı yoktu (ADR-0022 analiz tablosu; kod taraması boş). Yol haritasında sipariş/sözleşme köprüsü "hedef modül yok" diye bırakılmıştı. Yeniden kullanılabilir altyapı vardı: kayıt boru hattı, ortak firma/kişi kaydı, lookup, teklif hesap motoru ve PDF desenleri, aktivite, e-posta kuyruğu, `sp_getapplock` ile zamanlanmış iş deseni, RLS. Ayrı bir `Modulix.Modules.Contracts` modülü kuruldu; aynı işlev ikinci kez yazılmadı.

**Talimattaki bir varsayım geçersizdir:** "tenant başına ayrı veritabanı" ADR-0020 ile terk edilmiştir; modül tek paylaşımlı veritabanında `TenantId` + EF filtresi + SQL Server RLS ile çalışır. Tenant çözümleme, bağlantı seçimi ve tenant başına migration yoktur.

## 2. Yapılanlar (dilim ve PR'lar)

| Dilim | PR | İçerik |
|---|---|---|
| 1 İskelet | #83 | Modül, izinler, `Contracts`/`ContractParties`/`ContractLines`/`ContractEvents`, kayıt depoları, CRUD, liste ve detay |
| 2 Durum ve sürüm | #84, #85 | Dört ayrı durum alanı, eylem uçları, dönem aritmetiği (ay sonu/artık yıl/tenant günü), değişmez sürüm anlık görüntüsü (SHA-256), karşılaştırma |
| 3 Teklif → sözleşme | #86 | Kabul edilen teklif revizyonundan sözleşme (kalem sabitleme, mükerrer engeli), form metadata'sı, firma ve fırsat kartı, aktivite bağlantısı |
| 4 Şablon ve belge | #87 | Şablon sürümleri, kontrollü değişkenler, güvenli şablon dili, PDF, belge özeti ve anlık görüntü |
| 5 İç onay | #88 | Kural tabanlı çok adımlı onay, vekâlet, dört göz, geri gönderme, onay iş listesi |
| 6 İmza | #89 | İmzacılar, manuel doğrulama, sağlayıcı arayüzü + simülasyon, imzalı webhook (tekrar/sıra koruması) |
| 7 Taahhüt | #90 | Tek seferlik/dönemsel taahhüt, dönem başına ayrı gerçekleşme, ölçüm ve tolerans, kanıt dosyası, ihlal → takip aktivitesi |
| 8 Yenileme | #91 | Dönem kararı, otomatik uzatma (varsayılan kapalı), hatırlatma/eskalasyon, yenileme taslağı, fesih, zamanlanmış işler |
| 9 Ekranlar | #92 | Sıralama, kayıtlı görünümler, pano, CSV dışa aktarma, sihirbaz, ayarlar bağlantısı |
| 10 Teslim | bu PR | Bu rapor, ADR/README/yol haritası kapanışı |

## 3. Veri modeli, ekranlar ve API (özet)

**Tablolar (`contracts` şeması, hepsi `TenantId` + RLS):** `Contracts`, `ContractParties`, `ContractLines`, `ContractEvents`, `ContractVersions`, `ContractTemplates`, `ContractTemplateVersions`, `ContractDocuments`, `ContractApprovalRules`, `ContractApprovalDelegations`, `ContractApprovalDecisions`, `ContractSignatureRequests`, `ContractSignatories`, `ContractSignatureEvents`, `ContractSignatureFiles`, `ContractCommitments`, `ContractCommitmentOccurrences`, `ContractCommitmentFiles`, `ContractRenewals`, `ContractReminders` (20 tablo; `ContractSavedViews` ADR-0036 ile ortak `core.GridViews`'a taşınıp kaldırıldı). Migration'lar (sıralı, yalnızca ekleme): `ContractManagement`, `ContractVersions`, `ContractFromQuote`, `ContractTemplatesDocuments`, `ContractApprovals`, `ContractSignatures`, `ContractCommitments`, `ContractRenewals`, `ContractViews`.

**Ekranlar:** `/sozlesmeler` (liste: hızlı görünümler, süzgeç, sıralama, kayıtlı görünümler, CSV), `/sozlesmeler/ozet` (pano), `/sozlesmeler/sihirbaz`, `/sozlesmeler/yeni`, `/sozlesmeler/{id}` (sekmeler: genel, taraflar, kalemler, belgeler, sürümler, onay, imza, taahhüt, yenileme ve fesih, geçmiş), `/sozlesmeler/tekliften/{teklifId}`, `/sozlesmeler/sablonlar`, `/sozlesmeler/onaylar`, `/sozlesmeler/onay-kurallari`, `/sozlesmeler/imzalar`, `/sozlesmeler/taahhutler`, `/sozlesmeler/yenileme`; firma kartında ve fırsat ekranında ilişkili sözleşmeler; ayarlar `yonetim/ayarlar?grup=Sözleşmeler`.

**İzinler (18):** `contracts.contracts.read|write|approve|terminate|export`, `contracts.financials.read`, `contracts.templates.edit|publish`, `contracts.documents.generate|download`, `contracts.approvals.manage`, `contracts.signatures.manage|verify|provider-events` (yalnızca sistem kimliği), `contracts.commitments.write|complete|waive`, `contracts.renewals.manage`. Hazır roller: sözleşme sorumlusu, onaylayıcı, şablon yöneticisi, onay yöneticisi, imza sorumlusu, yenileme sorumlusu (fesih yok), izleyici. Hassas (tutar) alanlar `contracts.financials.read` olmadan okunmaz/yazılmaz; platform yöneticisi üyeliği olmayan tenant'ın sözleşmesini okuyamaz.

**Zamanlanmış iş:** `ContractJobsWorker` (tenant başına `sp_getapplock`, `contracts.scheduler` sistem kimliği). Ayrıntı: README ve `tenant-isletim.md`.

## 4. Testler ve kanıt

Son koşu (1 Kasım 2026, Release, yerel SQL Server + Mailpit): mimari 33, Core 214, CRM 206, Sözleşme birim 146, **tam entegrasyon paketi 431/431 geçti** (13 dk 15 sn). Sözleşme entegrasyon sınıfları 56 senaryo: `ContractTests`, `ContractFormTests`, `ContractLifecycleTests`, `ContractFromQuoteTests`, `ContractTemplateTests`, `ContractApprovalFlowTests`, `ContractSignatureFlowTests`, `ContractCommitmentTests`, `ContractRenewalTests`, `ContractViewTests`; her özellik mutlu yol, doğrulama, tenant izolasyonu ve yetki testleriyle teslim edildi. Yapısal testler (`TableInventoryTests`, `EndpointProtectionTests`, `ModulesAndRolesTests`, RLS) yeni tablo, uç nokta ve izinlerle geçer. Her dilimin ekranları tarayıcıda (Release, demo tenant) denendi; denenmeyen düğmeler ADR'de dilim bazında yazılıdır.

### 4.1. Kabul kriterleri (talimat bölüm 14)

| # | Kriter | Durum | Kanıt / not |
|---|---|---|---|
| 1 | Kabul edilen teklif revizyonundan doğru tutar ve kalem | Tamamlandı | `ContractFromQuoteTests` (12 ay, 2 × 1.000 → 1.800/360/2.160, izlenebilirlik) |
| 2 | Teklif/katalog değişince imzalı sözleşme değişmez | Tamamlandı | Aynı sınıf (teklif iptal edilir, sözleşme aynı kalır); kalemler kopyadır, referans değildir |
| 3 | Eksik zorunlu bilgiyle onay/imza geçişi yok | Tamamlandı | `ContractLifecycleTests`, `ContractSignatureFlowTests` |
| 4 | Onay gerektiren sözleşme onaysız ilerleyemez | Tamamlandı | `ContractApprovalFlowTests`, imza başlatma ön koşulu |
| 5 | Kısmi imza tamamlanmış sayılmaz | Tamamlandı | `ContractSignatureFlowTests` (PartiallySigned → Completed) |
| 6 | İmzalı içerik düzenlenmez, yeni sürüm | Tamamlandı | `ContractLifecycleTests`, imza sonrası dondurma |
| 7 | Eski sürümün webhook olayı güncel durumu değiştiremez | Tamamlandı (sınırlı) | Kapanmış/iptal edilmiş isteğe gelen olay `Ignored`; sıra dışı `OutOfOrder`; mükerrer `Duplicate`. "Eski sürüm numarasıyla" ayrı bir senaryo yok; koruma imza isteğinin kapanmasına dayanır |
| 8 | İmzalı sözleşme başlangıç gelince aktifleşir | Tamamlandı | İmza testi (`AwaitingStart`), `ContractRenewalTests` (tarihe bağlı geçişler) |
| 9 | 12 ay: ay sonu, artık yıl | Tamamlandı | `ContractLifecycleRulesTests`, `ContractRenewalTests` (takvim ayı) |
| 10 | Bildirim tarihi bitişten ayrı izlenir | Tamamlandı | Türetilmiş `NoticeDeadline`, hatırlatma ve eskalasyon testleri |
| 11 | Tekrarlanan job/webhook mükerrer kayıt üretmez | Tamamlandı (mevcut kayıtlar için) | Hatırlatma/dönem kararı benzersizliği, eşzamanlı işçi ve eşzamanlı taslak testleri, webhook `Duplicate`. Sipariş ve müşteri başlangıcı kaydı yok (hedef modül yok) |
| 12 | Fesih ve otomatik yenileme eşzamanlılığı tutarlı | Kısmen | Eşzamanlı işçiler ve eşzamanlı yenileme taslağı testli; fesih × otomatik uzatma çakışmasının kendisi ayrı sınanmadı (ikisi de kaydın sürüm denetimiyle yazılır, biri 409 alır) |
| 13 | Ek protokol yürürlükteki koşulları yanlış tarihte değiştirmez | Kısmen | Ek protokol ayrı tür değil; `revise` ile yeni sürüm açılır ve yürürlükteki sürüm işaretçisi yeni sürüm imzalanana kadar değişmez. Ek protokole ayrı yürürlük tarihi yok |
| 14 | Dönemsel taahhüt geçmişi korunur | Tamamlandı | `ContractCommitmentTests` (dönem başına satır, üzerine yazılmaz) |
| 15 | Yetkisiz kullanıcı API/dosya adresiyle veriye erişemez | Tamamlandı | Her sınıfta yetki senaryosu; belge/imza/kanıt indirme ayrı izin |
| 16 | Tenant'lar arası sızıntı yok | Tamamlandı (veritabanı, dosya, job) | Her sınıfta izolasyon senaryosu, RLS testleri, tenant başına kilit. Sözleşme modülü ayrı bir cache kullanmaz |
| 17 | PDF'de Türkçe karakter, uzun tablo, sayfa sonu | Kısmen | 150 satırlık tablo ≥ 4 sayfa ve örnek/taslak işareti testli; Türkçe karakterlerin görsel doğruluğu otomatik sınanmaz (bu raporda elle doğrulama iddia edilmez) |
| 18 | Temel akışlar gerçek servislerle çalışır | Tamamlandı | Tarayıcı denemeleri (dilim bazında); denenmeyen düğmeler ADR'de dilim bazında yazılıdır |
| 19 | Eşzamanlı düzenleme sessizce ezmez | Tamamlandı | Sürüm belirteci, 409 testleri, arayüzde çakışma uyarısı |
| 20 | Migration mevcut veriyi korur | Tamamlandı | Dokuz migration yalnızca ekler (veri kaybettiren işlem yok), yerel veritabanında ve CI'da uygulandı. Önceki sözleşme verisi olmadığından eski kayıt dönüşümü gerekmedi |

## 5. Çapraz modül akışları (talimat bölüm 10)

| # | Akış | Durum | Not |
|---|---|---|---|
| 1 | Firma/kişi kartında sözleşme, taahhüt, tarih | Kısmen | Firma kartında sözleşmeler ve taahhütler var; kişi kartında yok |
| 2 | Kabul edilen teklif revizyonundan sözleşme | Tamamlandı | `IAcceptedQuoteSource`, mükerrer engeli |
| 3 | Fırsat ekranından süreç izleme | Tamamlandı | Fırsat detayında ilişkili sözleşmeler |
| 4 | Sözleşmeden sipariş/satış devri | **Engelli** | Hedef modül yok; yalnızca sözleşme/olay sözleşmesi |
| 5 | Müşteri başlangıç sürecini başlatma | **Engelli** | Hedef modül yok |
| 6 | Kurulum/eğitim/belge toplama görevleri | Kısmen | Taahhüt kategorileri ve ihlalde takip aktivitesi var; görev şablonu/otomatik görev üretimi yok |
| 7 | Destek talebinde sözleşme/SLA | **Engelli** | Destek/SLA modülü yok |
| 8 | Aktivite/takvim üzerinden onay, imza, taahhüt, yenileme görevleri | Kısmen | Hatırlatma, eskalasyon, ihlal aktiviteleri CRM aktivitesi olarak açılır; zamanlanmış işin açtığı aktivite sözleşme kaydına bağlanmaz (numara konuda yazılıdır) |
| 9 | Belge yönetimi: sürüm ve imzalı dosya erişimi | Tamamlandı (modül içi) | Ayrı platform belge yönetimi modülü yok; belge, sürüm ve imzalı dosya izinli indirilir |
| 10 | Finansa ödeme planı aktarımı | **Engelli** | Finans modülü yok |

Çalışmayan bağlantılar tamamlanmış gösterilmedi: ilgili ekranlar yoktur, README "henüz yok" der.

## 6. Talimat bölümlerine göre durum

| Bölüm | Durum | Not |
|---|---|---|
| 3 Veri modeli | Tamamlandı (kısmi: bölüm 6.1) | Taraflar/roller, teklif ve fırsat izi, sürüm bazlı sabitleme, tek para birimi, lookup ve ortak kayıt. Sipariş/proje bağları, ekip/iş birimi alanı, teminat/üst limit/artış kuralı yok |
| 4 Şablon ve PDF | Tamamlandı (kısmi) | Sürüm, değişken, güvenli dil, önizleme, PDF, hash. Logo, çok dilli çeviri, WYSIWYG yok; örnek metin açıkça işaretli |
| 5 Sürüm ve ek protokol | Kısmen | Sürüm, karşılaştırma, dondurma, revizyon tamam; ayrı ek protokol türü ve yürürlük tarihi yok |
| 6 Durum modeli | Tamamlandı | Dört ayrı durum alanı; kurallar API'de; fesih talep/bildirim/fiilî tarih ayrımı; sürüm denetimi. Türe göre yapılandırılabilir onay/imza zorunluluğu kural motoruyla (onay) sağlanır; imza zorunluluğu yapılandırması yok |
| 7 Onay ve imza | Tamamlandı (sağlayıcı hariç) | Çok adımlı onay, vekâlet, dört göz; manuel imza + sağlayıcı arayüzü + simülasyon, HMAC webhook. **Gerçek sağlayıcı yok** (Engelli: sağlayıcı sözleşmesi ve hukuki değerlendirme gerekir). Müşteriye imza bağlantısı barındırılmaz |
| 8 Yenileme ve fesih | Tamamlandı | Politika varsayılan kapalı; eşikler, eskalasyon, taslak, zincir, ayrı fesih izni. Yönetici hiyerarşisi yok (eskalasyon kullanıcısı adlandırılır); fiyat artışı otomatik uygulanmaz |
| 9 Taahhüt | Tamamlandı (kısmi) | Dönem bazlı gerçekleşme, ölçüm, tolerans, kanıt, bağımlılık (tek taahhüde), ihlal/eskalasyon. Ekip sorumlusu yok; ceza/finansal sonuç uygulanmaz (yalnızca not) |
| 11 Ekranlar | Kısmen | Bölüm 6.2 |
| 12 Yetki ve güvenlik | Tamamlandı (kısmi) | Ayrı izinler, RLS, tenant oturumdan, platform yöneticisi kapalı, imzalı/yürürlükteki silinmez, dosya türü/boyut/imza doğrulaması, sırlar loglanmaz. Zararlı dosya taraması yoktur; arşiv/saklama politikası yoktur (iptal vardır) |
| 13 Operasyon | Tamamlandı | Transaction + outbox, idempotent işler, indeksler, sunucu tarafı süzgeç/sayfalama, iş hataları `ContractReminders` durumunda görünür, CRM kapalıysa açık durum (`Unavailable`) |

### 6.1. Veri modelinde bilinçli sınırlar
Sözleşme tek para birimlidir (kur olmadan toplanmaz); sipariş/proje bağlantı alanları hedef modül olmadığı için eklenmedi; ekip ve iş birimi sahipliği yerine kullanıcı sahipliği ve kayıt kapsamı (`RecordScope`: kendi ya da tümü) kullanılır.

### 6.2. Ekranlar
Liste (numara, başlık, müşteri, tür, sorumlu, durumlar, tarihler, kritik tarih, para birimli tutar; arama, süzgeç, sıralama, sayfalama, kayıtlı görünüm, hızlı görünümler dört istenenle, CSV) — **toplu işlem yok**. Oluşturma sihirbazı (boş / şablondan / tekliften; müşteri; kalem; tarih; taahhüt; önizleme ve gönderim; adım bazlı doğrulama; dönemsel taahhüt ve özel alan sihirbazda yok). Detay: istenen sekmelerden "Ek protokoller", "İlişkili kayıtlar" ve "Aktiviteler" ayrı sekme olarak yoktur (ek protokol = revizyon; ilişkili kayıtlar başlıkta ve firma/fırsat kartında; işlem geçmişi var). Diğer ekranlar: şablon yönetimi, sürüm karşılaştırma, onay iş listesi, imza takip, yenileme takvimi, taahhüt takip, pano — tamam; yönetici ayarları ortak firma ayarları sayfasında "Sözleşmeler" grubudur.

## 7. Gerçek erişim gerektirenler ve çalıştırılamayanlar

| Konu | Neden |
|---|---|
| Gerçek elektronik imza sağlayıcısı | Sağlayıcı seçimi, sözleşme, test hesabı ve hukuki değerlendirme gerekir; yalnızca arayüz ve test simülasyonu vardır (simülasyon "nitelikli" düzey bildirse bile nitelikli sayılmaz) |
| Müşteriye e-posta/imza isteği | Talimat gereği gerçek müşteriye gönderim yapılmadı; müşteri bildirimi uygulanmadı |
| Üretimde yenileme/fesih/zamanlanmış iş | Üretim ortamı kullanılmadı; işçi yalnızca yerel ve test tenant'larında çalıştırıldı |
| Hukuki şablon metni | Örnek metin "hukuki inceleme yapılmamıştır" diye işaretlidir; kurumun onaylı metni şablon olarak girilmelidir |
| Ekran okuyucu ve gerçek mobil cihaz | Denenmedi; klavye ile kullanım ve dar görünüm tarayıcıda yalnızca sınırlı incelendi |
| Sipariş, müşteri başlangıcı, destek/SLA, finans | Hedef modül olmadığı için uygulanamadı |
| Büyük hacim ve yük | Yük/performans testi yapılmadı; pano özetleri ilk 2000 yürürlükteki sözleşmeye, CSV 5000 satıra sınırlıdır |

## 8. Migration, yapılandırma ve dağıtım

1. `dotnet run --project src/Modulix.Api -- migrate` (tek zincir; dokuz sözleşme migration'ı, hepsi geriye uyumlu ve yalnızca ekler; yeni tablolar `TenantIsolationSql.AddTable` ile RLS'e girer). `dotnet ef database update` kullanılmaz.
2. `contracts` şeması ve uygulama rolü yetkisi: `docker/sqlserver/init.sql` ve ilk migration'daki idempotent GRANT (ayrıntı `tenant-isletim.md`).
3. Modülü tenant için aç (`platform.ModuleEntitlements`), hazır rolleri ata. Sözleşme ayarları: onay hedef süresi, kendi onayını kapalı tutma, imza sağlayıcısı (`none` varsayılan), hatırlatma eşikleri (`90,60,30,7`), **otomatik uzatma varsayılan kapalı**.
4. Zamanlanmış iş: `ContractJobs:WorkerEnabled` (varsayılan `true`), `ContractJobs:PollIntervalSeconds` (900). Birden çok örnek aynı tenant'ı aynı anda işlemez.
5. İmza webhook'u için sağlayıcı sırrı yapılandırması ve HMAC ayrıntısı README'dedir; sır koda yazılmaz.
6. CRM kapalı tenant'ta aktivite açılamaz: kayıt tutulur, durum `Unavailable` olur.
7. Geri alma: migration'lar yeni tablo ekler; modülü tenant için kapatmak ekranları ve uçları devre dışı bırakır, veri silinmez. Tenant verisi `tenant-export` ile alınır (sözleşme tabloları kendiliğinden kapsanır).

## 9. Açık riskler

- Zamanlanmış işin açtığı aktiviteler sözleşme kaydına bağlı değil; kullanıcı konudaki numaradan ulaşır.
- Yönetici hiyerarşisi olmadığından eskalasyon, sözleşmede adlandırılan kullanıcıya gider; kimse adlandırılmamışsa eskalasyon atlanır (kayıt `Skipped`).
- Otomatik uzatma imzalı sürümün dondurulmuş içeriğini güncellemez; dönem zinciri için yetkili kaynak `ContractRenewals` kayıtlarıdır.
- Fesih ile otomatik uzatma çakışması sürüm denetimine güvenir, ayrı testi yoktur.
- Ek protokol ayrı yürürlük tarihi taşımaz; gelecekte yürürlüğe girecek değişiklik yeni revizyon olarak izlenir.
- CSV durum ve türü anahtar olarak yazar, sorumluyu kimlik olarak yazar.
- Zararlı dosya taraması yoktur (dosya türü, imza ve boyut denetlenir; indirme her zaman ek olarak sunulur).
- Entegrasyon paketi yerelde ~13–15 dk, CI'da daha uzundur.

## 10. Kalan işler

Gerçek imza sağlayıcısı entegrasyonu; ek protokol türü ve yürürlük tarihi; sipariş/başlangıç/SLA/finans köprüleri (hedef modüller kurulunca; bağlantı sözleşmesi ve olaylar hazır); toplu işlemler ve paylaşılan görünüm; yönetici hiyerarşisi ve ekip sorumluluğu; kişi kartında taahhüt; imza zorunluluğunun türe göre yapılandırılması; kurum logosu, çok dilli şablon ve şablon içe/dışa aktarma; arayüz TR/EN çevirisi (ayrı iş kalemi); fesih × otomatik uzatma çakışma testi; PDF görsel regresyon testi; ekran okuyucu/mobil erişilebilirlik denetimi; saklama/arşiv politikası ve zararlı dosya taraması.

## 11. Durum tablosu

| Gereksinim | Backend | Frontend | Test/Doğrulama | Durum | Kanıt veya kalan engel |
|---|---|---|---|---|---|
| Veri modeli, tekil numara, taraflar, kalemler, hesap | Var | Var | Birim + entegrasyon | Tamamlandı | `ContractTests`, `ContractRulesTests` |
| Ayrı durum modelleri ve API kuralları | Var | Eylem çubuğu | Birim + entegrasyon | Tamamlandı | `ContractLifecycle*Tests` |
| Sürüm dondurma, karşılaştırma | Var | Sürümler sekmesi | Entegrasyon | Tamamlandı | `ContractLifecycleTests` |
| Ek protokol (ayrı tür, yürürlük tarihi) | Yok (`revise`) | Yok | — | Kısmen | Revizyonla karşılanır |
| Tekliften sözleşme | Var | Var | Entegrasyon | Tamamlandı | `ContractFromQuoteTests` |
| Şablon, belge, PDF | Var | Var | Entegrasyon | Tamamlandı (logo/çok dil yok) | `ContractTemplateTests` |
| İç onay, vekâlet, iş listesi | Var | Var | Entegrasyon + birim | Tamamlandı (hiyerarşi yok) | `ContractApprovalFlowTests` |
| İmza (manuel, sağlayıcı arayüzü, webhook) | Var | Var | Entegrasyon + birim | Tamamlandı (gerçek sağlayıcı Engelli) | `ContractSignatureFlowTests` |
| Taahhüt ve ihlal | Var | Var | Entegrasyon + birim | Tamamlandı (ekip sorumlusu yok) | `ContractCommitmentTests` |
| Yenileme, fesih, zamanlanmış iş | Var | Var | Entegrasyon + birim | Tamamlandı | `ContractRenewalTests` |
| Liste, görünümler, pano, dışa aktarma | Var | Var | Entegrasyon + birim | Tamamlandı (toplu işlem yok) | `ContractViewTests` |
| Oluşturma sihirbazı | Mevcut uçlar | Var | Tarayıcı | Kısmen | Dönemsel taahhüt/özel alan yok; bazı kollar denenmedi |
| Detay sekmeleri (ek protokol, ilişkili kayıtlar, aktiviteler) | — | Yok | — | Kısmen | Bölüm 6.2 |
| Firma kartı / fırsat izleme | Var | Var | Entegrasyon | Kısmen | Kişi kartı yok |
| Sipariş, müşteri başlangıcı, SLA, finans | Yok | Yok | — | **Engelli** | Hedef modül yok |
| Yetki, tenant izolasyonu, platform yöneticisi | Var | Var | Entegrasyon + RLS | Tamamlandı | Her dilimde 4 test türü |
| Dosya güvenliği | Tür/boyut/imza | — | Entegrasyon | Kısmen | Zararlı dosya taraması yok |
| Migration ve operasyon | Var | — | Yerel + CI | Tamamlandı | Üretim deploy yapılmadı |
| Erişilebilirlik | — | Etiket, rol, odak | Tarayıcı | Kısmen | Ekran okuyucu/mobil cihaz denenmedi |
| TR/EN arayüz çevirisi | — | Yok | — | Kapsam dışı | Ayrı iş kalemi (proje kuralı) |
| Müşteriye imza bağlantısı barındırma | — | — | — | Kapsam dışı | Kimlik doğrulama ve hukuki gereksinimler netleşmeden yapılmadı |
