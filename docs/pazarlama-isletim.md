# Pazarlama modülü — işletim notu (ADR-0025, dilim 9-10)

Bu not kampanya/segment modülünü işleten kişi içindir. Davranışın ayrıntısı README'de ("Pazarlama modülü", "İletişim tercihi ve engel"), kararlar `docs/adr/0025-kampanya-ve-segment-yonetimi.md` içindedir.

## 1. Gönderim hâlâ kapalıdır

- Gerçek e-posta/SMS/WhatsApp sağlayıcısı **yoktur**. Firma ayarı `marketing.provider` varsayılan "yok"tur; bu durumda çalıştırma başlatılamaz. Seçilebilen tek sağlayıcı iletiyi hiçbir yere göndermeyen test sağlayıcısıdır. Bu sürümde hiçbir ileti müşteriye ulaşmaz.
- Sağlayıcı bağlanmadan önce: İYS ve KVKK kararları (aydınlatma/rıza metni sürümü, İYS kaydı), `List-Unsubscribe` başlıklarının sağlayıcıda yazılması (RFC 8058), geri dönen ileti ve şikâyet olaylarının (webhook) engel olarak işlenmesi. Bunlar yapılmadan gerçek gönderim açılmamalıdır.
- **Gönderim işçisi (dilim 10):** kullanıcı bir çalıştırmayı "başlat" ile başlattıktan sonra sıradaki satırları arka plan işçisi gönderir (`MarketingSend:WorkerEnabled`, varsayılan açık). İşçi başlatmaz, onaylamaz; planlanan zamanda kendiliğinden başlama **yoktur**. Elle "sıradaki grubu işle" de çalışmaya devam eder.

## 2. Gerekli yapılandırma

| Ayar | Anlamı |
| --- | --- |
| `App:PublicUrl` | Abonelikten çıkma bağlantısı için zorunludur. Ayarlı değilse çalıştırma başlatılmaz ve işlenmez (özet "bağlantı üretilemiyor" der). |
| `marketing.provider` (firma ayarı) | Varsayılan `none`. |
| `marketing.frequency.daily-limit` (firma ayarı) | Adres başına günlük pazarlama iletisi sınırı, varsayılan 1; kampanyalar arasında ortak; 0 kapatır. |
| `marketing.approval.required` (firma ayarı) | Varsayılan evet; hazırlayan kendi çalıştırmasını onaylayamaz. |
| `MarketingSend:WorkerEnabled` / `PollIntervalSeconds` / `BusyIntervalSeconds` | Gönderim işçisi (varsayılan açık, boşta 15 sn, iş varken 2 sn). `BatchSize` (100), `MaxBatchesPerRun` (5) ve `MaxRunsPerTenant` (5) bir turda bir tenant'ın harcayacağı işi sınırlar. Birden çok uygulama örneği güvenle çalışır. |
| `CommunicationPublic:RateLimitPerMinute` | Oturumsuz tercih merkezi uçlarının IP+tenant başına dakikalık sınırı, varsayılan 30. |

## 3. Ölçülen performans (100.000 sentetik kişi)

Ölçüm: `MarketingScaleTests` (varsayılan olarak atlanır). Çalıştırma: `MODULIX_SCALE=1 dotnet test tests/Modulix.IntegrationTests -c Release --filter MarketingScaleTests` (isteğe bağlı `MODULIX_SCALE_PEOPLE`). Her kişi için kişi, iletişim kaydı, e-posta iletişim noktası ve pazarlama izni oluşturulur (küme tabanlı SQL, ~40 sn). Test süreleri çıktıya yazar; bütçeler yalnızca bariz gerilemeyi yakalayan geniş sınırlardır, **garanti değildir**.

Ortam: geliştirme bilgisayarı (Windows), SQL Server Docker kapsayıcısında, Release derleme, tek istek akışı, başka yük yok. Üretim donanımında sonuçlar farklı olacaktır.

| Adım | Ölçülen | Not |
| --- | --- | --- |
| Segment önizleme (sayı + ilk sayfa) | ~1,0 sn | 16.667 üyeli koşul |
| Segment yayımlama (hesaplama dahil) | ~1,9 sn | |
| Hedef kitle önizleme (alıcı çözümleme + izin/engel) | ~12,5 sn | 16.667 alıcı |
| Hedef kitle dondurma | ~6,4 sn | |
| Alıcı listesi ilk sayfa | ~2,0 sn | sayfa başına 100 |
| Sınırı aşan hedef kitlenin reddi | ~0,3 sn | 33.334 hedef |
| Çalıştırma başlatma (teslimat satırlarını açma) | ~24 sn | 16.667 satır |
| İşleme grubu (200 ileti) | ~2,6 sn (≈76 ileti/sn; dilim 10 öncesi ≈34) | adres çözme, izin/engel, toplu bağlantı üretimi, test sağlayıcısı |

Çıkarımlar:

- **Hedef kitle sınırı:** bir hedef kitle en fazla 20.000 hedefe çözülür; üstü "Segmenti daraltın" iletisiyle reddedilir (sessizce kesilmez). Daha büyük gönderimler için segmentleri bölün; sınırı artırmak ayrı bir ölçüm ister.
- **Gönderim hızı:** dilim 10 ile grup başına tek bağlantı üretimi ve tek günlük kaydı yapılır, sağlayıcı çağrıları sınırlı paralel çalışır: bir işleme çağrısı ≈ 76 ileti/sn (dilim 9: ≈ 34); 16.667 ileti ≈ 3,7 dakika, 100.000 ileti ≈ 22 dakika. Gerçek sağlayıcı gecikmesi bunun üstüne gelir (paralellik sabit 8; sağlayıcı hız sınırına göre ayarlanabilir değildir). Test sağlayıcısı anlıktır, bu yüzden ölçüm veritabanı yolunu ölçer; paralel sağlayıcı çağrısının kazancı gerçek sağlayıcıyla ayrıca doğrulanmalıdır.
- **Rapor** (dilim 8) her istekte hesaplanır, önbellek yoktur; 100.000 kişilik yanıt/lead hacminde ölçülmedi.

## 4. Veri büyümesi ve temizlik

- `core.CommunicationLinkTokens`: gönderim başına bir satır (400 gün ömürlü). **Temizlik işi vardır (dilim 12)**: süresi dolmasının üstünden 365 gün geçen satırlar silinir (`CommunicationCleanup:WorkerEnabled`, `IntervalMinutes`); 2,1 yıldan eski bir iletideki çıkış bağlantısı artık çalışmaz.
- `marketing.CampaignDeliveries`: çalıştırma başına alıcı sayısı kadar satır; silinmez (denetim izi). `marketing.AudienceSnapshots` alıcı listesini JSON olarak tutar (adres içermez).
- `core.FrequencyWindows`: adres × gün başına bir satır; 7 günden eskiler temizlik işiyle silinir (dilim 12).
- Tenant dışa aktarma/silme (`tenant-export`, `tenant-delete`) bu tabloları `TenantId` ile kapsar (envanter testi denetler).

## 5. Sorun giderme

| Belirti | Neden / çözüm |
| --- | --- |
| Çalıştırma başlatılamıyor, "sağlayıcı yapılandırılmamış" | `marketing.provider` ayarı; gerçek gönderim kapalıdır. |
| "Abonelikten çıkma bağlantısı üretilemiyor" | `App:PublicUrl` ayarlı değil. |
| "Sonuç N hedef; en fazla 20.000 kabul edilir" | Segmenti daraltın ya da bölün. |
| Alıcı "Atlandı: denied / unknown / suppressed:*" | İzin yok, reddedilmiş ya da engelli (İletişim tercihleri). Segmentte olmak izin vermez. |
| "Atlandı: frequency-limit" | Günlük sınır doldu (kampanyalar arasında ortak). |
| "Sonuç belirsiz" teslimat | Sağlayıcı kabulünden sonra yanıt kayboldu; **kör yeniden denenmez**. Sağlayıcı olayıyla netleştirilmeli (olay işleme henüz yok). |
| Entegrasyon testlerinde olay/teklif testleri rastgele kırılıyor | Aynı veritabanını kullanan bir API (ör. `modulix-api-1` konteyneri) çalışıyor; testlerden önce durdurun. |

## 6. Bilinen sınırlar (dürüst liste)

Gerçek sağlayıcı ve webhook, planlanan zamanda otomatik başlatma, özel alan değişkenleri, HTML içerik, gönderim penceresi, konuya özel çıkış bağlantısı, fırsat/ciro atfı, rapor dışa aktarma, alan düzeyi maliyet yetkisi, ölçeğin üretim donanımında doğrulanması. Hukuki uygunluk (KVKK/İYS/ETK) bu yazılımla garanti edilmez; hukuki karar ayrıca verilmelidir.
