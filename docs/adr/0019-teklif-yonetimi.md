# ADR-0019: Teklif yönetimi (M1.5) — analiz ve ilk dilim

Durum: Kısmen uygulandı (yalnızca saf çekirdek). Tarih: 2026-10-29.

## Mevcut durum (kodla kanıtlı)

Tarama: `src/**/*.cs` ve `src/Modulix.Web/Pages` içinde `Quote|Teklif|Proposal`. Sonuç: teklif varlığı, tablosu, uç noktası ve ekranı **yoktur**.
`Proposal` yalnızca fırsat aşaması anahtarıdır (`CrmModule.cs:321`), `Opportunity.Amount` tahmini tutardır (ADR-0017/7). Fiyat hesabı
(`IPriceCalculator`, `POST /api/crm/pricing/calculate`) satır başına çalışır ama sonucu saklayan belge yoktur (ADR-0018).
Tenant-başına-DB kabulü (ADR-0009) kodla uyumludur: yeni tablolar `ModulixDbContext`'e (tenant iş DB'si) eklenir.

| Süreç/Ekran | Mevcut | Eksik/risk | Uygulanacak | Öncelik | Bağımlılık | Kabul |
|---|---|---|---|---|---|---|
| Hesaplama | Satır fiyatı var, belge toplamı yok | Genel indirim, çoklu vergi, opsiyonel, tekrarlayan tutar tek yerde hesaplanmıyor | `QuoteCalculator` (yapıldı) | P0 | — | Birim testler (yapıldı) |
| Yaşam döngüsü | Yok | Onaysız sunum, geçersiz sürüm kabulü | `QuoteRules` (yapıldı) + iş akışı servisi | P0 | Veri modeli | Sunucuda engel + entegrasyon testi |
| Veri modeli | Yok | Snapshot, sürüm, aile, RowVersion | `Quote`, `QuoteLine`, onay/gönderim/kabul tabloları, migration + RLS | P0 | — | **Yapılmadı** |
| Revizyon / eşzamanlılık | Yok | Aynı ailede çelişen kabul | Aile başına tek kabul (filtreli benzersiz indeks) | P0 | Veri modeli | **Yapılmadı** |
| İç onay | Genel onay altyapısı yok (ADR-0018) | Eşik/kural yönetimi | Kural + istek tabloları | P1 | Veri modeli | **Yapılmadı** |
| PDF, paylaşım, müşteri kabulü | PDF/dosya altyapısı doğrulanamadı | Belirteç, hız sınırı, hash | Ayrı dilim | P1 | Veri modeli | **Yapılmadı** |
| Ekranlar (liste, düzenleme, detay, onay merkezi, karşılaştırma, müşteri sayfası, ayarlar, rapor) | Yok | — | `Pages/Crm/Quote*.razor` | P1 | API | **Yapılmadı** |
| Sipariş/sözleşme dönüşümü | Hedef modül yok | Sahte entegrasyon riski | Yalnızca entegrasyon sözleşmesi | P2 | Teklif kabulü | **Yapılmadı** |

## Bu dilimde yapılanlar

- `Modulix.Core.Contracts/Pricing/QuoteCalculator.cs`: sunucu tarafı tek hesap kaynağı. Sıra: satır tutarı → satır indirimi → genel indirim
  (indirim sonrası tutarla orantılı dağıtım, yuvarlama farkı en büyük satıra) → satır başı vergi → oran gruplarına özet. Vergi dahil/hariç;
  seçilmeyen opsiyonel/alternatif ana toplamdan ayrı (`OptionalNet`); paket bileşenleri toplanmaz; tek seferlik ve tekrarlayan tutarlar
  ayrı, sözleşme süresi bilinmeden genel toplam üretilmez. Oranlar dışarıdan gelir (mevzuat sabiti yok). 14 birim test.
- `Modulix.Modules.Crm/Quotes/QuoteRules.cs`: ticari durum, iç onay ve gönderim durumu ayrı sabitler; eylem ön koşulları
  (onaysız sunum engeli, kabul anında geçerlilik yeniden denetimi, geçersizleşmiş/iptal/sonuçlanmış sürümde kabul engeli, yalnızca taslakta
  ticari düzenleme) açıklama kodlarıyla. 14 birim test.

## Politika kararları (uygulanacak)

- Yeni **taslak revizyon açmak** önceki sunulmuş sürümü hemen geçersiz kılmaz; yeni sürüm **sunulduğunda** önceki `Superseded` olur
  (müşteri açıkken eski teklifi kabul edebilir, sunum anına kadar). Aile başına en fazla bir `Accepted` sürüm (benzersiz indeks).
- Kabul, durum güncellemesi `Version` koşuluyla atomik yapılır; kabul/iptal yarışında yalnızca biri başarılır, tekrar çağrı aynı sonucu döner.
- Sunulan sürümde fiyat, açıklama, vergi, kur ve koşullar kalem/başlıkta kopya olarak saklanır; katalog değişimi etkilemez.
- Maliyet/kârlılık ayrı izinle (`crm.quote-costs.read`) sunucuda süzülür; PDF ve dışa aktarma hiç okumaz.

## Yapılmayanlar (dürüst durum)

Veri modeli, migration, kayıt depoları, izinler, uç noktalar, onay kuralları, PDF, paylaşım/kabul, dönüşüm, ayarlar, raporlar ve
**tüm ekranlar** yazılmadı; entegrasyon testleri yok (SQL Server gerekir). Yol haritası M1.5 maddesi **tamamlandı işaretlenmedi**.
