# ADR-0015: Aktivite ve takvim yönetimi

- **Durum:** Kabul edildi; E1 (temel yaşam döngüsü, yetki, ilişkiler, liste/takvim) 29 Ekim 2026'da uygulandı. Kalan kapsam en altta "Yapılmadı" başlığında, hiçbiri sessizce çıkarılmadı
- **Karar:** Aktivite, CRM modülünün mevcut `crm.Activities` kaydıdır; yeni bir modül ya da ikinci bir görev sistemi açılmadı. Durum makinesi tek bir ortak liste değil, **tür ailesine** göre ayrıdır. Durum, tamamlanma, sonuç ve sorumlu değişiklikleri yalnızca eylem servisiyle (`IActivityWorkflow`) yapılır (Lead ile aynı kapı kalıbı). Diğer modüller aktivite çekirdeğini değiştirmeden kayıtlarını `IActivityLinkTarget` sözleşmesiyle bağlar.

## Mevcut durum ve eksik analizi (uygulamadan önce)

| Konu | Önceden | Bu ADR ile |
| --- | --- | --- |
| Aktivite kaydı | `Activities`: tür, konu, vade, tamamlanma, firma/kişi/fırsat/Lead sütunları, sonuç, sorumlu | Durum, öncelik, zaman bloğu, tüm gün, saat dilimi, konum, çevrim içi bağlantı, özel, kaynak, oluşturan, süre, erteleme sayacı, önceki aktivite, dış kimlik |
| Açık / gecikmiş | `CompletedAt IS NULL`; iptal kavramı yoktu (iptal edilen "açık" kalırdı) | `Status`; gecikme saklanmaz, `açık durum ∧ DueAt < şimdi` |
| Tamamlama | Düz `PUT` ile `completedAt` yazılırdı | `POST …/complete`; düz `PUT` durumu, tamamlanmayı, sonucu, sorumluyu değiştiremez |
| Tür | Düzenlenebilir liste (Arama/Toplantı/E-posta/Görev/Not) | Aynı liste; kategori = aile; yeni hazır değerler Ziyaret ve Takip |
| İlişki | Dört sabit FK | FK'lar korunur (doğrudan ilişki); ek olarak `ActivityLinks` (sınırsız tür, tek birincil) |
| Katılımcı | Yok | `ActivityParticipants` |
| Özel aktivite | Yok | Alan düzeyinde değil **kayıt düzeyinde** gizlilik |
| Takvim | Yok (liste) | Aralık süzgeci + ay/hafta/gün/ajanda + sürükle-bırak |
| BusinessUnit, ekip kuyruğu, oda/araç | Yok / `Modulix.Core` içinde, modüllere sözleşmesi yok | Yapılmadı; bağımlılık olarak not edildi |

## Veri modeli

`crm.Activities` (sütun ekleme + geri doldurma, silme/yeniden adlandırma yok). Rollerin ayrımı:

- **Oluşturan** `CreatedBy` (değişmez), **birincil sorumlu** `OwnerId` (tek kişi; ekip katılımı sorumluluğu belirsizleştirmez), **katılımcı/izleyici** `ActivityParticipants.Role` (Zorunlu/İsteğe bağlı/İzleyici), **düzenleyen** audit kaydındaki kullanıcı.
- **Zaman:** `DueAt` bir teslim anıdır; `StartAt/EndAt` takvimde yer tutan **zaman bloğudur**; ikisi bağımsızdır (son tarih göstermek rezervasyon değildir). Saatli kayıt UTC anı + IANA `TimeZone`; **tüm gün** kayıt `StartDate/EndDate` (`date`) olarak saat dilimi dönüşümü olmadan saklanır, bu yüzden "bir önceki güne kayma" oluşmaz. Zorunlu: saatli blokta `TimeZone`; tüm gün etkinliğinde saat yok; blok ≤ 14 gün, tüm gün ≤ 366 gün.
- **Açıklama:** `Notes` aktivitenin açıklamasıdır; `StatusReason` son durum değişikliğinin gerekçesi / sonuç açıklamasıdır (iptal, bloke, yeniden açma, erteleme, tamamlama notu). Eski değerleri audit geçmişi tutar.
- **Tür özel bilgisi** kontrolsüz JSON'a yığılmadı: ortak sütunlar tür ailesine göre anlam kazanır (örn. `Location` toplantı/ziyaret, `OnlineMeetingUrl` toplantı); gerçek tür-özel alan ihtiyacı Y9a özel alanlarıyla (`CustomFields`, tanımlı ve doğrulanır) karşılanır.
- **Dış kimlik:** `(ExternalSource, ExternalId)` filtreli benzersiz indeks; aynı webhook/içe aktarma olayı ikinci kez kayıt üretemez (veritabanı garantisi).
- **Eşzamanlılık:** `Version` + 409 (mevcut kayıt boru hattı); tüm eylemler sürüm ister.
- **İndeksler:** `(TenantId, OwnerId, Status, DueAt)`, `(TenantId, StartAt)`, `(TenantId, StartDate)`, `(TenantId, PreviousActivityId)`, `ActivityLinks(TenantId, EntityKey, RecordId)`, `ActivityParticipants(TenantId, UserId)`.

## Durum makinesi (tür ailesine göre)

Aile = `crm.activity-type` değerinin kategorisi (`task|call|meeting|visit|email`). Kategorisiz / firmanın eklediği değer görev gibi davranır; firma yeni tür eklerken aile seçer. Durum anahtarları ailelerde ortaktır, ekran etiketi aileye göre değişir (`Open` = görevde "Açık", aramada/toplantıda "Planlandı").

| Aile | Durumlar ve geçişler |
| --- | --- |
| Görev | Open ⇄ InProgress ⇄ Blocked; tümü → Completed / Cancelled; Completed, Cancelled → Open (yeniden aç). Bloke görev tamamlanamaz |
| Arama | Open → Completed (ulaşıldı) / NoAnswer (ulaşılamadı) / Cancelled; NoAnswer → Completed (düzeltme) |
| Toplantı, ziyaret | Open → Completed / NoShow (katılım olmadı) / Cancelled; NoShow → Completed |
| E-posta | Draft → Queued / Sent (elle kayıt) / Cancelled; Queued → Sent / Failed; Failed → Queued / Draft / Cancelled; Sent son durum |

- **Tamamlanma yalnızca gerçekleşen durumlarda** (Completed, NoAnswer, NoShow, Sent) kaydedilir; iptal tamamlanma değildir. Açık duruma dönen kayıtta tamamlanma ve sonuç temizlenir.
- **"Gecikmiş" türetilir**, saklanmaz; kapalı / iptal kayıt hiçbir zaman gecikmiş değildir.
- **Geçmiş tarihli etkileşim:** oluştururken `completedAt` (+ sonuç) verilirse kayıt doğrudan tamamlanmış açılır (gelecek tarih reddedilir). Sonradan değiştirilemez.
- **Yanlış tamamlama düzeltmesi:** `reopen` (ayrı izin, gerekçe zorunlu), eski tamamlanma audit'te kalır.
- **E-posta gerçeği:** Sent = "kullanıcı gönderildi olarak kaydetti"; **teslim edildi / okundu / yanıtlandı anlamına gelmez**. Queued/Failed yalnızca ileride gönderim altyapısının yazacağı sistem durumlarıdır; bugün hiçbir eylem bu durumları üretmez, e-posta gönderen kod yoktur.
- **Erteleme:** `reschedule` gerekçe ister, sayaç artar, eski/yeni tarih audit'te durur. Düz `PUT` ile zaman değişirse de sayaç artar. Kapanmış kayıt ertelenemez ve zamanı düz güncellemeyle değişmez ("tamamlanmış geçmişi koru"). Çakışma (aynı sorumlunun açık, saatli başka bloğu; yarı açık aralık, arka arkaya bloklar çakışmaz) 422 + `conflict` alan hatasıdır; `AllowConflict` ile bilinçli geçilir.

## Yetki

İzinler: `crm.activities.read` ve `write` (mevcut) ile yeni `assign`, `reopen`, `manage-calendar`, `view-private`.

**Bilinçli uyumluluk kararı:** oluşturma, düzenleme, tamamlama, iptal ve silme `write` izniyle birlikte kaldı; çünkü tenant veritabanlarındaki mevcut rollere yeni izin eklemek için tenant-DB'de çalışan bir rol yükseltme mekanizması yok (`PermissionGrantSql` platform veritabanına göre yazılmış) ve ayrı izin olarak koymak mevcut rolleri sessizce kısıtlardı. Ayrı izin olanlar: başkasına atama (`assign`; kendine almak `write`), yeniden açma (`reopen`; satış temsilcisi ve koordinatör şablonuna eklendi), ekip takvimi, özel aktivite ayrıntısı. Tamamlama/silme/oluşturma ayrımı, rol yükseltme mekanizması geldiğinde eklenecek.

- **Kapsam:** Y8 sorumlu kapsamı (Own/Team/All) + **katıldığı aktiviteler** (katılımcı, kapsam dışında bile o aktiviteyi görür). Alt BusinessUnit ve tenant dışı kapsam yok (BusinessUnit yok).
- **Eylem kapısı:** her eylem yalnızca kendi iznini ister (`reopen` izni atama yaptırmaz, `assign` izni alan düzenletmez); sorumlu devri (ADR-0004, `transfer` nedeni) mevcut yoldan çalışır.
- **Toplu işlem** (`POST /activities/bulk`, ≤ 100): kayıt başına bağımsız; her kayıt kendi yetki/kapsam/sürüm denetiminden geçer; sonuç başarı sayısı + kayıt başına hata. Hepsi ya da hiçbiri değildir.
- **Bildirim/rapor/arama/dışa aktarma** aynı boru hattı kapsamını kullanır (ayrı bir yol yok); özel aktivite kuralları aşağıda.

### Özel aktivite

`IsPrivate` kayıt düzeyinde gizliliktir (alan düzeyindeki hassas alan mekanizması kayıt bazlı olamaz). Ayrıntıyı **görenler**: sorumlu, oluşturan, katılımcılar, `view-private` sahipleri. Diğerleri (kapsamda olsalar bile) yalnızca **meşguliyet** görür: konu "Meşgul", zaman alanları ve sorumlu; başlık, açıklama, ilişkiler, konum, bağlantı, tür, öncelik yok (`Masked=true`). Ayrıca: arama başlığı eşleştirmez; ilişki süzgeçleri (`companyId`, `related`, `timeline`...) ve alt kayıt tabloları (ilişki, katılımcı) özel aktiviteyi göstermez; ayrıntıyı göremeyen kullanıcı düzenleyemez/silemez/iptal edemez ("bulunamadı"). Lead ilk-temas aboneliği sistem kimliğiyle `view-private` ister (aksi halde özel aranan Lead temas sayılmazdı).

**Bilinen sınır:** olay yükü ve audit geçmişi alan değişikliklerini taşır; özel bir aktivitenin başlığı **audit kaydını okuyabilen yöneticilere** görünür (boru hattının genel davranışı, kayıt bazlı redaksiyon yok). Bildirim altyapısı (E2) geldiğinde bildirim metinleri özel aktivitede başlık içermeyecek.

## Kayıt ilişkileri ve zaman akışı

- **Merkezi sözleşme:** `IActivityLinkTarget` (`Core.Contracts`): `EntityKey`, `IsVisibleAsync`, `GetTitleAsync`. Her modül kendi hedefini DI'a ekler; aktivite çekirdeği değişmez. CRM firma, kişi, fırsat, Lead hedeflerini sağlar. Hedef kimliğe veritabanı yabancı anahtarı **yoktur** (hedef başka modülde olabilir); doğrulama yazarken yapılır.
- **Doğrulama:** hedef var mı, aktif tenant'ta mı, çağıranın okuma izni ve veri kapsamı var mı; erişilemeyen kayıt "bulunamadı" der (varlığı belli edilmez). Doğrudan FK'lar da aynı denetimden geçer (değişmeyen eski bağ yeniden denetlenmez).
- **Sızıntı:** `GET …/related` başlığı yalnızca görülebilen kayıtlar için döner; görünmeyenin yalnızca sayısı gelir. Fırsat tutarı/açıklaması aktivite üzerinden hiçbir yolla taşınmaz (yalnızca başlık).
- **Birincil ilişki:** aktivite başına en fazla bir (filtreli benzersiz indeks); aynı kayıt iki kez bağlanamaz; bağlı kayıt kimliği değişmez (sil + yeniden ekle).
- **Birleşik zaman akışı:** `?related=<tür>:<id>` (kesin) ve `?timeline=crm.company:<id>` (firma + kişileri + fırsatları + dönüşmüş Lead'leri). Doğrudan sütunlar ve ilişki satırları tek koşulda birleşir; aynı aktivite çoğalmaz.
- **Lead dönüşümü:** aktiviteler Lead'e bağlı kalır (ADR-0014), açık takipler kaybolmaz ve dönüşümle oluşan firma/kişi/fırsatın akışında görünür.
- **Birleştirme (ADR-0013):** ilişki satırları ve katılımcı kişiler hedefe taşınır; hedefe zaten bağlıysa mükerrer satır silinir (benzersizlik), aktivite silinmez.
- **Bağlı kayıt silinir/arşivlenir:** aktivite ve geçmişi korunur (hedefe FK olmadığı için engel de yoktur); ilişki başlığı görünmez olur ("erişiminiz olmayan kayıt"). Doğrudan FK'lı eski sütunlarda silme `Restrict`'tir (referanslı kart silinemez, mevcut kural).

## Harici katılımcı

Katılımcı üç türdür (tam biri dolu): dahili kullanıcı, CRM kişisi, serbest e-posta. Harici katılımcı olmak hiçbir CRM kaydına erişim vermez (erişim yalnızca kullanıcı rolleriyle). Davet gönderimi / yanıt (kabul/ret) **yapılmadı**.

## Takvim

`GET /api/crm/activities?range=<başlangıçUTC>|<bitişUTC>|<başlangıçGünü>|<bitişGünü>`: saatli bloklar (yarı açık aralık), tüm gün etkinlikleri (tarih karşılaştırması) ve yalnızca son tarihli işler (blok yoksa). Pencere ≤ 93 gün, sayfa ≤ 200 (aşılırsa ekran uyarır); sınırsız aralık sorgusu yok. Ay/hafta/gün/ajanda görünümleri, Bugün/Gecikenler/Bana atananlar/Ekip, durum-tür-öncelik süzgeci, tarayıcıda saklanan kişisel görünümler, hızlı oluşturma, ayrıntı paneli, sürükle-bırak yeniden planlama (yerel saat korunarak gün değişir; yetki ve çakışmayı sunucu denetler), toplu işlem. Klavye: gün hücresi odaklanır, Enter hızlı oluşturur; aktiviteler düğmedir. Mobilde ay/hafta tek sütundur.

**Sınırlar:** saat ızgarası (dakikaya göre yerleşim) yok, günün aktiviteleri saat sırasıyla listelenir. "Kişisel görünüm" sunucuda değil tarayıcıda tutulur (cihazlar arasında taşınmaz). Çalışma saati, tatil, müsaitlik, hazırlık/ulaşım süresi yok (çalışma takvimi altyapısı yok, bkz. yol haritası Ç15/Y6.2). Takvimde kullanıcı saat dilimi olarak **firma saat dilimi** (Y6.2) kullanılır; kullanıcı bazlı saat dilimi tercihi yok.

## Sonraki adım zinciri

`complete` isteğine `nextStep` verilirse tek işlemde (`RunAtomicAsync`) önce aktivite tamamlanır, sonra yeni aktivite `previousActivityId` ile açılır; doğrudan ilişkiler ve ilişki satırları kopyalanır (her biri görünürlüğü yeniden doğrular). Bir adım geçersizse tamamlama da geri alınır.

## Yapılmadı (gerçek durum)

| Kapsam | Neden / bağımlılık |
| --- | --- |
| Tekrar serileri, istisnalar, "bu ve sonraki", yaz/kış saati, ayın 29-31'i | E2. Zaman modeli (IANA saat dilimi + yerel duvar saati + tüm gün tarihi) buna hazırlandı; üretim penceresi + `(SeriesId, OccurrenceKey)` benzersiz indeksi eklenecek |
| Hatırlatma, gecikme/son tarih bildirimi, eskalasyon, tercihler, sessiz saat, özetleme | Ç2 (arka plan iş/zamanlayıcı) ve Ç5 (bildirim tercihleri) kodda yok; "tamamlanınca bekleyen hatırlatma gönderilmez" testi bu yüzden yazılamadı |
| Takip planları (şablon, adım, iş günü, durma koşulu) | E2; iş günü hesabı çalışma takvimine bağlı |
| Davet, kabul/ret, güncelleme/iptal bildirimi, gündem, karar, ziyaret raporu, konumla giriş | E2; bildirim ve dosya altyapısı yok |
| Alt görev, kontrol listesi, bağımlılık (döngü engeli), yorum, dosya eki | E2; dosya altyapısı (Ç4) yok |
| Ekip kuyruğu / üstlenme, BusinessUnit kapsamı | Ekip yalnızca `Modulix.Core` içinde, modüllere sözleşme yok; BusinessUnit yok |
| Kullanıcı pasife alınınca devir | Mevcut ADR-0004 akışı `crm.activity` açık kayıtlarını devreder (`OwnedRecords`); yeni testle ayrıca doğrulanmadı |
| Harici takvim (Google/Microsoft), e-posta senkronu, webhook, kimlik bilgisi deposu | Sağlayıcı erişimi ve entegrasyon/sır deposu yok. Hiçbiri "çalışıyor" gösterilmez. `ExternalSource/ExternalId` benzersizliği hazır |
| Oda/araç/ekipman rezervasyonu | Ortak kaynak yönetimi (Ç) bağımlılığı |
| Otomasyon (Lead atanınca ilk temas görevi, ulaşılamayınca takip, teklif sonrası takip, hareketsizlik uyarısı, fırsat kapanınca takip sonlandırma) | E2; `activity.*` olay nedenleri ve `source=Automation` alanı hazır, tekrar üretimi `ExternalSource/ExternalId` ile engellenebilir |
| Yapay zekâ genişleme noktaları | `Modulix.AI` boş; `IActivityWorkflow` araç olarak kullanılabilecek biçimde (kullanıcı izniyle, onaylı yazma) tasarlandı, araçlar yazılmadı |
| Raporlar (zamanında tamamlama, iş yükü, takip eksik müşteri...) | E3; veri (durum, tamamlanma, sonuç, sayaç, planlanan/gerçekleşen süre) saklanıyor |
| `view-private`/`assign` dışındaki izin ayrımı (create/update/complete/delete) | Rol yükseltme mekanizması (yukarıya bakın) |
| Dışa aktarma | Uç nokta ve `crm.activities.export` izni yok; eklenince aynı boru hattı kapsamıyla yapılacak |
