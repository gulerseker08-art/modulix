# ADR-0005: Hazır rol şablonları

- **Durum:** Kabul edildi, 24 Ekim 2026
- **Karar:** Modüller önerdikleri rolleri bildirir (`IModule.RoleTemplates`); "Salt okur" şablonunu çekirdek kendisi üretir

## Problem

Yeni bir firma yöneticisi rol ekranını boş bir izin matrisiyle karşılar (Y3). "Satış temsilcisi", "Operasyon" gibi hazır rollerle başlamak hem kurulumu hızlandırır hem yanlış yetkilendirmeyi azaltır. Hangi izinlerin bir rolü oluşturduğunu en iyi o izinleri tanımlayan modül bilir; çekirdek modüllerin izin anahtarlarını bilmemelidir. Yol haritasındaki K3 kararı, modüller arası her yeni sözleşme için ADR ister.

## Seçenekler

| Seçenek | Artı | Eksi |
| --- | --- | --- |
| Modül bildirimi (seçildi) | Şablon izinlerin yanında durur; modül kapalıysa şablon da görünmez; kod yazmadan tek satır | Birden fazla modülü kapsayan şablon bir modüle ait olamaz |
| Çekirdekte sabit liste | Tek yerde | Çekirdek modül izinlerini bilir; yeni modül çekirdeği değiştirmeyi gerektirir |
| Veritabanında platform şablonları | Platform yöneticisi değiştirebilir | Ekran ve tablo gerekir; Y4 öncesi erken |

## Karar

- `IModule.RoleTemplates`: anahtar (modül anahtarıyla başlar, ör. `crm.sales-rep`), ad, kısa açıklama ve izinler. Varsayılanı boştur.
- Şablon yalnızca kendi modülünün izinlerini içerir; katalog açılışta bunu ve anahtarı denetler, uymazsa uygulama açılmaz.
- Çekirdek her tenant için bir "Salt okur" şablonu (`core.read-only`) ekler: açık modüllerin tüm görüntüleme izinleri, yönetim izni olmadan. Modüller arası şablonun tek örneği budur ve kuraldan (".read" ile biten izinler) üretilir.
- Şablon yalnızca formu doldurur: şablondan oluşturulan rol sıradan bir roldür, şablon sonradan değişirse rol değişmez. Yönetici olmayan kişi şablonu kullanırken kendinde olmayan izinler forma eklenmez (Y3 yetki kuralı).
- İlk şablonlar: CRM "Satış temsilcisi" (tüm CRM kayıtları, görüntüleme ve düzenleme), Lojistik "Operasyon" (sevkiyat ve taşıyıcı düzenler, maliyet görüntüler).

## Sonuçlar

- Rol listesinde yalnızca Yönetici rolü varken şablonlar kart olarak önerilir; yeni rol formunda "Şablondan başla" seçimi vardır (`GET /api/roles/templates`).
- Firma açılırken şablon önerilmesi firma oluşturma ekranıyla birlikte gelir (Y4); aynı uç noktayı kullanır.
- Sektöre özel şablonlar (Y9) gerekirse aynı sözleşmeye sektör etiketi eklenir.
