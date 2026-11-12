# Görsel QA kontrol listesi

Her yeni ya da değişen ekran için açık ve koyu temada, **1440px**, **1024px** ve **390px** genişlikte uygula.

## Kabuk
- [ ] Logo "Modulix" (M büyük, `ü` yok, turkuaz x); daraltılmış menüde yalnızca işaret.
- [ ] Sol menüde doğru öğe aktif; sayfa yenilenince ve doğrudan bağlantıyla açılınca da.
- [ ] Aktif öğede sol vurgu çizgisi (birincil mavi), açık mavi zemin (`--mx-surface-muted`) ve koyu mavi metin (`--mx-tone-brand-fg`).
- [ ] Menüyü Daralt çalışıyor, tercih yenilemeden sonra korunuyor; ikonlarda `title` ipucu var.
- [ ] Üst çubuk: tema düğmesi, avatar + ad + kuruluş, profil menüsü (Escape ve dışarı tıklama kapatır).
- [ ] Tab ile ilk odak "İçeriğe geç"; odak halkası her etkileşimli öğede görünür.

## Sayfa
- [ ] Başlık `PageHeader` ile; birincil aksiyon sağda, en sonda.
- [ ] Kart boşlukları 16–24px, köşe 12px, gölge hafif.
- [ ] Bütün input/select yükseklikleri aynı (40px; dokunmatikte 44px).
- [ ] Etiketler 13px/600, yardım ve hata metni 12px, hata alanın altında.
- [ ] Rozetlerde ton çiftleri (`--mx-tone-*`), durum metinle de yazılı.
- [ ] CSS'te sabit renk yok (`grep -n '#[0-9a-fA-F]\{3,6\}' wwwroot/css/app.css` yalnızca token bloklarını ve açıklamalı istisnaları döndürmeli, örn. logo önizlemesinin beyaz zemini).

## Responsive
- [ ] 390px'te yatay kaydırma yok (`document.documentElement.scrollWidth <= innerWidth`).
- [ ] Sol menü telefonda açılır panel; formlar tek kolon; tablolar kart listesi.
- [ ] Kaydet/İptal düğmelerine kolay erişim.

## Koyu tema
- [ ] Inputlar, select okları, tablo başlıkları ve rozetler koyu zemine uyumlu.
- [ ] Metin/zemin kontrastı ≥ 4.5:1 (büyük metin ≥ 3:1).
- [ ] Glow ve parlama yok (tek istisna: birincil düğmenin `--mx-shadow-cta` ışıması).

## Fonksiyonel regresyon
- [ ] Form açılıyor, kayıt yükleniyor, kaydetme ve doğrulama çalışıyor.
- [ ] Lookup araması, sekmeler/bölümler, liste süzme ve sayfalama çalışıyor.
- [ ] Yetkisiz kullanıcı menüde ve sayfada işlemi göremiyor; sunucu yine reddediyor.
- [ ] Başka tenant'ın verisi görünmüyor.
