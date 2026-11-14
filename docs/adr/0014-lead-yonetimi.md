# ADR-0014: Lead (potansiyel müşteri / satış talebi) yönetimi

- **Durum:** Kabul edildi ve uygulandı, 28 Ekim 2026 (kapsam ve açık işler en altta)
- **Karar:** Lead, firma/kişi/fırsattan ayrı, yeterliliği doğrulanmamış bir satış ilgisi kaydıdır (`crm.Leads`). Durum, sahiplik ve dönüşüm alanları yalnızca eylem izinli iş akışıyla değişir; dönüşüm kullanıcının kendi yetkileriyle, kayıt boru hattı üzerinden ve tek işlemde yürür; herkese açık toplama ucu sistem kimliğiyle, dar izinle ve alan listesiyle sınırlıdır

## Mevcut durum ve eksik analizi (uygulamadan önce)

- Lead kavramı yoktu. Fırsat aşamaları listesinde `Lead` ("Aday") adlı bir **aşama** vardı (`crm.opportunity-stage`); bu, mevcut bir fırsatın erken aşamasıdır, yeterliliği doğrulanmamış talep değildir.
- Firma/kişi (ortak taraf ADR-0006, ilişki ADR-0011, mükerrer ADR-0013), fırsat ve aktivite, kayıt boru hattı (izin, kapsam, hassas alan, özel alan, audit, outbox), atama e-postası aboneliği (`core.assignment-email`), rol/izin ve kayıt kapsamı (Own/Team/All) hazırdı.
- Yoktu: Lead modeli, web formu toplama ucu, hız sınırı, çok kayıtlı atomik işlem, hedef süreli gecikme göstergesi, kampanya modeli, ürün kataloğu, iş birimi (BusinessUnit), izin/rıza (consent) altyapısı, çalışma saati takvimi, AI ajan altyapısı (`Modulix.AI` boş proje).

## Lead ile fırsat aşaması "Lead" ilişkisi (geriye uyumluluk)

- Fırsat aşaması `Lead/Aday` **olduğu gibi kalır**; hiçbir fırsat otomatik olarak Lead kaydına çevrilmez ve hiçbir Lead otomatik fırsat olmaz. Mevcut kayıtlar yeniden sınıflandırılmadı (migration yalnızca ekleme yapar: yeni tablolar, `Activities.LeadId/Outcome`, `Opportunities.SourceLeadId`; hepsi boş değerli).
- Dönüşümle açılan fırsat, firmanın listesinde **etkinse** `Qualified` (Nitelikli) aşamasında başlar (Lead zaten nitelikli olduğu için "Aday" aşamasına geri düşmez); değilse listenin varsayılanı atanır. İstek aşamayı açıkça verebilir. İzlenebilirlik `Opportunity.SourceLeadId` ile sağlanır, aşama adıyla değil.

## Veri modeli

`Lead` (CrmEntity; `ICompanyShared`, çünkü CRM ana verisi işletici şirketlere ortaktır, ADR-0008; bu yüzden `ICompanyScoped`/işletici şirket alanı yoktur). Başlıca alanlar: `leadNumber` (`LD-yyyyMMdd-XXXXXX`, rastgele sonek; sıralı sayaç yarış koşulu üretirdi, benzersiz indeks korur), `subject`, `status`, `statusReason`, `priority` (Low/Normal/High/Urgent), talep sahibi (`firstName`, `lastName`, `companyName`, `email`, `phone`, `jobTitle`, `website`, `country`, `city`), mevcut müşteri bağları (`companyId`, `contactId`, isteğe bağlı), içerik (`productInterest` serbest metin, `needDescription`, `estimatedBudget?`, `currency`, `targetPurchaseDate?`, `industry`), kaynak (`source` düzenlenebilir liste, `sourceDetail`, `referrer`, `externalSource`, `externalSubmissionId`, UTM üçlüsü, `firstReceivedAt`), sahiplik (`ownerId`, `assignedAt`), takip (`firstContactAt`, `followUpAt`, `qualifiedAt`, `disqualifiedAt`, `disqualificationReason`) ve dönüşüm (`convertedAt/By`, `convertedCompanyId/ContactId/OpportunityId`). `NormalizedEmail/Phone` ve `WebsiteDomain` yalnızca aramadır, kimlik değildir.

- **Zorunlu olanlar** yalnızca konu ve talep sahibini tanıtan en az bir bilgidir. Bütçe, hedef tarih, karar verici vb. hiçbir tenant için zorunlu değildir.
- **Yapılmayanlar (gerekçesiyle):** `CampaignId` (kampanya modeli yok), ürün kataloğu ilişkisi (katalog yok; `productInterest` serbest metin), `OwningBusinessUnitId` (iş birimi yok; ekip/kapsam Own/Team/All ile sağlanır), işletici şirket referansı (CRM ortak). Hepsi sonraki aşamada alan eklenerek genişler.
- **Türetilebilir bilgi saklanmaz:** "son aktivite" ve "sonraki planlı iş" aktivite tablosundan sorgulanır (`activities?leadId=`). Saklananlar tarihsel olgulardır: `firstContactAt` (bir kez, yalnızca daha erken temas bulunursa değişir), durum zamanları.

## Durum modeli

`New → Contacting → Qualified → Converted`, ayrıca `Nurturing` ve `Disqualified`. Önerideki `Assigned` durumu **kullanılmaz**: atama `ownerId` ile temsil edilir; ayrı durum, aynı olgunun iki kaynağını (durum ve sahip) çelişkili hale getirirdi. "Atama bekleyen" = sorumlusu boş açık Lead.

| Eylem | Nereden | İzin | Kural |
| --- | --- | --- | --- |
| İletişime geç | New, Nurturing | `leads.qualify` | `firstContactAt` dolmaz |
| Nitelendir | New, Contacting, Nurturing | `leads.qualify` | `ILeadQualificationRule` kuralları: ihtiyaç ifadesi + iletişim bilgisi + talep sahibi. Sektör modülü kural ekleyebilir; bütçe/karar verici varsayılan olarak zorunlu değil |
| Daha sonra takip | New, Contacting, Qualified | `leads.qualify` | Gelecekte takip tarihi **ya da** vadeli açık aktivite (tarih ondan alınır) |
| Ele | Açık durumlar | `leads.disqualify` | Eleme nedeni (firmanın listesinden) zorunlu |
| Yeniden aç | Disqualified | `leads.disqualify` | Gerekçe zorunlu; ilk temas olduysa Contacting, yoksa New. `Converted` hiçbir zaman açılmaz |
| Ata | Açık durumlar | `leads.assign` | Hedef kuruluşun etkin üyesi; aynı kişiye tekrar atama değişiklik üretmez |
| Dönüştür | Qualified | `leads.convert` | Aşağıda; `Converted` yalnızca bu işlemle verilir, ikinci kez verilemez |

Durum/sorumlu/dönüşüm alanları depoda `SystemOnly` (genel `POST/PUT` ve `/api/records` tarafından **yok sayılır**; sorumlu yalnızca oluştururken verilebilir ve başkası için atama izni ister). Yazma yolu `LeadWorkflowGate`: iş akışı servisi kapıyı eylemin iziyle açar, Lead deposu o istekte güncelleme için **yalnızca o izni** ister. Böylece "ata" izni alan düzenleme, "nitelendir" izni atama vermez. İstemci kapıyı açamaz (istek ömürlü iç servis). Hepsi boru hattından geçtiği için kapsam, hassas alan, audit (eski/yeni değer, neden) ve olay (`RecordUpdated`, `reason: lead.*`) otomatiktir.

## Yetki ve kapsam

İzinler: `crm.leads.read|create|update|delete|assign|qualify|disqualify|convert|export`, ayrıca `crm.lead-forms.manage`. Satış temsilcisi şablonuna okuma/oluşturma/güncelleme/nitelendirme/eleme/dönüştürme eklendi (kapsam Own); yeni "Lead koordinatörü" şablonu atama ve dışa aktarma verir. Liste, detay, arama, lookup, mükerrer incelemesi, rapor ve CSV dışa aktarma **aynı boru hattı kapsamını** kullanır. Own/Team kapsamda sorumlusu boş Lead normalde görünmezdi; **atama iznine sahip kullanıcılara** görünür (atama havuzu). Aktivite ve Lead aktivitesi bağlama, bağlanan Lead'in görünürlüğünü doğrular.

**Kullanıcı devri (ADR-0004):** açık Lead'ler "devredilen kayıtlar" listesindedir. Sorumlu alanı genel uçlardan değişmediği için devir, boru hattının `IRecordCommandContext.Reason == transfer` bilgisini kullanır: yalnızca güvenilir iç çağıran (kayıt devri) neden verebilir, uç noktalar ve istemci veremez. Devir atama izni gerektirmez, `crm.leads.update` yeter (diğer kayıt türleriyle aynı kural); devredilen Lead'in `assignedAt` değeri güncellenir, atama e-postası yerine tek özet gider. Devri yapan kişide Lead düzenleme izni yoksa, tüm devir (diğer kayıtlar dahil) "hiçbir kayıt devredilmedi" mesajıyla durur.

## Aktivite

Ayrı görev sistemi yok: `crm.Activities.LeadId` + `Outcome` (Reached, NoAnswer, Sent). **Gerçekleşen temas** (ilk temasın kaynağı): tamamlanmış Toplantı (sonucu NoAnswer değilse); sonucu `Reached` olan tamamlanmış Arama; sonucu `Reached`/`Sent` olan tamamlanmış E-posta. Görev, Not, planlanmış ya da sonuçsuz/yanıtsız arama temas **değildir**. Tek otomasyon: `crm.lead-first-contact` olay aboneliği tamamlanan Lead aktivitesinden `firstContactAt`'ı doldurur ve New → Contacting yapar (sistem adına, audit'li, idempotent). Aktivite tamamlamak Lead'i nitelendirmez, elemez ya da dönüştürmez.

## Dönüşüm

`IRecordPipeline.RunAtomicAsync` (yeni): çok kayıtlı işlem tek veritabanı işleminde çalışır; her iç çağrı kendi izin/kapsam/audit/olay adımlarından geçer, hiçbiri sonuç başarılı olmadan kalıcı olmaz. Sıra: firma (mevcut ya da yeni) → kişi (yok / mevcut / yeni) → fırsat (`sourceLeadId`) → Lead'i Converted yap (sürüm denetimli). Kurallar:

- **Dönüşüm izni tek başına hak vermez:** yeni firma için `companies.write`, yeni kişi için `contacts.write`, fırsat için `opportunities.write`, mevcut kayıtlar için okuma kapsamı gerekir; hepsi kullanıcının kendi yetkileriyle denetlenir (ayrıcalıklı servis yok).
- **Mevcut firma/kişi verisi değiştirilmez** (Lead bilgisiyle ezilmez). Mevcut kişi seçilen firmaya bağlı değilse yeni bir firma–kişi ilişkisi açılır, kişi kaydına dokunulmaz.
- **Gerçek kişi müşteri:** fırsat firma ister; "firma adı" yoksa tür `Individual` olan bir firma (ortak taraf) açılır. Firma adı zorunlu kılınmaz.
- **Aktiviteler kopyalanmaz/taşınmaz:** Lead'e bağlı kalır; fırsat ekranı kaynak Lead üzerinden gösterir (açık görevler Lead sorumlusunda tamamlanır). Kaynak/kampanya bilgisi Lead'de durur.
- **Tekrar/eşzamanlılık:** ikinci istek `409` alır (dönüşmüş Lead tekrar dönüştürülemez); eşzamanlı iki istekte Lead sürümü ve `UX_Opportunities_SourceLead` (Lead başına en fazla bir fırsat) yalnızca birini geçirir, diğeri geri alınır. Başarısız her adım firma, kişi ve fırsatı geri alır.
- Dönüşmüş Lead'in hedef kayıtları yabancı anahtarla silinemez (iz korunur).

## Herkese açık toplama (web formu / entegrasyon)

`POST /api/public/lead-forms/{tenant}/{formKey}` (+ CORS `OPTIONS`). Sözleşme ve örnek README'de.

- **Tenant** URL'deki firma kısa adından sunucuda çözülür; gövde/başlık tenant kimliği belirlemez (gövdedeki `tenantId` bilinmeyen alan olarak 400). İstek, çerezden bağımsız, `crm.lead-intake` sistem kimliğiyle (yalnızca `leads.read/create`, `lead-forms.manage`) tenant kapsamında çalışır; audit'te bu anahtar görünür.
- **Form kimliği gizli değildir.** Sunucudan sunucuya çağrılar `X-Lead-Form-Secret` ile yapılır; sır 256 bit rastgeledir, yalnızca SHA-256 özeti saklanır (yazma-only alan; hiçbir yanıtta/audit'te/olayda yok), düz sır oluşturmada **bir kez** gösterilir, yenilemek eskisini geçersiz kılar. Sırlı form "yalnızca sunucu" yapılırsa tarayıcı gönderimi kapanır.
- **Origin/CORS kimlik doğrulama değildir**: izinli kaynak listesi yalnızca tarayıcı gönderimini sınırlar; asıl savunma alan listesi, uzunluk/biçim doğrulaması, 32 KB gövde sınırı, IP+form başına dakikalık hız sınırı (`LeadIntake:RateLimitPerMinute`, varsayılan 20), bal tuzağı (`website2`) ve tekrar engelidir.
- İstemci sorumlu, durum, öncelik, kaynak, iş birimi, rol ya da tenant atayamaz; sunucu belirler (`source=WebForm`, `externalSource=web-form:{formKey}`).
- **Yanıt hiçbir şey sızdırmaz:** kabul edilen her gönderim (yeni Lead, tekrar, mevcut müşteri/e-posta, bal tuzağı) aynı `202 {"accepted":true}` yanıtını alır.
- **Teknik tekrar** (aynı `submissionId`) → yeni Lead yok (idempotent; eşzamanlıda da, benzersiz indeks). **Aynı kişinin farklı gönderimi** → ayrı Lead.
- Varsayılan sorumlu pasif/üye değilse Lead atama havuzuna düşer (rastgele atama yok). Atama e-postası mevcut genel abonelikten gider (olay başına bir kez).

## Mükerrer incelemesi

`GET /api/crm/leads/duplicates` ve `/{id}/duplicates`: normalleştirilmiş e-posta, telefon (Türkiye numaraları +90'a çevrilir), firma adı, web alan adı üzerinden Lead/firma/kişi eşleşmesi. Arama boru hattından geçer: yetkisiz kayıt hiç dönmez; dönenlerde yalnızca ad/numara ve neden vardır. **Otomatik silme/birleştirme yoktur**; aynı e-posta Lead'i engellemez. Lead için birleştirme altyapısı yapılmadı; inceleme ve mevcut firma/kişiye bağlama ile sınırlı.

## Raporlama

`GET /api/crm/leads/report?from=&to=` (kullanıcının kapsamıyla). **Dönem kohortu** = `firstReceivedAt` aralığında gelen Lead'ler. Kaynak/durum dağılımı, ilk temas süresi (ortalama, medyan; yalnızca teması olanlar), hedef aşımı (hedef ayarlıysa: süresi aşılmış temaslar + hedef geçtiği halde hâlâ temassız açık Lead'ler), dönüşüm oranı (kohortta dönüşen / kohort; kohort boşsa `null`, uydurulmuş yüzde yok), dönüşüme kadar süre (yalnızca dönüşenler) ve eleme nedenleri kohorta aittir. "Dönemde yapılan dönüşüm" kohorttan ayrı sayılır ve orana katılmaz. "Sorumluya göre açık Lead" **şu anki** durumdur. Süreler **takvim saatidir** (çalışma saati takvimi yok; iş saati gibi gösterilmez). İlk temas hedefi tenant ayarı `crm.lead.first-contact-target-hours` (varsayılan 24, boş = hedef yok). Rapor/dışa aktarma tek istekte en fazla 5000 Lead okur (`truncated` bildirilir); büyük tenant için veritabanı tarafı toplama sonraki iştir.

## Olaylar ve denetim

Ayrı olay türü eklenmedi (gereksiz çoğaltma): `LeadCreated` = `RecordCreated(crm.lead)`, `LeadAssigned/StatusChanged/Qualified/Disqualified/Converted` = `RecordUpdated(crm.lead)` + `reason` (`lead.assigned`, `lead.contacting`, `lead.qualified`, `lead.disqualified`, `lead.nurturing`, `lead.reopened`, `lead.first-contact`, `lead.converted`) ve alan farkı yükü. Aboneler olay kimliğiyle idempotenttir. Olay yükü Lead alanlarının farkını taşır (kişi adı/e-posta dahil); boru hattının genel davranışıdır ve alan düzeyinde hassas Lead alanı tanımlanmadı. Audit: tüm alan değişiklikleri, sorumlu değişimi, durum+neden, dönüşüm referansları ve yeniden açma kayıt geçmişinde; CSV dışa aktarma `Exported` olarak kaydedilir.

## Bilinçli sınırlar ve açık işler

- **İzin/rıza (consent)** altyapısı yok; gönderim pazarlama izni sayılmaz ve izin kaydı oluşmaz. İzin amacı/kaynağı/zamanı izlemesi altyapı geldiğinde eklenecek.
- **AI ajan:** altyapı yok (kurallar: [ADR-0047](0047-yapay-zeka-ajan-kurallari.md) taslağı) (`Modulix.AI` boş). `ILeadWorkflow`, `ILeadConversion`, `ILeadIntake` ve Lead sorguları (kayıt boru hattı) araç olarak sunulabilecek biçimde tasarlandı; `SearchLeads`, `GetLeadDetails`, `ListLeadsNeedingFollowUp`, `GetLeadActivityHistory`, `SuggestLeadNextAction`, `CreateLeadFollowUpActivity` araçları **yapılmadı**. Kural: ajan Lead'i kendiliğinden elemez, birleştirmez, dönüştürmez; yazma eylemleri kullanıcının izin ve onay politikasından geçer.
- CSV/Excel içe aktarma, kampanya, e-posta ile Lead, Lead kuralı tabanlı otomatik atama (round-robin, bölge, ürün) ve Lead birleştirme yapılmadı; genişleme noktaları: `ExternalSource` + `ILeadIntake`, `ILeadQualificationRule`, atama eylemi.
- Sunucu tarafı toplama (rapor), ilk temas gecikmesinin bildirimi/hatırlatıcısı ve Lead'e özel arka plan işi yapılmadı. Dönüşümde ilk temas gecikmesi yalnızca göstergedir.
- Arkasında ters vekil (proxy) varsa hız sınırı için `ForwardedHeaders` yapılandırılmalıdır; aksi halde tüm istekler proxy IP'sinden sayılır.

## Güncel durum notu (13 Kasım 2026)

"Bilinçli sınırlar ve açık işler" listesindeki bazı maddeler sonraki ADR'lerle kapandı ya da engeli kalktı; kodla karşılaştırıldı:

- **CSV/Excel içe aktarma:** yapıldı. Genel içe aktarma altyapısı [ADR-0033](0033-import-takibi-ve-islem-gunlugu.md) ile geldi; ilk tanım müşteri adayıdır (`CrmImports`), XLSX dilim 4a ile eklendi.
- **Kampanya:** kampanya ve segment modülü var ([ADR-0025](0025-kampanya-ve-segment-yonetimi.md)); müşteri adayına `campaignId` bağı **yapıldı** (14 Kasım 2026, A-12): alan + lookup `crm.lead.campaign` + `campaignId` süzgeci; kampanya yanıtından açılan aday kampanyaya otomatik bağlanır, dönüşümde bağ fırsata geçer. Kampanya Pazarlama modülünün kaydı olduğundan veritabanında yabancı anahtar yoktur; bağ kayıt sırasında kayıt boru hattından çağıranın yetkisiyle doğrulanır (ayrıntı ADR-0025 "CRM kampanya bağı").
- **Otomasyon/hatırlatma altyapısı:** kural ve zamanlanmış iş altyapısı ile bildirim kutusu var ([ADR-0027](0027-kural-ve-is-akisi-altyapisi.md)); CRM otomasyon şablonları müşteri adayı türünü tanır. İlk temas gecikmesi bildirimi ayrıca yazılmadı.
- **Raporlama:** sunucu tarafı müşteri adayı raporları [ADR-0035](0035-raporlama-ve-satis-tahmini.md) kapsamındadır.
- **Hâlâ geçerli:** kural tabanlı otomatik atama (ürün kararı bekliyor, öncelik listesi B), müşteri adayı birleştirme, e-posta ile müşteri adayı, izin/rıza altyapısı (hukuk kararı, ADR-0025) ve AI araçları (`Modulix.AI` hâlâ boş).
