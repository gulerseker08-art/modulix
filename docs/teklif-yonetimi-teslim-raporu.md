# Teklif Yönetimi (M1.5) — Teslim Raporu

Tarih: 30 Ekim 2026. Kapsam: "6. Teklif Yönetimi" talimatı. Mimari karar ve dilim ayrıntıları [ADR-0019](adr/0019-teklif-yonetimi.md); uç nokta ve davranış özeti README "Teklif yönetimi" bölümünde.

Bu rapor yalnızca yapılan ve doğrulanan işleri "tamamlandı" sayar. Koşulmayan test, denenmeyen ekran ve kurulmayan özellik açıkça yazılıdır.

## 1. Bulunan eksikler ve yapılanlar

Başlangıçta teklif modülü yoktu (ADR-0019 analiz tablosu). Yedi ana ve beş tamamlayıcı dilimde kuruldu:

| Dilim | PR | İçerik |
|---|---|---|
| 1 Çekirdek | 57 | Sunucu hesap motoru (`QuoteCalculator`), geçiş kuralları, ADR analizi |
| 2 Veri modeli ve yaşam döngüsü | 58 | `Quotes/QuoteLines/QuoteEvents`, kayıt depoları, iş akışı, izinler, ilk ekranlar |
| 3 PDF ve müşteri | 59 | Sabit PDF (QuestPDF, SHA-256), paylaşım bağlantısı, oturumsuz müşteri sayfası, kabul/ret kanıtı |
| 4 Onay kuralları ve karşılaştırma | 60 | Yapılandırılabilir kurallar, adlı onaylayan, revizyon karşılaştırma |
| 5 E-posta | 61 | Müşteriye bağlantı e-postası, dürüst gönderim durumu (`Succeeded` = sağlayıcı kabul etti) |
| 6 Raporlar | 62 | Teklif raporu, CSV dışa aktarma |
| 7 Kabul sonrası | 63 | Devir sözleşmesi (`IAcceptedQuoteSource`), fırsat kazanma politikası, takip/hatırlatma, süre dolumu işi |
| CI düzeltmeleri | 64, 65, 66 | Geçersiz iş akışı dosyası (tüm CI 25 Ekim'den beri kırıktı), test temizlik hatası, çift çalışma, kararsız test, süre sınırı |
| 8 Küçük tamamlamalar | 67 | Teklife bağlı aktiviteler ve otomatik kapatma, sorumluya e-posta, iş kilidi |
| 9 Fırsat entegrasyonu | 68 | Esas teklif, ayara bağlı tutar eşitleme |
| 10 Çok adımlı onay | 69 | Sıralı/paralel adımlar, vekâlet, onay hatırlatması |
| 11 Marj raporu | 70 | Maliyet izniyle marj raporu |
| 12 Erişilebilirlik | 71 | Tarama, tablo/aria düzeltmeleri, yol haritası M1.5 durumu |

PR 57–66 main'e birleştirildi (main CI yeşil). PR 67–71 açık ve yığılı (67 → 68 → 69 → 70 → 71).

## 2. Veri modeli ve geçişler (özet)

- Tenant veritabanında (RLS'li): `Quotes`, `QuoteLines`, `QuoteEvents`, `QuoteDocuments`, `QuoteShareLinks`, `QuoteDecisions`, `QuoteApprovalRules`, `QuoteApprovalDelegations`; `Opportunities.PrimaryQuoteId`. Platform veritabanında `EmailMessages.Reference`.
- Ticari durum `Draft → Presented → Accepted | Rejected | Expired | Cancelled | Superseded`; iç onay `NotRequired | Pending | Approved | Rejected | ChangesRequested`; gönderim `NotSent | Pending | Processing | Succeeded | Failed` — üçü ayrı alandır.
- Revizyon ailesi: yeni sürüm sunulunca eski `Superseded`; aile başına tek `Accepted` (filtreli benzersiz indeks); sunulmuş sürüm yerinde değişmez; ticari değişiklik onayı geçersiz kılar; tüm geçişler sürüm denetimli (409).

## 3. Politikalar

- Hesap yalnızca sunucuda: satır indirimi → genel indirim dağıtımı → satır başı vergi; 2 ondalık; opsiyonel/alternatif ana toplam dışı; politika sürümü teklifte saklanır. Kabul örneği 2 × 1.000, %10, %20 → 1.800 / 2.160 testle doğrulandı; %20 test verisidir.
- Onay: genel eşik ayarı + kurallar (VE/VEYA), adımlar sırayla, adlı onaylayanların hepsi, vekâlet "adına" sayılır; kendi teklifini onaylama varsayılan kapalı; kural sürümü tarihçede.
- Kabul: müşteri sayfasında açık eylem, onay cümlesi, görülen belgenin özeti; geçerlilik kabul anında yeniden denetlenir; eşzamanlı kararda tek kazanan.
- Güvenlik: belirteç yalnızca bir kez görünür, DB'de özeti; URL parçasında taşınır; hız sınırı, tek tip 404; maliyet ve iç not hiçbir müşteri çıktısına, rapora, dışa aktarmaya girmez.
- Varsayılan kapalı ayarlar (davranış değişmez): fırsat kazanma, tutar eşitleme, takip/hatırlatma günleri, onay hatırlatması.

## 4. Ekranlar, yollar, API

Ekranlar: `crm/teklifler`, `crm/teklifler/yeni`, `crm/teklifler/{id}`, `crm/teklifler/{id}/karsilastir`, `crm/teklif-onaylari`, `crm/teklif-kurallari`, `crm/onay-vekaletleri`, `crm/teklif-raporu`, fırsat detayında "Teklifler", müşteri sayfası `/teklif/{firma}#t=...`. API listesi README'de.

## 5. Testler ve sonuçlar

| Test | Adet | Son sonuç |
|---|---|---|
| `QuoteCalculatorTests` (Core) | 14 | Geçti |
| `QuoteRulesTests`, `QuoteSharingRulesTests`, `QuoteApprovalAndComparisonTests`, `QuoteApprovalStepsTests` (CRM birim) | 206 toplam | Geçti |
| `QuoteTests`, `QuoteSharingTests`, `QuoteRuleTests`, `QuoteDeliveryTests`, `QuoteReportTests`, `QuotePostAcceptTests`, `QuoteOpportunityTests`, `QuoteMultiStepApprovalTests` (entegrasyon) | 12+10+5+3+4+6+2+4 | Geçti |
| Tam entegrasyon paketi | 360 | Son tam koşu PR 61 durumunda yerelde 360/360; CI'da main zinciri (PR 63 dahil) yeşil |
| Mimari testler | 29 | Geçti |

PR 67–71 için tam paket koşulmadı; ilgili kümeler koşuldu (sonuçlar PR açıklamalarında). CI her PR'ı tabanı main'e çevrilince doğrular.

## 6. Görsel denetim

Tarayıcıda denendi: liste, taslak/kalem/sunum akışı (1.800/360/2.160), mobil 375 px, onay merkezi, belge ve paylaşım paneli, müşteri sayfası (onay kutusuz hata, kabul), kural formu ve adım alanı, vekâlet formu, revizyon karşılaştırma, fırsat detayı esas teklif yap/kaldır, rapor (marj bölümü dahil), e-posta gönderiminin Mailpit'e düşmesi. PDF PNG çıktısıyla incelendi (Türkçe karakterler, tablo, toplamlar). Erişilebilirlik ağacı tarandı.

Denenmedi: gerçek klavye tuşu gezintisi (panel çizilmedi), ekran okuyucu, kontrast ölçümü, tablet, yetkisiz kullanıcı ekranları (API seviyesinde test var).

## 7. Çalıştırılamayanlar ve nedeni

- Gerçek müşteriye e-posta: geliştirmede SMTP Mailpit; teslim/okundu bilgisi SMTP'den alınamaz.
- Sipariş/sözleşme oluşturma: hedef modül yok; yalnızca devir sözleşmesi (`IAcceptedQuoteSource`).
- QuestPDF lisansı: Community lisansı yıllık geliri 1 milyon USD altı şirketlere ücretsizdir; uygunluk ürün sahibinin kararıdır.

## 8. Migration ve yapılandırma

Tenant: `QuoteManagement`, `QuoteSharing`, `QuoteApprovalRules`, `OpportunityPrimaryQuote`, `QuoteApprovalSteps` (`-- migrate-tenants`). Platform: `EmailReference` (`-- migrate`). Yapılandırma: `QuotePublic:RateLimitPerMinute` (60), `QuoteExpiry:WorkerEnabled` (true) / `PollIntervalSeconds` (300). Tenant ayarları "CRM Teklif" grubunda. Üretim migration'ı ve deploy yapılmadı.

## 9. Çapraz modül tutarlılığı

Fırsat: esas teklif ve tutar eşitleme ayarlı; kabulde kazanma ayarlı; tarihçe ve audit kayıtları var. Aktivite: tekliften açılan aktiviteler `ActivityLink` ile bağlı, elle açılanlara dokunulmaz. Katalog: kalemde kopya (kod/ad/fiyat/vergi/maliyet); katalog değişse de teklif değişmez. Müşteri: sunumda ünvan sabitlenir. E-posta: platform kuyruğu, `Reference` ile izleme.

## 10. Kalan işler

Sipariş/sözleşme köprüsü (M1.6), e-posta teslim/bounce (sağlayıcı), rol/grup tabanlı onay ve yükseltme, marj eşiğine bağlı onay, belge şablonu özelleştirme/çok dilli, kur sabitleme ekranı, kayıtlı görünümler, rapor anlık görüntüsü, CI testlerini hızlandırma (sınıf başına paylaşılan tenant; şu an 20–25 dk), `PlatformAdministrationTests.Catalog_lists...` kararsızlığı (paralel tenant sayımı).

## 11. Durum tablosu

| Gereksinim | Backend | Frontend | Test/Doğrulama | Durum | Kanıt veya kalan engel |
|---|---|---|---|---|---|
| Tek hesap motoru, yuvarlama, dahil/hariç, opsiyonel/paket/tekrarlayan | Var | Toplamlar sunucudan | 14 birim + entegrasyon | Tamam | `QuoteCalculatorTests`, `QuoteTests` |
| Ayrı ticari/onay/gönderim durumları, kurallar API'de | Var | Rozetler metinli | Entegrasyon | Tamam | `QuoteTests`, `QuoteDeliveryTests` |
| Revizyon ailesi, tek kabul, atomik yarış | Var | Revizyon listesi, karşılaştırma | Entegrasyon | Tamam | `QuoteTests`, `QuoteRuleTests` |
| Yapılandırılabilir onay, çok adım, vekâlet, hatırlatma | Var | Kural/vekâlet/onay merkezi | Entegrasyon + birim | Tamam | PR 60, 69; rol/grup yok |
| PDF, hash, şablon sürümü | Var | Belge paneli | Entegrasyon + PNG denetimi | Tamam | PR 59; şablon özelleştirme yok |
| Paylaşım bağlantısı, müşteri sayfası, kabul kanıtı | Var | Müşteri sayfası | Entegrasyon + tarayıcı | Tamam | PR 59 |
| E-posta gönderimi ve dürüst durum | Var | Panelde durum | Entegrasyon + Mailpit | Tamam (teslim bilgisi yok) | PR 61 |
| Fırsat/aktivite/kabul sonrası entegrasyon | Var | Fırsat detayı Teklifler | Entegrasyon + tarayıcı | Tamam (ayarlı, varsayılan kapalı) | PR 63, 67, 68 |
| Sipariş/sözleşme köprüsü | Sözleşme var | Yok | Entegrasyon (handover) | Kısmen | Hedef modül yok (M1.6) |
| Raporlar, dışa aktarma, marj | Var | Rapor sayfası | Entegrasyon + tarayıcı | Tamam | PR 62, 70 |
| İzinlerin API/PDF/export/lookup/iş'te zorlanması, maliyet sızıntısı yok | Var | Gizli alanlar | Entegrasyon | Tamam | `QuoteSharingTests`, `QuoteReportTests` |
| Ekranlar 11.1–11.11 | — | Var | Tarayıcı (ana akışlar) | Büyük ölçüde | Klavye/ekran okuyucu denemesi yok |
| Migration ve operasyon | Var | — | Yerel + CI | Tamam | Üretim deploy yapılmadı |
| CI | Düzeltildi | — | main yeşil | Tamam | Süre 20–25 dk, hızlandırma kalan iş |
