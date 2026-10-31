# Ekran testi sonucu

37/38 kontrol geçti.

| Alan | Kontrol | Sonuç | Ayrıntı |
|---|---|---|---|
| Giriş | Giriş sayfası açılır ve Modulix logosu görünür | Geçti | başlık: Giriş · Modulix |
| Giriş | ayse@acme.demo ile giriş yapılır | Geçti |  |
| Hazırlık | Bu hafta için örnek aktiviteler API ile eklenir | Geçti | 1/5 oluşturuldu (durumlar: 422,422,422,201,422); firma=true fırsat=true kişi=true |
| Kabuk | Ana sayfa: üst çubuk, logo ve sol menü çizilir | Geçti | logo metni "Modulix" |
| Kabuk | Profil menüsü açılır, Escape ile kapanır | Geçti | öğeler: Hesabım / Çıkış |
| Kabuk | Profil menüsü dışarı tıklanınca kapanır | Geçti |  |
| Kabuk | Koyu tema açılır ve sayfa yenilenince korunur | Geçti |  |
| Kabuk | Tema düğmesi koyu temada "basılı" durumda; geri açık temaya döner | Geçti |  |
| Kabuk | Menüyü Daralt: menü ikonlara iner ve yenilemede korunur | Geçti | genişlik 248px → 72px |
| Kabuk | Sol menüde aktif öğe vurgulu (Firmalar) | Geçti | aktif: Firmalar |
| Kabuk | Firmalar listesi PageHeader ile açılır | Geçti | başlık: Firmalar · açıklama: Müşteri, aday ve iş ortağı firmalarınızı yönetin. |
| Aktiviteler | Varsayılan görünüm haftalık saat çizelgesi | Geçti |  |
| Aktiviteler | Özet kartları sayı gösterir | Geçti | Bugün=2, Geciken=1, Bu hafta=2, Tamamlanan=0 |
| Aktiviteler | Saat çizelgesinde aktivite blokları ve bugün paneli dolu | Geçti | bloklar=0, bugün=1 |
| Aktiviteler | Çakışan aktiviteler yan yana (genişlik paylaşımı) | Geçti | genişlikler:  |
| Aktiviteler | Bloğa tıklayınca detay çekmecesi açılır, Escape ile kapanır | **Kaldı** | page.click: Timeout 30000ms exceeded. |
| Aktiviteler | Bugün panelinden detay açılır, arka plana tıklayınca kapanır | Geçti |  |
| Aktiviteler | Yeni aktivite çekmecede açılır; Kaydet/Vazgeç görünür | Geçti | aksiyonlar ekranda görünür |
| Aktiviteler | Ay, Gün, Ajanda ve Liste görünümleri çizilir | Geçti | Ay, Gün, Ajanda, Liste |
| Aktiviteler | "Geciken" kartı liste görünümünü gecikenler süzgeciyle açar | Geçti |  |
| Fırsat | Fırsat detayında huni, gösterge şeridi ve sağ panel çizilir | Geçti | aşamalar: is-done:Aday (tamamlandı) / is-done:Nitelikli (tamamlandı) / is-current:3Teklif / is-todo:4Pazarlık / is-todo:5Kazanıldı / is-todo:6Kaybedildi; 5 gösterge; 2 yan kart |
| Fırsat | Hunide başka bir açık aşamaya tıklayınca işlem paneli açılır ve aşama önseçilir | Geçti | tıklanan: 4Pazarlık; seçili: Pazarlık; odak: o-stage |
| Fırsat | Huni "Kazanıldı" aşaması kazanma panelini açar | Geçti |  |
| Fırsat | Aktivite ekle çekmecede açılır ve kapanır | Geçti |  |
| Firma | Firma detayında özet kartı ve sekmeler çizilir | Geçti | sekmeler: Genel bilgiler / Kişiler ve ilişkiler1 / Fırsatlar ve leadler1 / Aktiviteler2 / Geçmiş; özet: Sorumlu, Sektör, Şehir, Açık fırsatlar, Kazanılan, Açık aktiviteler |
| Firma | Sekme geçişinde yarım kalan form korunur (Kişi ilişkilendir) | Geçti | değer kayboldu: "" |
| Firma | Fırsatlar, Aktiviteler ve Geçmiş sekmeleri açılır | Geçti | Fırsatlar ve leadler, Aktiviteler, Geçmiş |
| Firma | Aktivite ekle çekmecede açılır | Geçti |  |
| Kişi | Kişi detayında özet kartı ve sekmeler çizilir | Geçti | sekmeler: Genel bilgiler / Firma ilişkileri1 / Aktiviteler0 / Fırsatlar ve leadler1 / Geçmiş |
| Koyu tema | Aktiviteler ve fırsat koyu temada çizilir | Geçti |  |
| Mobil | ana-sayfa: yatay taşma yok | Geçti |  |
| Mobil | aktiviteler: yatay taşma yok | Geçti |  |
| Mobil | firsat: yatay taşma yok | Geçti |  |
| Mobil | firma: yatay taşma yok | Geçti |  |
| Mobil | kisi: yatay taşma yok | Geçti |  |
| Mobil | Menü düğmesi sol menüyü açar | Geçti |  |
| Mobil | Fırsat sayfasında aktivite çekmecesi tam ekran açılır | Geçti | genişlik 390px |
| Genel | Blazor hata çubuğu hiç görünmedi | Geçti |  |

## Tarayıcı konsol hataları (1)
- Failed to load resource: the server responded with a status of 401 ()