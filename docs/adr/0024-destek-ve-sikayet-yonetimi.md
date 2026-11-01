# ADR-0024 — Destek ve şikâyet yönetimi (EPIC 10)

Durum: Kabul edildi (1 Kasım 2026). Uygulama durumu en sondaki bölümlerde; yalnızca kodda ve testte doğrulananlar "yapıldı" sayılır. Epic **dilimlenmiştir**: bu belge tüm epic'in analizini ve tasarımını, yalnızca **dilim 1**'in uygulandığını içerir.

Veritabanı modeli ADR-0002 ve ADR-0020'ye uyar: **tek paylaşımlı veritabanı**, her destek tablosu `TenantId` taşır, EF query filter ve SQL Server Row-Level Security ile korunur. Tenant istemciden değil oturumdan çözülür; bağlam yoksa işlem reddedilir, varsayılan tenant'a düşülmez. Bu epic tenant başına veritabanı, tenant bazlı bağlantı dizesi ya da ayrı migration orkestrasyonu **varsaymaz ve tasarlamaz**.

## 1. İstek (prompt) uygunluk değerlendirmesi

İstek, Modulix'in kurallarıyla büyük ölçüde örtüşüyor. Aşağıdaki tablo her ana maddeyi `CLAUDE.md` ve mevcut kodla karşılaştırır ve uygulamada nasıl ele alındığını söyler.

| İstek maddesi | Modulix uygunluğu | Karar |
|---|---|---|
| Tenant başına DB yok, tek paylaşımlı veritabanı | Uygun (ADR-0002/0020, kodda doğrulandı) | Aynen uygulandı: `support` şeması aynı veritabanında, `TenantId` + RLS |
| Tenant kimliği doğrulanmış sunucu bağlamından | Uygun (`ITenantContextAccessor`, kayıt boru hattı) | Hiçbir uçta istemciden tenant alınmaz; kayıt kimliği yetki kanıtı değildir |
| Destek bir iş yeteneği, CRM'e zorunlu bağımlılık yok | Uygun, mimari testlerle zorunlu (modüller yalnızca `Core.Contracts`'a başvurur) | Ayrı `Modulix.Modules.Support` modülü (anahtar `support`, şema `support`); CRM kapalıyken çalışır |
| Firma/kişi Core'a ait, çoğaltma yok | Uygun: `core.Parties` / `core.Persons` zaten Core'da (ADR-0006) | Talep doğrudan `PartyId`/`PersonId` taşır; ayrı adaptör gerekmedi. CRM firması aynı Party kaydının uzantısıdır |
| Core'a destek tablosu taşıma | Uygun | Core'a hiçbir şey eklenmedi; SLA/kuyruk/talep destek modülünde |
| Aktivite ticket yerine kullanılmaz | Uygun | Talep ayrı varlık; aktivite (görev/görüşme) `IActivityLinkTarget` ile talebe **bağlanır** |
| İsteğe bağlı CRM/sözleşme/sipariş/lojistik ilişkileri, modül kapalıyken çalışma | Uygun | Dilim 1'de ilişkili kayıt yok (hiçbir modül zorunlu değil). Güvenli referans sözleşmesi dilim 5 |
| Kod içinde sektör/müşteri koşulları yok | Uygun | Tür seçim listesi (`support.ticket-type`) ve kategoriler metadata; kodun anladığı yalnızca tür **kategorisi** (`complaint`) |
| Modül kapanınca kayıt silinmez | Uygun (`platform.ModuleEntitlements` yalnızca erişimi kapatır) | Tablolar kalır; kapalıyken API 403/404, veri korunur. Salt okunur tarihsel gösterim dilim 5 |
| Mevcut takvim/SLA altyapısı kullanılsın | **Doğrulanamadı → Yok**: kodda iş takvimi, tatil, çalışma saati altyapısı yoktur (`grep` boş) | Dilim 2'de destek modülünün kendi takvim hesaplayıcısı (saf, test edilebilir) yazılır; ikinci bir modül gerektirince Core'a taşınır. SLA anlamı Core'a **yüklenmez** |
| Dosya/ek, e-posta kanalı, müşteri portalı, tarama | **Yok**: genel dosya deposu (yol haritası Ç4), gelen e-posta adaptörü, genel müşteri portalı (yalnızca oturumsuz teklif sayfası var), dosya tarama yok | Çalışıyormuş gibi gösterilmez; eksik bağımlılık olarak kayıtlı (bölüm 8). Portal sıfırdan kurulmaz |
| Mevcut bildirim/e-posta altyapısı | Var: `IEmailQueue` (gönderim durumu kuyruktan izlenir) | Dış yanıt e-postası buraya yazılır; uygulama içi bildirim merkezi yoktur, kurulmaz |
| Outbox/olay altyapısı | Var (ADR-0007) | Kayıt boru hattı her yazmada olayı yazar; ek mesajlaşma altyapısı kurulmadı |
| Kanban | Mevcut üründe destek kanban'ı yok | Kurulmadı (liste); kanban gelirse sunucu kurallarını atlayamaz |
| Mevcut yapıyı değiştirmeden genişlet | Uygun | Mevcut tablolar, uç noktalar ve ekranlar değişmedi; yalnızca yeni şema, yeni modül, yeni ekranlar ve ortak listelerde yeni giriş (menü, seçim listesi) |
| Her özellik dört testle teslim edilir (mutlu yol, hata, tenant, yetki) | Uygun | Dilim 1 bunu karşılar (bölüm 9) |
| Tek PR'da bütün epic | **Uygun değil**: proje kuralı "küçük, incelenebilir adımlar" ve Sipariş/Sözleşme epic'leri dilimlerle ilerledi | 6 dilim; **yalnızca dilim 1 bu PR'da**; her dilim kendi PR'ı ve testleriyle |
| Yol haritasında yalnızca doğrulananı tamamlandı işaretle | Uygun | Epic tamamlanmış **sayılmaz**; yol haritası yalnızca dilim 1'i doğrulandı olarak yazar |

İstekteki bazı bölümler (kanban, portal, gerçek kanal adaptörleri, AI) isteğin kendi cümleleriyle de önkoşul olmadığı için kapsam dışıdır.

## 2. Mevcut durum analizi (kod kanıtlarıyla)

Etiketler: **Var**, **Kısmen**, **Yok**, **Doğrulanamadı**.

| Gereksinim | Mevcut kanıt | Durum | Eksik davranış | Yapılacak | Bağımlılık | Kabul kriteri |
|---|---|---|---|---|---|---|
| Talep/ticket varlığı | `grep ticket` → yalnızca CRM yorumları; destek tablosu yok | Yok | Her şey | `support.Tickets` + mesaj + tarihçe | Yok | Bölüm 9 |
| Müşteri kimliği (firma/kişi) | `Core.Contracts/SharedRecords/SharedRecords.cs` (`Party`, `Person`), `SharedRecordAccess` | Var | Destek erişimi | `SupportModule.SharedRecordAccess`, FK `(TenantId, PartyId)` | Core.Contracts | CRM kapalı testi |
| Kullanıcı/ekip/iş birimi/yetki | `ICurrentAccess`, `RecordScope` (Tümü/Ekip/Kendi), `RoleTemplate`, `Team` (Core) | Var | Talep kapsamı | `IOwnedEntity.OwnerId`; atanmamış talep kapsamda görünür | Core | Kapsam testi |
| Durum makinesi altyapısı | Sipariş/teklif `Transitions` + iş akışı kapısı kalıbı | Var (kalıp) | Talep durumları | `TicketTransitions`, `TicketWorkflowGate` | Yok | Geçiş tablosu ve eylem birim testleri (59 birim testinin parçası) |
| Kategori / seçim listeleri | `IModule.ChoiceLists`, `IChoiceLists` (Y9b) | Var | Tür, çözüm kodu | `support.ticket-type`, `support.resolution-code` | Yok | Tür testi |
| Aktivite / görev | `crm.Activities`, `IActivityLinkTarget`, `IFollowUpActivities` | Var (CRM'e ait) | Talebe bağlama | `TicketLinkTarget` | CRM (yalnızca aktivite için) | Aktivite bağlantı testi |
| E-posta | `IEmailQueue`, `AssignmentEmailSubscriber` | Var | Dış yanıt | Yanıt e-postası kuyruğa | Yok | `Queued` durumu |
| Bildirim merkezi | — | Yok | Uygulama içi bildirim | Kurulmaz; aktivite + e-posta | — | — |
| İş takvimi / tatil / SLA | `grep WorkCalendar|Holiday|Tatil` boş | Yok | SLA hesabı | Dilim 2: destek takvim hesaplayıcısı | Zaman dilimi/DST | Dilim 2 |
| Dosya/ek | `QuoteDocument`, teslim kanıtı: tablolarda `byte[]`; genel depo yok | Yok | Talep eki | Dilim 4'te kanıt tablosu kalıbı ya da Ç4 | Ç4 | Dilim 4 |
| Müşteri portalı | Yalnızca oturumsuz teklif sayfası | Yok | Portal talebi | Kurulmaz; koşullu | Portal | — |
| Gelen e-posta kanalı | — | Yok | Message-Id bağlama | Adaptör arayüzü dilim 4; kurulum gerektirir | SMTP/IMAP | Dilim 4 |
| Dışa aktarma | CRM teklif CSV (`crm.quotes.export`) | Kısmen (kalıp) | Talep export | Dilim 4, ayrı izin | Yok | Dilim 4 |
| Denetim / audit | `AuditedEntities`, kayıt boru hattı | Var | Talep alanları | `AuditedEntities` tanımlandı (mesaj gövdesi hariç) | Yok | Destek için denetim kaydı içeriği ayrıca **test edilmedi**; boru hattı mevcut audit testleriyle kapsanır |
| RLS ve tablo envanteri | `TenantIsolationSql.AddTable`, `TableInventoryTests` | Var | Yeni tablolar | Migration'da `AddTable` | Yok | Envanter testi (geçti) |

### Bulguların sonucu

- Epic'in çekirdeği (talep, kuyruk, kategori, durum, çözüm, mesaj) kodda **yoktu**; yol haritası M8'i "ihtiyaca bağlı, yeni" olarak işaretler.
- SLA, eskalasyon, memnuniyet, rapor, dosya ve kanal adaptörleri için altyapı yoktur; bunlar sonraki dilimlerdir ve varmış gibi gösterilmez.
- Finans/tazminat, lojistik, sipariş, sözleşme bağlantıları isteğe bağlıdır ve modül sınırını aşan hiçbir zorunlu bağ kurulmamıştır.

## 3. Modül sahipliği

| Kavram | Sahip |
|---|---|
| Kimlik, kullanıcı, üyelik, rol, izin, tenant/modül hakları, audit, e-posta kuyruğu, ortak firma/kişi, seçim listeleri, olay altyapısı | Core |
| Firma/kişi kartı, aktivite, fırsat, teklif | CRM (Core'daki Party/Person'ı genişletir) |
| Talep, kuyruk, kategori, yönlendirme, durum makinesi, çözüm, mesaj/iç not, (dilim 2+) SLA politikası/örneği, eskalasyon, (dilim 3+) şikâyet inceleme, memnuniyet | **Destek** |
| İş takvimi hesabı | Destek (dilim 2); ikinci modül gerektirirse Core'a |
| Sevkiyat/antrepo kaydı, sipariş, sözleşme, tazmin/iade ödemesi | Kendi modülleri (destek yalnızca **referans** bağlar, kapatma/ödeme yaptırmaz) |

## 4. Tasarım kararları

### 4.1 Veri modeli (dilim 1)

Şema `support`. Hepsi `TenantId`, `Version` (eşzamanlılık belirteci), `(TenantId, Id)` alternatif anahtar ve RLS taşır; tablolar arası bağlar `(TenantId, Id)` üzerinden kurulur (başka tenant'a veritabanı düzeyinde bağlanılamaz).

| Tablo | Amaç | Önemli kısıtlar |
|---|---|---|
| `Queues` | Bekleme yeri (sahiplikten ayrı) | `UX(TenantId, Name)`; tenant başına tek varsayılan (`UX_Queues_Default`, filtreli) |
| `Categories` | Kategori / alt kategori (en çok 2 düzey), varsayılan kuyruk | `UX(TenantId, ParentId, Name)`; kendi-kendine FK |
| `Tickets` | Talep | `UX(TenantId, Number)`; `UX(TenantId, ClientKey)` filtreli (tekrar koruması); durum/kanal/etki/aciliyet/öncelik `CHECK`; `CHECK` başvuran (müşteri, kişi ya da başvuran adı); Party/Person/Category/Queue/Parent FK |
| `Messages` | Mesaj (dış yanıt / iç not) | `CHECK`: iç görünürlük yalnızca `Note` yönüyle, dış görünürlük yalnızca `Outbound`/`Inbound` ile; `CHECK` teslim durumu |
| `TicketEvents` | Tarihçe (durum, atama, kuyruk, öncelik, mesaj olayı; **gövde yok**) | `(TenantId, TicketId, CreatedAt)` indeksi |

Kararlar: tür ve çözüm kodu **seçim listesidir** (yönetilebilir; kategori ile kod anlamı), durum/öncelik/kanal sabit enum + `CHECK`'tir (kod davranışı taşır). Müşteri adı kopyası (`CustomerName`) liste için tutulur, bağlantı değişince yenilenir. Silme yoktur: talep ve mesaj silinmez; kuyruk/kategori kullanılıyorsa silinmez, pasife alınır.

### 4.2 Durum makinesi (sunucuda uygulanır; `TicketTransitions`)

Durumlar: Yeni, Sınıflandırmada, Atandı, İşlemde, Müşteriden bilgi bekleniyor, İç ekip bekleniyor, Çözüldü, Kapandı, Yeniden açıldı, İptal edildi. **Çözüldü ile Kapandı farklıdır**: çözüldü çözümün kaydı ve müşteri teyidi/itirazı beklenen durumdur; kapandı, yalnızca pencere içinde yetkiyle açılabilen son durumdur.

| Kaynak | Hedef | Eylem | Yetki | Zorunlu alan | Yan etki |
|---|---|---|---|---|---|
| Yeni | Sınıflandırmada | `triage` | `tickets.write` | — | Tarihçe |
| Yeni/Sınıflandırmada/Yeniden açıldı | Atandı | `assign` / `take` | `tickets.assign` / `tickets.take` | sorumlu (ve/veya kuyruk); sahipli talepte değişim için gerekçe | Sahiplik, tarihçe |
| Çalışılan durum | aynı durum, başka kuyruk | `transfer` | `tickets.assign` | gerekçe, kuyruk | Sorumlu sıfırlanır (sahipsiz kalan "Atandı" → Sınıflandırmada) |
| Yeni/Sınıflandırmada/Atandı/Yeniden açıldı | İşlemde | `start` | `tickets.write` | sorumlu atanmış | — |
| Atandı/İşlemde/bekleme | Müşteriden bilgi / iç ekip bekleniyor | `wait-customer` / `wait-internal` | `tickets.write` | gerekçe | Bekleme nedeni (dilim 2: SLA sayaçlarını durdurma politikası) |
| Bekleme | İşlemde | `resume` | `tickets.write` | — | — |
| Atandı/İşlemde/bekleme/Yeniden açıldı | Çözüldü | `resolve` | `tickets.resolve` | çözüm kodu, açıklama; şikâyette kök neden + düzeltici aksiyon (ayarla) | Çözen, zaman |
| Çözüldü | Kapandı | `close` | `tickets.close` | — | Kapanış zamanı |
| Çözüldü / Kapandı (pencere içinde) | Yeniden açıldı | `reopen` | `tickets.reopen` | gerekçe | Sayaç artar; önceki çözüm kaydı yerinde kalır (audit'te önce/sonra) |
| Çalışılan durum | İptal edildi | `cancel` | `tickets.cancel` | gerekçe | — |
| Son olmayan her durum | aynı durum | `set-priority` | `tickets.priority` | gerekçe, öncelik | `PriorityOverridden`, tarihçe |

Desteklenmeyen geçiş **409**, eksik gerekçe/alan **422**, yetkisiz eylem **403**, kapsam dışı/başka tenant talebi **404**, eski sürüm **409**. Genel `PUT` durum, sorumlu, kuyruk, öncelik ve çözüm alanlarını **yok sayar** (sistem alanı); silme yoktur. Pencere dışı kapalı talebe gelen yanıt/mesaj reddedilir; ilişkili yeni talep (`ParentTicketId`) açılır. Çözülmüş talebe gelen müşteri mesajı talebi yeniden açar (itiraz).

### 4.3 Öncelik, yönlendirme, atama

- Öncelik = etki × aciliyet matrisi (dilim 3: tenant başına yönetilebilir, 9 hücre eksiksiz olmalı; kayıt yoksa sabit varsayılan). Elle değişiklik ayrı izin + gerekçe ister, `PriorityOverridden` ile işaretlenir ve sonraki etki/aciliyet düzenlemesiyle sessizce ezilmez.
- Yönlendirme (dilim 1): açık kuyruk (yetkiyle) → kategori varsayılan kuyruğu → üst kategori kuyruğu → tenant varsayılan kuyruğu → kuyruksuz (atanmamış). Sıra sabit olduğundan sonuç **belirlidir**; pasif kuyruk atlanır. Dilim 3: açık atama yoksa **yönlendirme kuralları** (bölüm 7) kategori/varsayılan kuyruktan önce değerlendirilir.
- Kuyruk ile sahiplik ayrı alanlardır; atanmamış talepler çalışma alanında "Atanmamış" olarak görünür ve veri kapsamı ne olursa olsun kuyrukta herkese görünür (üzerine alınabilsin diye).
- Eşzamanlı üzerine alma: ikinci istek sürüm çakışması (409) ya da talep artık başkasının olduğu için kapsam dışı (404) alır; hiçbir durumda ikinci sahip yazılmaz.

### 4.4 Mesaj ve iç not ayrımı

Müşteriye görünen yanıt (`Public/Outbound`), müşteriden gelen mesaj kaydı (`Public/Inbound`) ve iç not (`Internal/Note`) tek tabloda, ancak: (1) veritabanı `CHECK` iç notun dış kanalda saklanmasını engeller; (2) mesajlar **genel kayıt uçlarında yoktur**, yalnızca `GET|POST /api/support/tickets/{id}/messages` üzerinden, iç not `support.internal.read` olmayana **hiç dönmez**; (3) tarihçe satırları gövde içermez ve iç not olayı iç not iznine bağlıdır; (4) arama mesaj gövdesinde çalışmaz; (5) audit alan listesinde mesaj yoktur. Dış yanıt e-postası `IEmailQueue`'ya (HTML kodlanmış) yazılır; `Queued` = kuyruğa alındı, **müşteriye ulaştı demek değildir** (teslim/başarısızlık kanal adaptörü işi, dilim 4). Geçersiz adres `Failed` olur.

İlk insan yanıtı kuralı (`FirstResponseRules`, birim testli): müşteriye giden, otomatik olmayan, gönderimi kabul edilmiş (kuyrukta/gönderildi/kaydedildi; başarısız/taslak değil) ilk mesaj. İç not, müşteri mesajı ve otomatik mesaj sayılmaz. Değer talepte `FirstResponseAt` olarak saklanır ve dilim 2 SLA ölçümünün girdisidir.

### 4.5 Yetki matrisi

| İzin | Anlam | Temsilci | Lider | Yönetici | İzleyici |
|---|---|:-:|:-:|:-:|:-:|
| `support.tickets.read` | Talepleri, tarihçeyi, dış mesajları görür | ✓ | ✓ | ✓ | ✓ |
| `support.tickets.create` | Talep açar | ✓ | ✓ | ✓ | |
| `support.tickets.write` | Düzenler, sınıflandırır, işleme alır, bekletir | ✓ | ✓ | ✓ | |
| `support.tickets.take` | Atanmamışı üzerine alır | ✓ | ✓ | ✓ | |
| `support.tickets.assign` | Atar, devreder | | ✓ | ✓ | |
| `support.tickets.resolve` | Çözer | ✓ | ✓ | ✓ | |
| `support.tickets.close` | Kapatır | | ✓ | ✓ | |
| `support.tickets.reopen` | Yeniden açar | | ✓ | ✓ | |
| `support.tickets.cancel` | İptal eder | | ✓ | ✓ | |
| `support.tickets.priority` | Önceliği elle değiştirir | | ✓ | ✓ | |
| `support.messages.reply` | Dış yanıt, müşteri mesajı kaydı | ✓ | ✓ | ✓ | |
| `support.notes.write` | İç not yazar | ✓ | ✓ | ✓ | |
| `support.internal.read` | İç notları okur | ✓ | ✓ | ✓ | |
| `support.settings.manage` | Kuyruk/kategori yönetimi | | | ✓ | |

Rol şablonları: `support.agent` (kapsam Ekip), `support.lead` (Ekip), `support.manager` (Tümü), `support.viewer` (Tümü, iç not yok). "Tenant yöneticisi" sistem rolüyle tüm izinleri alır. Portal müşterisi ve dışa aktarma/silme/birleştirme izinleri ilgili dilimlerde eklenir. Platform yöneticisine talep içeriği erişimi **verilmez**: modül yalnızca tenant üyeliği ve RLS ile çalışır, mevcut denetlenebilir destek erişimi modeline (Y1.3) dokunulmamıştır.

### 4.6 API haritası (dilim 1)

`/api/support/tickets` (liste/detay/oluştur/düzenle; silme 409), `/queues`, `/categories` (aynı kalıp), `GET /summary` (sayılar), `GET /tickets/{id}/actions|history|messages`, `POST /tickets/{id}/messages` (`kind`: `reply|customer-message|note`), `POST /tickets/{id}/{triage|assign|take|transfer|start|wait-customer|wait-internal|resume|resolve|close|reopen|cancel|set-priority}` (hepsi kayıt sürümü ister). Liste süzgeçleri: `partyId`, `personId`, `categoryId`, `queueId`, `ownerId=me|id`, `status` (virgülle çoklu), `priority`, `channel`, `typeKey`, `number`, `parentTicketId`, `open`, `unassigned`, `awaitingFirstResponse`, `search`, `sort`. Bilinmeyen süzgeç/sıralama 422. Oluşturmada `clientKey` verilirse aynı anahtarla ikinci istek **409** alır ve mevcut talebi söyler (tenant kapsamlı benzersiz indeks yarışta da korur).

### 4.7 Ekranlar (dilim 1)

| Ekran | Route | Roller/izin | Davranış |
|---|---|---|---|
| Destek çalışma alanı + talep listesi | `/destek/talepler` | `tickets.read` | Hızlı görünümler (benim, atanmamış, yanıt bekleyen, müşteri bekleyen, yeniden açılan, çözüldü, tüm açık) sayılarla; durum/öncelik/tür/sıralama; arama; sayfalama (sunucuda); boş/yükleniyor/hata durumları; mobilde kart düzeni |
| Yeni talep | `/destek/talepler/yeni` (`?musteri=`, `?iliskili=`) | `tickets.create` | Aranabilir müşteri/kişi/kategori lookup'ları; müşteri yoksa başvuran alanları; şikâyette olay tarihi/beklenen sonuç; hesaplanan öncelik; sunucu doğrulama hataları alan altında; istemci anahtarı çift gönderimi korur |
| Talep detayı | `/destek/talepler/{id}` | `tickets.read` | Başlık (numara, durum, öncelik, sorumlu, ilk yanıt), işlemler paneli (yetkiye ve duruma göre, yapılamama nedeniyle), yazışma zaman çizelgesi (iç not belirgin), mesaj bileşeni, tarihçe, bilgi kenar çubuğu, çözüm, müşteriye bağlama |
| Kuyruk ve kategori yönetimi | `/destek/ayarlar` | `settings.manage` | Kuyruk/kategori ekle, pasife al, varsayılan yap |

Renk tek gösterge değildir (durum/öncelik metinli rozet), düğmeler klavye erişimlidir, hata mesajları `role="alert"`/`aria-describedby` ile okunur. Dilim 2+ ekranları (SLA/eskalasyon yönetimi, şikâyet inceleme, memnuniyet, raporlar, kuyruk/atama toplu işlemleri, müşteri portalı) henüz yoktur.

### 4.8 SLA tasarım kararları (dilim 2; uygulandı)

- İlk yanıt ve çözüm hedefi ayrı izlenir; sonraki müşteri mesajına yanıt ayrı sayaç.
- Politika seçimi: sözleşme/hizmet kapsamı (varsa) → müşteri + tür + kategori + öncelik → tenant varsayılanı; eşitlikte en özel (en çok eşleşen alan) ve sonra en yeni sürüm; kapsam dışı kayıt "hedefsiz" gösterilir.
- Zaman UTC saklanır; süre, ilgili takvime (çalışma saatleri, hafta sonu, tatil, yarım gün, zaman dilimi, DST) göre hesaplanır. Örnek: Pzt–Cum 09:00–18:00, hedef 2 iş saati, Cuma 17:00 açılış → Pazartesi 10:00 (Pazartesi tatilse Salı 10:00).
- Müşteri beklemesi SLA'yı durdurur, iç ekip beklemesi varsayılan olarak durdurmaz (politikayla değişir). Her SLA örneği politika/takvim sürüm anlık görüntüsü, başlangıç, hedef, harcanan iş süresi, durdurma aralıkları, gerçekleşme ve ihlal tutar; açık kayıtlar politika değişikliğinden etkilenmez (yetkili, gerekçeli, audit'li yeniden hesaplama).
- İhlal, işin çalıştığı ana değil gerçek hedef/gerçekleşme zamanına göre değerlendirilir. İş tekrarı/paralelliği tekil eylem anahtarıyla korunur: `(TenantId, SlaInstanceId, RuleId)`; örnek zaten `(TenantId, TicketId, Cycle, Metric)` ile tekildir; kesinti sonrası kaçırılan ihlal doğru hedef zamanıyla işlenir; tenant başına `sp_getapplock` (sipariş/sözleşme işçisi kalıbı). Öncelik düşürme/bekletme/yeniden açma geçmiş ihlali silmez; her döngü ayrı saklanır.

**Uygulanan tasarım (dilim 2):**

- **Takvim** (`support.sla-calendar`): saat dilimi (IANA), haftalık çalışma pencereleri (gün başına birden çok pencere), tatiller (tam gün kapalı ya da yarım gün penceresi), 7/24 seçeneği. Hesap saf sınıflardadır (`WorkCalendar`, `SlaClock`): `AddWorkingTime` / `WorkingTimeBetween`. Pencere sonunda biten süre o anda biter (ertesi güne taşmaz); yaz/kış saatinde geçersiz yerel saat bir saat ileri, belirsiz yerel saat ilk (yaz saati) karşılığıyla çözülür; pencereler gerçek geçen süreyle ölçülür.
- **Politika** (`support.sla-policy`): kısıtlar (tür, kategori — üst kategori de uyar —, öncelik, müşteri), ilk yanıt ve çözüm hedefi (dakika; biri yeterli), takvim, bekleme kuralları (müşteri beklemesi varsayılan durdurur, iç ekip beklemesi durdurmaz), eskalasyon kuralları, öncelik sırası. Seçim sırası `SlaPolicySelector`: yalnızca etkin ve **tüm** kısıtları uyan politikalar aday; (1) çok kısıtlı olan, (2) küçük `precedence`, (3) ad (harf duyarsız), (4) kimlik. Kısıtsız "varsayılan" politika yalnızca özel politika yoksa. Uyan yoksa talep **hedefsiz**dir. Sözleşme/hizmet kapsamı sinyali yoktur (sözleşme bağlantısı dilim 5).
- **Anlık görüntü ve sürüm:** her SLA örneği politika + takvim anlık görüntüsünü (JSON) ve politika sürümünü taşır; politika ya da takvim düzenlemesi açık örnekleri **değiştirmez**. Yetkili `POST /api/support/tickets/{id}/sla/recalculate` (`support.sla.recalculate`, gerekçe zorunlu) açık örnekleri güncel politika/takvimle yeniden anlık görüntüler; önce/sonra özeti tarihçeye (`SlaRecalculated`) ve denetim kaydına (talep hedef alanları) düşer.
- **Sayaç:** `WorkedSeconds` (duraklatma öncesi birikmiş iş süresi) ve `RunningSince`; hedef anı = çalışmaya başlanan andan kalan iş süresi kadar sonrası. Duraklatma/sürdürme aralıkları `SlaPauses`'ta saklanır. Bekleme durumuna girince/çıkınca sayaç **aynı işlemde** eşitlenir (`SlaEngine`).
- **Tamamlama:** ilk insan yanıtı (dilim 1 kuralı) ilk yanıt örneğini, çözüm çözüm örneğini tamamlar (çözümle açık ilk yanıt örneği de kapanır); iptal örnekleri iptal eder. Hedef anı geçtikten sonra tamamlanan örnek **ihlal** olarak, gerçek hedef anıyla işaretlenir.
- **Yeniden açma:** yeni bir çözüm **döngüsü** başlatır (güncel politika ve öncelikle); önceki döngü ve ihlali olduğu gibi kalır. İlk yanıt yeniden ölçülmez.
- **Öncelik değişimi:** ihlal edilmemiş örnekte politika yeniden seçilir; harcanan iş süresi korunur, hedef kalan süreden yeniden hesaplanır (deterministik). İhlal edilmiş örnek hiçbir zaman değiştirilmez/silinmez. Eşleşen politika yoksa mevcut hedef korunur.
- **Zamanlayıcı** (`SupportJobsRunner`, `support.scheduler` sistem kimliği, tenant başına `sp_getapplock`; `SupportJobs:WorkerEnabled`, `PollIntervalSeconds` varsayılan 300): (1) hedef anı geçmiş çalışan örneği gerçek hedef anıyla ihlal işaretler ve tarihçeye `SlaBreached` yazar; (2) her örneğin eskalasyon kurallarını eşik anına göre değerlendirir ve tekil anahtarla bir kez uygular. Eylemler ayrıdır: `NotifyOwner` (sahibine e-posta), `NotifyUser` (adlı kullanıcıya, ör. ekip lideri, e-posta), `Reassign` (kuyruk ve/veya kullanıcı; tarihçeye `SlaEscalated`). E-posta yalnızca talep numarası ve konusunu içerir (iç not ve açıklama gitmez). Geçici hata (sürüm çakışması) satırı yazmaz ve sonraki turda yeniden denenir; geçersiz yapılandırma satırı "uygulanamadı" sonucuyla yazar (döngü olmaz).
- **Göstergeler:** `GET /api/support/summary` `slaAtRisk` (çalışan, ihlal olmamış, ilk uyarı eşiği geçmiş) ve `slaBreached` (açık örnek hedef anını aşmış) sayar; liste `slaAtRisk=true|slaBreached=true` süzgeçleri ve `sort=resolutionDue|firstResponseDue` ile aynı kaynaktan gelir. Taleplerde `firstResponseDueAt`/`resolutionDueAt` listeler için örnekten kopyalanır (otorite örnektir).
- **Arayüz:** talep detayında SLA paneli (durum, hedef, harcanan/kalan, duraklatma aralıkları, eskalasyon eylemleri, yetkiliye yeniden hesaplama); `/destek/sla` yönetim ekranı (takvim, politika, eskalasyon kuralları, örnek talep için önizleme). Sayaç yazısı **gösterim amaçlıdır**, 30 sn'de bir sunucudan yenilenir.

### 4.9 Kanal, ek, memnuniyet, dışa aktarma, toplu işlem (dilim 4; uygulandı)

- **Ek:** ek bir mesaja bağlıdır ve görünürlüğünü ondan alır (iç nota bağlı ek iç not iznine bağlıdır; listede, indirmede ve tarihçede görünmez, 404). Yükleme mesajı yazma izniyle yapılır (yanıt ya da iç not). Genel dosya deposu (Ç4) olmadığından içerik geçici olarak `support.Attachments` tablosunda `varbinary(max)` durur; sınır 5 MB, mesaj başına 5, talep başına 20. Tür, istemcinin bildirdiği içerik türüne değil **uzantı + dosya imzasına** bakılarak belirlenir (pdf, png, jpg, gif, docx, xlsx, doc, xls, txt, csv); `html`, `svg`, betik ve çalıştırılabilir uzantılar ve `fatura.exe.pdf` gibi çift uzantı reddedilir; dosya adı yol parçalarından ve denetim karakterlerinden arındırılır; aynı içerik aynı mesaja ikinci kez eklenmez (SHA-256). İndirme her zaman `attachment`, `nosniff`, `no-store` ve kısıtlayıcı CSP ile verilir. **İçerik taraması (kötü amaçlı yazılım) yoktur**; ek `NotScanned` olarak işaretlenir ve arayüz bunu söyler. Genel depo gelince `Content` bir depo anahtarına çevrilir (ad, tür, boyut ve özet alanları hazırdır). Kapalı, iptal ve birleştirilmiş talebe ek eklenmez; ekler silinmez.
- **Gelen kanal (adaptör sözleşmesi):** `POST /api/support/inbound` (`support.inbound.ingest`; çağıran ayrıca talep oluşturma ve yanıt iznine sahip bir servis kullanıcısıdır, rol şablonu `support.integration`). **Gerçek IMAP/webhook alıcısı kurulu değildir**; bu uç, böyle bir alıcının (ya da web formunun) çağıracağı normalize sözleşmedir. Kurallar: (1) aynı kanal ve `ExternalMessageId` ikinci kez işlenmez (tenant kapsamlı benzersiz indeks yarışta da korur) ve `Duplicate` döner; (2) `Auto-Submitted` (no dışı), `Precedence: bulk/junk/list/auto_reply`, `X-Auto-Response-Suppress`, boş dönüş yolu, `mailer-daemon@`, `postmaster@`, `no-reply@` göndericiler **otomatik** sayılır, talep açmaz ve talebe eklenmez (`IgnoredAutomatic`; yanıt döngüsünü keser, mesaj saklanmaz); (3) eşleştirme sırası: `In-Reply-To`/`References` → konudaki `[TK-…]` numarası; **gönderen talebin başvuran ya da alıcı adresiyle aynı olmalıdır**, değilse eşleşme yok sayılır ve yeni talep açılır (numara/zincir tahminiyle başkasının talebine mesaj sokulamaz); (4) eşleşme yoksa yeni talep (`Email` kanalı, başvuran adı/e-postası, konu `Re:` ve numara etiketinden arındırılır); (5) birleştirilmiş talebe gelen mesaj hedefe gider, kapalı/iptal talebe gelen mesaj `ParentTicketId` ile ilişkili yeni talep açar; (6) aynı göndericiden saatte 30'dan fazla mesaj `429` alır (sel koruması). Müşteri kimliği (Party) gönderen adresinden **eşleştirilmez**: yeni talep başvuran e-postasıyla açılır, müşteriye bağlama elle yapılır.
- **Memnuniyet anketi:** ayar `support.survey.enabled` (varsayılan **hayır**) açıkken talep çözüldüğünde, alıcı e-postası varsa, çözüm döngüsü başına bir anket açılır ve bağlantısı e-posta kuyruğuna yazılır (`App:PublicUrl` ayarlı değilse anket oluşur ama e-posta yazılmaz; durum `NotSent` görünür). Belirteç yalnızca üretildiği anda vardır; veritabanında SHA-256 özeti durur. Herkese açık uçlar `POST /api/public/support-survey/{tenant}/view|answer` (tenant adresi tenant kimliği ya da kısa ad; belirteç gövdede, adreste `#t=` parçasındadır): hız sınırı (IP + tenant), 4 KB gövde sınırı, önbellek/yönlendirici/dizinleme yasakları, geçersiz/yanlış tenant/biçimi bozuk belirteçte tek tip 404. Yanıt 1-5 puan (+ isteğe bağlı yorum), **tek kullanımlık** ve süreli (`support.survey.valid-days`, varsayılan 14); eşzamanlı ikinci yanıt sürüm denetimiyle reddedilir. Yanıt talebin tarihçesine yazılır; 2 ve altı puan ayrı `LowSatisfaction` olayıdır (takip için). Yeniden açılıp tekrar çözülen talebe yeni döngü anketi açılır. Anket sonucu personele talep okuma izni ve veri kapsamıyla görünür; belirteç ve alıcı adresi dönmez. Anket e-postası kuyruğa alındı demektir, müşteriye ulaştığı doğrulanmaz.
- **Dışa aktarma:** `GET /api/support/tickets/export` (`support.tickets.export`; yönetici rolünde) liste süzgeçleriyle aynı süzgeç, izin ve veri kapsamıyla CSV üretir (en çok 5000 satır); mesaj gövdesi, iç not, çözüm notu ve kök neden sütunu yoktur; hücre başındaki formül karakterleri etkisizleştirilir; çıkış denetim kaydına yazılır (kim, kaç kayıt, hangi süzgeç).
- **Toplu işlem:** `POST /api/support/tickets/bulk` (`assign`, `transfer`, `close`, `cancel`; en çok 50 talep). Her talep kendi eyleminin izin, veri kapsamı, geçiş tablosu, gerekçe, audit ve tarihçe kuralıyla tek tek çalışır; toplu işlem hiçbir kuralı atlamaz. **Atomik değildir**: her talebin sonucu ayrıdır, başarısız olan diğerlerini geri almaz. Başka tenant ya da kapsam dışı talep, var olmayan talep gibi `NotFound` döner.
- **Şikâyet inceleme** (tekrarlayan neden, düzeltici aksiyon takibi) **dilim 6'ya (raporlar) kaydırıldı**: tekrarlayan neden bir rapor/gruplama işidir ve düzeltici aksiyon takibi ayrı bir iş kalemi olarak raporlarla birlikte tasarlanmalıdır. Çözümdeki kök neden ve düzeltici aksiyon alanları (dilim 1) değişmeden çalışır.

### 4.10 Entegrasyonlar (dilim 5; uygulandı)

- **Modül sınırı:** destek başka modüllerin tablolarına ve tiplerine bağlanmaz. İlişkili kayıt (`support.TicketLinks`) yalnızca hedef anahtarı (`contracts.contract`, `orders.order`, `orders.delivery`), kimliği ve bağlandığı andaki **etiketi** (numara · başlık) saklar; yabancı anahtar yoktur. Hedef kayıt bağlanırken kayıt boru hattından (`IRecordPipeline`) **çağıranın kendi yetkisi ve veri kapsamıyla** okunur: kayıt yoksa, başka tenant'ınsa, çağıranın okuma izni ya da veri kapsamı dışındaysa ya da ilgili modül kapalıysa aynı yanıt (422) döner, varlığı sızmaz. Böylece destek, karşı modülün yetki kurallarını yeniden yazmadan onlara uyar.
- **Müşteri uyuşması:** bağlanan kayıt talebin müşterisine (Party) ait olmalıdır; müşterisi olmayan talebe kayıt bağlanmaz. Teslimatın müşterisi siparişinden gelir. Aynı kayıt talebe iki kez bağlanmaz, talep başına en çok 20 bağ. Kapanmış, iptal edilmiş ya da birleştirilmiş talebin bağları değişmez. Bağlama talep düzenleme izniyle yapılır; bağlama ve kaldırma tarihçeye (`LinkAdded`/`LinkRemoved`) yazılır. Destek karşı modülde hiçbir şey değiştirmez (kapatma, ödeme, iade yapmaz).
- **Modül kapanınca:** destek kapalıysa API kapalıdır (`RequireModule`), veri silinmez ve yeniden açılınca bağlar yerindedir. Sözleşme/sipariş modülü kapalıysa yeni bağ kurulamaz; mevcut bağın etiketi talepte okunur kalır ama bağlantı (tıklanır adres) çizilmez. Karşı modül sayfalarında ilişkili talepler destek kapalıyken görünmez (bileşen istek yapmaz).
- **Sözleşmeye bağlı SLA:** SLA politikasına `ContractId` kısıtı eklendi (seçimde bir kısıt olarak sayılır: kısıt sayısı çok olan önce). Politika kaydedilirken sözleşme yöneticinin yetkisiyle okunabilmeli ve politikadaki müşteriyle çelişmemeli, varsayılan (yedek) politikada kısıt olamaz. Talebe sözleşme bağlanınca ya da bağı kalkınca açık SLA örnekleri güncel bağlı sözleşmelerle yeniden seçilir (aynı işlemde; değişen politika/hedef varsa anlık görüntü yenilenir, harcanan süre korunur, ihlal edilmiş örnek değişmez); tarihçe satırı bunu söyler. Önizleme sözleşmeyi dikkate alır. Sözleşmenin kendi alanlarından (ör. taahhüt süreleri) SLA türetilmez; kapsam politikayı tanımlayan yöneticinin kararıdır.
- **Karşı modül sayfaları:** Web, destek API'sini çağıran `RelatedTickets` bileşenini firma kartında (müşterinin talepleri, "Talep aç" önceden müşteri seçili), sözleşme ve sipariş sayfalarında (`GET /api/support/tickets?linked=anahtar:kimlik`) gösterir; destek modülü kapalıysa ya da okuma izni yoksa hiçbir şey çizmez ve istek yapmaz.
- **Olay adları:** talep kayıt olayları standart `RecordCreated`/`RecordUpdated` türünde ve **neden** alanı `support.<TarihçeOlayı>` biçimindedir (ör. `support.Resolved`, `support.SlaBreached`, `support.LinkAdded`, `support.LowSatisfaction`); abonelikler nedene göre ayırt eder. Bu adlar ADR'deki olay çizelgesiyle (bölüm 4.2, 4.8, 4.9) aynıdır ve değişmez kabul edilir.

## 5. Migration ve geri dönüş yaklaşımı

- Yeni **tek zincir** migration'lar: `SupportManagement` (5 tablo, RLS politikasına `TenantIsolationSql.AddTable`, `modulix_app_role` için idempotent `GRANT`) ve `SupportCustomerName` (tek sütun). Mevcut tablolara dokunulmaz; geriye uyumlu ve kesintisizdir. `docker/sqlserver/init.sql` yeni şemayı ve yetkiyi içerir.
- Geri dönüş: `Down` migration'ları RLS'i kaldırıp tabloları düşürür; **veri kaybı** getirir, bu yüzden üretimde geri dönüş yerine ileri düzeltme tercih edilir. Modülü kapatmak (entitlement) veriyi silmez ve geri dönüşün güvenli yoludur.
- Seed: demo tenant (`acme`) için `DemoDataSeeder` `support` hakkını açar (idempotent: `SetEnabledAsync`). Hazır değerler (tür, çözüm kodu) kodda tanımlıdır, ayrı seed gerekmez. Tekrar çalıştırmada mükerrer kayıt oluşmaz.
- Farklı tenant kapsamlarını içeren örnek test verisi: entegrasyon testleri her test için yeni tenant(lar) açar; kapsam (Tümü/Kendi), rol (yönetici, lider, temsilci ×2, izleyici, yetkisiz), CRM açık/kapalı tenant'lar ve ikinci bir tenant `SupportTicketTests.SetUpAsync` içinde kurulur.

## 6. Dilimler

1. **Dilim 1 — Çekirdek (uygulandı):** analiz, ADR, modül, talep/kuyruk/kategori/mesaj/tarihçe, durum makinesi, atama/devir, iç not ayrımı, çözüm, yeniden açma penceresi, öncelik matrisi, yönlendirme, tekrar koruması, çalışma alanı/liste/yeni/detay/ayar ekranları, testler.
2. **Dilim 2 — SLA ve eskalasyon (uygulandı):** destek iş takvimi, sürümlü politika ve anlık görüntü, politika seçimi (kesin sıra), SLA örneği/döngü, duraklatma, ihlal ve eskalasyon işçisi (tekil eylem), yeniden hesaplama, önizleme, yönetim ekranı ve talep SLA paneli. **Dilim 3'e kaydırıldı:** yönetilebilir öncelik matrisi ve öncelik sıralı yönlendirme kuralları (dilim 2'de yalnızca SLA ve eskalasyon kapsandı).
3. **Dilim 3 — Çözüm derinliği ve yönlendirme (uygulandı):** yönetilebilir öncelik matrisi, sıralı yönlendirme kuralları (kanal, tür, öncelik, kategori, müşteri), mükerrer uyarısı ve birleştirme, alt iş/engelleyici iş, zaman aşımıyla kapanma ve müşteri teyidi, kategoriye bağlı zorunlu alanlar. **Dilim 4'e kaydırıldı:** şikâyet inceleme (tekrarlayan neden, düzeltici aksiyon takibi); hizmet/sözleşme bazlı kural koşulu dilim 5.
4. **Dilim 4 — Kanallar, ek, memnuniyet (uygulandı):** gelen kanal adaptör sözleşmesi (Message-Id/In-Reply-To ve numara eşleştirmesi, gönderen doğrulaması, otomatik yanıt döngüsü koruması, mesaj tekrar koruması, sel sınırı), mesaja bağlı dosya ekleri (geçici veritabanı deposu, imza doğrulaması), memnuniyet anketi (süreli tek kullanımlık belirteç, düşük puan olayı, yeni döngü), CSV dışa aktarma, toplu atama/devir/kapatma/iptal. **Yapılmadı:** gerçek e-posta alıcısı (IMAP/webhook), içerik taraması, genel dosya deposu, müşteri portalı. **Dilim 6'ya kaydırıldı:** şikâyet inceleme (tekrarlayan neden, düzeltici aksiyon takibi).
5. **Dilim 5 — Entegrasyonlar (uygulandı):** firma kartında destek talepleri ve "Talep aç", sözleşme/sipariş/teslimat ilişkili kayıtları (boru hattıyla doğrulanan güvenli referans), sözleşmeye bağlı SLA politikası, karşı modül sayfalarında ilişkili talepler, olay adları belgesi, modül kapatma davranışı. **Yapılmadı:** finans iade/tazmin bağlantısı (hedef modül yok), lojistik sevkiyat kaydı (lojistik modülünde kayıt türü yok), müşteri portalı (portal yok), kapalı modülde karşı sayfada "salt okunur" talep gösterimi.
6. **Dilim 6 — Raporlar ve şikâyet inceleme:** şikâyet inceleme (tekrarlayan neden, düzeltici aksiyon takibi), hacim, backlog yaşı, ilk yanıt/çözüm süresi (takvim ve iş süresi; ortalama/medyan/yüzdelik), SLA uyumu (pay/payda), yeniden açılma, CSAT (yanıt oranı, örneklem), drill-down.

## 7. Sınırlamalar (baştan bilinen)

- İş takvimi destek modülündedir (Core'da genel takvim yoktur); ikinci bir modül gerektirirse Core'a taşınır.
- Takvim arayüzü gün başına tek pencere düzenler (API ve hesap birden çok pencereyi destekler). Tatil listesi elle girilir; resmî tatil takvimi içe aktarılmaz.
- Açık SLA örnekleri anlık görüntüyle yaşar: politika/takvim/tatil düzenlemesi onları etkilemez, yalnızca yetkili yeniden hesaplama günceller. Talebin türü, kategorisi ya da müşterisi değişince politika yeniden seçilmez (yalnızca öncelik değişiminde ve yeniden hesaplamada).
- "Sonraki müşteri mesajına yanıt" ayrı sayacı ve sözleşme/hizmet kapsamına göre politika seçimi uygulanmadı (sözleşme bağlantısı dilim 5).
- Zamanlayıcı bir turda en çok 200 ihlal ve 400 çalışan örnek işler (kalanı sonraki turda).
- `Reassign` yalnızca kural kuyruk ve/veya kullanıcı verir; yalnızca kuyruk verilirse sorumlu korunur. Eskalasyonda ek olarak aktivite açılmaz (yalnızca e-posta ve tarihçe).
- Eskalasyon e-postası kuyruğa alınır; iç kullanıcıya gider, teslim durumu e-posta kuyruğundan izlenir.
- Genel dosya deposu, gerçek gelen e-posta alıcısı (IMAP/webhook), müşteri portalı, dosya tarama ve uygulama içi bildirim merkezi yoktur. Ekler geçici olarak veritabanında durur (5 MB sınırı); büyük hacimde genel depoya taşınması gerekir.
- Gelen kanal girişi bir adaptör sözleşmesidir: gönderen adresinden müşteri (Party) eşleştirilmez; otomatik yanıtlar saklanmaz (yalnızca yok sayılır); kanal adaptörü gönderen doğrulamasını (SPF/DKIM) yapmaz, sunucu yalnızca başvuran adresiyle eşleşmeyi arar. Otomatik "alındı" yanıtı üretilmez.
- Anket bağlantısı `App:PublicUrl` ister; ayarsızsa anket oluşur ama e-postası yazılmaz. Anket yalnızca e-posta kanalıyla dağıtılır (SMS/portal yok); anketi yanıtsız bırakma hatırlatması yoktur. Düşük puan yalnızca tarihçe olayıdır (otomatik görev/bildirim açılmaz).
- Toplu işlem atomik değildir; yalnızca atama, devir, kapatma ve iptal desteklenir (toplu öncelik/çözüm yok). Dışa aktarma en çok 5000 satırdır.
- İlişkili kayıt etiketi bağlandığı andaki numara ve başlıktır; kayıt sonradan değişirse etiket güncellenmez, silinirse bağ kendiliğinden kalkmaz. Bağlanabilen türler sözleşme, sipariş ve teslimat'tir; finans (iade/tazmin) ve lojistik sevkiyatı için hedef kayıt türü olmadığından bağ yoktur. Karşı modülün kaydı ancak çağıranın o modülde okuma izni varsa bağlanır; destek yöneticisinin sözleşme okuma izni yoksa sözleşmeye bağlı politika tanımlayamaz.
- Sözleşmeye bağlı politika, talep açılırken değil bağ kurulunca devreye girer (talep oluşturma isteği bağ taşımaz); arayüzde talep açıldıktan sonra sözleşme bağlanır. Birden çok sözleşme bağlıysa en özel (kısıt sayısı çok), sonra küçük öncelik sırası, sonra ad kazanır.
- Karşı modül sayfalarındaki ilişkili talep listesi son 10 talebi gösterir (tam liste çalışma alanındadır).
- Aktivite altyapısı CRM modülüne aittir: görev/görüşmeyi talebe bağlamak için CRM açık olmalıdır; destek tek başına çalışır, yalnızca aktivite bağlantısı CRM ister.
- `PriorityOverridden` bir kez işaretlenince sonraki etki/aciliyet düzenlemesi öncelik hesabını yenilemez (bilinçli: elle karar korunur).
- Talep kategorisi değişince kuyruk yeniden yönlendirilmez (yönlendirme açılışta; sonrası atama/devir).
- Mükerrer uyarısı yalnızca öneridir (aynı müşteri ya da aynı başvuran e-posta/telefon, açık talepler, konu benzerliği ≥ 0,5, çağıranın görünürlüğü içinde); engellemez. Konu benzerliği basit sözcük örtüşmesidir (Jaccard, tr-TR), anlamsal eşleştirme değildir.
- Yönlendirme kuralı yalnızca açılışta bir kez değerlendirilir; matris ve kural değişikliği mevcut talepleri yeniden yönlendirmez. "Müşteri segmenti" kuralı için ayrı segment kavramı yoktur: kural belirli bir müşteriye (`partyId`) bağlanır. Hizmet/sözleşme bazlı koşul dilim 5.
- Birleştirme geri alınamaz (kaynak `Merged` son durumdur); farklı müşteriler birleştirilemez, kopya mesajlar özgün zamanını ve `SourceTicketId` kaynağını taşır, kaynağın açık SLA sayaçları iptal edilir, tamamlanmış (karşılanmış) örnekler geçmiş olarak kalır; hedefin sayaçları etkilenmez.
- Alt işler talep başına serbest listedir; CRM aktivitesinden bağımsızdır (CRM kapalıyken de çalışır). Engelleyici açık alt iş çözmeyi/kapatmayı reddeder.
- Otomatik kapanma yalnızca `Resolved` taleplerde, `support.auto-close-days` (0 = kapalı) süresi dolunca, zamanlayıcı tekil eylemiyle işler; müşteri teyidi (`customer-confirmation` mesajı) süreyi beklemeden kapatır.
- Şikâyet inceleme (tekrarlayan neden raporu, düzeltici aksiyon takibi) uygulanmadı; çözümdeki kök neden/düzeltici aksiyon alanları dışında kalan kısım dilim 4'e kaydırıldı.

## 8. Dış bağımlılıklar ve açık riskler

| Bağımlılık | Durum | Etki |
|---|---|---|
| SMTP / gelen e-posta (IMAP/webhook) kurulumu | Kurulmadı | Dilim 4 yalnızca adaptör sözleşmesini (`/api/support/inbound`) sunar; gerçek bir alıcı kurulana kadar gelen e-posta kendiliğinden talebe dönüşmez |
| Genel dosya deposu (Ç4) ve içerik tarama | Yok | Ekler geçici olarak veritabanında (5 MB sınırı, imza doğrulaması); içerik taraması yok, ek `NotScanned` işaretlidir |
| Müşteri portalı | Yok | Portal talebi kurulmaz; anket yalnızca e-postadaki özel bağlantıyla yanıtlanır |
| Hukuki/KVKK saklama süreleri | Doğrulanmadı | Talep ve mesaj saklama/anonimleştirme politikası ayrı karar gerektirir |
| E-posta teslim durumu | Kuyruk durumu mevcut, teslim/bounce yok | `Queued` müşteriye ulaştı demek değildir; arayüz bunu söyler |

## 9. Uygulama durumu

### Dilim 1 — Çekirdek (1 Kasım 2026): uygulandı ve doğrulandı

**Kod:** `Modulix.Modules.Support` (entity, EF yapılandırması, kayıt depoları, durum kuralları, iş akışı/mesaj/sorgu servisleri), `Modulix.Shared/Support`, `Modulix.Api/Support/SupportEndpoints.cs` (+ ayar köprüsü), migration `SupportManagement` ve `SupportCustomerName`, Blazor ekranları (`/destek/talepler`, `/yeni`, `/{id}`, `/destek/ayarlar`), menü, `TicketActionsPanel`.

**Testler (hepsi çalıştırıldı):**

- Birim (`Modulix.Modules.Support.Tests`): 59 test geçti — geçiş tablosu, atama hedef durumu, yeniden açma penceresi sınırı, öncelik matrisi (9 hücre + simetri), çözüm ve şikâyet alanı kuralları, ilk yanıt kuralı, numara biçimi.
- Entegrasyon (`SupportTicketTests`): 16 test geçti (aşağıdaki izlenebilirlik tablosu).
- Mimari testler 41, çekirdek testler 214 geçti. Etkilenebilecek mevcut paketler (tablo envanteri, tenant izolasyonu, veritabanı kurulumu, tenant dışa aktarma, uç nokta koruması, ortak kayıtlar, modül ve roller, rol yönetimi, lookup, ayarlar, seçim listeleri, kayıt devri, denetim) 73/73 geçti.
- Tarayıcıda (yerel demo tenant) çalışma alanı, talep açma doğrulaması, "üzerime al" → "işleme al", iç not + yanıt (e-posta kuyruğa alındı), çözümde şikâyet için kök neden alanları ve sunucu doğrulama mesajları, ayar ekranı denendi. Bu doğrulamada **bir hata bulundu ve düzeltildi**: `CrmLookups` geçici (transient) kayıtlı olduğundan eylem panelinin kendi örneği boş kalıyor, çözüm kodları ve şikâyet alanları görünmüyordu.

### İzlenebilirlik: kabul kriteri → test → sonuç

| # | Kabul kriteri (istek bölüm 10) | Test / kanıt | Sonuç |
|---|---|---|---|
| 1 | CRM kapalı, destek açık tenant'ta talep açılır, atanır, çözülür | `Ticket_is_created_assigned_answered_resolved_and_closed_without_the_crm_module` | Geçti |
| 2 | CRM + destek: firma kartından açılan kayıt aynı müşteriye bağlanır, müşteri kopyalanmaz | `A_ticket_for_a_crm_company_uses_the_same_shared_customer_record_without_copying_it` (API); dilim 5: firma kartında "Destek talepleri" paneli ve "Talep aç" (`?musteri={partyId}` ile açılır, aynı Party). Tarayıcıda yerel demo ile doğrulandı | Geçti |
| 3 | Lojistik kaydına bağlı hasar şikâyeti | Teslimat/sipariş kaydına bağlı şikâyet yapılabilir (`orders.delivery`, `orders.order`; tarayıcıda yerel demo ile doğrulandı, entegrasyon testi yok: sipariş kaydı kabul edilmiş teklif zinciri ister). **Lojistik modülünün sevkiyat kaydı yok**, ona bağ kurulamaz | Kısmen |
| 4 | Yetkisiz kullanıcı başka kullanıcının kaydına, iç nota erişemez | `Unauthorized_users_cannot_see_other_agents_tickets_or_run_actions_they_lack_permission_for`, `Internal_notes_never_reach_users_without_the_internal_permission_and_replies_stay_separate`. Export/dosya: dilim 4; **ekip/iş birimi kapsamı** yalnızca "kendi" ve "tümü" ile denendi | Kısmen |
| 4b | Başka tenant'ın kimliğiyle okuma/yazma/ilişkilendirme sızıntı/değişiklik yaratmaz | `Another_tenant_cannot_read_change_or_link_tickets_customers_categories_or_queues` (talep, müşteri, kategori, kuyruk, kullanıcı, ilişkili talep; toplu işlem yok) | Geçti |
| 5 | Etki/aciliyet önceliği belirler; manuel gerekçe kaydı; yönlendirme deterministik | `Priority_follows_impact_and_urgency_and_a_manual_change_needs_permission_and_a_reason`, `Routing_uses_the_category_queue_then_the_parent_category_then_the_default_queue_deterministically`, `Impact_and_urgency_decide_the_priority` (birim); dilim 3: `The_priority_matrix_is_managed_per_tenant_and_only_affects_new_or_edited_tickets`, `A_new_ticket_follows_the_first_matching_rule_by_order_and_specificity_and_explicit_assignment_wins`, `Rules_with_equal_order_resolve_by_specificity_and_a_departed_owner_is_not_assigned` | Geçti |
| 6 | Mesai takvimi hedefi (Cuma 17:00 → Pazartesi 10:00; Pazartesi tatilse Salı 10:00) | `Friday_afternoon_two_working_hours_ends_on_monday_morning`, `A_holiday_on_monday_pushes_the_target_to_tuesday`, `A_half_day_holiday_only_counts_its_own_window`, `Starting_outside_working_hours...` (birim, sabit saat); `Preview_shows_the_selected_policy_and_calendar_based_targets_for_a_sample_ticket` (API) | Geçti |
| 7 | Pause/resume yalnızca izinli sayaçları etkiler; iç ekip beklemesi varsayılan durdurmaz; DST | `Waiting_for_the_customer_pauses_the_clock_internal_waiting_does_not_by_default_and_the_policy_can_change_that`, `Pausing_and_resuming_moves_the_target_by_the_paused_time_only`, `Daylight_saving_gap_and_overlap_use_real_elapsed_time` | Geçti |
| 8 | Otomatik alındı/iç not ilk yanıt sayılmaz; başarısız dış gönderim sayılmaz | `First_response_counts_only_accepted_human_replies_to_the_customer` (birim), `Internal_notes_never_reach...` (iç not ve `Failed` e-posta ilk yanıtı doldurmaz); dilim 4: gelen otomatik yanıt talep açmaz/eklenmez (`Automatic_replies_never_create_or_extend_tickets...`). Otomatik alındı mesajı **henüz üretilmiyor** | Kısmen |
| 9 | SLA job tekrarında/paralel çalışmada eylem çoğalmaz; kesinti sonrası ihlal doğru hedef zamanıyla işlenir | `The_scheduler_marks_breaches_at_the_real_target_time_and_applies_each_escalation_exactly_once` (tekrar ve 3 paralel çalıştırma; kayıt, e-posta ve tarihçe sayıları sabit; ihlal anı = gerçek hedef anı) | Geçti |
| 10 | Çözüm alanları eksikken çözme, yetkisiz geçiş, eşzamanlı üzerine alma engellenir | `The_state_machine_is_enforced_on_the_server_and_generic_updates_cannot_bypass_it`, `Complaints_need_a_root_cause...`, `Concurrent_takes_and_stale_versions_end_in_a_single_owner_and_clear_conflicts` | Geçti |
| 11 | Yeniden açılan talebin önceki çözümü ve SLA ihlali korunur; yeni döngü | `Complaints_need_a_root_cause_and_reopening_keeps_the_previous_resolution_inside_the_window`, `A_breach_survives_priority_changes_and_reopening_starts_a_new_cycle_while_keeping_the_old_one` | Geçti |
| 12 | Birleştirme veri kaybetmez | `Merging_keeps_every_record_marks_the_source_final_and_refuses_unsafe_merges` (mesajlar özgün zamanıyla kopyalanır, kaynak ve tarihçe korunur; farklı müşteri, başka tenant, kapalı hedef reddedilir) | Geçti |
| 13 | Aynı oluşturma isteği/harici mesaj tekrarında duplicate oluşmaz | `Repeating_a_creation_request_with_the_same_client_key_does_not_create_a_second_ticket` (ardışık ve eşzamanlı); harici mesaj: `Inbound_email_creates_threads_and_deduplicates_and_only_the_known_sender_can_join_a_ticket` (aynı Message-Id `Duplicate`, mesaj sayısı sabit) | Geçti |
| 14 | Memnuniyet tekrar/süre/sıfır puan | `A_survey_is_sent_on_resolution_when_enabled_answered_once_with_a_valid_link_and_low_scores_are_flagged` (ikinci yanıt 409, süresi dolan bağlantı 409, 0 ve 6 puan 400, düşük puan olayı), `Reopened_tickets_get_a_new_survey_cycle...` | Geçti |
| 15 | Dashboard ve liste tutarlı; mevcut liste/CRM'de regresyon yok | `Ticket_is_created_...` (sayılar liste süzgeciyle aynı), `List_filters_sorting_and_search_work_on_the_server...`, regresyon paketi 73/73 | Geçti (tam paket sonucu bölüm 9.1) |
| 16 | Migration sonrası eski kayıtlar/ilişkiler/tenant izolasyonu korunur; yeni tenant DB yok | Yeni şema, mevcut tablolara dokunulmadı; migration yerel veritabanına uygulandı; `TableInventoryTests`, `TenantIsolationTests` geçti | Geçti |
| 17 | Tenant bağlamı eksik/geçersiz API reddedilir; job'larda bağlam sızmaz | API: mevcut `EndpointProtectionTests`/`AuthAndTenantTests` kapsar; destek uçları `RequireModule`+`RequirePermission` taşır. **Job yok** (dilim 2) | Kısmen |
| 18 | Başka tenant'ın müşteri/ekip/SLA/dosya ID'siyle oluşturma/atama/birleştirme/toplu güncelleme reddedilir | #4b (müşteri, kategori, kuyruk, kullanıcı); dilim 3-5: birleştirme (`Merging_keeps_every_record...`), ek/giriş/toplu işlem (`SupportChannelTests`), ilişkili sözleşme ve sözleşmeli SLA politikası (`Linking_follows_the_callers_permissions_scope_tenant...`, `Contract_policies_are_validated...`). Ekip/iş birimi kimliği yok | Kısmen |
| — | Aktivite talebe yalnızca görünürse bağlanır | `Activities_link_to_a_ticket_only_when_the_ticket_is_visible_to_the_user` | Geçti |

### 9.1 Tam paket sonucu

Tam entegrasyon paketi (`dotnet test tests/Modulix.IntegrationTests -c Release`, SQL Server + Mailpit) **488/488 geçti** (19 dk 15 sn; mevcut 472 test + destek 16). Ayrıca `dotnet build Modulix.slnx -c Release` hatasız, mimari 41, çekirdek 214 ve destek birim 59 test geçti.

### Dilim 2 — SLA ve eskalasyon (1 Kasım 2026): uygulandı ve doğrulandı

**Kod:** `Sla/WorkCalendar.cs` (takvim, SLA saati), `Sla/SlaPolicyRules.cs` (politika seçimi, eskalasyon kuralı), `Sla/SlaEngine.cs` (yaşam döngüsü), `Lifecycle/TicketSla.cs` (okuma, önizleme, yeniden hesaplama), `Lifecycle/SlaScheduler.cs` (ihlal, eskalasyon), `Records/SupportSlaRecordStores.cs` (takvim ve politika), API `SupportEndpoints`/`SupportJobsWorker`, migration `SupportSla` (5 tablo, RLS dahil; `Tickets`'a iki sütun), ekranlar `/destek/sla` ve talep SLA paneli, liste SLA görünümleri.

**Testler:** destek birim 73 (14 yeni: takvim, tatil, yarım gün, bölünmüş gün, 7/24, DST boşluk/çakışma, duraklatma matematiği, eşik anları, takvim ve kural doğrulaması, politika seçimi ve sıra bağımsızlığı); entegrasyon `SupportSlaTests` 11 yeni test (yönetim ve doğrulama, seçim ve anlık görüntü, önizleme, deterministik seçim, duraklatma, tamamlama ve gerçek-hedef-anı ihlali, zamanlayıcı ve tekil eskalasyon, ihlalin öncelik değişiminden etkilenmemesi ve yeniden açma döngüsü, yeniden hesaplama, görünürlük ve tenant izolasyonu, gösterge/liste tutarlılığı). Hedef anları saat beklenmeden veritabanında geriye alınarak sınanır; hesap sabit saatle birim testlerdedir. Tarayıcıda (yerel demo) takvim/politika listesi, önizleme, talep SLA paneli ve duraklatma denendi; bu sırada **bir arayüz hatası bulundu ve düzeltildi** (haftalık özet "Pazartesi"yi "Paz" olarak kısaltıp Pazar ile karıştırıyordu).

**Tam paket:** `dotnet test tests/Modulix.IntegrationTests -c Release` **499/499 geçti** (22 dk 56 sn; dilim 1 sonrası 488 + 11 yeni SLA testi). Ardından zamanlayıcı `Records` klasörüne ve izin çapası ayrı dosyaya taşındı (mimari testler `IModuleData` yalnızca `Records` içinde ve abone sınıfı gövdesinde yasak servis olmaması kurallarını denetler; ikisi de ilk sürümde ihlal edilmişti ve mimari testlerce yakalandı); taşımadan sonra `dotnet build Modulix.slnx -c Release` hatasız, mimari 41, çekirdek 214, destek birim 73 ve destek/SLA/envanter/olay entegrasyon testleri (53) yeniden geçti.

### Dilim 3 — Yönlendirme, birleştirme, alt iş, otomatik kapanma (1 Kasım 2026): uygulandı ve doğrulandı

**Kod:** `Lifecycle/TicketRulesCore.cs` (matris, kural seçimi, alan kuralları, benzerlik, birleştirme kuralları; saf sınıflar), `Lifecycle/TicketMerge.cs`, `TicketTasks.cs`, `TicketConfiguration.cs` (mükerrer, matris, kural önizleme), `Records/SupportRoutingRecordStores.cs`, `Records/SlaScheduler.cs` (`AutoCloseAsync`), API `/priority-matrix`, `/routing-rules`, `/routing/preview`, `/tickets/duplicates`, `/tickets/{id}/merge`, `/tickets/{id}/tasks`; migration `SupportRouting` (3 tablo RLS dahil; `Merged` durumu CHECK; `Tickets.MergedIntoId`, `TicketMessages.SourceTicketId`, `TicketCategories.RequiredFields`); yeni izin `support.tickets.merge` (yönetici ve ekip lideri); ayar `support.auto-close-days`; ekranlar `/destek/yonlendirme`, talepte alt iş ve birleştirme panelleri, yeni talepte mükerrer uyarısı, kategori zorunlu alanları.

**Testler:** destek birim 86 (13 yeni: matris doğrulama/hesap, kural seçimi sırası ve özgüllük, alan kuralları, benzerlik, birleştirme kuralları); entegrasyon `SupportRoutingTests` 9 yeni test (matris ve kural yönetimi, tenant ve yetki, sıra/özgüllük/açık atama, kategori alan kuralları ve devralma, mükerrer görünürlüğü, güvenli birleştirme, engelleyici alt iş, müşteri teyidi ve tekil otomatik kapanma). Otomatik kapanma süresi saat beklenmeden veritabanında geriye alınarak sınanır.

**Tarayıcıda (yerel demo):** yönlendirme sayfası (matris, kural listesi, kural denemesi "E-posta talepleri → Faturalama"), e-posta talebinin kurala göre farklı kuyruğa düşmesi, mükerrer adayı (%75, "Aynı müşteri"), alt iş ekleme, boş gerekçeyle birleştirmenin reddi, gerekçeyle birleştirme ve hedef talepte tarihçe satırları denendi.

**Tam paket:** `dotnet test tests/Modulix.IntegrationTests -c Release` **508/508 geçti** (23 dk 51 sn; dilim 2 sonrası 499 + 9 yeni yönlendirme testi). `dotnet build Modulix.slnx -c Release` hatasız (0 uyarı), mimari 41, çekirdek 214, destek birim 86 test geçti.

**Bilinen sınırlar:** bölüm 7. Şikâyet inceleme **yapılmadı**.

### Dilim 4 — Kanal, ek, memnuniyet, dışa aktarma, toplu işlem (1 Kasım 2026): uygulandı ve doğrulandı

**Kod:** `Lifecycle/ChannelRules.cs` (ek, gelen mesaj ve anket kuralları; saf sınıflar), `TicketAttachments.cs`, `TicketInbound.cs`, `TicketSurveys.cs` (anket açma, personel görünümü, herkese açık yanıt servisi), `TicketBulk.cs`, `Domain/SupportChannelEntities.cs`, `Persistence/SupportChannelConfigurations.cs`, API `SupportChannelEndpoints.cs` (ek, giriş, anket, dışa aktarma, toplu işlem uçları + herkese açık anket uçları), migration `SupportChannels` (2 tablo RLS dahil; `Messages`'a dört kolon ve tekrar koruması indeksi), izinler `support.tickets.export` ve `support.inbound.ingest`, rol şablonu `support.integration`, ayarlar `support.survey.enabled|valid-days`, ekranlar: talepte Ekler ve Memnuniyet anketi panelleri, çalışma alanında CSV dışa aktarma ve toplu işlem çubuğu, herkese açık `/anket/{tenant}` sayfası.

**Testler:** destek birim 109 (23 yeni: ek türü/imza/boyut/ad güvenliği, otomatik yanıt sınıflandırması, konu ve numara ayıklama, zincir kimlikleri, puan ve süre kuralları); entegrasyon `SupportChannelTests` 9 yeni test (ek doğrulama ve güvenli indirme, ek görünürlüğü/yetki/tenant, gelen e-posta eşleştirme ve tekrar ve gönderen doğrulaması, otomatik yanıt ve doğrulama ve yetki, tenant izolasyonu ve sel sınırı, anket akışı, yeni döngü ve kapsam, dışa aktarma, toplu işlem).

**Tarayıcıda (yerel demo, gerçek Mailpit e-postasıyla):** ayar açık talep çözülünce anket e-postası Mailpit'e düştü, bağlantı `/anket/{tenant}#t=…` sayfasını açtı, puansız gönderim "Lütfen bir puan seçin" verdi, 2 puanla yanıt kaydedildi, tarihçede "Düşük memnuniyet puanı alındı" ve personel panelinde yanıt göründü; ek yükleme (imza doğrulamalı) ve aynı dosyanın ikinci yüklemesinin reddi; çalışma alanında CSV bağlantısı ve toplu işlem çubuğu denendi.

**Tam paket:** `dotnet test tests/Modulix.IntegrationTests -c Release` ilk koşuda **516/517** geçti (21 dk 56 sn; dilim 3 sonrası 508 + 9 yeni kanal testi); tek hata `EndpointProtectionTests.Every_api_endpoint_requires_a_session_unless_listed` idi: yeni herkese açık anket uçları oturumsuz izin listesinde yoktu (bilerek herkese açık oldukları için liste güncellendi, gerekçesi testte yazılı). Düzeltmeden sonra sınıfın 3 testi geçti; paketin kalanı bu değişiklikten etkilenmediği için yeniden koşulmadı. `dotnet build Modulix.slnx -c Release` hatasız, mimari 41, çekirdek 214, destek birim 109 test geçti.

### Dilim 5 — Entegrasyonlar (1 Kasım 2026): uygulandı ve doğrulandı

**Kod:** `Lifecycle/TicketLinks.cs` (`ITicketLinks`, bağlanabilen türler), `Domain/SupportIntegrationEntities.cs` (`TicketLink`), `Sla/SlaPolicyRules.cs` ve `SlaEngine.cs` (sözleşme kısıtı, bağ değişince yeniden seçim), `Records/SupportStoreContext.ReadRecordAsync` (karşı modül kaydını çağıranın yetkisiyle okuma), `SlaPolicy.ContractId` + doğrulama, `linked` liste süzgeci, API `/tickets/{id}/links`, migration `SupportIntegrations` (1 tablo RLS dahil; `SlaPolicies.ContractId`), Web: `TicketLinksPanel`, `RelatedTickets` (firma kartı, sözleşme ve sipariş sayfaları), SLA ekranında sözleşme seçimi.

**Testler:** destek birim 112 (3 yeni: sözleşme kısıtı seçimi, kısıt sayısı, çoklu sözleşme belirliliği); entegrasyon `SupportIntegrationTests` 6 yeni test (bağlama/ters arama/kaldırma ve olay adı, doğrulama ve müşteri uyuşması ve kapalı talep, yetki ve veri kapsamı ve tenant izolasyonu ve modül kapanması, sözleşmeye bağlı SLA ve geri dönüş ve önizleme, politika doğrulaması ve izolasyon, destek modülü kapanışı).

**Tam paket:** `dotnet test tests/Modulix.IntegrationTests -c Release` **523/523 geçti** (24 dk 6 sn; dilim 4 sonrası 517 + 6 yeni entegrasyon testi). `dotnet build Modulix.slnx -c Release` hatasız, mimari 41, çekirdek 214, destek birim 112 test geçti.

**Tarayıcıda (yerel demo):** talebe sözleşme, sipariş ve teslimat bağlandı (etiketler, tarihçe), arayüzdeki seçim listesinden ikinci kayıt bağlandı, firma kartında "Destek talepleri" paneli ("4 açık talep · toplam 6", "Talep aç" müşteri seçili), sözleşme ve sipariş sayfalarında ilişkili talep, SLA ekranında sözleşme seçimi görüldü.

## 10. Kalan işler (epic tamamlanmış sayılmaz)

Dilim 6 (raporlar ve şikâyet inceleme); finans iade/tazmin ve lojistik sevkiyat bağları (hedef kayıt türleri oluşunca), kapalı modülde salt okunur talep gösterimi, müşteri portalı; ayrıca: otomatik alındı mesajı, "sonraki müşteri mesajına yanıt" SLA sayacı, kanban (isteğe bağlı), ekip/iş birimi kapsamı testi, kayıtlı görünümler, mesaj düzenleme/geri alma politikası, KVKK saklama politikası, gerçek gelen e-posta alıcısı (IMAP/webhook) ve gönderenden müşteri eşleştirme, dosya taraması ve genel depo, anket hatırlatması, portal. Hiçbiri yapılmış ya da çalışıyormuş gibi sunulmaz.
