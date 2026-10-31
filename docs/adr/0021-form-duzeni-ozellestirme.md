# ADR-0021: Form düzeni özelleştirme (sekme, bölüm, alan yerleşimi, zorunluluk)

- **Durum:** Kabul edildi; 31 Ekim 2026'da uygulandı.
- **Karar:** Her kayıt formunun düzeni (sekmeler, bölümler, alanların sırası, genişliği, görünürlüğü ve zorunluluğu) **modülün kodda sabit tanımı** (`IModule.Forms` → `FormDefinition`) üzerine **firma başına tek bir özelleştirme satırı** (`meta.FormLayouts`, JSON) ile kurulur. Firma yöneticisi Yönetim → Form tasarımı ekranından alan taşır, yeni sekme ve bölüm açar, genişlik/zorunluluk/görünürlük belirler. Formlar (`LayoutForm`) etkin düzeni `GET /api/form-layouts` ile alır ve ona göre çizilir; alanın kendisi (girdi tipi, doğrulama, kaydetme) kodda kalır. Firmanın zorunlu kıldığı alan **sunucuda** kayıt boru hattında denetlenir; arayüzdeki yıldız yalnızca göstergedir.

## Bağlam

Tasarım sistemi fazlarında (faz 6) formlar ikon başlıklı bölüm kartlarına ayrıldı; ancak yerleşim koddaydı. Ürün kuralı "sektöre özel davranış metadata ile tanımlanır" (CLAUDE.md) ve "metadata-driven form desteği" hedefi, düzenin firma tarafından değiştirilebilmesini gerektirir. Mevcut metadata altyapısı (özel alanlar Y9a, seçim listeleri Y9b, lookup ayarı ADR-0016) aynı deseni izler: kodda tanım, firmada özelleştirme satırı, `core.customization.manage` izni, denetim kaydı.

## Model

| Katman | Ne | Nerede |
| --- | --- | --- |
| Tanım | `FormDefinition(EntityKey, DisplayName, Tabs, Sections, Fields)`; alan: `Key` (kayıt alanının teknik adı), `Label`, `Section`, `Span` (3/4/6/8/12), `Required` (kodun zorunluluğu), `Fixed` (gizlenemez, zorunluluğu değişmez) | `Modulix.Core.Contracts/Modules/FormDefinition.cs`; CRM tanımları `Modulix.Modules.Crm/CrmForms.cs`; katalog doğrulaması `ModuleCatalog.Forms.cs` (ilk sekme `genel`, `ek` bölümü zorunlu) |
| Özelleştirme | `TenantFormLayout` (TenantId, EntityKey, LayoutJson?, Version, UpdatedAt/By); `LayoutJson` = `FormLayoutInput` (sekmeler, bölümler, alan yerleşimleri). Satır silinmez; "varsayılana dön" JSON'u boşaltır ve sürümü ilerletir | `Modulix.Core/Customization/FormLayout.cs`, tablo `meta.FormLayouts` (RLS, migration `FormLayouts`) |
| Çözümleme | `FormLayoutRules.Resolve(tanım, özel alanlar, kayıtlı düzen)` → etkin düzen: sekme/bölümler firmanın; düzende olmayan alanlar (sonradan eklenen özel alan) varsayılan bölümüne eklenir; bölümü yoksa ilk bölüme düşer; sabit alan görünür kalır; zorunluluk azaltılamaz | `FormLayoutRules` (birim testli) |
| Doğrulama | `FormLayoutRules.Validate`: 1–8 sekme, 1–24 bölüm, anahtar `[a-z0-9-]`, ad ≤ 60; bölüm var olan sekmeye, alan var olan bölüme bağlı; genişlik 3/4/6/8/12; bilinmeyen / eksik / tekrar eden alan reddedilir; sabit alan gizlenemez ve zorunluluğu değişmez; kodun zorunlu alanı isteğe bağlı yapılamaz | aynı |
| Sunucu denetimi | `IFormLayoutProvider.GetRequiredFieldsAsync` → boru hattı `RecordRequiredFields.Check`: oluştururken firmanın eklediği zorunlu alanlar dolu olmalı; güncellemede yalnızca gönderilip boşaltılan alan reddedilir (eski kayıt zorunluluktan önce açılmış olabilir); özel alanlar `custom.{anahtar}` adıyla birleşmiş özel alan nesnesinde aranır | `RecordPipeline` (özel alan hazırlığından sonra, depo doğrulamasından önce) |
| API | `GET /api/form-layouts` (her üye; açık modüllerin formları, etkin düzen); `GET/PUT/DELETE /api/customization/form-layouts/{kayıtTürü}` (`core.customization.manage`; PUT gövdesi `SaveFormLayoutRequest` + `Version`, eski sürüm 409, geçersiz düzen 422 alan hatalarıyla, tanımsız form 404; DELETE `?version=` varsayılana döner) | `Modulix.Api/Customization/FormLayoutEndpoints.cs` |
| Denetim | `core.form-layout`: form, sekmeler, bölümler, zorunlu alanlar, gizli alanlar, düzen kaynağı | `FormLayoutService` |
| Arayüz | `LayoutForm` bileşeni: sekmeler (`TabBar`, `form-` önekli anahtarlar), bölüm kartları (`FormSection`), 12 kolonlu `form-grid`; yerleşik alanın girdisini editör `Field` şablonuyla verir (anahtara göre `switch`), özel alanlar `CustomFieldInputs Only=…` ile aynı yerde; gizli alan ve boş bölüm çizilmez; izne/duruma bağlı alanlar editörün `Shows` süzgeciyle atlanır. Yönetim ekranı `Pages/Admin/FormDesigner.razor` (`/yonetim/form-tasarimi`): ok düğmeleri ve "taşı" listeleri (klavyeyle çalışır), yeni sekme/bölüm, ikon, genişlik, zorunlu, görünür, varsayılana dön | `Modulix.Web` |

Alan anahtarı kayıt alanının teknik adıdır (`taxNumber`, `custom.arac_tipi`); hem düzenin hem sunucu denetiminin ortak dilidir. Yalnızca ekranda anlamı olan alanlar (`isAllDay`, `logAsDone`) tanımda **sabit** işaretlenir ki zorunlu kılınamasın (sunucu denetimi onları kayıtta arayamaz).

## Sınırlar ve yapılmayanlar

- Alan **eklemek** bu ekranın işi değildir: yeni alan Özel alanlar ekranından tanımlanır, burada yalnızca yerleşir ve zorunluluğu artırılabilir. Özel alanın kendi zorunluluğu (tanım) burada kaldırılamaz.
- Düzen tenant genelidir; rol ya da şirket (ADR-0008) başına farklı düzen yoktur. Koşullu görünürlük ("X doluysa Y göster"), alan etiketini değiştirme ve sürükle-bırak bu ADR'nin dışındadır.
- Detay (salt okunur) sayfaları düzeni kullanmaz; yalnızca ekleme/düzenleme formları (Firma, Kişi, Fırsat, Lead, Aktivite). Teklif ve yönetim formları tanım eklenince aynı altyapıyı kullanır.
- Sunucu denetimi yalnızca "boş olamaz" kuralıdır; biçim/aralık kuralları alanın kendi doğrulamasındadır.
- Formlar düzen yüklenemezse (API hatası) kaydetmeyi kapatır ve uyarı gösterir; varsayılan düzen istemcide yoktur (tek doğruluk kaynağı sunucudur).

## Testler

- Birim: `FormLayoutRulesTests` (varsayılan düzen, kayıtlı düzenin uygulanması, eksik/bilinmeyen/sabit/zorunlu kuralları).
- Entegrasyon: `FormLayoutTests` — mutlu yol (yeni sekme + bölüm, taşıma, zorunluluk, gizleme, sürüm, denetim kaydı, varsayılana dön), doğrulama hataları, tenant izolasyonu (başka firmanın düzeni ve zorunluluğu etkilenmez), yetki (satış temsilcisi düzen yazamaz), sunucuda zorunlu alan denetimi (oluşturma, güncellemede boşaltma, özel alan).
