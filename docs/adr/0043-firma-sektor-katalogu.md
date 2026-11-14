# ADR-0043: Firma sektörü — Core'da platform sektör kataloğu

- **Durum:** Uygulandı (dilim 1). Raporlama/segment/içe aktarma adım adım katalog adına geçirildi; kalanlar "Yapılmadı / sınırlar" başlığında.
- **Karar:** Firmanın sektör bilgisi serbest metin olmaktan çıkar; **platform sektör kataloğundan** (`platform.Industries`) seçilen bir koddur ve **ortak firma kaydında** (`core.Parties.IndustryCode`) durur. Böylece CRM, Lojistik, Antrepo ve sonraki bütün modüller aynı değeri kullanır.

## Neden

- `Company.Industry` CRM'e ait serbest metindi (100 karakter); "Gıda", "gıda", "Gida" ayrı değer sayılıyor, raporlama ve segment tutarsız sonuç veriyordu.
- Sektör firmanın özelliğidir, CRM'in değil: Lojistik'te açılan müşteri firması da sektör taşımalıdır. Ortak ana veri (Firma, Kişi, Adres) Core'a aittir (CLAUDE.md).
- Mevcut "müşteri sektörü" (`Party.CustomerSectors`, ADR-0029 K9) faaliyet ağacından çoklu seçimdir ve ayrı bir kavramdır: "ne iş yapıyor / hangi hizmeti alıyor". Bu ADR'deki alan tek değerli, düz bir sınıflandırmadır ("hangi sektörde"). İkisi birleştirilmedi.

## Tasarım

| Konu | Karar |
| --- | --- |
| Katalog | `platform.Industries` (`Id`, `Code` benzersiz, `Name`, `IsActive`); `TenantId` yok, RLS dışı (`RelationshipRoles` ile aynı desen). Platform yöneticisi yönetir, **silinmez, pasife alınır**; her yazma `PlatformAuditLog`'a aynı işlemde yazılır (`sector.industry.created/active`). |
| Varlık yeri | `Industry` entity'si `Modulix.Core.Contracts.Sectors`'tadır (Party gibi); modüller adı sorgu içinde (sıralama, rapor, segment) `Party.Industry` navigasyonuyla okur, ad kopyalanmaz. |
| Alan | `Party.IndustryCode` (nvarchar 50, boş olabilir) → `Industries.Code` yabancı anahtarı (`Restrict`), `(TenantId, IndustryCode)` indeksi. |
| Okuma sınırı | `IIndustryCatalog` (Core.Contracts): `ListAsync`, `ResolveAsync(kod ya da etkin ad)`. Yönetim `IIndustries` (Core). |
| Doğrulama | Katalogda olmayan kod 422 (`industry` / `industryCode`). **Pasife alınmış sektör** kayıtta zaten varsa kalır, başka alan düzenlenirken engel olmaz; yeni seçilemez. |
| CRM alanı | `industry` kayıt alanı artık `Party.IndustryCode`'dur ve **kod ya da etkin sektör adı** kabul eder (içe aktarma, Lead dönüşümü); kayda daima kod yazılır. Yanıt `industry` (kod), `industryName` (ad), `legacyIndustry` (aşağıya bakın) taşır. |
| API | `GET /api/industries` (her tenant kullanıcısı, oturum şart), `GET/POST /api/platform/sectors/industries`, `PUT /api/platform/sectors/industries/{code}/active` (`PlatformAccessRules`: Operasyon). Firma yöneticisi katalog yazamaz. |
| Ortak kayıt | `core.party` kaydında `industryCode` alanı vardır ve aynı doğrulamadan geçer; başka bir modülün yazdığı sektör CRM'de görünür. |

## Mevcut veri (geriye uyumlu geçiş)

- Migration kataloğu hazır sektörlerle yükler (yoksa ekler, sonradan değiştirilen adı ezmez).
- Mevcut `crm.Companies.Industry` metni **adına göre** (büyük/küçük harf duyarsız, tam eşleşme) kataloğa eşlenir ve `Party.IndustryCode` yazılır. Eşleşmeyen metin **silinmez**: `Company.Industry` sütunu "katalog öncesi metin" olarak kalır, ekranda ve API'de salt okunur `legacyIndustry` olarak görünür; kullanıcı listeden sektör seçip kaydedince temizlenir.
- Eşleşmeyen metinler platform kataloğuna **otomatik eklenmedi**: katalog bütün tenant'larca görülür, bir tenant'ın serbest metni diğerlerine sızardı.
- Tenant verisi dönüşümü RLS yüzünden `TenantIsolationPolicy` kapatılıp açılarak yapılır (diğer geri doldurmalar gibi).

## Rapor, segment, sıralama

- Firma veri kümesi sürümü 2: `industryName` ("Sektör") yeni sütundur, `industry` "Sektör kodu" olarak kaldı (kayıtlı raporlar bozulmasın); hazır rapor "Sektöre göre firmalar" `industryName` ile gruplar.
- Segment alanı `crm.company.industry` katalog adıyla (yoksa eski metinle) karşılaştırır; `crm.contact.companyIndustry` aynı.
- Liste sıralaması (`sort=industry`) ada göredir; süzgeç `industry=<kod>`.

## Yapılmadı / sınırlar

- **Lead’in sektörü de katalogdandır:** `crm.Leads.IndustryCode` (katalog kodu, yabancı anahtar); kayıt alanı `industry` kod ya da etkin ad kabul eder, yanıt `industryName` ve `legacyIndustry` taşır; kural firma ile aynıdır (`IndustryResolution`). Mevcut Lead metinleri migration’da adına göre eşlenir, eşleşmeyen `legacyIndustry` olarak kalır. Lead firmaya dönüşünce kod firmaya aynen geçer. Dış form girişi (`LeadIntake`) sektör alanı taşımadığından etkilenmez.
- **Platform ekranı (dilim 2):** Platform → Firma sektörleri (`/platform/firma-sektorleri`): liste (etkin önde, ad/kod araması), "Yeni sektör" (adan kod önerisi, kod elle değiştirilebilir), pasife alma/etkinleştirme. Faaliyet ağacı sayfasından (Sektörler ve faaliyetler) ayrıdır. Yazma yetkisi API'deki gibi platform Operasyon rolündedir; ekran yalnızca API'yi çağırır.
- Katalog öğesinin **yeniden adlandırılması** yok (kod ve ad değişmez; yanlış girişte pasife alıp yenisi eklenir). Ad değişimi eklenirse `Party` üzerinde kopya olmadığı için yayılım gerekmez.
- Birleştirme sihirbazı (`MergeWizard`) sektör farkını kod olarak gösterebilir.
- Sektöre göre alan kuralı/davranış (ör. sektör profili) bu katalogdan bağımsızdır (ADR-0029, Y9c).
- Çeviri: katalog adları tek dillidir (Türkçe); TR/EN katalog adı ayrı iş.
- Entegrasyon testleri `IndustryCatalogTests` (mutlu yol, doğrulama, tenant izolasyonu, yetki, pasife alma, katalog yönetimi) ve güncellenen mevcut testlerdir.
