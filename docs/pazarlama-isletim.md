# Pazarlama modülü — işletim notu (ADR-0025, dilim 9)

Bu not kampanya/segment modülünü işleten kişi içindir. Davranışın ayrıntısı README'de ("Pazarlama modülü", "İletişim tercihi ve engel"), kararlar `docs/adr/0025-kampanya-ve-segment-yonetimi.md` içindedir.

## 1. Gönderim hâlâ kapalıdır

- Gerçek e-posta/SMS/WhatsApp sağlayıcısı **yoktur**. Firma ayarı `marketing.provider` varsayılan "yok"tur; bu durumda çalıştırma başlatılamaz. Seçilebilen tek sağlayıcı iletiyi hiçbir yere göndermeyen test sağlayıcısıdır. Bu sürümde hiçbir ileti müşteriye ulaşmaz.
- Sağlayıcı bağlanmadan önce: İYS ve KVKK kararları (aydınlatma/rıza metni sürümü, İYS kaydı), `List-Unsubscribe` başlıklarının sağlayıcıda yazılması (RFC 8058), geri dönen ileti ve şikâyet olaylarının (webhook) engel olarak işlenmesi. Bunlar yapılmadan gerçek gönderim açılmamalıdır.
- **Otomatik işçi yoktur:** çalıştırma elle "başlat" ve "sıradaki grubu işle" ile ilerler (planlanan zamanda kendiliğinden başlamaz).

## 2. Gerekli yapılandırma

| Ayar | Anlamı |
| --- | --- |
| `App:PublicUrl` | Abonelikten çıkma bağlantısı için zorunludur. Ayarlı değilse çalıştırma başlatılmaz ve işlenmez (özet "bağlantı üretilemiyor" der). |
| `marketing.provider` (firma ayarı) | Varsayılan `none`. |
| `marketing.frequency.daily-limit` (firma ayarı) | Adres başına günlük pazarlama iletisi sınırı, varsayılan 1; kampanyalar arasında ortak; 0 kapatır. |
| `marketing.approval.required` (firma ayarı) | Varsayılan evet; hazırlayan kendi çalıştırmasını onaylayamaz. |
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
| İşleme grubu (200 ileti) | ~6 sn (≈34 ileti/sn) | adres çözme, izin/engel, bağlantı üretimi, test sağlayıcısı |

Çıkarımlar:

- **Hedef kitle sınırı:** bir hedef kitle en fazla 20.000 hedefe çözülür; üstü "Segmenti daraltın" iletisiyle reddedilir (sessizce kesilmez). Daha büyük gönderimler için segmentleri bölün; sınırı artırmak ayrı bir ölçüm ister.
- **Gönderim hızı:** işleme satır satır ilerler (her ileti kendi kaydıyla). Bir işleme çağrısı ≈ 34 ileti/sn; 16.667 ileti ≈ 8 dakika, 100.000 ileti ≈ 50 dakika sürer. Gerçek sağlayıcı gecikmesi bunun üstüne gelir. Paralel işçi, toplu kayıt ve sağlayıcı çağrılarının eşzamanlılığı **yapılmadı**; büyük gönderimlerden önce yapılmalıdır.
- **Rapor** (dilim 8) her istekte hesaplanır, önbellek yoktur; 100.000 kişilik yanıt/lead hacminde ölçülmedi.

## 4. Veri büyümesi ve temizlik

- `core.CommunicationLinkTokens`: gönderim başına bir satır (400 gün ömürlü). **Temizlik işi yoktur**; gönderim hacmi büyüdükçe tablo büyür. Gerçek gönderim açılmadan temizlik (süresi dolanları silme) ve gerekirse belirteç yeniden kullanımı eklenmelidir.
- `marketing.CampaignDeliveries`: çalıştırma başına alıcı sayısı kadar satır; silinmez (denetim izi). `marketing.AudienceSnapshots` alıcı listesini JSON olarak tutar (adres içermez).
- `core.FrequencyWindows`: adres × gün başına bir satır; eski günler temizlenmez.
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

Gerçek sağlayıcı ve webhook, otomatik işçi, kişi bazlı değişken, HTML içerik, gönderim penceresi, konu bazlı tercih, yönetim ekranında tercih listesi, fırsat/ciro atfı, rapor dışa aktarma, alan düzeyi maliyet yetkisi, belirteç/sıklık satırı temizliği, ölçeğin üretim donanımında doğrulanması. Hukuki uygunluk (KVKK/İYS/ETK) bu yazılımla garanti edilmez; hukuki karar ayrıca verilmelidir.
