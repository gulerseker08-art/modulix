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

- Öncelik = etki × aciliyet matrisi (dilim 1: sabit varsayılan, dilim 2: yönetilebilir). Elle değişiklik ayrı izin + gerekçe ister, `PriorityOverridden` ile işaretlenir ve sonraki etki/aciliyet düzenlemesiyle sessizce ezilmez.
- Yönlendirme (dilim 1): açık kuyruk (yetkiyle) → kategori varsayılan kuyruğu → üst kategori kuyruğu → tenant varsayılan kuyruğu → kuyruksuz (atanmamış). Sıra sabit olduğundan sonuç **belirlidir**; pasif kuyruk atlanır. Öncelik sıralı kural motoru (kanal, müşteri segmenti, hizmet) dilim 2.
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

### 4.8 SLA tasarım kararları (dilim 2; **henüz uygulanmadı**)

- İlk yanıt ve çözüm hedefi ayrı izlenir; sonraki müşteri mesajına yanıt ayrı sayaç.
- Politika seçimi: sözleşme/hizmet kapsamı (varsa) → müşteri + tür + kategori + öncelik → tenant varsayılanı; eşitlikte en özel (en çok eşleşen alan) ve sonra en yeni sürüm; kapsam dışı kayıt "hedefsiz" gösterilir.
- Zaman UTC saklanır; süre, ilgili takvime (çalışma saatleri, hafta sonu, tatil, yarım gün, zaman dilimi, DST) göre hesaplanır. Örnek: Pzt–Cum 09:00–18:00, hedef 2 iş saati, Cuma 17:00 açılış → Pazartesi 10:00 (Pazartesi tatilse Salı 10:00).
- Müşteri beklemesi SLA'yı durdurur, iç ekip beklemesi varsayılan olarak durdurmaz (politikayla değişir). Her SLA örneği politika/takvim sürüm anlık görüntüsü, başlangıç, hedef, harcanan iş süresi, durdurma aralıkları, gerçekleşme ve ihlal tutar; açık kayıtlar politika değişikliğinden etkilenmez (yetkili, gerekçeli, audit'li yeniden hesaplama).
- İhlal, işin çalıştığı ana değil gerçek hedef/gerçekleşme zamanına göre değerlendirilir. İş tekrarı/paralelliği tekil eylem anahtarıyla korunur: `(TenantId, TicketId, SlaCycle, Threshold/Action)`; kesinti sonrası kaçırılan ihlal doğru hedef zamanıyla işlenir; tenant başına `sp_getapplock` (sipariş/sözleşme işçisi kalıbı). Öncelik düşürme/bekletme/yeniden açma geçmiş ihlali silmez; her döngü ayrı saklanır.

## 5. Migration ve geri dönüş yaklaşımı

- Yeni **tek zincir** migration'lar: `SupportManagement` (5 tablo, RLS politikasına `TenantIsolationSql.AddTable`, `modulix_app_role` için idempotent `GRANT`) ve `SupportCustomerName` (tek sütun). Mevcut tablolara dokunulmaz; geriye uyumlu ve kesintisizdir. `docker/sqlserver/init.sql` yeni şemayı ve yetkiyi içerir.
- Geri dönüş: `Down` migration'ları RLS'i kaldırıp tabloları düşürür; **veri kaybı** getirir, bu yüzden üretimde geri dönüş yerine ileri düzeltme tercih edilir. Modülü kapatmak (entitlement) veriyi silmez ve geri dönüşün güvenli yoludur.
- Seed: demo tenant (`acme`) için `DemoDataSeeder` `support` hakkını açar (idempotent: `SetEnabledAsync`). Hazır değerler (tür, çözüm kodu) kodda tanımlıdır, ayrı seed gerekmez. Tekrar çalıştırmada mükerrer kayıt oluşmaz.
- Farklı tenant kapsamlarını içeren örnek test verisi: entegrasyon testleri her test için yeni tenant(lar) açar; kapsam (Tümü/Kendi), rol (yönetici, lider, temsilci ×2, izleyici, yetkisiz), CRM açık/kapalı tenant'lar ve ikinci bir tenant `SupportTicketTests.SetUpAsync` içinde kurulur.

## 6. Dilimler

1. **Dilim 1 — Çekirdek (uygulandı):** analiz, ADR, modül, talep/kuyruk/kategori/mesaj/tarihçe, durum makinesi, atama/devir, iç not ayrımı, çözüm, yeniden açma penceresi, öncelik matrisi, yönlendirme, tekrar koruması, çalışma alanı/liste/yeni/detay/ayar ekranları, testler.
2. **Dilim 2 — SLA ve eskalasyon:** destek takvimi, politika sürümü, SLA örneği/döngü, durdurma aralıkları, uyarı/ihlal işçisi (tekil eylem), eskalasyon eylemleri, yönetilebilir öncelik matrisi ve öncelik sıralı yönlendirme kuralları, yönetim ekranları.
3. **Dilim 3 — Çözüm derinliği ve şikâyet:** mükerrer birleştirme, alt görev/engelleyici iş, zaman aşımıyla kapanma ve müşteri teyidi, şikâyet inceleme (tekrarlayan neden, düzeltici aksiyon), kategoriye bağlı alan kuralları, mükerrer uyarısı.
4. **Dilim 4 — Kanallar, ek, memnuniyet:** gelen kanal adaptör arayüzü (Message-Id/In-Reply-To, otomatik yanıt döngüsü koruması, mesaj tekrar koruması), dosya ekleri (genel depo kararı), memnuniyet anketi (süreli belirteç, tekrar yanıt önleme, düşük puan takibi), dışa aktarma, toplu atama/kapatma.
5. **Dilim 5 — Entegrasyonlar:** CRM firma kartı (talep aç, geçmiş talepler), sözleşme destek kapsamı/SLA seçimi, sipariş/teslimat/lojistik ilişkili kayıt (güvenli referans), finans iade/tazmin bağlantısı, domain event adları, modül kapatma salt okunur gösterim, mevcut portal varsa koşullu müşteri erişimi.
6. **Dilim 6 — Raporlar:** hacim, backlog yaşı, ilk yanıt/çözüm süresi (takvim ve iş süresi; ortalama/medyan/yüzdelik), SLA uyumu (pay/payda), yeniden açılma, CSAT (yanıt oranı, örneklem), drill-down.

## 7. Sınırlamalar (baştan bilinen)

- İş takvimi altyapısı yoktur (dilim 2'de destek modülünde kurulur).
- Genel dosya deposu, gelen e-posta kanalı, müşteri portalı, dosya tarama, uygulama içi bildirim merkezi yoktur.
- Aktivite altyapısı CRM modülüne aittir: görev/görüşmeyi talebe bağlamak için CRM açık olmalıdır; destek tek başına çalışır, yalnızca aktivite bağlantısı CRM ister.
- `PriorityOverridden` bir kez işaretlenince sonraki etki/aciliyet düzenlemesi öncelik hesabını yenilemez (bilinçli: elle karar korunur).
- Talep kategorisi değişince kuyruk yeniden yönlendirilmez (yönlendirme açılışta; sonrası atama/devir).
- Mükerrer müşteri uyarısı ve eşleştirme önerisi dilim 3'tedir; bugün yalnızca elle bağlama vardır.

## 8. Dış bağımlılıklar ve açık riskler

| Bağımlılık | Durum | Etki |
|---|---|---|
| SMTP / gelen e-posta (IMAP/webhook) kurulumu | Kurulmadı | Kanal adaptörleri dilim 4'te arayüz olarak sunulur, gerçek kanal çalışıyormuş gibi gösterilmez |
| Genel dosya deposu (Ç4) ve içerik tarama | Yok | Ek yok; teslim kanıtı kalıbı (tür/boyut/hash) olası geçici yol |
| Müşteri portalı | Yok | Portal talebi/anketi koşullu, kurulmaz |
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
| 2 | CRM + destek: firma kartından açılan kayıt aynı müşteriye bağlanır, müşteri kopyalanmaz | `A_ticket_for_a_crm_company_uses_the_same_shared_customer_record_without_copying_it` (API); "Talep aç" düğmesi firma kartında **yok** (dilim 5) | Kısmen |
| 3 | Lojistik kaydına bağlı hasar şikâyeti | — | **Yapılmadı** (dilim 5) |
| 4 | Yetkisiz kullanıcı başka kullanıcının kaydına, iç nota erişemez | `Unauthorized_users_cannot_see_other_agents_tickets_or_run_actions_they_lack_permission_for`, `Internal_notes_never_reach_users_without_the_internal_permission_and_replies_stay_separate`. Export/dosya: dilim 4; **ekip/iş birimi kapsamı** yalnızca "kendi" ve "tümü" ile denendi | Kısmen |
| 4b | Başka tenant'ın kimliğiyle okuma/yazma/ilişkilendirme sızıntı/değişiklik yaratmaz | `Another_tenant_cannot_read_change_or_link_tickets_customers_categories_or_queues` (talep, müşteri, kategori, kuyruk, kullanıcı, ilişkili talep; toplu işlem yok) | Geçti |
| 5 | Etki/aciliyet önceliği belirler; manuel gerekçe kaydı; yönlendirme deterministik | `Priority_follows_impact_and_urgency_and_a_manual_change_needs_permission_and_a_reason`, `Routing_uses_the_category_queue_then_the_parent_category_then_the_default_queue_deterministically`, `Impact_and_urgency_decide_the_priority` (birim). Yönlendirme **çakışması** (kural motoru) dilim 2 | Kısmen |
| 6 | Mesai takvimi hedefi (Cuma 17:00 → Pazartesi 10:00) | — | **Yapılmadı** (dilim 2) |
| 7 | Pause/resume yalnızca izinli sayaçları etkiler; DST | Bekleme durumları test edildi (`The_state_machine_is_enforced...`); **SLA sayaçları yok** | **Yapılmadı** (dilim 2) |
| 8 | Otomatik alındı/iç not ilk yanıt sayılmaz; başarısız dış gönderim sayılmaz | `First_response_counts_only_accepted_human_replies_to_the_customer` (birim), `Internal_notes_never_reach...` (iç not ve `Failed` e-posta ilk yanıtı doldurmaz). Otomatik alındı mesajı **henüz üretilmiyor** | Kısmen |
| 9 | SLA job tekrarında eylem çoğalmaz | — | **Yapılmadı** (dilim 2) |
| 10 | Çözüm alanları eksikken çözme, yetkisiz geçiş, eşzamanlı üzerine alma engellenir | `The_state_machine_is_enforced_on_the_server_and_generic_updates_cannot_bypass_it`, `Complaints_need_a_root_cause...`, `Concurrent_takes_and_stale_versions_end_in_a_single_owner_and_clear_conflicts` | Geçti |
| 11 | Yeniden açılan talebin önceki çözümü korunur; yeni döngü | `Complaints_need_a_root_cause_and_reopening_keeps_the_previous_resolution_inside_the_window`, `A_customer_message_on_a_resolved_ticket_reopens_it...`. SLA döngüsü dilim 2 | Kısmen |
| 12 | Birleştirme veri kaybetmez | — | **Yapılmadı** (dilim 3) |
| 13 | Aynı oluşturma isteği/harici mesaj tekrarında duplicate oluşmaz | `Repeating_a_creation_request_with_the_same_client_key_does_not_create_a_second_ticket` (ardışık ve eşzamanlı). Harici mesaj anahtarı dilim 4 | Kısmen |
| 14 | Memnuniyet tekrar/süre/sıfır puan | — | **Yapılmadı** (dilim 4) |
| 15 | Dashboard ve liste tutarlı; mevcut liste/CRM'de regresyon yok | `Ticket_is_created_...` (sayılar liste süzgeciyle aynı), `List_filters_sorting_and_search_work_on_the_server...`, regresyon paketi 73/73 | Geçti (tam paket sonucu bölüm 9.1) |
| 16 | Migration sonrası eski kayıtlar/ilişkiler/tenant izolasyonu korunur; yeni tenant DB yok | Yeni şema, mevcut tablolara dokunulmadı; migration yerel veritabanına uygulandı; `TableInventoryTests`, `TenantIsolationTests` geçti | Geçti |
| 17 | Tenant bağlamı eksik/geçersiz API reddedilir; job'larda bağlam sızmaz | API: mevcut `EndpointProtectionTests`/`AuthAndTenantTests` kapsar; destek uçları `RequireModule`+`RequirePermission` taşır. **Job yok** (dilim 2) | Kısmen |
| 18 | Başka tenant'ın müşteri/ekip/SLA/dosya ID'siyle oluşturma/atama/birleştirme/toplu güncelleme reddedilir | #4b (müşteri, kategori, kuyruk, kullanıcı; ekip/SLA/dosya/birleştirme/toplu yok) | Kısmen |
| — | Aktivite talebe yalnızca görünürse bağlanır | `Activities_link_to_a_ticket_only_when_the_ticket_is_visible_to_the_user` | Geçti |

### 9.1 Tam paket sonucu

Tam entegrasyon paketi (`dotnet test tests/Modulix.IntegrationTests -c Release`, SQL Server + Mailpit) **488/488 geçti** (19 dk 15 sn; mevcut 472 test + destek 16). Ayrıca `dotnet build Modulix.slnx -c Release` hatasız, mimari 41, çekirdek 214 ve destek birim 59 test geçti.

## 10. Kalan işler (epic tamamlanmış sayılmaz)

Dilim 2–6 (bölüm 6); ayrıca: otomatik alındı mesajı, kanban (isteğe bağlı), ekip/iş birimi kapsamı testi, dışa aktarma, toplu atama/kapatma, birleştirme, kayıtlı görünümler, mesaj düzenleme/geri alma politikası, KVKK saklama politikası, kanal adaptörleri, portal. Hiçbiri yapılmış ya da çalışıyormuş gibi sunulmaz.
