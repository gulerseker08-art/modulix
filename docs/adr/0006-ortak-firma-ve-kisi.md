# ADR-0006: Ortak firma ve kişi kaydı

- **Durum:** Kabul edildi, 25 Ekim 2026
- **Karar:** Firma (Taraf) ve kişi çekirdekte tek kayıttır; modüller kendi bilgilerini bu kayda bağlı uzantı tablolarında tutar, ortak kaydın sınıfları ve bildirimleri `Modulix.Core.Contracts`'tadır

## Problem

Yol haritasındaki K10 kararı: bir kavram bir kez tanımlanır. CRM'de açılan müşteri Lojistik, Antrepo ve Muhasebe'de yeniden açılmamalı; sisteme kullanıcı olarak eklenen kişi İK'da yeniden tanımlanmamalı.

Bugünkü durum buna izin vermiyor:

- Firma ve kişi yalnızca CRM'de var (`crm.Companies`, `crm.Contacts`). Mimari kural gereği diğer modüller CRM'e başvuramaz; CRM'in tablolarını göremez.
- Kayıt referansı (`Field.Reference`) yalnızca aynı modülün tablosunda varlık denetler.
- Kullanıcı hesabı platform düzeyindedir (`platform.Users`) ve birden fazla firmada kullanılır; adını ve e-postasını kişi kendisi değiştirir (Y7a). Bir firmadaki çalışan, sürücü veya müşteri yetkilisi ise o firmaya özgü bir kayıttır.
- Örnek veride Beta'da yalnızca Lojistik açıktır: müşteriyi CRM'siz de açabilmek gerekir.

Yol haritasındaki K3 kararı, modüller arası her yeni sözleşme için ADR ister.

## Seçenekler

| Seçenek | Artı | Eksi |
| --- | --- | --- |
| Çekirdekte ortak kayıt + modül uzantıları (seçildi) | Tek kayıt, tek doğruluk; modüller birbirini bilmez; CRM kapalıyken de çalışır | Çekirdekte yeni sözleşme; mevcut CRM verisinin taşınması |
| Her modül kendi kaydını tutar, olaylarla eşitlenir | Modüller bağımsız | Aynı firma birden fazla kez girilir; eşitleme gecikir ve çakışır; tekilleştirme her modülde ayrı |
| CRM ana veri sahibi olur, diğerleri CRM'e başvurur | Yeni tablo az | Modüller arası bağımlılık (mimari kurala aykırı); CRM'i olmayan firma müşteri açamaz |
| Ortak kayıt modül alanlarını da tutar (geniş tablo veya JSON) | Tek tablo | Çekirdek modül alanlarını bilir; yetki alan bazında karmaşıklaşır; her modül çekirdeği değiştirir |

## Karar

### Ortak kayıtlar

Çekirdek (`core` şeması, RLS'li) iki kayıt türü sunar. Kayıt boru hattından geçer; genel uçtan da erişilir (`/api/records/core.party`, `/api/records/core.person`).

| Kayıt | Alanlar | Kural |
| --- | --- | --- |
| **Taraf** (`core.party`) | tür (tüzel/gerçek kişi), ünvan, vergi dairesi, vergi no veya TCKN, telefon, e-posta, web sitesi, adres, şehir | Aynı tenant'ta aynı vergi no / TCKN ile ikinci taraf açılamaz (422, `taxNumber`). |
| **Kişi** (`core.person`) | ad, soyad, e-posta, telefon, unvan, çalıştığı taraf (isteğe bağlı); sonra bağlı üyelik ve TCKN | Bir üyeliğe en fazla bir kişi bağlanır. Çalıştığı taraf boşsa kişi firmanın kendi personeli veya bağımsızdır. TCKN İK ile gelir ve hassas alandır (Y8b). |

Adres ilk aşamada düz metindir; yapılandırılmış ve çoklu adres Ç3b'de gelir.

### Kişi ve kullanıcı

- Hesap (`platform.Users`) kimlik ve giriş içindir, kişi (`core.person`) firmadaki kayıttır. Kişi bir üyeliğe bağlanınca kullanıcı olur.
- Bağlı kişide ad, soyad ve e-posta hesaptan gelir ve kişi kaydında salt okunurdur; hesabın sahibi Hesabım'dan değiştirir, değişiklik bağlı olduğu bütün firmalardaki kişi kaydına yansır. Telefon, unvan ve diğer alanlar firmaya özgüdür.
- Davet mevcut bir kişiden yapılabilir ("Kullanıcı yap"). E-postayla davet edilen kişi firmada zaten varsa (aynı e-posta) yeni kişi açılmaz, mevcut kişi bağlanır; yoksa davetle birlikte kişi oluşur.
- Kayıtlardaki sorumlu alanları (`ownerId`) kullanıcı kimliği tutmaya devam eder. ADR-0004 ve kayıt kapsamı (Y8a) değişmez.
- Üyelik devre dışı kalınca kişi kaydı ve bağlantısı kalır; kişi İK'da çalışan olarak görünmeye devam eder.

### Modül uzantıları

- Modül yalnızca kendine özgü bilgiyi kendi şemasında, ortak kaydın kimliğiyle (`PartyId`, `PersonId`) tutar. Ortak alanlar uzantıda tekrarlanmaz.
- Rol ayrı bir etiket değildir, uzantının varlığından anlaşılır: CRM uzantısı olan taraf "CRM firması", Lojistik taşıyıcı uzantısı olan taraf "taşıyıcı", İK çalışan uzantısı olan kişi "çalışan"dır. Aynı taraf veya kişinin birden fazla uzantısı olabilir.
- Uzantı ve ortak kayıt dışarıya tek kayıt olarak verilir: `crm.company` bugünkü alanlarını (ad, vergi no, telefon…) aynen döndürür ve kabul eder, yanına `partyId` eklenir. Mevcut CRM API'si ve ekranları değişmez.
- Ortak kaydın entity sınıfları (`Party`, `Person`) Contracts'tadır; tabloları çekirdekte yapılandırılır. Uzantı entity'si ortak kayda navigation ile bağlanır (`Company.Party`) ve deposu ortak alanları bu navigation üzerinden okur ve yazar (`IModuleData`). Uzantı ve ortak kayıt aynı işlemde kaydedilir ya da hiçbiri; ikisinin de sürümü vardır, başka bir modülün araya giren değişikliği 409 verir.
- Uzantı oluşturulurken mevcut bir ortak kayda bağlanabilir (`partyId` / `personId`, yalnızca oluştururken; sonra değişmez). Bağlanırken boş gönderilen ortak alanlar mevcut değeri silmez.
- Örnekler:

| Modül | Uzantı | Tuttuğu bilgi |
| --- | --- | --- |
| CRM | `crm.company` → Taraf | durum, kaynak, sektör, etiketler, sorumlu, notlar, özel alanlar |
| CRM | `crm.contact` → Kişi | notlar, özel alanlar |
| Lojistik | müşteri, taşıyıcı → Taraf | varsayılan tarife; taşıyıcının araçları |
| Lojistik | sürücü → Kişi | ehliyet ve SRC bilgisi |
| İK | çalışan → Kişi | işe giriş, pozisyon, ücret |
| Muhasebe | cari → Taraf | hesap kodu, vade, risk limiti |

### Yetki

- Modül, ortak kayıt türlerinden hangisini hangi iznine bağlı olarak okuyup yazdığını `IModule` üzerinden bildirir (ör. CRM: Taraf okuma `crm.companies.read`, yazma `crm.companies.write`; Lojistik: Taraf okuma `logistics.shipments.read`). Bildirim, ADR-0004 ve ADR-0005'teki gibi tek satırdır; katalog açılışta iznin modülün kendi izni olduğunu denetler.
- Ortak kaydı, tenant için açık modüllerden birinde bildirilen okuma iznine sahip olan okur; yazma için yazma izni gerekir. Yönetici her şeyi görür.
- Uzantı verisi yalnızca kendi modülünün izniyle görünür (ör. ücret yalnızca İK izniyle). Hassas alan kuralı (Y8b) ortak kayıtta da geçerlidir.
- Kayıt kapsamı (Y8a) uzantıya uygulanır, ortak kayda uygulanmaz: "yalnızca kendi kayıtları" kapsamındaki satış temsilcisi CRM listesinde yalnızca kendi firmalarını görür, ama yeni firma eklerken (benzer kayıt uyarısı) veya başka bir modülde (ör. sevkiyat açarken) seçim kutusunda bütün tarafları adıyla bulabilir.

### Arama, tekilleştirme ve birleştirme

- Seçim kutuları için ortak arama: ad, vergi no, e-posta, telefon (`/api/records/core.party?search=`).
- **Vergi numarası kesin eşleşmedir:** yeni uzantı, aynı vergi numaralı bir tarafla oluşturulursa yeni taraf açılmaz, mevcut tarafa bağlanır (ör. Lojistik'te açılmış müşteri CRM'e eklenir). O tarafın aynı türden uzantısı zaten varsa 422 (`taxNumber`).
- Oluşturmadan önce olası eşleşmeler gösterilir (benzer ad, aynı e-posta/telefon); kullanıcı mevcut kaydı seçerse yeni uzantı ona bağlanır.
- **Birleştirme:** iki taraftan veya iki kişiden biri seçilir, diğerinin uzantıları ve referansları ona taşınır, eski kayıt silinir. Modüller ortak kayda başvuran alanlarını `IModule` üzerinden bildirir; taşıma ADR-0004'teki gibi bu bildirimle ve kayıt boru hattından yapılır, tek işlemdir, denetim kaydına yazılır. Aynı türden iki uzantı çakışırsa (iki CRM firması) kullanıcı hangisinin kalacağını seçer.

### Silme ve modül kapatma

- Uzantı silinince ortak kayıt kalır. Ortak kayıt, bildirilmiş hiçbir uzantı veya referans yoksa silinebilir; varsa 409 ve hangi modülde kaç kayıtta kullanıldığı.
- Modül kapatılınca uzantısı ve verisi kalır, ortak kayıt diğer modüllerde kullanılmaya devam eder.
- Kişisel veri silme talebi (KVKK) anonimleştirme olarak Ç13b'de gelir.

### Denetim ve olaylar

- Değişiklik, hangi kayıt üzerinden yapıldıysa onun denetim kaydına yazılır: CRM firma formundan değişen ünvan `crm.company` kaydında, ortak kayıt ekranından (veya başka bir modülden) değişen ünvan o kaydın denetim kaydında görünür. Uzantının "Değişiklik geçmişi"nde bağlı ortak kaydın geçmişinin de gösterilmesi sonraki adımdır.
- Ortak kayıt değişince olay yayınlanır (Ç1); modüller gerekiyorsa dinler (ör. Muhasebe ünvan değişikliğini not eder).

## Geçiş

1. Çekirdek tabloları, entity sınıfları, bildirimler ve genel uç (`core.party`, `core.person`).
2. Migration: her CRM firması için Taraf, her CRM kişisi için Kişi oluşur (kimlikleri aynı kalır). CRM'de vergi numarası zaten tekil olduğu için çakışma olmaz. Taşınan alanlar CRM tablolarından kaldırılır.
3. CRM deposu ortak alanları ortak kayıttan okur ve yazar; API değişmez.
4. Sonraki adımlar: üyelikler için Kişi, kullanıcı daveti ve kullanıcı ayrıntısının kişiyle bağlanması; benzer kayıt uyarısı; birleştirme aracı; uzantı geçmişinde ortak kaydın geçmişi.

## Sonuçlar

- Lojistik, Antrepo, İK ve Muhasebe yeni firma veya kişi tablosu açmaz; uzantı ekler ve iki satır bildirim yazar.
- CRM'i olmayan firma müşteriyi Lojistik ekranından açar; sonradan CRM açılırsa aynı taraf CRM'e eklenir.
- Ortak kayıt üzerinden her modül her müşterinin adını görebilir. Firma adı ve vergi numarası tenant içinde gizli bilgi sayılmaz; iletişim dışındaki ticari bilgiler (sorumlu, notlar, tutarlar) uzantıda kalır.
- Uzantı ile ortak kaydı birleştiren sorgular iki tabloya bakar; ad ve vergi no aramaları için ortak kayıtta indeks gerekir.

## Kabulde verilen kararlar

- **Ortak alanı kim düzenler:** ortak kaydı yazma izni bildiren herhangi bir modülde yazma izni olan düzenler (ör. CRM'deki satış temsilcisi, Lojistik'te de kullanılan müşterinin ünvanını değiştirebilir). Muhasebe geldiğinde, faturası kesilmiş carinin ünvan ve vergi bilgisinin değişmesi onaya bağlanabilir.
- **Kapsam:** "yalnızca kendi kayıtları" kapsamındaki kişi seçim kutusunda bütün tarafların adını görür; aynı firmanın ikinci kez açılmasını önlemek bundan önemlidir.
