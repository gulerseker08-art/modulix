# ADR-0036: Kişisel liste görünümleri

Durum: Kabul edildi (5.11.2026)

## Bağlam

Liste ekranları (grid) ortak bir şablona taşınıyor (`docs/design-system/component-catalog.md`, `ViewTabs`). Kullanıcılar, bir listede hangi sütunları
hangi sırayla gördüklerini, hangi sıralamayı ve süzgeci kullandıklarını **kendileri** ayarlayıp kaydedebilmeli ve bu görünümü her cihazda
bulmalı. Mevcut durum yetersizdi: `ColumnPicker` sütun seçimini yalnızca tarayıcıda (`localStorage`, sayfa yoluna göre) tutuyordu; sıralama ve sıra
yoktu; kayıtlı görünüm yalnızca Sözleşmeler modülünde (`ContractSavedView`, yalnızca süzgeç) vardı ve modüle özeldi.

## Karar

1. **Ortak, modülden bağımsız tek tablo:** `core.GridViews` (tenant verisi, `ITenantEntity` + `ICompanyShared`, RLS altında). Satır: üyelik
   (`MembershipId`, `platform.Memberships`'e bileşik yabancı anahtar), ekran anahtarı (`ScreenKey`, ör. `crm.companies`), ad, sıralı sütun anahtarları,
   sıralama anahtarı ve sorgu dizgisi biçiminde süzgeç. Sürüm (`Version`) eşzamanlı düzenleme denetimi içindir. Ad, üyenin aynı ekranındaki
   görünümler arasında tekildir; ekran başına en fazla 20 görünüm.
2. **Kişiseldir:** her sorgu oturumdaki üyeliğe göre süzülür; başkasının görünümü ne okunur ne değiştirilir (aynı tenant'ta da "bulunamadı").
   Paylaşılan/takım görünümü bu kararın kapsamı dışındadır (ileride ayrı bir karar).
3. **Yetki vermez:** görünüm yalnızca sunumu seçer. Liste uç noktası her açılışta kullanıcının izinlerini, veri kapsamını ve alan seviyesi
   yetkisini yeniden uygular; sunucu `sort` anahtarını kendi izin verilen listesine göre doğrular. Bu yüzden `/api/grid-views` ek bir izin istemez,
   yalnızca oturum ve aktif tenant ister.
4. **Sunucu sütun listesini bilmez:** sütun anahtarları ekranın kendi tanımından gelir; sunucu yalnızca biçimini doğrular (harf/rakam/nokta,
   tekrarsız, en çok 40). Ekranda artık bulunmayan bir anahtar arayüzde yok sayılır. Süzgeç yalnızca `anahtar=değer` çiftleridir; `page`,
   `pageSize`, `search` ve `sort` içeremez.
5. **Denetim kaydı yazılmaz:** kişisel tercihtir, iş verisi ya da yönetim işlemi değildir.
6. **Sıralama sunucudadır:** `CrmRecordStore` artık Siparişler/Sözleşmeler gibi `SortKeys` + `Order(source, sort)` ile `sort` anahtarını kabul eder;
   her başlık, deposunun izin verdiği anahtarlarla sıralanır.

## Sonuçlar

- Tenant dışa aktarma ve silme tabloyu veritabanı kataloğundan otomatik bulur (ek iş yok); RLS ve tablo envanteri testleri tabloyu kapsar.
- `ColumnPicker` (tarayıcıya kayıtlı, DOM tabanlı) yerine, kişisel görünümü olan ekranlar sütunlarını `GridColumnPicker` ile sunucu görünümüne bağlar.
  **7 Kasım 2026:** son üç ekran (Otomasyon kuralları, Onay süreçleri, Onay merkezi) ve Aktiviteler (tarayıcıda saklanan görünümleri) taşındı;
  eski `ColumnPicker` ve `js/table-columns.js` silindi. Otomasyon kural listesi sunucuda `sort` aldı; Onay süreçleri sayfasız tek liste
  olduğundan sıralama istemcidedir. Aktiviteler sunucuda sıralanmaz (takvim kipleri tarih penceresiyle çalışır); görünüm sütun, kip ve süzgeçleri saklar.
- Sözleşmelerdeki `ContractSavedView` **7 Kasım 2026**'da ortak tabloya taşındı: `ContractSavedViewsToGridViews` migration'ı eski satırları
  `core.GridViews`'a (ekran `contracts.contracts`, varsayılan sütunlar, süzgeçteki `sort` ayrı sütuna, `search` atılır, ad 60 karaktere kısaltılır,
  üye başına 20 sınırı) kopyalar ve tabloyu kaldırır; `/api/contracts/saved-views` ucu yoktur.
- Her ekran kendi `ScreenKey`'ini ve sütun tanımını verir; yeni ekran bu mekanizmayı bedelsiz kullanır.
