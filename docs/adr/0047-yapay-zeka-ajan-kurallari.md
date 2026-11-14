# ADR-0047: Yapay zekâ ajanı ve AI otomasyonu kuralları

- **Durum:** Öneri, 14 Kasım 2026. Kod yazılmadı; bu belge yapay zekâ ile ilgili ilk kod dilimi başlamadan önce onaylanmalıdır.
- **Karar (öneri):** Yapay zekâ Modulix'te ayrı bir yetkili kimlik değil, **çağıran kullanıcının vekilidir**. Yalnızca modüllerin beyan ettiği araçları, kayıt boru hattı ve otomasyon aksiyon sözleşmesi (ADR-0027) üzerinden kullanır. Her aracın etkisi `AutomationSideEffect` ile sınıflanır ve etkiye göre bir onay merdiveninden geçer. Okuma serbesttir, taslak etiketlenir, yazma önce planlanıp kullanıcıya onaylatılır. Geri alınamaz ya da dışarıya etki eden işlemleri yapay zekâ yapmaz, yalnızca önerir. Her çağrı denetim kaydına ve kullanım defterine yazılır; tenant verisi tenant dışına, yetkisiz alan modele çıkmaz.

> Numara notu: 0046 açık bir PR'da ("modül dikey dilimleri") kullanılıyor. Bu taslak 0047 aldı; birleştirmeden önce numara yeniden kontrol edilmeli.

## Problem

CLAUDE.md iki kural koyuyor: "Yapay zekâ yalnızca tenant ve kullanıcı yetkisi sınırları içinde, modül servisleri üzerinden çalışır" ve "tüm AI işlemleri denetim kaydına yazılır". Bu kurallar bugün yalnızca niyet olarak duruyor:

- `Modulix.AI` projesi boş (yalnızca `.csproj`).
- ADR-0027 ajan için bağlantı noktalarını hazırladı ama kullanmadı: `AutomationCallerKind.Agent` ("bugün hiçbir çağrı yolu üretmez"), `AutomationSideEffect` (Read/Draft/Write), `AutomationActionDefinition.SupportsDryRun`, `AutomationActionRequest.DryRun`, denetimde `ExecutionSource.AiTool` / `AuditSource.AiTool`. ADR-0027'de adı geçen `AutomationUsage` kullanım defteri kodda yok.
- ADR-0014 müşteri adayı için araç adlarını listeledi (`SearchLeads`, `GetLeadDetails`, `ListLeadsNeedingFollowUp`, `GetLeadActivityHistory`, `SuggestLeadNextAction`, `CreateLeadFollowUpActivity`) ve bir kural koydu: ajan müşteri adayını kendiliğinden elemez, birleştirmez, dönüştürmez. Bu kural yalnızca o ADR'de yazılı; diğer modüller için genel bir kural yok.

İlk yapay zekâ özelliği kodlanmadan önce kurallar yazılmazsa her modül kendi sınırını kendisi çizer. Paylaşımlı veritabanında bir hata tenant'lar arası veri sızıntısı ya da kullanıcının yetkisini aşan yazma olarak sonuçlanır. Ayrıca KVKK açısından kişisel verinin yurt dışındaki bir model sağlayıcısına gitmesi kendi başına hukuki bir karardır.

## Seçenekler

1. **Ajana servis hesabı ve kendi rolü ver.** Uygulaması kolaydır. Ama yapay zekâ kullanıcının göremediği veriyi görür, kayıt kapsamı (kendi/takım/birim/şirket) delinir ve CLAUDE.md'deki kural çiğnenir. **Reddedildi.**
2. **Yapay zekâ doğrudan veritabanına sorgu yazsın (metinden SQL).** Esnektir. Ama ham SQL'de tenant süzgeci ve alan yetkisi garanti edilemez, kayıt boru hattı atlanır. **Reddedildi.**
3. **Kullanıcının vekili olarak yalnızca beyan edilmiş araçlar (önerilen).** Mevcut otomasyon aksiyon sözleşmesini, kayıt boru hattını ve denetimi yeniden kullanır; yetki sunucuda ve sorgu düzeyinde kalır. Maliyeti: her modül araçlarını tek tek beyan eder, yapay zekânın erişimi beyan edilenle sınırlı kalır. Bu sınırlama bilerek seçilir.

## Karar

### Kural 1 — Kimlik: yapay zekâ kullanıcının vekilidir

- Her yapay zekâ çağrısı bir kullanıcı oturumundan başlar ve `AutomationCallContext(Kind = Agent, UserId = <çağıran kullanıcı>)` taşır. Kullanıcısız ajan çağrısı yoktur. Zamanlanmış ya da kural motorundan tetiklenen AI adımları, kuralın sahibinin değil kuralın **çalıştırma kimliğinin** (ADR-0027) yetkisiyle çalışır ve Kural 3'teki onay merdiveninde bir üst basamağa çıkar.
- Tenant istekten değil oturumdan belirlenir (genel kural). Modelin ürettiği metinde geçen tenant, firma ya da kullanıcı kimliği yetki kararında kullanılmaz.
- Destek erişimi (`MembershipStatus.Support`) ile açılmış oturumda yapay zekâ yazma araçları kapalıdır, yalnızca okuma araçları çalışır.
- Lisans: AI araçları sahibi olan modülün lisansına tabidir. Salt okunur moddaki modülün Draft/Write araçları çağrılamaz (`IModuleLicense.IsWritableAsync`). Yapay zekâ kullanımı ayrıca tenant başına açılıp kapatılan bir özelliktir; kapalıyken hiçbir araç ve hiçbir model çağrısı çalışmaz.

### Kural 2 — Araçlar: yalnızca modüllerin beyan ettiği araçlar

- Yapay zekânın kullanabileceği her araç bir modül tarafından beyan edilir. Yazan araçlar mevcut `AutomationActionDefinition` kayıtlarıdır; aynı aksiyon hem kural motoru hem ajan tarafından çağrılır, ayrı bir "AI yazma yolu" yazılmaz. Okuma araçları (arama, ayrıntı, geçmiş, özet sorgusu) modülün sözleşme projesinde (ADR-0044) beyan edilen yeni bir salt okunur araç tanımıyla açılır.
- Her okuma aracı kayıt boru hattının okuma yolunu kullanır: EF süzgeci + RLS + kayıt kapsamı + alan yetkisi (`IModule.SensitiveFields`). Araç, kullanıcının listede göreceğinden fazlasını döndüremez.
- `Modulix.AI` yalnızca `Modulix.Core.Contracts`'a ve modül sözleşme projelerine başvurur (mevcut proje kuralı). Modülün ana projesine, `DbContext`'e ya da ham SQL'e erişmez. Metinden SQL üretilmez.
- Anlamsal arama (vektör dizini) eklenirse dizindeki her parça `TenantId` ve kaynak kayıt kimliği taşır. Arama sonucu kullanıcıya dönmeden önce kaynak kayıt kayıt boru hattından yeniden okunur; okunamayan parça atılır. Dizin, tenant silme ve dışa aktarma kapsamındadır (tenant-delete dizini de temizler).

### Kural 3 — Onay merdiveni: etkiye göre

| Basamak | Örnek | Yapay zekâ ne yapar | İnsan ne yapar |
|---|---|---|---|
| **Okuma** (`Read`) | Kayıt özeti, liste süzme, sonraki adım önerisi, risk sinyali | Doğrudan çalıştırır | — |
| **Taslak** (`Draft`) | E-posta taslağı, not, aktivite önerisi, toplantı özeti | Üretir, "Yapay zekâ üretimi" etiketiyle taslak olarak bırakır | Düzenler, kaydeder ya da atar |
| **Tek kayıt yazma** (`Write`, geri alınabilir) | Takip aktivitesi oluştur, alan güncelle, sorumlu öner | Önce `DryRun` ile planlar, değişikliği alan alan gösterir | Onaylar; onay sonrası aynı plan idempotent anahtarla çalışır |
| **Yasak** | Silme, birleştirme, müşteri adayını dönüştürme ya da eleme, fırsatı kazanıldı/kaybedildi yapma, fiyat/indirim/teklif onayı, dışarıya e-posta/SMS gönderimi, toplu işlem, yetki ya da ayar değişikliği | **Yapmaz.** Yalnızca öneri ve gerekçe üretir | Kendi ekranından, kendi yetkisiyle yapar |

- `SupportsDryRun = false` olan bir `Write` aksiyonu ajana açılmaz.
- Onay, planın kendisine bağlıdır: onaydan sonra plan değişirse (kayıt bu arada güncellendiyse) yeniden onay istenir.
- Bir onay yalnızca o plan içindir; "bundan sonra hep onayla" seçeneği yoktur.
- Tenant yöneticisi bir aracı tamamen kapatabilir ya da basamağını yükseltebilir (ör. Taslak aracını da onaya bağlar), düşüremez. Yasak listesi tenant ayarıyla açılamaz (dinamik tasarım ilkesinin bilinçli istisnası).

### Kural 4 — Dış içerik veridir, talimat değildir

- E-posta gövdesi, web formu, not, ek dosya, içe aktarılan satır gibi kullanıcı dışı kaynaklardan gelen metin modele **veri** olarak, ayrı işaretlenmiş bir bölümde verilir.
- Dış içerikten başlayan akışlarda (ör. "gelen e-postayı sınıflandır") yalnızca okuma ve taslak araçları açıktır; yazma araçları kapalıdır.
- Modelin araç çağrısındaki kimlik ve parametreler sunucuda doğrulanır: kayıt kimliği kullanıcının erişebildiği bir kayıt olmalıdır, değilse "bulunamadı" döner (mevcut 422 davranışı).

### Kural 5 — Veri koruma ve KVKK

- Modele giden bağlam en az veri ilkesiyle kurulur: yalnızca aracın döndürdüğü alanlar gider. Alan yetkisi olmayan ve hassas işaretli alanlar (TCKN, banka hesabı, maaş, sağlık bilgisi vb.) maskelenmeden gönderilmez.
- Model sağlayıcı, bölge ve "veriyle model eğitilmez" koşulu platform yapılandırmasıdır. Tenant, yapay zekâyı açarken yurt dışı aktarım bilgilendirmesini görür ve onaylar; onay denetim kaydına yazılır. Hukuki metin bu ADR'nin kapsamı dışındadır (açık soru 1).
- On-prem kurulum için sağlayıcı yapılandırmayla değiştirilebilir (yerel model dahil); müşteriye özel kod yazılmaz.
- Model girdisi ve çıktısının tam metni varsayılan olarak saklanmaz; denetimde özet ve kayıt bağlantıları tutulur. Hata ayıklama için tam metin saklama tenant başına, süreli ve kapalı varsayılanla açılır.

### Kural 6 — Denetim ve kullanım defteri

- Her araç çağrısı denetim kaydına `AuditSource.AiTool` ile yazılır: kullanıcı, tenant, araç, basamak, hedef kayıtlar, sonuç, onaylayan kullanıcı ve onay zamanı, model ve sürümü, bağıntı (correlation) kimliği.
- Yazma araçları kayıt boru hattından geçtiği için veri değişikliği, denetim ve outbox aynı transaction'dadır (genel kural); ayrıca bir şey yazılmaz.
- ADR-0027'de tasarlanan kullanım defteri (`AutomationUsage`) bu ADR ile kodlanır: tenant, kullanıcı, araç, model, giriş/çıkış token sayısı, süre, sonuç. Kota ve faturalama bu defterden okunur.
- Yapay zekâ üretimi metin kalıcı olarak işaretlenir (ADR-0027 madde 5): "şablondan" ve "yapay zekâ üretimi" ayrı kaynaklardır ve kayıt geçmişinde görünür.

### Kural 7 — Açıklanabilirlik ve karar sınırı

- Her öneri, skor ya da sınıflandırma kısa bir gerekçe ve dayandığı kayıtların bağlantısıyla döner. Gerekçesiz öneri arayüzde gösterilmez.
- Yapay zekâ skoru kural motorunda **koşul** olarak kullanılabilir (ör. "risk yüksekse yöneticiye bildir"), ama kişi ya da firma aleyhine tek başına sonuç doğuran bir **karar** veremez: müşteri adayını elemek, kampanyadan çıkarmak, başvuruyu reddetmek gibi işlemler insan onayı ister. İK modülünde aday ve çalışan hakkında skor üretilmez (açık soru 3).

### Kural 8 — Kota, döngü ve hata davranışı

- Tenant başına günlük ve aylık kota (çağrı ve token) platform tarafından tanımlanır; kota dolunca araçlar 429 ile ve Türkçe açıklamayla durur, iş akışı kırılmaz.
- AI adımı içeren kural zincirinde `AutomationCallContext.Depth` sınırı geçerlidir; AI → olay → kural → AI döngüsü en fazla bir kez döner.
- Model hata verirse ya da zaman aşımına uğrarsa hiçbir şey yazılmaz; taslak ve plan yarım bırakılmaz. Yapay zekâ çalışmasa da ürünün bütün işlevleri elle kullanılabilir.

### Kural 9 — Test: beşinci test

Proje kuralı her özelliğin dört testle teslim edilmesini ister (mutlu yol, doğrulama, tenant izolasyonu, yetki). Yapay zekâ aracı olan her özellik için ek olarak:

- **Bağlam izolasyonu:** A tenant'ında çalışan aracın modele gönderdiği bağlamda B tenant'ının ve kullanıcının göremediği kayıtların verisi yoktur (model yerine kayıt tutan sahte sağlayıcıyla test edilir).
- **Basamak testi:** `Write` aracı onaysız çağrılınca hiçbir şey yazmaz; yasak listedeki bir işlem ajan bağlamında reddedilir.

Mimari testler: `Modulix.AI` için proje başvuru kuralı (yalnızca sözleşme projeleri), ajana açılan her `Write` aksiyonunun `SupportsDryRun = true` olması ve yasak listedeki aksiyonların ajan kaydında bulunmaması.

## Arayüz

- Yapay zekâ önerileri kayıt ekranında ayrı bir panelde, öneri olarak görünür; kayıt alanına kendiliğinden yazılmaz.
- Onay adımı değişikliği alan alan gösterir ("Önce → Sonra"); birincil düğme "Onayla ve uygula", ikincil "Vazgeç" (tasarım kuralları geçerli).
- Yapay zekâ üretimi taslak ve notlarda kalıcı bir rozet bulunur.

## Dilim planı

| Dilim | Kapsam | Basamak |
|---|---|---|
| 0 | Bu ADR'nin onayı; sağlayıcı ve KVKK açık sorularının yanıtı | — |
| 1 | `Modulix.AI` iskeleti: sağlayıcı soyutlaması, tenant aç/kapa, kullanım defteri, denetim, sahte sağlayıcıyla test altyapısı ve mimari testler | — |
| 2 | CRM okuma araçları: müşteri adayı ve fırsat arama/ayrıntı/geçmiş, kayıt özeti, sonraki adım önerisi | Okuma |
| 3 | Taslaklar: takip e-postası taslağı, aktivite/not özeti | Taslak |
| 4 | Onaylı yazma: `CreateLeadFollowUpActivity` ve fırsat takip aktivitesi, dry-run + onay ekranı | Tek kayıt yazma |
| 5 | Kural motorunda AI koşulu/adımı (sınıflandırma, skor) | Okuma/Taslak |
| 6 | Diğer modüller (Destek, Lojistik) kendi araçlarını beyan eder | — |

Her dilim kendi PR'ında, dört + beşinci testle teslim edilir.

## Açık sorular

1. **Model sağlayıcı ve bölge:** hangi sağlayıcı, hangi veri merkezi, sözleşmede eğitim yasağı. Yurt dışı aktarım için açık rıza mı, standart sözleşme mi (hukuk kararı)?
2. **Fiyatlandırma:** yapay zekâ ayrı lisanslanan bir modül mü (`IModuleLicense`), yoksa her modüle kota ile mi dahil?
3. **İK:** İK modülünde yapay zekâ tamamen kapalı mı kalsın, yoksa yalnızca belge özeti gibi kişiye skor üretmeyen araçlar mı açılsın?
4. **Tam metin saklama:** hata ayıklama için en fazla kaç gün saklanabilir?

## Sonuçlar

- **Olumlu:** Yetki, tenant izolasyonu ve denetim mevcut altyapıdan gelir; yapay zekâ için ikinci bir güvenlik modeli yazılmaz. Kural motoru ve ajan aynı aksiyonları paylaşır.
- **Olumsuz:** Ajan yalnızca beyan edilmiş araçlarla çalışır; "her soruyu yanıtlayan" serbest bir asistan bu modelle yapılamaz. Onay adımı kullanıcıya bir tıklama ekler. Bu iki kısıtlama bilerek seçilmiştir.
- **Geri dönüş:** Yapay zekâ tenant başına ve platform genelinde kapatılabilir; kapalıyken ürün değişmeden çalışır.
