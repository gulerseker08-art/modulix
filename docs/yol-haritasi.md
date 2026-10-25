# Yol haritası

- **Güncelleme:** 25 Ekim 2026
- **Hedef:** Modulix, birden fazla sektörün iş süreçlerini aynı platformda yöneten çok kiracılı (multi-tenant) bir SaaS'tır. Sektörler ortak bir çekirdeği paylaşır; sektöre özgü iş modüllerle eklenir, sektör profilleriyle (Y9c) paketlenir.
- **Pilot müşteri:** lojistik sektörü.
- **Modül sırası:** Çekirdek → CRM → Lojistik → Antrepo → İK → Muhasebe.

## Kararlar

| No | Karar |
| --- | --- |
| K3 | Modüller arası her yeni sözleşme için ADR yazılır (ör. [ADR-0004](adr/0004-kayit-devri.md), [ADR-0005](adr/0005-rol-sablonlari.md)). |
| K4 | Pilot sırası yukarıdaki gibidir. Bir modüle başlamadan önce onun ihtiyaç duyduğu çekirdek işler biter (bkz. [Modül–çekirdek bağımlılıkları](#modül-ve-çekirdek-bağımlılıkları)). Modüle özgü görünen ama ikinci bir modülün de ihtiyaç duyacağı her yetenek modülde değil çekirdekte yapılır. |
| K5 | Modüller birbirine proje referansı vermez (mimari test). Birbirleriyle yalnızca `Modulix.Core.Contracts` üzerinden konuşur: ortak ana veri (Ç3), olaylar (Ç1) ve kayıt boru hattı. |

## Mevcut durum

Çekirdeğin kimlik, yetki ve özelleştirme katmanı tamam. Ayrıntılar README'de.

| İş | Kapsam |
| --- | --- |
| Görev 2–4, E4 | SQL Server + RLS, giriş ve aktif tenant, modül kataloğu, modül hakları, roller ve izinler |
| Kayıt boru hattı | İzin, kapsam, doğrulama, audit ve outbox kaydı tek yoldan; genel `/api/records` ucu |
| Y1–Y3 | Yönetici sistem rolü, kullanıcı yönetimi, davet, kayıt devri (ADR-0004), rol ekranları, hazır roller (ADR-0005) |
| Y4 | Platform yönetimi: firma açma, kapatma, modül hakları, katalog |
| Y5 | Denetim kaydı, CSV, kayıt geçmişi, oturum olayları |
| Y6 | Tipli firma ayarları, bölgesel biçimler, logo |
| Y7 | Hesabım, sunucu oturumları, iki adımlı doğrulama |
| Y8 | Ekipler, kayıt kapsamı, hassas alanlar |
| Y9 | Özel alanlar, düzenlenebilir listeler, sektör profilleri |
| E-posta | Kuyruk, arka plan gönderimi, yeniden deneme |
| CRM | Firma, kişi, fırsat, aktivite; huni, firma detayı, mobil kart görünümü |
| Lojistik | Yalnızca modül tanımı ve izinleri (sevkiyat, taşıyıcı, maliyet); tablo ve ekran yok |

Eksik olanlar: outbox'a yazılan olayları işleyen bir işleyici yok (eski E12), modüller arası ortak veri yok, dosya eki, bildirim, içe aktarma, belge üretimi, raporlama, abonelik ve canlı ortam işletimi yok. `Modulix.AI` projesi boş.

## Çekirdek (Ç)

Çekirdek işleri dört dalgada yapılır. Her dalga bir modülün önündeki engeli kaldırır.

| Dalga | Ne zaman | İşler |
| --- | --- | --- |
| **D1: CRM pilotu** | CRM'in pilot müşteride canlıya çıkmasından önce | Ç1, Ç3a, Ç4, Ç5a, Ç7, Ç13a, Ç14, Ç15a |
| **D2: Lojistik** | Lojistik modülüne başlamadan önce | Ç2, Ç3b, Ç6a, Ç8, Ç9, Ç10a, Ç11a |
| **D3: Antrepo ve İK** | Antrepo modülünden önce | Ç3c, Ç6b, Ç10b, Ç12, Ç16 |
| **D4: Muhasebe ve büyüme** | Muhasebe modülünden önce | Ç3d, Ç11b, Ç13b, Ç15b, Ç17 |

Her iş için "Bitti" ölçütü: uç noktalar izin/modül korumalı, yönetim işlemleri denetim kaydında, yeni tenant tabloları RLS'li, entegrasyon testi var, README bölümü yazılmış. Modüller arası yeni bir sözleşme getiren işlerde ADR yazılır (K3); aşağıda **ADR** ile işaretlidir.

---

### Ç1 · Olay altyapısı (D1) — ADR

Bugün boru hattı her kayıt değişikliğinde `platform.OutboxMessages`'a olay yazıyor ama okuyan yok. Lojistikte "fırsat kazanıldı → sevkiyat taslağı", muhasebede "sevkiyat teslim edildi → fatura taslağı" gibi her modüller arası akış buna dayanır.

- **Outbox işleyicisi:** arka planda okur, tenant bağlamını açıkça kurar (ADR-0002), aboneleri çağırır, işlenmiş olarak işaretler. Birden fazla uygulama örneğinde her olay bir kez alınır (e-posta kuyruğundaki kilit kalıbı).
- **Abone sözleşmesi** (`Modulix.Core.Contracts`): modül `IModule` üzerinden hangi olay türlerini dinlediğini bildirir. Olay yükü sürümlüdür; abone bir olayı birden fazla kez alabileceğini varsayar (idempotent).
- **Hata yönetimi:** artan aralıklarla yeniden deneme, sınırı aşan olay "başarısız" durumunda kalır, platform ekranında görünür ve elle yeniden denenir.
- **Sıra:** aynı kaydın olayları yazılış sırasıyla işlenir.
- **Temizlik:** işlenmiş olaylar saklama süresinden sonra silinir (Ç2'ye kadar aynı işleyici yapar).
- Ç5 (bildirim), Ç6 (otomasyon) ve Ç11 (webhook) bu altyapıyı kullanır.

### Ç2 · Arka plan işleri ve zamanlayıcı (D2)

E-posta işleyicisi ve outbox işleyicisi kendi döngüleriyle çalışıyor. Lojistikte geciken sevkiyat uyarısı, antrepoda günlük ardiye hesabı, muhasebede kur çekme gibi zamanlanmış işler gelecek.

- Ortak iş çerçevesi: iş tanımı, zamanlama (cron), tenant başına çalıştırma, tenant bağlamı kurma, tek örnekte çalışma kilidi.
- Modüller işlerini `IModule` üzerinden bildirir; iş, modülü kapalı tenant'ta çalışmaz.
- İş geçmişi ve hata kaydı; platform ekranında son çalışmalar, elle tetikleme.
- Arka plan işinin yaptığı kayıt değişiklikleri boru hattından geçer, denetim kaydına kaynak "Arka plan işi" olarak yazılır (kaynak zaten tanımlı).
- Mevcut e-posta ve outbox işleyicileri bu çerçeveye taşınır.

### Ç3 · Ortak ana veri (D1–D4) — ADR

Pilot sırasının en kritik çekirdek işi. CRM'deki firma, lojistikte yükleyici/alıcı, antrepoda ardiye müşterisi, muhasebede cari hesaptır. Her modül kendi müşteri tablosunu tutarsa aynı firma dört kez girilir ve modüller birbirine proje referansı veremediği için (K5) bağ kurulamaz.

- **Ç3a · Taraf (cari) ve kayıt referansı (D1):**
  - Çekirdekte ortak **Taraf** kaydı: ünvan, vergi dairesi/no veya TCKN, tür (tüzel/gerçek kişi), iletişim. CRM firması bu kayda bağlanır; diğer modüller yeni firma açmaz, Taraf'a referans verir.
  - Modüller arası **kayıt referansı sözleşmesi:** bir modülün kaydı başka modülün kaydına kayıt türü + kimlikle bağlanır; boru hattı referansın varlığını, aynı tenant'ta olduğunu ve okuyanın görebildiğini denetler. Seçim kutuları (lookup) için ortak arama ucu.
  - Tekilleştirme: aynı vergi numarasıyla ikinci taraf açılmaz; birleştirme (merge) aracı.
  - Mevcut CRM firmalarını Taraf'a taşıyan migration.
- **Ç3b · Adres ve konum (D2):** ülke, il, ilçe, mahalle sözlüğü; yapılandırılmış adres; taraf başına çoklu adres (fatura, sevk, depo); isteğe bağlı koordinat. Lojistikte yükleme/boşaltma noktası, antrepoda depo adresi bunu kullanır.
- **Ç3c · Ölçü birimi ve ürün/kalem (D3):** birim sözlüğü (adet, kg, m³, palet, TEU) ve dönüşümler; ortak ürün/hizmet kartı (antrepoda stok kalemi, muhasebede fatura satırı, lojistikte navlun hizmeti).
- **Ç3d · Para birimi ve döviz kuru (D4):** para birimi sözlüğü, TCMB günlük kurları (Ç2 işi), kayıt tarihindeki kurla çevirme, tutar + para birimi ortak tipi. Lojistik maliyetleri D2'de tek para birimiyle başlayabilir.

### Ç4 · Dosya ve ekler (D1)

CRM'de teklif dosyası, lojistikte CMR/irsaliye fotoğrafı, antrepoda beyanname, İK'da özlük belgesi.

- Depolama soyutlaması: geliştirmede yerel disk, canlıda nesne depolama (S3 uyumlu); veritabanında yalnızca üst bilgi (RLS'li).
- Her kayda ek: yükleme, listeleme, indirme, silme; ekin yetkisi kaydın yetkisidir.
- Tür içerikten anlaşılır (logo kalıbı), boyut sınırı, tenant başına kota (Ç12 ile ilişkili), çalıştırılabilir dosya reddi.
- İndirme kısa ömürlü bağlantıyla; dosya adı ve yükleyen denetim kaydında.
- Telefonda kameradan doğrudan yükleme (lojistik teslim fotoğrafı için gerekli).

### Ç5 · Bildirimler (D1–D3)

- **Ç5a · Uygulama içi bildirim (D1):** üst çubukta zil, okunmamış sayısı, bildirim listesi. İlk kullanım: kayıt kişiye atandı, aktivitenin günü geldi. Modüller bildirim türlerini `IModule` üzerinden bildirir.
- **Ç5b · Tercihler ve kanallar (D2):** kişi bildirim türü başına kanal seçer (uygulama, e-posta); e-posta şablonları firma logosu ve diliyle. Toplu özet e-postası (günlük).
- **Ç5c · Mobil anlık bildirim ve SMS (D3):** PWA web push; SMS sağlayıcı soyutlaması (sürücüye teslim bilgisi, müşteriye sevkiyat durumu).

### Ç6 · İş akışı (D2–D3)

- **Ç6a · Onay akışları (D2):** kayıt türü için onay tanımı (ör. belirli tutarı aşan teklif, maliyet kalemi); onaycı rol veya kişi, sıralı adımlar, onay/ret ve gerekçe. Onay bekleyen kayıt kilitlenir. Bildirim (Ç5) ve denetim kaydı.
- **Ç6b · Kural tabanlı otomasyon (D3):** "olay olunca, koşul tutarsa, eylem yap": alan ata, görev oluştur, bildirim gönder, e-posta gönder. Olaylar Ç1'den gelir, eylemler boru hattından geçer (kaynak: otomasyon). Döngü koruması.

### Ç7 · İçe ve dışa aktarma (D1)

Pilot müşterinin mevcut verisi (Excel'deki müşteri listesi, kişiler) olmadan CRM canlıya çıkamaz.

- Excel/CSV içe aktarma sihirbazı: dosya yükle, sütun eşle (özel alanlar dahil), önizleme, satır satır doğrulama hatası raporu, onayla.
- Her satır boru hattından geçer (izin, doğrulama, liste değerleri, audit kaynağı "İçe aktarma"); büyük dosyalar arka planda, ilerleme göstergesiyle.
- Tekrar eden kayıtlarda seçim: atla, güncelle.
- İçe aktarma geri alınabilir (aynı içe aktarmanın kayıtlarını toplu sil).
- Liste ekranlarından görünür sütun ve filtreyle Excel'e dışa aktarma; hassas alanlar (Y8b) izne göre; dışa aktarma denetim kaydında.

### Ç8 · Belge üretimi ve numaralandırma (D2)

- **Belge numaralandırma:** kayıt türü başına seri ve sayaç (ör. `SVK-2026-000123`), yıl başında sıfırlama, eşzamanlı isteklerde boşluksuz/çakışmasız. Muhasebe ve e-belgeler için ön koşul.
- **PDF şablonları:** teklif, sevk formu, iş emri; firma logosu ve profili (Y6) ile. Modüller şablonlarını bildirir; firma başlık/alt bilgi ve bazı alanları özelleştirir.
- Üretilen belge kayda ek olarak saklanır (Ç4), e-postayla gönderilebilir.

### Ç9 · Ekran altyapısı ve arama (D2)

Lojistik, CRM'den çok daha fazla ekran getirecek; her birini elle yazmak tek geliştiriciyle sürdürülemez.

- **Modül menüsü:** modüller menü öğelerini ve sırasını bildirir; menü açık modüllere ve izinlere göre kurulur (Y9c notu: bugün menü elle).
- **Layout tabanlı form ve liste** (ADR-0003): alan sırası, bölümler, mobilde kart önceliği tanımdan gelir; firma bölüm/sıra özelleştirebilir. Özel alanlar (Y9a) aynı tanıma girer.
- **Kayıtlı görünümler:** kişinin kaydettiği filtre ve sütun seçimi; firmaya paylaşılan görünümler.
- **Genel arama:** üst çubukta tüm açık modüllerde ad/numara araması; sonuçlar yetki ve kapsama göre.
- **Ortak kayıt bileşenleri:** etiketler, notlar/yorumlar ve @bahsetme, takip edenler. CRM'deki aktivite akışı genelleştirilir.

### Ç10 · Raporlama ve gösterge paneli (D2–D3)

- **Ç10a · Gösterge paneli (D2):** modüller widget'larını bildirir (sayı, liste, grafik); kişi kendi panelini düzenler; rol başına varsayılan panel. Widget verisi kapsam ve hassas alan kurallarına uyar.
- **Ç10b · Rapor tasarlayıcı (D3):** kayıt türü seç, alan/filtre/gruplama/toplam seç, tablo veya grafik, kaydet, zamanlanmış e-posta (Ç2). Okuma için ayrı salt okur sorgu yolu; büyük raporlar arka planda.

### Ç11 · Entegrasyon (D2–D4) — ADR

- **Ç11a · Dış API ve webhook (D2):** tenant yöneticisinin oluşturduğu API anahtarı (kapsamlı izin, son kullanma, iptal, denetim kaydı); anahtarla `/api/records` erişimi; olaylara webhook aboneliği (Ç1), imzalı gönderim, yeniden deneme. İstek sınırı (rate limit) anahtar başına.
- **Ç11b · Entegratör çerçevesi (D4):** dış sistem bağlantılarının ortak yapısı: firma başına kimlik bilgileri (şifreli saklama), bağlantı testi, senkron günlüğü, hata ekranı. İlk kullanımlar: e-Fatura/e-Arşiv/e-İrsaliye özel entegratörü (GİB), banka hesap hareketleri, TCMB kurları. e-İrsaliye lojistikte gerekiyorsa D2'ye çekilir.

### Ç12 · SaaS işletimi: abonelik ve kullanım (D3)

Bugün modül hakları platform yöneticisince elle açılıyor. Pilotta bu yeterli; ikinci müşteriden itibaren gerekli.

- **Planlar:** plan = modüller + sınırlar (kullanıcı sayısı, depolama, aylık belge/e-posta). Firma bir plana bağlıdır; modül hakları plandan türetilir, platform yöneticisi istisna verebilir.
- **Sınırların uygulanması:** kullanıcı daveti, dosya yükleme vb. sınırda durur, firma yöneticisine anlaşılır uyarı.
- **Kullanım ölçümü:** kullanıcı, depolama, belge sayıları; platform ve firma ekranında.
- **Deneme süresi ve yaşam döngüsü:** deneme → aktif → ödeme gecikti (salt okur) → kapalı → veri silme (saklama süresi sonunda).
- **Faturalama:** ödeme sağlayıcısı entegrasyonu ve platformun kendi faturası; Muhasebe modülü hazır olunca Modulix kendi faturasını onunla keser.
- **Kendi kendine kayıt** ve firma kurulum sihirbazı (profil seçimi, ilk ayarlar, kullanıcı daveti, içe aktarma) bu işin sonunda; pilotta firma platform yöneticisince açılır.

### Ç13 · KVKK ve güvenlik (D1, D4)

- **Ç13a · Pilot öncesi (D1):** aydınlatma metni ve gizlilik sayfası (oturum olaylarındaki IP/tarayıcı bilgisi dahil, bkz. Y5b); kişisel veri envanteri (hangi tabloda hangi kişisel veri); veri saklama süreleri (oturum, denetim kaydı, e-posta kuyruğu, outbox); güvenlik başlıkları (CSP, HSTS) ve genel istek sınırı; bağımlılık taraması (CI'da var) ve sızma testi kontrol listesi.
- **Ç13b · Veri sahibi hakları (D4):** kişi/çalışan için erişim talebi (verisini dışa aktarma) ve silme talebi (anonimleştirme; yasal saklama süresi olan muhasebe kayıtları hariç); açık rıza kaydı; firma kapanışında veri dışa aktarma paketi.

### Ç14 · Canlı ortam ve işletim (D1)

Pilot müşteri canlıya çıkmadan önce zorunlu.

- **Dağıtım:** pilot VPS kurulumu (ADR-0001), ters vekil ve TLS, `App:PublicUrl`, gizli bilgilerin ortam değişkeninde/kasada tutulması; CI'dan tek komutla sürüm çıkma; migration'ın dağıtımda ayrı kullanıcıyla çalışması.
- **Yedekleme ve geri yükleme:** SQL Agent olmadığı için uygulamanın zamanlanmış işiyle tam + fark yedeği, sunucu dışına şifreli kopya, aylık geri yükleme provası (belgelenmiş). Express sürümünün Linux veritabanı boyut sınırı doğrulanır (ADR-0001 açık noktası).
- **Gözlemlenebilirlik:** yapılandırılmış log (tenant ve kullanıcı kimliğiyle, kişisel veri olmadan), hata izleme, temel metrikler (istek süresi, kuyruk derinliği, başarısız e-posta/olay), çalışma süresi izleme ve uyarı.
- **Platform sağlık ekranı:** kuyruklar, başarısız işler, yedeklerin son durumu.
- **Sürüm notları:** kullanıcıya "yenilikler" duyurusu.

### Ç15 · Kullanıcı deneyimi temeli (D1, D4)

- **Ç15a (D1):** uygulama teması ve tasarım dili (renk, tipografi, boşluk, bileşen kuralları; açık/koyu); boş durumlar, yükleniyor ve hata ekranlarının tek tipi; klavye ve erişilebilirlik denetimi; PWA (ana ekrana ekleme). Tema kararı bekleniyor.
- **Ç15b (D4):** çoklu dil altyapısı (metinlerin kaynak dosyalara taşınması, İngilizce). Bölgesel biçimler Y6'da hazır.

### Ç16 · Mobil saha kullanımı (D3)

Lojistikte sürücü ve depo personeli için. Ayrı uygulama değil, aynı PWA'nın sade görünümü (ADR-0003).

- Saha rolü için sadeleştirilmiş menü ve büyük dokunma hedefleri.
- Barkod/QR okuma (kamera), konum alma, imza alma.
- Zayıf bağlantıda işlem kuyruğu: çevrimdışı alınan teslim/okuma bağlantı gelince gönderilir; çakışma kuralları.

### Ç17 · AI altyapısı (D4)

`Modulix.AI` projesi boş; denetim kaydında "AI" kaynağı tanımlı.

- Sağlayıcı soyutlaması, firma başına açma/kapama ve kullanım sınırı (Ç12), istem ve yanıtların kişisel veri kuralına uygun günlüğü.
- AI yalnızca boru hattı üzerinden, işlemi isteyen kişinin yetkisiyle okur ve yazar; yazdığı değişiklik onaya sunulur.
- İlk kullanım adayları: e-posta/belgeden kayıt oluşturma (sevkiyat talebi), kayıt özeti, doğal dille arama.

---

## Modül ve çekirdek bağımlılıkları

✔ = modülden önce bitmeli · ○ = sonra da gelebilir

| Çekirdek işi | CRM pilotu | Lojistik | Antrepo | İK | Muhasebe |
| --- | :-: | :-: | :-: | :-: | :-: |
| Ç1 Olay altyapısı | ✔ | ✔ | ✔ | ✔ | ✔ |
| Ç2 Arka plan işleri | ○ | ✔ | ✔ | ✔ | ✔ |
| Ç3a Taraf ve kayıt referansı | ✔ | ✔ | ✔ | ○ | ✔ |
| Ç3b Adres ve konum | ○ | ✔ | ✔ | ✔ | ✔ |
| Ç3c Birim ve ürün kartı | | ○ | ✔ | | ✔ |
| Ç3d Para birimi ve kur | ○ | ○ | ○ | | ✔ |
| Ç4 Dosya ve ekler | ✔ | ✔ | ✔ | ✔ | ✔ |
| Ç5 Bildirimler | ✔ (a) | ✔ (b) | ✔ (c) | ✔ | ○ |
| Ç6 Onay ve otomasyon | ○ | ✔ (a) | ✔ | ✔ | ✔ |
| Ç7 İçe/dışa aktarma | ✔ | ✔ | ✔ | ✔ | ✔ |
| Ç8 Numaralandırma ve PDF | ○ | ✔ | ✔ | ○ | ✔ |
| Ç9 Ekran altyapısı ve arama | ○ | ✔ | ✔ | ✔ | ✔ |
| Ç10 Rapor ve panel | ○ | ✔ (a) | ✔ | ○ | ✔ |
| Ç11 Entegrasyon | | ✔ (a) | ○ | ○ | ✔ (b) |
| Ç12 Abonelik ve kullanım | ○ | ○ | ✔ | ✔ | ✔ |
| Ç13 KVKK ve güvenlik | ✔ (a) | ✔ | ✔ | ✔ | ✔ (b) |
| Ç14 Canlı ortam | ✔ | ✔ | ✔ | ✔ | ✔ |
| Ç15 Tema ve UX | ✔ (a) | ✔ | ✔ | ✔ | ✔ (b) |
| Ç16 Mobil saha | | ○ | ✔ | ○ | |
| Ç17 AI | ○ | ○ | ○ | ○ | ○ |

## Modüller

Modüllerin ayrıntısı, sırası gelince bu dosyada açılır. Her modül CRM kalıbını izler (`Domain`, `Persistence`, `Records`) ve kendi sektör profili katkısını (Y9c) getirir.

### M1 · CRM (pilot)

- Firma kaydının ortak Taraf'a (Ç3a) bağlanması
- Teklif yönetimi (kalemler, PDF, onay)
- Lojistiğe özel alanlar ve listeler (Lojistik firması profili)
- Pilot verisinin içe aktarılması (Ç7)
- Satış raporları ve panel widget'ları
- Pilot geri bildirimleri

### M2 · Lojistik

- Taşıma siparişi / sevkiyat (yükleme ve teslim noktaları, durum akışı)
- Taşıyıcı, araç ve sürücü yönetimi
- Planlama ve araç atama
- Sefer takibi ve teslim kanıtı (fotoğraf, imza)
- Maliyet ve navlun; satış fiyatı ve kârlılık
- e-İrsaliye
- Müşteriye sevkiyat durumu bildirimi
- Fırsattan sevkiyata geçiş (CRM bağlantısı)

### M3 · Antrepo

- Depo, alan ve raf adresleme
- Mal kabul ve çıkış (giriş/çıkış fişleri)
- Stok, lot ve seri takibi
- Gümrüklü antrepo süreçleri ve beyanname bilgileri
- Ardiye ve elleçleme ücretlendirmesi
- Sayım
- El terminali / barkodlu işlemler

### M4 · İK

- Personel özlük kartı ve belgeleri
- Organizasyon yapısı ve pozisyonlar
- İzin talepleri ve onay
- Puantaj ve vardiya
- Sürücü belgeleri ve süre takibi (ehliyet, SRC, psikoteknik)
- Bordro entegrasyonu
- Çalışan self servis

### M5 · Muhasebe

- Cari hesaplar (ortak Taraf üzerinden)
- Satış ve alış faturaları; e-Fatura / e-Arşiv
- Tahsilat, ödeme, kasa ve banka
- Çek/senet
- Genel muhasebe ve hesap planı
- KDV ve vergi raporları; e-Defter
- Modüllerden fatura taslağı (sevkiyat, ardiye)

## Açık sorular

- **Uygulama teması** (Ç15a): renk ve tipografi kararı bekleniyor.
- **Pilot müşteride e-İrsaliye** zorunlu mu ve hangi entegratör kullanılıyor? Cevaba göre Ç11b'nin bir kısmı D2'ye çekilir.
- **Antrepo türü:** pilot müşteri gümrüklü antrepo mu işletiyor, genel depo mu? M3'ün kapsamını belirler.
- **Bordro:** İK modülü bordroyu kendisi mi hesaplayacak, mevcut bir bordro yazılımına mı bağlanacak?
