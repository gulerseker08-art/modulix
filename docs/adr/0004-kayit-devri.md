# ADR-0004: Kullanıcı ayrılırken kayıt devri

- **Durum:** Kabul edildi, 24 Ekim 2026
- **Karar:** Modüller kişiye atanan kayıt türlerini bildirir (`IModule.OwnedRecords`); devri çekirdek, kayıt boru hattıyla yapar

## Problem

Bir kullanıcı devre dışı bırakıldığında sorumlu olduğu firma, fırsat ve aktiviteler sahipsiz kalmamalı (Y2b). Kayıtlar modüllerin tablolarındadır; mimari kural gereği çekirdek modül tablolarına erişemez, modüller de çekirdeğin servislerini yalnızca `Modulix.Core.Contracts` üzerinden görür. Yol haritasındaki K3 kararı, modüller arası her yeni sözleşme için ADR ister.

## Seçenekler

| Seçenek | Artı | Eksi |
| --- | --- | --- |
| Bildirim + boru hattı (seçildi) | Modül tek satır bildirim yazar, kod yazmaz; her kayıt için yetki, doğrulama, denetim kaydı ve olay normal düzenlemedeki gibi çalışır | Kayıt başına bir güncelleme; on binlerce kayıtta yavaş |
| Modülün uyguladığı arayüz (`CountOwned`, `Transfer`) | Modül toplu güncelleme yapabilir (hızlı) | Her modül devri yeniden yazar; denetim kaydı ve olay atlanabilir |
| Olay: çekirdek "kullanıcı ayrıldı" yayınlar, modüller dinler | Gevşek bağ | Devralan kişi seçimi ve "kaç kayıt var" sorusu olayla yanıtlanamaz; sonuç anında bilinmez |

## Karar

- `IModule.OwnedRecords`: kayıt türü, görünen ad ("firma") ve isteğe bağlı filtreler (ör. `open=true`). Varsayılanı boştur; bildirmeyen modül etkilenmez.
- Sözleşme: sorumlu alanı ve filtresi `ownerId` adındadır (`OwnedRecordType.OwnerField`), değeri kullanıcı kimliğidir.
- Çekirdek (`RecordHandover`) yalnızca tenant için açık modüllere bakar. Sayı için `ownerId` filtreli sorgu, devir için her kayda `{ "ownerId": yeni }` güncellemesi gönderir. İşlemi yapan kişinin okuma ve yazma izni gerekir; yoksa devir yapılmaz.
- Devir ve devre dışı bırakma tek veritabanı işlemindedir: biri başarısız olursa hiçbiri yapılmaz.
- CRM: firmalar, açık fırsatlar ve açık aktiviteler devredilir. Kapanmış fırsat ve tamamlanmış aktivite geçmiş olarak eski sorumlusunda kalır. Kişilerin sorumlusu yoktur.

## Sonuçlar

- Lojistik modülü sorumlu alanı olan kayıt eklediğinde yalnızca `OwnedRecords` listesine bir satır ekler.
- Kayıt başına güncelleme, tek kullanıcıda birkaç bin kayda kadar kabul edilebilir. Daha büyük devirler gerekirse devir arka plan işine taşınır; sözleşme değişmez.
- Devir, kaydın denetim kaydında işlemi yapan kişi adına "sorumlu değişti" olarak görünür.
