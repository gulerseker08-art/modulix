Modülix — Sektör Bağımsız Platform Yol Haritası
Sürüm: 2.0 · Tarih: 25 Ekim 2026
Kapsam: Mimari inceleme, mevcut epiclerin genişletilmesi, yeni epicler, bağımlılıklar ve aşamalı teslim planı.
1. İncelemenin kapsamı ve sonuç
Bu çalışma, paylaşılan “Yol haritası” metnini esas alır. Kaynak kod, README ve metinde bağlantısı verilen ADR dosyaları bu incelemede okunmamıştır. “Tamamlandı” bilgileri gönderilen metindeki beyanlardır; uygulanmış veya güvenli olduklarına dair bağımsız doğrulama değildir. Aşağıdaki yeni işler geliştirme önerisidir. Yeni ADR başlıkları öneridir; mevcut ADR numaralarıyla çakışmaması için numaraları depoda atanmalıdır.

Yol haritasının ortak veri, kayıt boru hattı, RLS ve outbox yönelimi korunmuştur. Temel değişiklik: lojistik bir pilot ve sektör profilidir; ürün mimarisinin merkezi değildir. CRM, İK, muhasebe, depo ve web kanalı farklı sektörlerde tek başına veya birlikte çalışabilmelidir.
1.1. Düzeltilen mimari kararlar
Kaynaktaki yaklaşım	Revize karar ve gerekçe
İkinci modülde ihtiyaç duyulan her yetenek core'a taşınır	Yalnızca anlamı ve yaşam döngüsü gerçekten ortak olan altyapı core'a alınır. İki modülde tutar hesaplanması, navlun ve bordronun aynı domain servisi olması anlamına gelmez.
Bütün sözleşmeler Core.Contracts içinde	Core sözleşmeleri burada kalır; iş modülleri kendi küçük, sürümlü *.Contracts paketlerini yayınlayabilir. Başka modülün Domain/Persistence projesine referans yasaktır.
Her olay bir kez alınır	Taşıma ve yeniden deneme nedeniyle olay tekrar gelebilir. Hedef: en az bir kez teslim + abone bazlı inbox/idempotency ile iş etkisinin yinelenmemesi.
Kişi, kullanıcı, çalışan ve sürücü tek kayıt	Tenant kapsamlı kişi, giriş kimliği, üyelik, istihdam ve sürücü yetkinlikleri ayrı varlıklardır; uygun bağlarla aynı kişiye işaret ederler.
Rol, uzantının varlığından anlaşılır	Müşteri/tedarikçi gibi iş rolleri tarih ve durum içerebilir; açık iş rolü ilişkisi kullanılır. Erişim rolleri tamamen ayrıdır.
Ortak kaydı kullanan modülün okuyucusu ortak veriyi görür	Bağlı kayıt üzerinde gerekli minimum görünüm sağlanır; müşteri adını görmek tüm kişi kartını, telefonunu veya TCKN'sini açmaz.
Her hesaplama core içinde	Para, birim, yuvarlama ve hesap izi ortak; navlun, ardiye, bordro ve muhasebe hesaplarının sahibi ilgili modüldür. Dış sistem mutabakat/aktarım sözleşmesiyle desteklenebilir.
Boşluksuz ve çakışmasız her belge numarası	Çakışmasız seri zorunludur; boşluksuzluk yalnızca belge türünün kuralı gerektiriyorsa kesinleştirme sırasında uygulanır. Taslak numarası ile yasal seri ayrılır.
İçe aktarmayı toplu silerek geri alma	Yeni, değişmemiş ve bağımlılığı olmayan kayıtlar geri alınabilir. Güncellemeler ve sonraki işlemler için telafi/değişiklik kontrolü gerekir.
Ürün/birim D3, hesaplama D2	Birim ve hizmet kartının minimum sürümü D2'ye çekilir; lot/seri/depo uzantıları D3'te kalır.
İK yasal dosyaları D4, bordro D3	İK temel paketi D3, bordro ve yasal teslim kapısı D4'tür. Bordro pilotu seçilirse ilgili bağımlılıklar birlikte öne alınır.
KVKK veri talebi ve kapanış ihracı D4	Temel dışa aktarma, saklama ve silme talebi işletimi pilot öncesi tanımlanır; ilk aşamada kayıtlı manuel operasyon olabilir.
Antrepo için genel bir gümrük anahtarı	Ülke/rejim/tesis/alan yetkileriyle açılan uyum profili kullanılır; statü değişimi sıradan alan güncellemesi değildir.


2. Ürün sınırları ve değişmez kurallar
2.1. Katmanlar
1. Core: kimlik, tenant, yetki, olay, iş, belge, ortak veri, özelleştirme, entegrasyon, kullanım ölçümü.
2. İş modülleri: CRM, lojistik, depo/antrepo, İK, muhasebe; ihtiyaç doğrulandıkça satış, satın alma ve destek.
3. Sektör profilleri: lojistik, toptan dağıtım, profesyonel hizmet gibi modül kombinasyonları ve varsayılanlar. Profil yeni bir veri silosu oluşturmaz.
4. Kanallar: yönetim uygulaması, saha PWA'sı, firma web sitesi, müşteri portalı ve dış API.
5. Ülke/uyum paketleri: dil, vergisel parametreler, e-belge ve gümrük adaptörleri. Sektör profili ile ülke paketi ayrı boyutlardır.
2.2. Güncellenmiş kararlar
- K3 — ADR: Veri sahipliği, tenant izolasyonu, modüller arası sözleşme ve işlem tutarlılığı kararları gerekçesi ve alternatifleriyle kaydedilir.
- K4 — Teslim sırası: Core → CRM pilotu → lojistik pilotu → depo/antrepo ve İK temel → finansal/uyum genişlemesi önerilir. Bu sıra ürün kullanım bağımlılığı değildir; lojistik CRM satın alınmadan çalışır.
- K5 — Bağımsız modüller: Bir modül başka modülün tablolarına yazmaz, ORM varlığını kullanmaz, kapalı modülün ekranını zorunlu kılmaz. Sözleşme, olay veya açık sorgu servisi kullanılır. API uçları da domain kurallarını atlamaz.
- K6 — Tema: Modulix varsayılan renkleri (lacivert / mavi / turkuaz, docs/design-system/theme-tokens.md) korunur; bütün renkler ve görsel durumlar semantik tema değişkenlerinden gelir.
- K7 — Hesaplamalar: Core teknik hesap araçlarını sağlar; sektör ve yasal hesap kuralları sahip modülde sürümlenir. Resmî bildirim ve dış sistem aktarımı hesap sorumluluğundan ayrılır.
- K8 — e-belge: Teknik sağlayıcı katmanı ortak; belgeyi doğuran iş kuralı ilgili modüldedir. e-İrsaliye lojistik pilot ihtiyacı varsa D2'dedir, her sektöre zorunlu değildir.
- K9 — Depo: Genel depo yetenekleri ortak depo modülündedir. Gümrüklü antrepo bunun seçilebilir ülke/rejim uyum uzantısıdır; hukuken izin verilen alan düzeni ayrıca doğrulanır.
- K10 — Ortak kimlik: Aynı tenant içinde taraf/kişi yeniden yaratılmaz; modüller ilişkiler ve uzantılar kurar. Farklı tenantların kişi ve müşteri kayıtları otomatik birleştirilmez.
- K11 — Başlangıç mimarisi: Modüler monolit + bağımsız çalıştırılabilen worker önerilir. Mikroservis ayrımı ancak ölçek, ekip veya bağımsız dağıtım ihtiyacı kanıtlandığında yapılır.
- K12 — Sektör genişlemesi: if (sector == logistics) dalları core'a yayılmaz. Sektör katkıları sürümlü metadata ve kayıtlı yeteneklerle sağlanır.
- K13 — Tenant ve şirket: Tenant bir abonelik/izolasyon sınırıdır. Tenantın kendi tüzel kişileri ve şubeleri ile müşteri/tedarikçi tarafları ayrıdır. Şirket/şube erişimi tenant erişiminin alt kapsamıdır.
- K14 — Süreç sahipliği: İşlemi hedef modül doğrular; süreç koordinatörü adımları takip eder. Olay alınması işlemin otomatik ve koşulsuz yapılacağı anlamına gelmez.
- K15 — Güvenli varsayılan: Yeni alan/işlem/modül varsayılan olarak kapalıdır. Tenant, modül hakkı, kullanıcı yetkisi, kayıt kapsamı ve alan yetkisi birlikte uygulanır.
- K16 — Dondurulmuş belge: Kesinleşmiş belge, o anki ünvan/adres/kalem/fiyat bilgilerini snapshot olarak saklar. Ana kart değişince geçmiş belge değişmez.
- K17 — Ürün paketi: Satın alınan modül hakkı, teknik özellik bayrağı ve kullanıcı izni birbirinden ayrılır. Sektör profili bunları aşamaz.
2.3. Core'a alma kontrolü
Bir yetenek ancak şu sorular olumlu cevaplanıyorsa core'a alınır: Anlamı sektörler arasında aynı mı? İş modülü olmadan açıklanabiliyor mu? Veri sahibi tek mi? Alan dilini bozmadan ortaklaştırılabiliyor mu? Bağımlılık döngüsü yaratmıyor mu? Aksi hâlde modül sözleşmesi veya isteğe bağlı ortak iş modülü tercih edilir.
3. Mevcut durum ve işaretleme
Beyan edilen mevcut işler: SQL Server/RLS, tenant seçimi, giriş, modül kataloğu/hakları, izinler; kayıt boru hattı; Y1–Y9 yönetim kabiliyetleri; e-posta kuyruğu; CRM firma/kişi/fırsat/aktivite; Ç3a'nın ilk migration adımı. Lojistik yalnızca tanım ve izin seviyesinde, AI projesi boş olarak bildirilmiştir.

Durum etiketleri: BEYAN kaynakta mevcut; GENİŞLETME mevcut epice ek kapsam; YENİ kaynakta ayrı epic yok; DOĞRULAMA kod ve test kanıtı istenir. Bu belgede belirtilmeyen hiçbir iş tamamlanmış sayılmaz.

Her alt epic için ortak teslim çıktısı: uygulama/domain davranışı, gerekiyorsa veri migration'ı, yetkili API ve ekran, denetim izi, ilgili negatif senaryo testi, kısa işletim dokümanı. Aşağıdaki kabul ölçütleri bu ortak gereksinimlere eklenir.
4. Yönetim epikleri — mevcut Y1–Y9'un detaylandırılması
Kaynakta ayrıntıları bulunmayan alt numaralar burada önerilmiştir; depodaki gerçek görev kimlikleriyle eşlenmelidir.
Y1 — Yönetici rolü ve yetki devri [BEYAN + GENİŞLETME]
Amaç: Platform yöneticisi, tenant yöneticisi ve operasyon sorumlularının sınırlarını ayırmak. Bağımlılık: Ç0, Ç13, Ç22.
- Y1.1 Rol sınırları: Platform destek/operasyon/finans rolleri ile tenant yönetici rollerini ayır. Platform yöneticisi olmak müşteri operasyon verisine sürekli erişim vermesin. Kabul: Tenant yöneticisi başka tenantı veya platform planlarını değiştiremez.
- Y1.2 Kritik yetki devri: Son yöneticinin kaldırılması, tenant sahipliği devri ve kritik rol yükseltmeleri için yeniden doğrulama ve kayıtlı süreç oluştur. Kabul: Tenant yöneticisiz kalamaz; eski oturumların yetkileri güncellenir.
- Y1.3 Destek erişimi: Gerekçeli, süreli ve hedef tenantla sınırlı destek erişimi; hassas işlemler ayrıca kısıtlı. Kabul: Başlangıç/bitiş ve yapılan işlemler denetlenir; süresi dolan erişim reddedilir.
Y2 — Kullanıcı, davet ve kayıt devri [BEYAN + GENİŞLETME]
Bağımlılık: Ç3a, Ç12, Ç13, Ç22.
- Y2.1 Kimlik ve üyelik: Global giriş kimliğini tenant üyeliğinden ayır; yerel kişi bağı üyelikte kurulsun. Davet tokenı süreli, tek kullanımlı ve hedef üyeliğe bağlı olsun. Kabul: Bir tenanttan çıkarılmak başka tenant üyeliğini silmez.
- Y2.2 Yaşam döngüsü: Davet edildi/aktif/askıda/ayrıldı durumları; ayrılmada oturum, API ve görev sahipliği politikaları. Kabul: Pasif üye işlem yapamaz, geçmiş kayıtların sorumluluk izi kaybolmaz.
- Y2.3 Kayıt devri: ADR-0004'e uygun önizleme, tür bazlı sahiplik kontrolü, arka planda ilerleme ve kısmi hata raporu. Kabul: Yeni sorumlunun erişemeyeceği kaydın devri sessizce yapılmaz.
Y3 — Roller, izinler ve görev ayrılığı [BEYAN + GENİŞLETME]
Bağımlılık: Ç0, Ç20, Ç22.
- Y3.1 İzin kataloğu: Kaynak/işlem bazlı izinleri kodla bildir; okuma, dışa aktarma, onaylama, kesinleştirme ayrı olsun. Kabul: Menü gizlemekten bağımsız olarak API reddi kanıtlanır.
- Y3.2 Rol şablonları: ADR-0005 kapsamındaki hazır roller sektör profilinden önerilsin; tenant özelleştirmesi yeni sürümle ezilmesin. Kabul: Şablon güncellemesinin izin farkı yöneticiye gösterilir.
- Y3.3 Görev ayrılığı: Hazırlayanın kendi ödeme/teklifini onaylaması gibi çakışmalar yapılandırılabilsin. Kabul: Yasaklı rol birleşimi ve kendini onaylama sunucuda engellenir.
Y4 — Platform yönetimi [BEYAN + GENİŞLETME]
Bağımlılık: Ç12, Ç14, Ç20.
- Y4.1 Tenant açma/kapatma: Tekrarlanabilir kurulum, adım günlüğü ve başarısız adımı güvenli sürdürme. Kabul: Aynı kurulum komutu iki tenant veya iki yönetici oluşturmaz.
- Y4.2 Hak ve plan yönetimi: Plan/istisna/süre/deneme kaynağı görünür olsun. Kabul: Hak iptalinde çalışan işlerin ve mevcut verinin davranışı önceden belirlenir.
- Y4.3 Sağlık ve operasyon: Tenant araması, kullanım, kuyruk, sürüm, yedek ve güvenlik olaylarına yetki bazlı ekran. Kabul: Destek rolü ödeme bilgilerini veya tüm müşteri dosyalarını varsayılan göremez.
Y5 — Audit, kayıt geçmişi ve oturum olayları [BEYAN + GENİŞLETME]
Bağımlılık: Ç13, Ç14, Ç23.
- Y5.1 Değişiklik izi: Kim, ne zaman, hangi tenant/şirket, kaynak, correlation ve önce/sonra değişimleri; sır ve hassas alanlar maskeli. Kabul: Normal uygulama rolü audit kaydını değiştiremez.
- Y5.2 Güvenlik olayları: Başarılı/başarısız giriş, MFA, rol değişimi, veri ihracı ve destek erişimi. Kabul: Önemli yetki değişiklikleri yalnızca teknik logda kalmaz.
- Y5.3 İnceleme/ihracat: Zaman ve kaynak filtreleri, izinli CSV ihracı, büyük taleplerde arka plan işi. Kabul: CSV formül enjeksiyonu önlenir; audit ihracı da kaydedilir.
Y6 — Tenant ve bölgesel ayarlar [BEYAN + GENİŞLETME]
Bağımlılık: Ç15, Ç22.
- Y6.1 Tipli ayarlar: Şema, varsayılan, doğrulama, gizlilik sınıfı ve değişiklik geçmişi. Tenant ayarıyla yasal şirket ayarını ayır. Kabul: Yanlış tipte veya yetkisiz ayar güncellenemez.
- Y6.2 Bölgesel davranış: Saat dilimi, dil, hafta başlangıcı, tarih/sayı biçimi ve çalışma takvimi. Anlık zaman UTC, iş tarihi ayrı tipte. Kabul: Gece yarısı ve yaz saati sınırları tarih kaydırmaz.
- Y6.3 Marka ve iletişim: Logo, resmi ünvan/iletişim, belge ve e-posta profilleri. Kabul: Geçmiş belgeler yeni logo/ünvan nedeniyle yeniden üretilip değiştirilmez.
Y7 — Hesabım, oturum ve MFA [BEYAN + GENİŞLETME]
Bağımlılık: Ç13, Ç19.
- Y7.1 Oturumlar: Cihaz/son etkinlik görünümü, tek veya tüm oturumları sonlandırma, güvenli parola sıfırlama. Kabul: Parola/MFA kritik değişiminde yenileme tokenları politika uyarınca iptal edilir.
- Y7.2 MFA yaşam döngüsü: Kurulum, kurtarma kodu, yeniden kayıt ve kontrollü kurtarma. Kabul: Kurtarma yolu MFA'yı kolayca devre dışı bırakan bir arka kapı oluşturmaz.
- Y7.3 Kurumsal kimlik: OIDC/SSO ve gerekirse otomatik kullanıcı temini sonraki paket olarak tasarlanır. Kabul: Tenant bağlantısı başka tenant üyeliği kazandırmaz; portal kimliği ayrı politikadadır.
Y8 — Ekip, kayıt kapsamı ve hassas alanlar [BEYAN + GENİŞLETME]
Bağımlılık: Ç3, Ç10, Ç17, Ç22.
- Y8.1 Kapsam modeli: Kendim/ekibim/şubem/şirketim/izinli tenant kapsamı; sahiplik ile ekip paylaşımı ayrılır. Kabul: Liste, lookup, ilişki, arama ve dosya indirme aynı kapsamı uygular.
- Y8.2 Alan güvenliği: Okuma, değiştirme, ihracat ve AI kullanımı ayrı kontrol edilir; maskeleme tek başına yetki kontrolü sayılmaz. Kabul: Gizli alan API, rapor, bildirim veya aramada açığa çıkmaz.
- Y8.3 Erişim açıklaması: Yetkili yönetici “bu kullanıcı neden görüyor/göremiyor?” incelemesi yapabilir. Kabul: Açıklama başka kullanıcıların hassas verisini açmaz.
Y9 — Özelleştirme ve sektör profilleri [BEYAN + GENİŞLETME]
Bağımlılık: Ç9, Ç20, Ç23.
- Y9.1 Özel alanlar: Tür, zorunluluk, doğrulama, indeksleme ve alan yetkisi. Kabul: API ve import, ekranla aynı kuralları uygular; alan silme bağımlılık analizi gerektirir.
- Y9.2 Listeler ve değerler: Değişmeyen teknik kod, çevrilebilir etiket, aktiflik ve kullanım analizi. Kabul: Kullanılmış değer kaldırılırken geçmiş kayıt bozulmaz.
- Y9.3 Profil paketleri: Modül önerileri, form/görünüm/rol varsayılanları, örnek iş akışları ve ülke paketi uyumu. Kabul: Profil değiştirmek kayıt silmez, lisans açmaz veya izin yükseltmez.
- Y9.4 Sürüm ve taşıma: Varsayılan → sektör → tenant → kullanıcı katmanları; dışa/içe alma, önizleme, geri dönüş ve metadata migration. Kabul: Yeni profil sürümü tenant düzenini sessizce ezmez.
5. Çekirdek epikler
Ç0 — Mimari temel, domain sınırları ve kayıt sözleşmesi [YENİ / D0]
Amaç: Mevcut geliştirmelerin üzerine güvenilir bir modül geliştirme sözleşmesi koymak. Bağımlılık: Yok; mevcut yapı üzerinden doğrulama yapılır. Çıktı: Domain haritası, modül şablonu, ADR ve mimari testler.
Durum (25 Ekim 2026): Envanter ve mevcut varlıkların domain haritası [envanter.md](envanter.md)'de. Ç0.2 ve Ç0.3'ün atlama kapıları test edildi; açık bulgular: zorunlu sürüm (Ç0.5, B1), iş komutu (Ç0.4, B2), silme politikası (Ç0.6, B3).
- Ç0.1 Domain haritası: Her varlık için sahibi, tenant/şirket kapsamı, ana veri/işlem ayrımı ve izin modeli yazılır. Kabul: Teklif, sevkiyat, personel ve fatura için iki farklı yazma sahibi bulunmaz.
- Ç0.2 Proje sınırları: Domain, uygulama, persistence, kayıt adaptörü ve contracts ayrılır. Modüller diğer modülün tablosuna ulaşamaz; izinli referanslar test edilir. Kabul: Yasak proje referansı CI'ı düşürür.
- Ç0.3 Kayıt boru hattı: Tenant ve hak → komut/alan yetkisi → domain doğrulaması → eşzamanlılık → transaction → audit/outbox; hook sıraları açık olur. Kabul: Genel /api/records ucu dahil hiçbir yol onay/kesinleştirme kuralını atlayamaz.
- Ç0.4 CRUD ve iş komutu ayrımı: ApproveQuote, DeliverShipment, PostInvoice gibi işlemler açık komuttur; genel alan güncellemesiyle taklit edilemez. Kabul: Sıradan update ile faturayı kesinleştirme denemesi reddedilir.
- Ç0.5 Tutarlılık ve sürüm: Transaction sınırları, rowversion/ETag, hata zarfı, sayfalama, correlation, API/olay sürümleme ve iptal politikaları. Kabul: Aynı kaydı iki kişi düzenlediğinde sessiz veri ezme olmaz.
- Ç0.6 Silme ve arşiv: Soft delete her veri için otomatik çözüm sayılmaz; referans, yasal kayıt, dosya ve olay etkisi belirlenir. Kabul: Referanslı ana kart silinemez; arşivlenen kayıt geçmiş belgeyi bozmaz.
Ç1 — Olay altyapısı ve güvenilir teslim [GENİŞLETME / D1 / ADR]
Bağımlılık: Ç0, Ç13, Ç14. İlk kullanım: Atama bildirimi; sonraki kullanım modüller arası süreçler.
ADR: [ADR-0007 Olay altyapısı](adr/0007-olay-altyapisi.md) (kabul edildi).
Durum (25 Ekim 2026): ADR'nin geçiş adımları 1-6 yapıldı. Olay zarfı, abone başına teslim (`EventDeliveries`), dağıtıcı ve işleyici (kira, heartbeat, sıra, yeniden deneme, dead-letter, saklama), sistem kimliği ve hassas alan süzgeci, atama e-postası, platform "Olaylar" ekranı, ölçümler ve health hazır; kabul testleri entegrasyon testi olarak yazıldı. Kalan: modül iş olayları (Ç0.4), webhook (Ç11a.4), işleyicinin Ç2 çerçevesine taşınması ve ayrı süreçte çalışması (K11).
- Ç1.1 Transactional outbox: İş kaydı ve olay aynı veritabanı transaction'ında yazılır. Zarf: eventId, tenant, aggregateId/type, sürüm, sequence, occurredAt, correlation/causation ve gerekli asgari veri. Kabul: Rollback edilen işlemden olay çıkmaz.
- Ç1.2 Dispatcher ve lease: Worker atomik sahiplenme, süreli kilit, heartbeat ve çökme sonrası tekrar alma uygular. Tenant bağlamı doğrulanmış üretici kaydından kurulur. Kabul: İki worker ve kilit süresi aşımı senaryosu veri kaybı yaratmaz.
- Ç1.3 Abone bazlı inbox: Başarı her abone için ayrı tutulur; tenant+subscriber+eventId benzersizliği ve mümkünse iş etkisiyle aynı transaction. Kabul: Bildirim başarılı, muhasebe başarısızsa yalnızca başarısız abone yeniden çalışır; aynı fatura iki kez oluşmaz.
- Ç1.4 Sıra ve zehirli olay: Global sıra vaat edilmez; gerekli yerde tenant+aggregate sırası korunur. Bozuk olayın sonraki adımları durdurma/karantina politikası tanımlanır. Kabul: Tek tenantın hatası bütün kuyruğu kilitlemez.
- Ç1.5 Retry ve işletim: Artan gecikme+jitter, sınırlı tekrar, dead-letter, yetkili yeniden yürütme ve saklama politikası. Dış etkiler ayrıca idempotency anahtarı kullanır. Kabul: Replay yeni iş etkisi oluşturmaz; hata ekranı kişisel veri dökmez.
Ç2 — Arka plan işleri ve zamanlayıcı [GENİŞLETME / minimum D1, tam D2]
Bağımlılık: Ç0, Ç14, Ç20.
- Ç2.1 İş sözleşmesi: İş türü, tenant/global sınıfı, actor, parametre şeması, timeout, iptal, tekrar ve sürüm tanımlanır. Kabul: Sahipsiz tenant işi veya kapalı modül işi çalışmaz.
- Ç2.2 Zamanlama: Cron+saat dilimi, çakışma ve kaçırılan çalıştırma politikası, dağıtık kilit ve takvim. Kabul: Yaz saati değişimi aynı bordro/hesabı iki kez başlatmaz.
- Ç2.3 Adil kapasite: Tenant başına eşzamanlılık, kuyruk limiti ve iş önceliği. Kabul: Büyük import küçük tenantın e-postalarını süresiz bekletmez.
- Ç2.4 Operasyon ekranı: İlerleme, deneme geçmişi, durdurma/yeniden başlatma, redakte hata ve metrikler. Kabul: Yeniden tetikleme de yetkili ve idempotenttir.
- Ç2.5 Mevcut worker geçişi: E-posta/outbox gözlemleme ve sahiplenme standartlarını ortaklaştır; polling işini cron tetikleyicisine zorla dönüştürme. Kabul: Geçişte mesaj kaybı veya çift gönderim riski ölçülür ve sınanır.
Ç3 — Ortak ana veri ve güvenli kayıt referansı [GENİŞLETME / D1–D3 / ADR]
Bağımlılık: Ç0, Ç13, Ç22. Sınır: Ortak kartlar tenant kapsamlıdır; global kişisel veri havuzu kurulmaz.
- Ç3a.1 Taraf modeli: Tüzel/gerçek kişi tarafı, iletişim, ülkeye göre resmi kimlik ve müşteri/tedarikçi/taşıyıcı iş rolleri. Kimliksiz aday kaydı da desteklenir. Kabul: Aynı taraf müşteri ve tedarikçi olabilir; farklı tenant kayıtları karışmaz.
- Ç3a.2 Kişi ve ilişkiler: Kişi–taraf ilişkisi görev, tarih, iletişim tercihi ve aktiflik taşır; kişi birden çok firmada yetkili olabilir. Kimlik/üyelik/çalışan ayrı bağlanır. Kabul: Çalışanın ayrılması kişinin müşteri yetkilisi geçmişini silmez.
- Ç3a.3 Uzantı sahipliği: CRM segmenti, lojistik taşıyıcı yetkinliği, İK istihdamı ve muhasebe cari ayarı kendi modülünde. Kabul: CRM kapalıyken lojistik temel taraf oluşturur; İK ücreti ortak kartta görünmez.
- Ç3a.4 Referans ve lookup: Tür+kimlik, aynı tenant/izinli şirket kontrolü, varlık/aktiflik doğrulama ve minimum gösterim projeksiyonu. Kabul: Başka tenant ID'si ilişkiye yazılamaz; lookup PII sızdırmaz.
- Ç3a.5 Tekilleştirme ve merge: Ülke+kimlik türü+normalize resmi numara benzersizliği; ad/telefon benzerlik uyarısı; e-posta tek başına kişi birleştirmez. Birleştirmede referans, çakışma ve geçmiş denetlenir. Kabul: Yanlış merge için kontrollü telafi planı vardır; kesinleşmiş belge snapshot'ı değişmez.
- Ç3a.6 Migration: CRM kartları, kullanıcı–kişi bağları ve olası tekrarlar için eşleme tablosu, kuru çalıştırma ve sayım mutabakatı. Kabul: Önce/sonra kayıt ve ilişki sayıları raporlanır; veri kaybı olmadan geçiş yapılır.
- Ç3b Adres ve konum: Ülkeye göre şema, çoklu adres, kullanım türü, geçerlilik, koordinat ve doğrulama düzeyi. Türkiye il/ilçe sözlüğü ülke adaptörüdür. Kabul: Yabancı adres zorunlu Türkiye ilçesi istemez; geçmiş teslim adresi korunur.
- Ç3c.1 Birim ve ürün/hizmet temeli (D2): Kod, tür, temel birim, kategori ve aktiflik; fiyat ve depo miktarı kartın kendisine yazılmaz. Kabul: Hizmet faturası ve taşıma bedeli depo modülü olmadan çalışır.
- Ç3c.2 Birim dönüşümü ve uzantılar (D3): Boyut uyumu, ürün bazlı ambalaj dönüşümü, ülke/ürün kodları. Lot/seri, stok defteri ve maliyetleme depo modülündedir. Kabul: kg→adet gibi dönüşüm ürün kuralı olmadan yapılmaz.
- Ç3d Para birimi ve kur: Ondalık tip, kaynak, kur türü, tarih, tatil geri dönüşü ve manuel kur gerekçesi. TCMB Türkiye sağlayıcısıdır. Kabul: Hesapta kullanılan kur saklanır; sonraki kur güncellemesi geçmiş belgeyi değiştirmez.
Ç4 — Dosya ve ekler [GENİŞLETME / D1]
Bağımlılık: Ç0, Ç13, Ç14; kota için Ç12 minimum.
- Ç4.1 Depolama ve metadata: Yerel/nesne depolama adaptörü; tenant, kayıt referansı, hash, sürüm, boyut, sınıf ve yükleyen. Kabul: Nesne anahtarı tahminiyle dosyaya erişilemez.
- Ç4.2 Güvenli yükleme: İçerik türü doğrulama, boyut, karantina, zararlı içerik taraması ve sıkıştırılmış dosya limitleri. Kabul: Tarama bekleyen dosya indirilemez; uzantı değiştirme kontrolü aşamaz.
- Ç4.3 Erişim ve paylaşım: Kaydın ve belgenin sınıfına göre erişim; her indirmede kontrol, kısa ömürlü imzalı URL, zorunlu indirme başlıkları. Kabul: Portal kullanıcısı personel belgesine ulaşamaz; iptal/TTL davranışı belgelenir.
- Ç4.4 Yaşam döngüsü: Versiyon, saklama, legal hold, silinen kaydın ekleri, yetim dosya temizliği ve kota rezervasyonu. Kabul: Yarım yükleme kotayı kalıcı tüketmez; yasal saklanan belge temizlenmez.
- Ç4.5 Mobil ek: Kamera yüklemesi, yeniden deneme ve devam ettirilebilir yükleme ihtiyacı. Kabul: Bağlantı kesilmesi aynı teslim fotoğrafını iki kez ilişkilendirmez.
Ç5 — Bildirim, tercihler ve iletişim [GENİŞLETME / D1–D3]
Bağımlılık: Ç1, Ç2, Ç13; kanal kullanımı Ç12 ile ölçülür.
- Ç5a Uygulama içi (D1): Tür kataloğu, alıcı çözümleme, atama/hatırlatma, okunma ve toplu okunma. Kabul: Olay tekrarı ikinci bildirim üretmez; kayda erişimi kalmayan kullanıcı hassas metin görmez.
- Ç5b E-posta ve tercihler (D2): Tür bazlı kanal, sessiz saat, dil, günlük özet, şablon sürümü ve teslim durumu. İşlemsel bildirimle pazarlama tercihini ayır. Kabul: Zorunlu güvenlik bildirimleri ile isteğe bağlı iletiler farklı kurallarla yönetilir.
- Ç5c Push/SMS (D3): Sağlayıcı sözleşmesi, cihaz aboneliği iptali, ücret/kota, teslim makbuzu ve tekrar önleme. Kabul: Kullanıcı çıkışında yanlış tenant push'ı sürmez; sır veya gereksiz kişisel veri iletilmez.
- Ç5d Şablon ve gönderici güveni: Tenant gönderici doğrulaması, HTML temizleme ve test gönderimi. Kabul: Tenant başkasının doğrulanmamış alan adıyla gönderim yapamaz.
Ç6 — Onay ve otomasyon [GENİŞLETME / D2–D3]
Bağımlılık: Ç0 iş komutları, Ç1, Ç2, Ç5, Ç20 minimum, Ç22. Ç21 ile bağlantı isteğe bağlıdır.
- Ç6a.1 Onay tanımı: Tutar/para birimi/şirket koşulu, sıralı/paralel adım, kişi/rol/ekip, vekalet, süre ve eskalasyon. Kabul: Ayrılan onaycı süreci sahipsiz bırakmaz.
- Ç6a.2 Onay örneği: Başlatıldığı andaki tanım sürümü ve belge snapshot'ı saklanır. Tüm kaydı kilitlemek yerine onayı etkileyen alanlar korunur. Kabul: Tutar değişirse onay geçersizleşir; eşzamanlı iki onay çift ilerletmez.
- Ç6a.3 Domain ayrımı: Onay tamamlanması, hedef modülün kesinleştirme komutuna girdi olabilir; finansal doğrulamayı geçersiz kılamaz. Kabul: Geçersiz fatura onaylı olsa da muhasebeleşmez.
- Ç6b.1 Otomasyon tanımı: Olay/koşul/eylem, tipli alan erişimi, kuru çalıştırma ve sürümlü yayın. Kullanıcı kodu/SQL'i çalıştırılmaz. Kabul: Kuralı hazırlayan yayın öncesi örnek kayıtta etkisini görür.
- Ç6b.2 Güvenli yürütme: Ayrı servis kimliği, kapsamlı yetki, modül hakkı, idempotency ve causation zinciriyle döngü/derinlik/süre sınırı. Kabul: Otomasyon başka tenantı veya yetkisiz ücret alanını değiştiremez.
- Ç6b.3 Geçmiş: Hangi sürüm hangi koşulla ne yaptı, hata ve telafi. Kabul: Hatalı kural kapatılabilir; geçmiş örneklerin devam politikası nettir.
Ç7 — İçe/dışa aktarma ve veri taşıma [GENİŞLETME / D1]
Bağımlılık: Ç0, Ç2 minimum, Ç3, Ç4, Ç13.
- Ç7.1 Import sihirbazı: CSV/XLSX, şablon, encoding, locale, sütun eşleme, özel alanlar, lookup çözümü ve kuru çalışma. Kabul: Ondalık/tarih yanlış yorumlanırsa kullanıcıya açık hata verilir.
- Ç7.2 Uygulama: Satır bazlı domain komutu, batch/checkpoint, idempotency, ilerleme, kısmi hata ve devam. Kabul: Kesinti sonrası başarıyla işlenen satır tekrar yaratılmaz.
- Ç7.3 Güncelleme ve geri alma: Dış sistem kimliği ve açık anahtar stratejisi; yaratılan/güncellenen ayrımı, önceki değer ve rowversion. Kabul: Sonradan değiştirilmiş veya faturaya bağlanmış kayıt topluca silinmez; telafi raporlanır.
- Ç7.4 Export: Filtre/görünür alan, yetki, maskeleme, CSV enjeksiyon koruması, büyük işlerde süreli indirme. Kabul: Arka plan export çalışırken yetki kaldırılmışsa veri yeniden yetkilendirilir.
- Ç7.5 Mutabakat: Kaynak/hedef toplam, hata ve kimlik eşleme raporu. Kabul: Pilot verisinin kaçının ve neden taşınamadığı izlenir.
Ç8 — Belge, şablon ve numaralandırma [GENİŞLETME / D2]
Bağımlılık: Ç3, Ç4, Ç13, Ç18, Ç22.
- Ç8.1 Seri yönetimi: Tenant/şirket/belge/yıl kapsamı, eşzamanlı tahsis, taslak ve kesin numara, iptal izi. Kabul: Paralel kesinleştirme çift numara vermez; gerekli seride atlanan/iptal numarası açıklanır.
- Ç8.2 Şablon motoru: Tipli ve sürümlü veri modeli, logo/dil, tekrar eden kalem, sayfa/alt bilgi ve güvenli ifade sınırı. Kabul: Şablon veritabanı, dosya sistemi veya internete keyfi erişemez.
- Ç8.3 Üretim ve snapshot: PDF ve gerekirse düzenlenebilir çıktı; sürüm, veri özeti/hash ve dosya bağı. Kabul: Geçmiş teklif o tarihteki fiyat/adresle tekrar görülebilir.
- Ç8.4 Dağıtım: Önizleme, e-posta, portal paylaşımı, gerektiğinde imza adaptörü ve gönderim geçmişi. Kabul: Alıcının görme yetkisi olmayan belge paylaşılmaz.
Ç9 — Ekran, liste, arama ve ortak kayıt deneyimi [GENİŞLETME / D2]
Bağımlılık: Ç0, Ç3, Ç15, Ç20, Y8–Y9.
- Ç9.1 Modül menüsü: Manifestten menü, rota, ikon ve sıralama; hak/izin kontrolü. Kabul: Gizlenen menünün doğrudan URL/API erişimi de reddedilir.
- Ç9.2 Metadata form/listeleri: Alan, bölüm, koşullu görünürlük, lookup, mobil kart; gelişmiş modül ekranları gerektiğinde özel kalabilir. Kabul: Görsel zorunluluk sunucu doğrulamasıyla eşleşir; sınırsız form motoru kapsamı açılmaz.
- Ç9.3 Görünümler: Kişisel/paylaşılan filtre, sütun ve sıralama; güvenli operatör listesi ve sayfalama. Kabul: Alan silinir veya izin değişirse görünüm güvenli biçimde uyarlanır.
- Ç9.4 Genel arama: Açık modül ve yetkili kapsamla sonuç; indeks gecikmesi ve silme/izin iptali politikası. Kabul: İndeks ve önbellek başka tenant veya gizli alan sonucu göstermez.
- Ç9.5 Ortak etkileşim: Not, etiket, yorum, bahsetme, takip ve aktivite zaman çizelgesi; edit/delete kuralları. Kabul: Bahsetme bildirimi erişimi olmayan kişiye kayıt içeriği taşımaz.
Ç10 — Raporlama ve gösterge paneli [GENİŞLETME / D2–D3]
Bağımlılık: Ç2, Ç9, Ç13, Ç22, Ç24. Sınır: Salt okur yol, yetkisiz yol değildir.
- Ç10a Panel (D2): Modül widget kataloğu, rol varsayılanı, kişisel düzen, filtre ve veri güncellik zamanı. Kabul: Toplamlar dahil tenant/şirket/kayıt/alan kapsamı uygulanır; kapalı modül widget'ı veri çekmez.
- Ç10b.1 Rapor modeli (D3): İzinli varlık/alan/ilişki kataloğu, gruplama, toplama, para birimi ve zaman dilimi anlamı. Kabul: USD ve TL tutarlar kur/tarih kuralı olmadan tek toplamda birleştirilmez.
- Ç10b.2 Çapraz modül okuma: Sürüm kontrollü projection/read model ve veri soy ağacı; doğrudan modül tablolarına serbest join verilmez. Kabul: Kullanıcı CRM fırsatını görebilse de yetkisiz personel maliyetini rapordan öğrenemez.
- Ç10b.3 Büyük rapor: Arka plan, satır/süre/kota, zamanlanmış e-posta ve erişimi tekrar denetlenen indirme. Kabul: Pahalı rapor operasyonel işlemleri kapasitesiz bırakmaz.
- Ç10c Ölçüm sözlüğü: Ciro, pipeline, gerçekleşen gelir, maliyet ve kârın tanımı/sahibi. Kabul: CRM fırsat bedeli muhasebe geliri diye sunulmaz; rapor tanımı kullanıcıya açıklanır.
Ç11 — Dış API, webhook ve sağlayıcı entegrasyonları [GENİŞLETME / D2–D4 / ADR]
Bağımlılık: Ç0–Ç2, Ç12 minimum, Ç13, Ç20. Sınır: Genel teknik bağlantı bu epicte, web kanalının ürün davranışı Ç19'dadır.
- Ç11a.1 Servis kimliği: Tenant/environment bağlı anahtar, scope, süre, iptal, rotasyon ve son kullanım. Rastgele yüksek entropili anahtarın yalnızca hash'i tutulur; dış sağlayıcı sırrı ise kasada şifreli tutulur. Kabul: Anahtar ikinci kez açık gösterilmez; başka tenant seçemez.
- Ç11a.2 API sözleşmesi: Sürümlü uçlar, sayfalama, ETag, açık iş komutları, idempotency, hata zarfı ve OpenAPI. Alan allowlist'iyle mass assignment engellenir. Kabul: Genel records erişimi yönetici rolü, onay durumu veya korunan tutarı keyfi değiştiremez.
- Ç11a.3 Kota: Tenant+anahtar+uç bazlı limit, 429 ve yeniden deneme bilgisi; eşzamanlılık/iş yükü sınırları. Kabul: Tek anahtarın sınırı aşılmasa da tenant toplamı sınırlanır.
- Ç11a.4 Webhook: Abonelik/olay sürümü, asgari payload, eventId+timestamp+gövde imzası, secret rotasyonu, tekrar/replay kontrolü ve teslim kaydı. HTTPS, çıkış ağı kontrolü, özel ağ/metadata hedef engeli, DNS yeniden çözümleme ve redirect politikası. Kabul: Aynı olay tekrar işlenmez; callback üzerinden iç servislere istek gönderilemez.
- Ç11b.1 Bağlayıcı çerçevesi: Sağlayıcı tanımı, bağlantı testi, secret kasası, mapping sürümü, health, cursor/checkpoint, retry ve mutabakat. Kabul: Kesinti sonrası tüm geçmişi çoğaltmadan kaldığı yerden sürer.
- Ç11b.2 e-belge temeli: Ülke/belge profili, şema doğrulama, sağlayıcı adaptörü, özgün belge, durum/hata/yanıt ve iptal/itiraz akışı. UBL-TR Türkiye paketidir. Kabul: Gönderim timeout'unda durum sorgulanmadan ikinci yasal belge oluşturulmaz.
- Ç11b.3 e-İrsaliye (D2): Lojistik/depo tarafından hazırlanmış sevk verisinin aktarımı ve sağlayıcı test ortamı. Kabul: Aynı sevk olayından birden fazla belge oluşmaz; reddedilen belge operasyon ekranında görünür.
- Ç11c.1 Finans ve İK (D4): e-Fatura/e-Arşiv/e-Defter ayrı kapsam ve doğrulama kapıları; banka hareketi adaptörleri; SGK/vergi dosya adaptörleri. Kabul: Teknik başarı ile yasal kabul ayrı durumlarda tutulur; güncel format uzman doğrulamasından geçer.
- Ç11c.2 Dış ERP/muhasebe: İsteğe bağlı veri ihracı/ithali, sahiplik ve kaynak önceliği. Kabul: Modülix ve dış sistem aynı tutarı kontrolsüz karşılıklı güncelleyip döngü yaratmaz.
Ç12 — SaaS aboneliği, kullanım ve tenant yaşam döngüsü [GENİŞLETME / minimum D1, ticari D3]
Bağımlılık: Ç2, Ç13, Ç14, Ç20.
- Ç12.1 Plan ve hak: Modül, özellik, kullanıcı ve kapasite hakları; tarihli istisna ve deneme. Minimum kota çerçevesi pilotta hazır olur. Kabul: Deneme bitişi veya düşürme veriyi silmez; kapalı modülün API/yürütme davranışı tanımlıdır.
- Ç12.2 Kullanım defteri: Kullanıcı, dosya, API, belge, e-posta ve AI ölçümleri; olay tekilleştirme ve dönem kesimi. Kabul: Olay tekrarı iki kez ücretlendirmez; tenant kullanımını görebilir.
- Ç12.3 Yaşam döngüsü: Taslak/kuruluyor/deneme/aktif/kısıtlı/askıda/kapanıyor/silinmiş; her durumda izin verilen erişim ve çalışan işler. Kabul: Askıya alınan tenantta API anahtarları ve işler kaçak yazma yapmaz.
- Ç12.4 Tahsilat ve fatura: Ödeme sağlayıcı imzası, ödeme durumu, idempotent callback, gecikme ve mutabakat. Platformun kendi faturalaması ürünün Muhasebe modülünü satın alma şartı değildir. Kabul: Çift callback çift abonelik/ödeme kaydı üretmez.
- Ç12.5 Kurulum ve kapanış: Profil, yönetici, modül seçimi, veri aktarımı; kapanışta ihracat, erişim iptali, saklama ve silme planı. Kabul: Yarım kurulum kurtarılabilir; veri paketi yalnızca ilgili tenantı içerir.
- Ç12.6 Ticari ayrıştırma: White-label, özel alan adı, portal, AI ve bağlayıcıların hak/kota tanımı. Kabul: Özellik bayrağı değiştirmek tek başına abonelik hakkı kazandırmaz.
Ç13 — Güvenlik, mahremiyet ve veri yaşam döngüsü [GENİŞLETME / D1, sürekli]
Bağımlılık: Ç0, Y1–Y8, Ç14. Not: Bu kapsam hukuki uygunluk onayı değildir; sözleşme ve yasal süreler uzmanla doğrulanır.
- Ç13a.1 Tehdit modeli: İnternet, platform yönetimi, tenant, worker, API, portal, nesne deposu ve AI güven sınırları. Kabul: Tenant atlama, IDOR, toplu alan atama, CSRF/XSS, injection ve secret sızıntısı senaryoları karşılanır.
- Ç13a.2 İzolasyon: RLS envanteri, normal uygulama DB rolü, pooling/tenant bağlamı, toplu işlemler, cache/arama/dosya ve kuyruk kapsamı. Kabul: İki tenantlı negatif test bütün erişim yollarında geçer; bağlam yoksa tenant verisi reddedilir.
- Ç13a.3 Sertleştirme: CSP/HSTS, güvenli cookie, CSRF/CORS modeli, parola/MFA, TLS, secret rotasyonu, bağımlılık/SAST/secret taraması. Kabul: Log ve hata yanıtlarında anahtar/parola/bağlantı sırrı bulunmaz.
- Ç13a.4 Envanter ve saklama: Veri kategorisi, amaç, sorumlu, süre ve silme işi; aktif DB, log, ek, cache, yedek ve AI indeksi kapsamda. Kabul: Süresi dolan veri tanımlı politikayla temizlenir; legal hold ayrı işler.
- Ç13b.1 Veri sahibi talepleri: Kimlik doğrulama, başvuru kaydı, kontrollü ihracat/silme/anonimleştirme, istisna ve sonuç kaydı. Pilot öncesi manuel işletim mümkündür. Kabul: Bir kişinin talebi başka kişilerin verisini dökmez; referanslı finans kaydı körlemesine silinmez.
- Ç13b.2 Rıza ve tercihler: İşleme amacı/hukuki dayanak ile açık rıza ve pazarlama tercihi ayrılır; metin sürümü ve geri alma saklanır. Kabul: Her web formuna otomatik “zorunlu açık rıza” konmaz; gerçek ihtiyaca göre yapılandırılır.
- Ç13c Olay müdahalesi: Erişim iptali, inceleme, kanıt saklama, iletişim ve kurtarma runbook'u. Kabul: Anahtar sızıntısı tatbikatında etkilenen tenant ve işlemler belirlenebilir.
Ç14 — Canlı ortam, gözlemleme ve kurtarma [GENİŞLETME / D1]
Bağımlılık: Ç0, Ç13, Ç23.
- Ç14.1 Ortam ve dağıtım: Dev/test/prod ayrımı, altyapı tanımı, TLS, health/readiness, secret kasası, kontrollü migration ve geri dönüş. Kabul: Üretim migration hesabı uygulama hesabından ayrıdır; eski uygulama ile yeni şema geçişi sınanır.
- Ç14.2 Veritabanı kapasitesi: Gerçek SQL Server sürümü/edition/OS sınırları, lisans, disk, bellek ve büyüme planı ADR-0001 üzerinden doğrulanır. Kabul: Express veya VPS varsayımı üretim kapasitesi kanıtı olmadan onaylanmaz.
- Ç14.3 Yedekleme: İş yükünden bağımsız tetikleme/izleme, DB+dosya+ayar eşgüdümü, şifreli dış kopya ve ayrı kimlik bilgileri. Kabul: Uygulama kapalıyken de yedek ve alarm çalışır; gerektiğinde log yedekleri recovery modeline göre tasarlanır.
- Ç14.4 Kurtarma: Önerilen pilot hedefi RPO ≤24 saat, RTO ≤4 saat; müşteri ihtiyacına göre kabul edilip kapasiteyle doğrulanır. Tenant bazlı kurtarma ortak yedeği izole ortama açıp seçici taşıma ile yapılır. Kabul: Prova ölçülür; tenant A kurtarılırken B'nin verisi karışmaz.
- Ç14.5 Gözlemleme: Log/trace/metrikte correlation, tenant ve işlem; gecikme, hata oranı, outbox yaşı, disk, kota ve yedek alarmları. Kabul: Bir web talebinin CRM kaydı ve webhook'una kadar izi takip edilir; PII maskelidir.
- Ç14.6 İşletim: Sağlık ekranı, nöbet/iletişim planı, kapasite ve sürüm notları. Kabul: Arıza runbook'u uygulamayı yazmayan bir operatörce takip edilebilir.
Ç15 — Tasarım sistemi, tema, erişilebilirlik ve dil [GENİŞLETME / D1–D4]
Bağımlılık: Y6, Y9; ekranlar Ç9, belge Ç8 ile bağlanır.
- Ç15a.1 Semantik tema (D1): --mx-primary, --mx-surface, --mx-success, --mx-danger vb.; lacivert / mavi / turkuaz varsayılanı (docs/design-system/theme-tokens.md; 31.10.2026 kararıyla indigo/lila/krem denemesi geri alındı). Kabul: Elle gömülü durum ve marka rengi kalmaz; hata yalnızca renkle anlatılmaz.
- Ç15a.2 Bileşenler: Form, tablo, modal, boş/yükleniyor/hata, toast, menü ve modül kartları. Kabul: Klavye odağı ve etiketler çalışır; temel akışlarda WCAG AA hedefi doğrulanır.
- Ç15b Firma teması (D2): Hazır tema, özel ana/vurgu renkleri, açık/koyu/sistem, logo ve güvenli önizleme. Kabul: Kontrastı yetersiz renk reddedilir; CSS/HTML enjeksiyonu kabul edilmez.
- Ç15c Dil (D1 temel, D4 İngilizce): Metin anahtarları baştan kullanılır; çoğul, çeviri, tarih/sayı/para ve dil fallback'i. Kabul: Domain kodu Türkçe etiketlere bağlı değildir; dil değişimi kaydedilmiş teknik değerleri değiştirmez.
- Ç15d Kanal tutarlılığı: Portal, form, e-posta ve belge görünümü ortak marka verisini kullanır. Kabul: White-label görünüm yetki veya veri sahipliği sınırlarını değiştirmez.
Ç16 — Mobil saha ve çevrimdışı işlemler [GENİŞLETME / D3]
Bağımlılık: Ç0, Ç4, Ç13, Ç15, Ç21, Ç24.
- Ç16.1 Saha deneyimi: Sürücü/depo/servis rolüne göre sade ekran, büyük dokunma hedefi, barkod/QR, kamera, izinli konum/imza. Kabul: Desteklenen cihaz ve tarayıcı matrisiyle gerçek cihazda doğrulanır.
- Ç16.2 Çevrimdışı kapsam: Hangi verinin cihazda saklanacağı, son kullanma, çıkışta temizleme ve hassas veri yasağı belirlenir. Kabul: Bordro gibi veri varsayılan çevrimdışı cache'e girmez; cihaz kaybı senaryosu değerlendirilir.
- Ç16.3 Kuyruk ve çakışma: Client operationId, kayıt sürümü, ek upload sırası, tekrar gönderme ve kullanıcıya çatışma çözümü. Kabul: Aynı barkod okutması çift stok hareketi yaratmaz; iptal edilmiş sevkiyata offline teslim sessizce işlenmez.
- Ç16.4 Güven sınırı: Cihaz saatine, konumuna ve çizilmiş imzaya mutlak delil anlamı yüklenmez; sunucu zamanı ve kaynak işaretlenir. Kabul: Ağ yokken yetki iptalinin etkisi ve yeniden bağlanma kontrolü açıkça tanımlıdır.
Ç17 — Sektör ve modül bağımsız AI altyapısı [GENİŞLETME / D4, okuma pilotu isteğe bağlı]
Bağımlılık: Ç3–Ç4, Ç9–Ç13, Ç20–Ç24.
- Ç17.1 Model geçidi: Sağlayıcı/model yönlendirme, timeout, kullanım ve maliyet bütçesi, veri saklama ve bölge politikası. Kabul: Kota aşımında kontrollü durur; istem/yanıtlar varsayılan ham loglanmaz.
- Ç17.2 Modül agent sözleşmesi: Agent manifesti; yetenek, araç şeması, gereken modül ve scope. CRM agent'i fırsat özetler, lojistik agent'i teslim riski analiz eder. Kabul: Agent başka modülün domain tablosuna doğrudan erişemez.
- Ç17.3 Güvenli RAG: Tenant/şirket/alan yetkili retrieval, belge kökeni, silme/izin değişikliğiyle indeks temizleme. Kabul: Farklı tenant veya yetkisiz İK içeriği cevap/alıntıda görünmez.
- Ç17.4 Araç ve yazma: Sunucuda yeniden yetkilendirme, taslak/dry-run, insan onayı, idempotency; onay hangi veri/sürüm/eylem için verildiğini kapsar. Kabul: Kullanıcı onayından sonra değişen fiyatla işlem yürütülmez; kamuya açık bot iç API araçlarını kullanamaz.
- Ç17.5 Güvenlik ve kalite: Prompt injection ve veri sızıntısı senaryoları; doğruluk, kaynaklılık, başarısızlık, maliyet ve gecikme değerlendirmesi. Kabul: Eşik altındaki agent yayınlanmaz; model değiştirme kontrolsüz davranış değişimi yaratmaz.
- Ç17.6 İlk teslim: Kayıt özeti, belge/e-postadan alan önerisi ve kaynaklı arama. Agent'lar arası koordinasyon daha sonra Ç21 üzerinden. Kabul: Belirsiz bilgi uydurulmaz; kritik hesapların sahibi deterministik domain motorudur.
Ç18 — Ortak hesap araçları ve sürümlü parametreler [GENİŞLETME / D2–D4 / ADR]
Bağımlılık: Ç3b–d, Ç22. Sınır: Core genel araçları tutar; bordro/navlun/ardiye algoritmaları sahip modüldedir.
- Ç18a.1 Tipler ve yuvarlama: Decimal para+miktar, para birimi, hassasiyet, birim, satır/toplam kuralları. Kabul: Float kullanılmaz; dönüşüm ve yuvarlama aynı test vektöründe aynı sonuç verir.
- Ç18a.2 Tarife altyapısı: Tipli boyut, geçerlilik aralığı, öncelik, kapsam ve sürüm; güzergâh/araç gibi boyutları lojistik kaydeder. Kabul: Eşit öncelikli çakışan tarifeler sessizce seçilmez; önizlemede kullanılan tarife görülür.
- Ç18a.3 Hesap izi: Girdi, kur, parametre, algoritma sürümü, kalem ve toplam saklanır. Kabul: “Bu tutar nasıl çıktı?” açıklanır; eski hesap aynı sürümle yeniden üretilebilir.
- Ç18a.4 Yeniden hesaplama: Taslak bağımlılıkları ve stale işareti; kesinleşmiş tutara düzeltme/ters kayıt politikası. Kabul: Tarife değişimi onaylı teklifi veya muhasebeleşmiş belgeyi değiştirmez.
- Ç18b.1 Parametre sicili: Ülke, tür, yürürlük tarihi, kaynak, doğrulayan ve yayınlayan; firma parametreleri ayrı. Kabul: Geriye dönük parametre değişimi geçmiş bordroyu sessizce yeniden hesaplamaz.
- Ç18b.2 Dönem araçları: Şirket/modül/dönem bazlı açık-kapalı-kilitli; kapatma denetimleri ve yeniden açma yetkisi. Kabul: İK dönemi kapanması depo operasyonunu yanlışlıkla durdurmaz.
- Ç18b.3 Domain sınama setleri: Vergi/bordro için uzman doğrulanmış örnekler; navlun/depo için sözleşme örnekleri. Kabul: Parametre/algoritma sürümü değiştiğinde ilgili modülün beklenen sonuçları geçer.
Ç19 — Firma web sitesi entegrasyonu ve dış kullanıcı kanalı [YENİ / D1–D4 / ADR]
Amaç: Firma mevcut PHP, WordPress veya özel web sitesini koruyarak Modülix'e bağlanabilsin. Bağımlılık: Ç0–Ç5, Ç11 minimum, Ç12 minimum, Ç13, Ç20–Ç22. Sınır: Genel web sitesi/CMS geliştirmek bu epicin ilk kapsamı değildir. API ve webhook güvenliği Ç11'den kullanılır; burada form, kanal ve iş yönlendirme davranışı geliştirilir.
Ç19.1 — Web sitesi ve bağlantı yönetimi [D1]
- Tenant altında birden çok site, ortam, ad, domain, sahip ve bağlantı türü tanımlanır. Domain sahipliği DNS veya dosya doğrulamasıyla kontrol edilebilir; yeniden doğrulama ve iptal politikası bulunur.
- Dev/test/prod bağlantıları ve kimlik bilgileri ayrıdır. Domain/CORS allowlist'i yalnızca ek kontroldür, kimlik doğrulama yerine geçmez.
- Firma yöneticisi sitelerini; platform yöneticisi yetkisi ölçüsünde bağlantı sağlığını ve kötüye kullanım metriklerini görür.
- Kabul: Bir site bağlantısına verilen credential başka tenantı seçemez; test formu üretim verisi oluşturmaz.
Ç19.2 — Yerleştirilebilir form oluşturucu [D1]
- İletişim, teklif ve destek talebi şablonları; metin, sayı, seçenek, dosya, aydınlatma metni ve gerekliyse rıza/tercih alanı. Form sürümlenir; taslak/önizleme/yayın/kapalı durumu vardır.
- İlk teslim iframe; sonra JS bileşeni. Iframe postMessage iletişiminde kaynak/hedef ve mesaj şeması doğrulanır. Tema erişilebilirlik sınırları içinde özelleştirilir.
- Yayınlanan form kimliği herkese açık olabilir; gizli yönetim anahtarı değildir. Göndericiye yalnızca alındı numarası verilir; kayıt listesi/arama açılmaz.
- Honeypot, bot tespiti, gerektiğinde CAPTCHA, payload/dosya sınırı ve tenant+form+IP bazlı hız kontrolü uygulanır. Origin kontrolü tek başına koruma sayılmaz.
- Kabul: Firma sitesine tek yerleştirme koduyla form eklenir; form kimliğini bilen kişi başka başvuruları okuyamaz veya hedef modülü değiştiremez.
Ç19.3 — Başvuru kutusu, eşleme ve modül yönlendirme [D1]
- Kamuya açık veri önce WebsiteSubmission benzeri karantinalı başvuruya alınır. Doğrudan yetkili bir müşteri kartını güncellemez. Kaynak form/site/sürüm ve güvenli alındı zamanı saklanır.
- Yönetici alan eşlemesini, zorunlulukları, mükerrer stratejisini, ekip/sorumlu ve hedef işlemi seçer. Hedef adaptörler açık modülün yetkisini ve domain kurallarını uygular.
- CRM açıksa satış adayı/aktivite; lojistik açıksa taşıma talebi; İK açıksa aday; destek açıksa talep üretilebilir. Hedef modül yoksa başvuru kutusunda manuel değerlendirme sürer.
- E-posta benzerliği yalnızca aday eşleşme sağlar; doğrulanmamış başvuru mevcut müşterinin telefon/adresini ezmez. Ekler Ç4 karantinasından geçer.
- Kabul: Aynı submissionId yeniden işlendiğinde ikinci fırsat oluşmaz; hedef modül kapanırsa başvuru kaybolmaz ve anlaşılır durum gösterilir.
Ç19.4 — Mevcut formlar için sunucudan API bağlantısı [D1/D2]
- PHP veya başka backend, tenant/site/scope bağlı servis kimliğiyle başvuru ucu çağırır. Kapsam örneği: website.submissions:create; genel yönetim izni verilmez.
- İstek; form sürümü, dış başvuru kimliği, izinli alanlar ve idempotency anahtarı taşır. Sunucu requestId ve başvuru durumunu döndürür; 202 asenkron kabul ile tamamlanma ayrılır.
- Kurulum rehberi, örnek PHP istemcisi, timeout/retry ve hata sözlüğü geliştirilir. API anahtarı JS veya HTML'e konulmaz.
- Kabul: Web sitesinde bağlantı kesilip gönderim tekrarlanınca tek talep oluşur; tarayıcı kaynak kodunda secret bulunmaz.
Ç19.5 — Kaynak, aydınlatma ve tercih kaydı [D1]
- Kaynak URL, izinli UTM alanları, kampanya ve referrer sınırlandırılarak tutulur; URL sorgusundaki kişisel veriler körlemesine kaydedilmez. Metin sürümü ve gerekiyorsa rıza olayı ayrıdır.
- Saklama süresi başvuru türüne göre belirlenir. CV ve destek eki gibi içerikler aynı süreye zorlanmaz. Ziyaretçi analitiği ve pazarlama çerezleri ayrı sonraki kapsamdır.
- Kabul: Bir teklif talebi otomatik pazarlama izni üretmez; başvuru kaynağı satış dönüşüm raporunda izlenebilir.
Ç19.6 — Ürün ve içerik yayınlama [D2/D3]
- Ortak ürün/hizmet kartından seçilmiş yayın projeksiyonu: başlık, açıklama, görsel, kategori ve izinli fiyat. İç maliyet, tedarikçi ve müşteri özel fiyatları varsayılan dışarı çıkmaz.
- Alan bazlı kaynak sahipliği belirlenir: örneğin ürün kodu Modülix'te, SEO açıklaması web CMS'inde yönetilebilir. Yayın taslağı/onayı, sürüm, cache invalidation ve kaldırma işlemi vardır.
- WordPress/PHP bağlayıcısı pilot ihtiyacına göre seçilir. İlk aşamada bütün CMS'leri destekleme hedefi konmaz.
- Kabul: Yayından kaldırılmış ürün cache süresi/iptal kuralına uygun kaybolur; maliyet fiyatı public API yanıtında yer almaz.
Ç19.7 — Sipariş, stok ve durum senkronizasyonu [D3]
- Alan sahipliği tablosu, dış kimlik eşlemesi, cursor, olay, retry ve periyodik mutabakat birlikte uygulanır. Stok sahibinin depo, sipariş sahibinin satış veya seçilen dış e-ticaret sistemi olduğu açıkça belirlenir.
- Web siparişi domain komutuyla alınır; fiyat/indirim/vergi sunucuda doğrulanır. Stok gösterimi, rezervasyon ve fiziksel mevcut ayrı kavramlardır. Aynı ürünü iki kanal satarken atomik rezervasyon gerekir.
- Ödeme sonucu imzalı sağlayıcı callback'iyle doğrulanır; tarayıcının “ödendi” alanına güvenilmez. İade, iptal ve kısmi teslim karşılıklı durum eşlemesine dahildir.
- Kabul: Aynı sipariş iki kez yaratılmaz; ağ kesintisinden sonra stok/sipariş farkı mutabakat ekranında görünür; döngüsel güncelleme olmaz.
Ç19.8 — Müşteri ve iş ortağı portalı [D3/D4]
- İç kullanıcıdan ayrı dış üyelik: kişi–taraf ilişkisi, doğrulanmış davet, taraf yöneticisi, alt kullanıcı ve taraf kapsamı. Bir müşteri birden çok tenantla çalışıyorsa üyelikler açıkça ayrılır.
- Teklif görüntüleme/kabul, sipariş, gönderi, destek ve paylaşılan belge; yalnızca açık ve yetkili modülün dar projeksiyonu. İç not, maliyet, personel ve diğer müşteriler görünmez.
- Kimlik politikası; MFA ihtiyacı, oturum süresi, üyelik iptali ve gerekiyorsa şirket SSO'su. Anonim takip varsa tahmin edilemez süreli token ve çok sınırlı bilgi kullanılır.
- Kabul: URL'deki kayıt kimliği değiştirilince başka müşteri kaydı açılmaz; firma yetkilisi ayrılınca portal erişimi kesilir; sadece e-posta domain'i şirket yetkisi kazandırmaz.
Ç19.9 — AI web asistanı [D4 / isteğe bağlı]
- Onaylı kamuya açık bilgi tabanı ile ziyaretçi soruları, kaynaklı yanıt, insan desteğine aktarma ve teklif başvurusu oluşturma. Ç17'nin ayrı public agent profili kullanılır.
- Fiyat taahhüdü, iç kayıt sorgusu, ödeme ve hassas değişiklikler anonime açılmaz; doğrulanmış kullanıcı işlemleri portalın araçları ve yetkileriyle yürür.
- Kabul: Prompt injection ile iç müşteri/personel verisi alınamaz; bot işlem yaptıysa başvuru kimliği ve denetim izi vardır.
Ç19.10 — İzleme, kurulum ve hizmet kalitesi [D1–D4]
- Firma ekranı: siteler, formlar, gönderimler, eşlemeler, son başarı, hata, kuyruk, kota ve yeniden işleme. Platform ekranı: toplu sağlık, kötüye kullanım ve bağlantı sürümleri.
- Form sürümü güncellemesi, kapalı modül, servis kesintisi, spam, çoklu site ve tenant geçişi test edilir. Kurulum rehberi ve örnek payload sağlanır.
- Kabul: Yanlış mapping kuru çalıştırmada görülür; başarısız başvuru gerekçesi kaybolmaz; yeniden işleme yalnızca yetkili kişiye açıktır.
İlk ticari teslim sınırı: Ç19.1–Ç19.5 ve Ç19.10'un minimumu. İframe form + mevcut PHP formu API bağlantısı + başvuru kutusu + CRM yönlendirme. Katalog, e-ticaret, portal ve bot sonraki aşamadır.
Ç20 — Modül kataloğu, yaşam döngüsü ve uyumluluk [YENİ / D0–D2 / ADR]
Bağımlılık: Ç0 hak kontrolü sözleşmesi, Y3/Y9 minimum. Ç12 bu sözleşmenin ticari uygulamasını sağlar. Amaç: Modüllerin bağımsız kurulması, yükseltilmesi ve birlikte çalışması.
- Ç20.1 Manifest: Kimlik/sürüm, core uyumu, izin, menü, kayıt, olay, iş, rapor, agent ve gereken yetenekler. Kabul: Geçersiz veya döngüsel bağımlılık açılışta reddedilir.
- Ç20.2 Sert/yumuşak bağımlılık: Teknik zorunluluk ile isteğe bağlı bağlantı ayrılır. CRM→lojistik bağlantısı opsiyoneldir; lojistik CRM'e zorunlu bağlanmaz. Kabul: Yalnız lojistik lisanslı tenant işlem yapabilir.
- Ç20.3 Açma/kapatma: Provision, migration, seed, kapatma öncesi analiz, devam eden işler ve veri saklama politikası. Kabul: Modül kapatma faturaları silmez; bekleyen süreç yöneticiye gösterilir.
- Ç20.4 Yükseltme ve rollout: Paket sürümü/tenant konfigürasyonu, canary/feature flag, geriye uyum ve geri dönüş. Kabul: Yeni sözleşme eski olayları okuyabilir veya açık migration sağlar.
- Ç20.5 Sektör paketleme: Aynı modül birden çok profilde kullanılabilir; profil geçişi fark planıyla uygulanır. Kabul: Lojistik ve toptan profillerinin CRM'i ayrı fork'lara dönüşmez.
Ç21 — Modüller arası süreç koordinasyonu ve durum makineleri [YENİ / D2–D3 / ADR]
Bağımlılık: Ç0–Ç2, Ç20 minimum. Onay gereken adımlar Ç6 kullanır. Sınır: Her modül kendi durum makinesinin sahibidir; koordinatör domain kurallarını taşımaz.
- Ç21.1 Süreç sözleşmesi: Süreç kimliği, tetikleyici, adım, hedef komut, ön koşul, süre, sonuç ve sürüm. Kabul: Bir fırsat birden çok sevkiyat veya siparişe bağlanabilir; tekil ilişki zorlanmaz.
- Ç21.2 Kalıcı örnek: Adım geçmişi, bekleme, correlation, timeout ve elle müdahale; tenant+iş kaynağı tekilleştirme. Kabul: Worker çökünce kaldığı adımdan sürer; çift olay ikinci süreç açmaz.
- Ç21.3 Hata ve telafi: Retry ile iş reddi ayrılır; rezervasyon iptali, taslak kapama veya ters belge gibi domain telafileri. Kabul: Bir modül başarısız olduğunda “hepsi oldu” görünmez; kesinleşmiş finans kaydı fiziksel silinmez.
- Ç21.4 Opsiyonel köprüler: CRM kazanıldı → satış/lojistik taslağı; teslim → fatura önerisi; çalışan→sürücü ilişkilendirme. Köprü ayrı sözleşme adaptörüdür. Kabul: Hedef modül yoksa kaynak işlem başarısız olmaz; yönetilen alternatif oluşur.
- Ç21.5 Görünürlük: Uçtan uca süreç kartı; kullanıcı yalnızca yetkili olduğu adım ve alanları görür. Kabul: Satış kullanıcısı sürecin ilerlediğini görürken gizli bordro maliyetini göremez.
Ç22 — Tenant içi şirket, şube ve organizasyon [YENİ / minimum D1, tam D2]
Bağımlılık: Ç0, Y2–Y3. Amaç: Tenantı müşteri firma kartı veya tek tüzel kişiyle karıştırmamak.
Durum (25 Ekim 2026): [ADR-0008](adr/0008-sirket-ve-sube-kapsami.md) kabul edildi. Adım 1 yapıldı (şirket ve şube tabloları, varsayılan şirket, kapsam sınıfı kapısı). Adım 2 yapıldı (üyelik–şirket bağı `core.MembershipCompanies`, oturumda aktif şirket ve her istekte doğrulama, `/api/auth/active-company`, `core.companies.manage` ile şirket yönetimi; yönetici bütün şirketlere erişir; arayüzde şirket seçici ve yönetim ekranı). Adım 3 yapıldı (kayıt boru hattında şirket kapsam adımı: okumada erişilebilir şirket süzgeci, yazmada aktif şirkete düşme + erişim doğrulaması, güncellemede şirket değişmezliği; olay zarfında `CompanyId`, `CompanyField` ile kapılı, migration `OlayZarfiSirket`). Adım 4 yapıldı (CRM tabloları "ortak" teyidi). Adım 5 yapıldı (migration `SirketAdim5VekaletSubeAyar`): geçici vekâlet (`MembershipCompany.ValidFrom`/`ValidUntil`, süresi dolunca erişim kendiliğinden kapanır — pasif, ADR'ye birebir; verme/alma denetlenir), şube yönetimi (`/api/companies/{id}/branches`, idari birim; kapsam sınırı değil) ve üyeliğin varsayılan şubesi, şirket başına ayar çerçevesi (`SettingDefinition.PerCompany` + `TenantSetting.CompanyId`; mekanizma kuruldu, çekirdek henüz `PerCompany` ayar sevk etmez). Kalan: abonenin olayı kendi şirket kapsamına göre süzmesi (ilk `ICompanyScoped` tabloyla) ve Ç22.4 şirketler arası işlem.
- Ç22.1 Organizasyon modeli: Tenantın işletici tüzel kişileri, şube, departman, ekip ve lokasyon ayrı anlamlarla modellenir. Kabul: Müşteri Taraf kaydı şirket sahibi hesabıyla aynı varlık değildir.
- Ç22.2 Kayıt kapsamı: Tenant ortak ana veri ile şirket özel işlem/finans kayıtları sınıflandırılır. Her tabloda kapsam bilinçli seçilir. Kabul: Aynı tenantın şirket A muhasebecisi B faturalarını izin olmadan göremez.
- Ç22.3 Kullanıcı bağları: Birden çok şirket/şube görevi, geçici vekalet ve varsayılan çalışma kapsamı. Kabul: Ekrandaki aktif şirket değiştirmek yetki kazanımı sağlamaz.
- Ç22.4 Şirketler arası işlem: Karşılıklı belge, borç/alacak ve mutabakat ileriki paket olarak ayrı tasarlanır. Kabul: Farklı şirketlerin hesap hareketi tek defterde karışmaz; otomatik konsolidasyon ilk kapsam sayılmaz.
Ç23 — Mimari kalite, test ve teslim kapıları [YENİ / D0, sürekli]
Bağımlılık: Ç0. Ç13 ve Ç14 için kalite kapılarını sağlar. Amaç: “Tamamlandı” beyanını ölçülebilir kanıta dönüştürmek.
Durum (25 Ekim 2026): Beyanlar kanıtlarıyla [envanter.md](envanter.md)'de. Yeni kapılar: tablo sınıflandırması ve RLS, uç nokta koruma matrisi, modül veri erişimi. Eksik: E2E, restore provası, güvenlik taraması (B7, B8).
- Ç23.1 Test katmanları: Domain kuralı birim testleri; transaction/RLS için gerçek SQL Server entegrasyon; sözleşme ve kritik uçtan uca akış. Kabul: In-memory test RLS kanıtı sayılmaz; build'de mimari sınırlar kontrol edilir.
- Ç23.2 Negatif matris: İki tenant, iki şirket, farklı rol, kapalı modül, gizli alan; API/import/rapor/arama/AI yolları. Kabul: Yeni tenant tablosu envanter ve izolasyon testine girmeden birleşmez.
- Ç23.3 Dayanıklılık ve yük: Tekrar/sıra dışı olay, worker çökmesi, entegratör timeout, iki kullanıcılı çakışma ve tenant gürültüsü. Kabul: Mutabakat ve idempotency sonuçları iş kaydı üzerinden doğrulanır.
- Ç23.4 Sürüm kapısı: Migration provası, sözleşme farkı, güvenlik taraması, geri dönüş ve pilot kabul senaryoları. Kabul: Kritik açık, başarısız restore veya veri kaybı testi olan sürüm yayınlanmaz.
- Ç23.5 Dokümantasyon: Her epic için sahibi, API/olay, kabul kanıtı, kullanım ve runbook. Kabul: Yeni geliştirici şablondan bağımsız bir örnek modülü ekleyebilir.
Ç24 — Ölçek, cache, projection ve veri erişim performansı [YENİ / minimum D1, tam D3]
Bağımlılık: Ç0–Ç2, Ç10, Ç13–Ç14.
- Ç24.1 Sorgu standardı: Sayfalama, üst limit, indeks, N+1 kontrolü, pahalı filtre sınırı ve büyük export ayrımı. Kabul: Tenant veri hacmi büyürken sınırsız liste okuması yapılmaz.
- Ç24.2 Cache izolasyonu: Tenant/şirket/kullanıcı/izin sürümü gereken anahtara dahil edilir; değişim ve hak iptalinde invalidation. Kabul: A kullanıcısının cache sonucu B'ye yetkisiz sunulmaz.
- Ç24.3 Projection: Olaydan beslenen rapor/arama/public katalog görünümleri; checkpoint, gecikme, rebuild ve schema sürümü. Kabul: Rebuild canlı yazmayı bozmaz; kullanıcı verinin ne kadar güncel olduğunu görür.
- Ç24.4 Ölçek kapıları: Kuyruk yaşı, p95 gecikme, DB/disk ve tenant başına tüketim ölçülür; ihtiyaçta indeks, worker havuzu, ayrılmış kaynak veya tenant taşıma değerlendirilir. Kabul: Ölçüm olmadan mikroservis/tenant başına DB zorunluluğu getirilmez.
6. İş modülleri — sektör bağımsız sınırlar ve detaylı epikler
M1–M5 kaynak yol haritasındaki kapsamı korur. M6–M8, mevcut metindeki satış/tedarik/destek boşluklarını görünür kılan öneri epikleridir; ilk pilotun tamamını bekletmez. Yeni modüller pazar ihtiyacı doğrulanınca açılır. Her modülün kendi rol, rapor, API, olay ve sektör profil katkısı olmalıdır.
M1 — CRM [MEVCUT + GENİŞLETME / D1–D2]
Amaç: Sektör bağımsız ilişki ve satış fırsatı yönetimi. Zorunlu: Core+ortak taraf/kişi. Opsiyonel: Satış, lojistik, destek, web. Sınır: Fiziksel stok ve muhasebe defteri CRM'e ait değildir.
- M1.1 Müşteri ve ilişki: Ortak Taraf/Kişi, müşteri segmenti, kaynak, iletişim tercihi, ilişkili firmalar ve kayıt geçmişi. Kabul: CRM olmadan oluşturulmuş taraf CRM açılınca tekrar yaratılmaz; ortak kart yetkileri korunur.
- M1.2 Potansiyel müşteri: Web/import/manuel aday, benzer kayıt, değerlendirme ve nitelendirme. Kabul: Nitelendirme aday→taraf/kişi/fırsat bağlarını tekilleştirir; reddedilen adayın gerekçesi saklanır.
  Lead Yönetimi iş paketleri ve gerçek durum (28 Ekim 2026; tasarım ve sınırlar: [ADR-0014](adr/0014-lead-yonetimi.md)). "Doğrulandı" = otomatik testle sınandı; "Yapıldı, elle doğrulanmadı" = kod derleniyor ama tarayıcıda gezilmedi.
  - M1.2a Lead modeli, izinler, tenant migration (`LeadManagement`, `LeadForms`), temel CRUD, kayıt boru hattı, RLS, özel alan/seçim listesi/audit kaydı. **Doğrulandı** (birim + entegrasyon + tablo envanteri).
  - M1.2b Yetki ve kapsam: ayrı oluştur/güncelle/sil/ata/nitelendir/ele/dönüştür/dışa aktar izinleri, Own/Team/All kapsamı, atama havuzu görünürlüğü. **Doğrulandı.**
  - M1.2c Aktivite entegrasyonu (`leadId`, sonuç), gerçekleşen temastan ilk temas, New→Contacting otomasyonu, tekrar teslimde çoğalmama. **Doğrulandı.** İlk temas gecikme göstergesi liste/detay ekranında: **yapıldı, elle doğrulanmadı**.
  - M1.2d Atama (audit eski/yeni sorumlu, idempotent), nitelendirme/eleme/takip/yeniden açma ve geçiş kuralları. **Doğrulandı.** Atama bildirimi mevcut genel abonelikten gider (Lead için ayrıca e-posta testi **yok**).
  - M1.2e Fırsata dönüşüm (mevcut/yeni firma ve kişi, gerçek kişi müşteri, atomik, geri alma, eşzamanlı tek fırsat, aktivite çoğalmaması, yetki). **Doğrulandı.** Dönüşüm ekranı: **yapıldı, elle doğrulanmadı.**
  - M1.2f Web formu/entegrasyon toplama (tenant URL'den, alan listesi, sır özeti, origin/CORS, hız sınırı, bal tuzağı, idempotent gönderim, sızıntısız yanıt, pasif varsayılan sorumlu → havuz). **Doğrulandı** (entegrasyon testleri; 32 KB gövde sınırı sınanmadı). Form yönetim ekranı: **yapıldı, elle doğrulanmadı.**
  - M1.2g Mükerrer incelemesi (Lead/firma/kişi, yetki sınırlı, otomatik birleştirme yok). **Doğrulandı** (Lead–Lead). Firma/kişi eşleşmesinin yetki sınırı mevcut CRM kapsam testleriyle aynı boru hattına dayanır; Lead uçlarına özel ayrı test **yok**.
  - M1.2h Raporlama (kaynak, durum, sorumlu, ilk temas süresi/hedef aşımı, dönüşüm oranı, dönüşüme kadar süre, eleme nedenleri; kohort/payda kuralları, kapsam). **Doğrulandı:** kohort/dönem ayrımı, boş kohortta oran ve süre `null`, dönüşüm oranı, eleme nedeni, kapsam ve dışa aktarmanın audit'i. **Doğrulanmadı:** ilk temas süresi ortalaması/medyanı ve hedef aşımı hesabı (veri içeren test yok; yalnızca boş kohort sınandı). Büyük hacimde veritabanı tarafı toplama **yapılmadı** (5000 kayıt sınırı).
  - M1.2i Ekranlar: Lead listesi (görünümler/filtreler/rapor/CSV), detay (zaman çizelgesi, durum geçmişi, mükerrer uyarısı, eylemler), firma/kişi "İlişkili Lead'ler", fırsatta "Kaynak Lead". **Yapıldı, elle doğrulanmadı** (tarayıcı/E2E testi yok; yalnızca derleme).
  - M1.2j **Yapılmadı:** AI ajan araçları (altyapı yok; `ILeadWorkflow`/`ILeadConversion` araç olarak sunulabilecek şekilde tasarlandı), izin/rıza (consent) kaydı, CSV/Excel içe aktarma, kampanya ve ürün kataloğu ilişkisi, iş birimi sahipliği, kural tabanlı otomatik atama (round-robin/bölge/ürün), Lead birleştirme, ilk temas gecikmesi bildirimi.
- M1.3 Fırsat ve huni: Birden çok satış süreci, aşama kuralları, tahmini bedel, kapanış, kazanma/kaybetme ve gerekçe. Kabul: Sektör profili huniyi değiştirebilir; aşama değişimi domain kurallarına tabidir.
  - **Durum (29 Ekim 2026, [ADR-0017](adr/0017-firsat-yonetimi.md)) — Fırsat yönetimi, kısmen tamamlandı.** M1.3a Veri modeli ve migration (`OpportunityManagement`: numara, durum, kapanış alanları, ihtiyaç analizi, bütçe durumu; `OpportunityContacts`, `OpportunityCompetitors`, `OpportunityHistory`; RLS; mevcut kayıtların geri doldurulması). **Uygulandı; migration yerel veritabanlarına uygulandı, `acme` verisiyle doğrulandı** (kapanmış kayıtların geri doldurulması gerçek veriyle sınanmadı). M1.3b Yaşam döngüsü (aşama değiştir, bekle/çık, kazan, kaybet, iptal, yeniden aç; aşama–durum tutarlılığı; aşama koşulları; olasılık kuralı; tarihçe; kapanış döngüsü; eşzamanlılık). **Uygulandı, entegrasyon testleriyle doğrulandı.** M1.3c Fırsat–kişi rolleri ve rakipler; kaybetme nedeni/kazanan rakip. **Uygulandı, test edildi.** M1.3d Yetki (`change-stage`, `close`, `reopen`, `export`), kapsam ve tutar izni; rapor ve dışa aktarma. **Uygulandı, test edildi.** M1.3e Ekranlar (pano eylemleri, fırsat sayfası, rapor). **Yapıldı, tarayıcıda elle doğrulanmadı.**
  - **M1.3f Yapılmadı:** çoklu pipeline ve aşama başına yönetilebilir olasılık/bekleme eşiği; teklif entegrasyonu (esas teklif, tutar kaynağı); sipariş/sözleşme/proje devri (hedef modül yok, sahte entegrasyon kurulmadı); satış tahmini anlık görüntüsü; atama, geciken aksiyon, yaklaşan kapanış ve aşamada bekleme bildirimleri; kampanya/referans bağlantısı; dosya eki (Ç4); iş birliği yapan kullanıcılar ve ekip/business unit sahipliği; ayrı atama izni; toplu işlemler; sunucu taraflı sıralama seçimi; kur dönüşümü.
- M1.4 Aktivite ve görev: Görüşme, toplantı, yapılacak iş, hatırlatma, sahiplik ve ekip görünümü. Kabul: Atama bildirimi ve kapsam uyumludur; ayrılan kullanıcıda açık görevler devredilebilir.
  - **Durum (29 Ekim 2026, [ADR-0015](adr/0015-aktivite-ve-takvim.md)):** M1.4a Yaşam döngüsü (tür ailesine göre durum makinesi, eylemler, gerekçe, yeniden açma, geçmiş tarihli kayıt, türetilmiş gecikme, sonraki adım zinciri, erteleme geçmişi, eşzamanlılık). **Uygulandı; entegrasyon testleri ile doğrulandı (sonuçlar teslim notunda).** M1.4b Yetki ve gizlilik (ayrı atama/yeniden açma/ekip takvimi/özel aktivite izinleri, katılımcı kapsamı, özel aktivite meşguliyet görünümü, toplu işlemde kayıt başına sonuç). **Uygulandı, test edildi.** M1.4c İlişkiler (merkezi `IActivityLinkTarget`, çoklu ve birincil ilişki, görünürlük doğrulaması, birleşik zaman akışı, birleştirme taşıması). **Uygulandı, test edildi** (Lead dönüşümü ve birleştirmede taşıma: kodlandı, ayrı entegrasyon testi yok). M1.4d Liste + takvim (ay/hafta/gün/ajanda, süzgeçler, kayıtlı görünüm, sürükle-bırak, çakışma). Sunucu **test edildi**; ekranlar **yapıldı, tarayıcıda elle doğrulanmadı**.
  - **M1.4e Yapılmadı (bağımlılıkla):** tekrar serileri; hatırlatma/bildirim/eskalasyon (Ç2, Ç5); takip planları; davet ve katılım yanıtı; alt görev/kontrol listesi/bağımlılık; yorum ve dosya eki (Ç4); ekip kuyruğu ve BusinessUnit; çalışma saati, tatil ve müsaitlik; oda/araç kaynağı; harici takvim ve e-posta senkronu (sağlayıcı erişimi yok, hiçbiri çalışıyor gösterilmez); otomasyon kuralları; yapay zekâ araçları; raporlar; ayrı oluştur/tamamla/sil izinleri (rol yükseltme mekanizması gerekir).
- M1.5 Teklif: Ürün/hizmet satırı, para birimi, indirim, tarife, revizyon, geçerlilik, onay ve PDF. İlk teklif sahibi CRM'dir; M6 aynı teklif tablosunu yeniden oluşturmaz. Kabul: Onaylı revizyon korunur; kabul edilen teklif ile taslak fiyat birbirine karışmaz.
  - **Durum (30 Ekim 2026, [ADR-0019](adr/0019-teklif-yonetimi.md)) — Teklif yönetimi, kabul kriteri sağlandı; tamamlandı sayılır (aşağıdaki sınırlarla).** M1.5a Çekirdek: sunucu hesap motoru (`QuoteCalculator`: satır indirimi → genel indirim dağıtımı → vergi; dahil/hariç; opsiyonel/alternatif/paket/tekrarlayan; politika sürümü), geçiş kuralları, veri modeli (`Quotes`, `QuoteLines`, `QuoteEvents`; RLS). **Uygulandı, birim ve entegrasyon testleriyle doğrulandı.** M1.5b Yaşam döngüsü: ticari/iç onay/gönderim durumları ayrı; onaysız sunum, süresi dolmuş kabul ve çifte kabul sunucuda engellenir; revizyon ailesi (V1/V2), sunumda eski sürüm `Superseded`, aile başına tek `Accepted` (filtreli benzersiz indeks), sunulmuş sürüm yerinde değişmez, ticari değişiklik onayı geçersiz kılar, eşzamanlılık 409. **Uygulandı, test edildi.** M1.5c Yetki: `read|write|discount|manual-price|approve|present|record-decision|cancel|share|export`, `quote-costs.read`, `quote-rules.manage`; maliyet yetkisiz yanıta hiç girmez. **Uygulandı, test edildi.** M1.5d PDF ve müşteri: sunumda sabit PDF (QuestPDF; SHA-256, şablon sürümü), paylaşım bağlantısı (bir kez gösterilen belirteç, süre, iptal), oturumsuz müşteri sayfası (hız sınırı, tek tip 404), açık kabul/ret eylemi ve onay metni kanıtı, müşteriye e-posta (durum dürüst: "sağlayıcı kabul etti" ≠ teslim). **Uygulandı, test edildi; PDF ve müşteri sayfası tarayıcıda/PNG ile denetlendi.** M1.5e Onay: yapılandırılabilir kurallar (indirim/tutar eşiği, para birimi), çok adımlı (sıralı/paralel) onay, adlı onaylayan, vekâlet, kural sürümü tarihçede, hatırlatma. **Uygulandı, test edildi.** M1.5f Entegrasyon ve sonrası: fırsatın esas teklifi ve ayara bağlı tutar eşitleme, kabulde fırsat kazanma politikası, takip/hatırlatma aktiviteleri (teklife bağlı, sonuçlanınca otomatik kapanır), zamanlanmış süre dolumu, devir sözleşmesi (`IAcceptedQuoteSource`). **Uygulandı, test edildi.** M1.5g Raporlar: teklif raporu, CSV dışa aktarma, maliyet izniyle marj raporu; revizyon karşılaştırma. **Uygulandı, test edildi.** M1.5h Ekranlar: liste, detay/kalem editörü, onay merkezi, kurallar, vekâletler, karşılaştırma, rapor, müşteri sayfası. **Yapıldı; ana akışlar tarayıcıda elle denendi, klavye/ekran okuyucu denemesi sınırlı (bkz. ADR-0019 son dilim).**
  - **M1.5i Yapılmadı (bağımlılıkla):** sipariş/sözleşme oluşturma (M1.6; yalnızca devir sözleşmesi var), e-posta teslim/okundu ve bounce bildirimi (sağlayıcı entegrasyonu gerekir), rol/grup tabanlı onay ve otomatik yükseltme, marj eşiğine bağlı onay kuralı, belge şablonu özelleştirme (logo, çok dilli), kur sabitleme ekranı, kayıtlı görünümler, geçmiş dönem rapor anlık görüntüsü. QuestPDF Community lisansı (yıllık gelir < 1 milyon USD) ürün sahibinin sorumluluğundadır.
- M1.6 Dönüştürme köprüleri: Kabul/kazanım sonrası opsiyonel satış siparişi veya lojistik talep taslağı; ilişki ve miktar/bedel devri. Kabul: Otomatik kesin sevkiyat/fatura oluşmaz; aynı fırsattan birden çok teslim ilişkisi desteklenir.
- M1.7 Pilot ve rapor: Excel taşıma, fırsat dönüşümü, aktiviteler, kaynak performansı ve kullanıcı kabulü. Lojistik profili ayrı alan/görünüm katkısıdır. Kabul: Aynı CRM toptancı veya hizmet firmasında lojistik alanlarını zorunlu tutmadan çalışır.
M7 — Sözleşme yönetimi (EPIC 7) [YENİ / D2, ADR-0022]
Amaç: Şablon, sürüm, onay, imza, taahhüt ve yenileme/fesih takibiyle sektörden bağımsız sözleşme yönetimi; kabul edilen tekliften on iki aylık hizmet sözleşmesi örnek akışıdır. Ayrı modül (`Modulix.Modules.Contracts`, şema `contracts`); müşteri ve kişiler ortak kayıttır (ADR-0006), CRM kapalıyken de çalışır. Bağımlılık: Ç0, Y1–Y9, ADR-0016 lookup, ADR-0019 devir sözleşmesi (`IAcceptedQuoteSource`). Dilim planı ADR-0022'de.
  - **Durum (31 Ekim 2026) — Dilim 1 uygulandı, testle doğrulandı:** modül iskeleti, `Contracts/ContractParties/ContractLines/ContractEvents` tabloları (RLS), dört ayrı durum alanı (yalnızca iş akışı yazar), tarih/yenileme/bildirim kuralları ve sunucu hesaplı son bildirim tarihi, sunucu toplamları (`QuoteCalculator`), hassas finansal alanlar, lookup'lar, liste ve detay ekranı. Birim 10, entegrasyon 4 test (mutlu yol, doğrulama, tenant izolasyonu, yetki/kapsam).
  - **Durum (31 Ekim 2026) — Dilim 2 uygulandı, testle doğrulandı:** durum geçişi eylemleri (inceleme, yayıma hazır, onaya gönderme/geri çekme/onay/ret, iptal, revizyon; ön koşullar API'de), sürüm dondurma (`ContractVersions`, SHA-256), sürüm karşılaştırma, takvim ayı tabanlı dönem aritmetiği ve tenant gününe göre tarih değerlendirmesi, ayrı onay izni ve kendi onayını engelleme ayarı; detayda eylem çubuğu ve sürümler sekmesi. Birim 26, entegrasyon 8 test.
  - **Durum (31 Ekim 2026) — Dilim 3 uygulandı, testle doğrulandı:** kabul edilmiş tekliften sözleşme (kalem alt kümesi, mükerrer devir engeli, atomik, kaynak izi), sözleşme formu metadata'dan (`IModule.Forms`), firma kartı/fırsat sayfasında ilişkili sözleşmeler, aktiviteye bağlama. Yeni 9 entegrasyon testi.
  - **Durum (31 Ekim 2026) — Dilim 4 uygulandı, testle doğrulandı:** şablon (beyaz listeli değişkenler, sürüm yayımlama ve SHA-256 mühür, ayrı düzenleme/yayımlama izni), belge üretimi (yayımlanmış sürümden PDF, eksik veri engeli, taslak kopya işareti, finansal izin), şablon ve Belgeler ekranları. Birim 10, entegrasyon 4 yeni test; örnek metin hukuki inceleme gerektirir.\n"
  - **Durum (31 Ekim 2026) — Dilim 5 uygulandı, testle doğrulandı:** kural tabanlı iç onay (tutar, indirim, süre, standart dışı madde, risk), kuralların sürümle dondurulan planı, sıralı ve paralel adımlar, vekâlet, dört göz, ret ve değişiklik için geri gönderme, hedef süre ve gecikme işareti, onay iş listesi ile kural/vekâlet ekranları. Adım hesabı CRM ile ortak (`ApprovalFlow`). Birim 20, entegrasyon 10 yeni test. Gecikme e-postası ve eskalasyon dilim 8'deki zamanlanmış işle gelecek.
  - **Durum (31 Ekim 2026) — Dilim 6 uygulandı, testle doğrulandı:** imza (sıralı/paralel imzacılar, iç/dış, zorunluluk), manuel imza (tarama yükleme + doğrulayan yetkili kaydı) ve sağlayıcı yöntemi ayrı güvence düzeyleriyle, değiştirilebilir sağlayıcı arayüzü + simülasyon, HMAC imzalı idempotent ve sıra denetimli webhook, imza sonrası yürürlük, İmza sekmesi ve imza takibi ekranı. Birim 13, entegrasyon 7 yeni test. Gerçek sağlayıcı entegrasyonu, müşteriye barındırılan imza sayfası ve e-posta yoktur; süre dolumu işi dilim 8'de.
  - **Durum (1 Kasım 2026) — Dilim 7 uygulandı, testle doğrulandı:** taahhüt ve yükümlülükler (tek seferlik/dönemsel, her dönem ayrı gerçekleşme, ölçüm ve tolerans, kanıt dosyası, bağımlılık), ihlal, tolerans ve eskalasyon, ihlalden CRM takip aktivitesi (`IFollowUpActivities` bağlantı sözleşmesi), muafiyet/iptal, Taahhütler sekmesi, iş listesi ve firma kartı. Birim 26, entegrasyon 7 yeni test. Ceza/finansal sonuç uygulanmaz. Değerlendirmeyi kendiliğinden çalıştıran zamanlanmış iş dilim 8'de.
  - **Yapılmadı:** yenileme/fesih/ek protokol ve zamanlanmış iş (8), dashboard/görünümler/dışa aktarma (9), teslim raporu (10). Sipariş, müşteri başlangıcı, destek/SLA ve faturalama devri hedef modül olmadığı için yalnızca sözleşme/olay olarak planlıdır.
M2 — Lojistik [DETAYLANDIRMA / D2]
Amaç: Taşıma talebinden teslim ve maliyet mutabakatına kadar operasyon. Zorunlu: Ç1–Ç4, Ç8, Ç18, Ç21 minimum. Opsiyonel: CRM, depo, İK, muhasebe, web.
- M2.1 Taşıma talebi/siparişi: Yük, taraf rolleri, taşıma türü, çoklu yükleme/boşaltma, zaman penceresi ve belge. Kabul: CRM kapalıyken manuel/web/API talebinden operasyon açılır.
- M2.2 Sevkiyat ve sefer ayrımı: Müşteri talebi, sevkiyat, sefer, durak ve yük kalemi ayrı kavramlar; bölme/birleştirme/aktarma açık kurallı. Kabul: Bir sefer birden çok müşterinin sevkiyatını taşıyabilir; müşteri portalı diğer yükleri göstermez.
- M2.3 Kaynak ve uygunluk: Taşıyıcı, araç, römork, sürücü rolü, kapasite, belge geçerliliği ve atama çakışması. Temel sürücü kartı İK lisansı gerektirmez. Kabul: Geçersiz belge/kapasite aşımı politika uyarınca engel veya gerekçeli istisna üretir.
- M2.4 Planlama/durum: Talep→planlandı→yükleniyor→yolda→teslim/kısmi/iptal; gerçek zaman ve sapma nedeni. Kabul: Sıradan status update ile teslim doğrulamaları atlanamaz; geçişler auditlidir.
- M2.5 Teslim kanıtı: Alıcı, zaman, fotoğraf/imza, hasar/eksik ve başarısız teslim. Ç16 geldiğinde offline desteklenir. Kabul: Kanıtın tenant/kayıt yetkisi vardır; ikinci teslim komutu ikinci mali olay yaratmaz.
- M2.6 Navlun ve maliyet: Tarife ve sözleşme, para birimi, ek hizmet, taşıyıcı maliyeti, revizyon ve kârlılık. Kabul: Hesap izi açıklanır; planlanan/gerçekleşen maliyet ve faturalanan bedel ayrıdır.
- M2.7 Belgeler ve bildirim: CMR/sevk belgeleri, ihtiyaçta e-İrsaliye, gecikme ve teslim bildirimi. Kabul: Entegratör hatası teslimatı başarılı yasal bildirim gibi göstermez; alıcıya asgari veri gider.
- M2.8 Modül ilişkileri: Depoya varış→kabul önerisi; teslim→fatura taslağı; İK→sürücü yetkinlik okuması. Kabul: Hedef modül yoksa işlem sürer; açık ise tekilleştirilmiş ve izlenebilir köprü çalışır.
M3 — Depo ve antrepo [DETAYLANDIRMA / D3]
Amaç: Sektör bağımsız fiziksel stok operasyonu; gümrüklü antrepo seçilebilir uzantı. Zorunlu: Ortak ürün/birim, olay, dosya, numara, yetki, hesap ve stok domain kuralları. Opsiyonel: Lojistik, satış/satın alma, muhasebe.
- M3.1 Tesis ve adresleme: Depo, alan, raf/lokasyon, kapasite, kısıt ve işletici şirket; müşteri malı ile işletmenin kendi stoku ayrılır. Kabul: Aynı ürün farklı malik ve stok statüsüyle ayrı bakiyededir.
- M3.2 Kabul ve yerleştirme: Beklenen kabul, sayım, eksik/fazla/hasar, kalite karantinası, lot/seri/SKT ve adres önerisi. Kabul: Kabul tekrarı ikinci hareket üretmez; lojistik modülü olmadan manuel kabul çalışır.
- M3.3 Stok hareket defteri: Giriş, çıkış, transfer, rezervasyon, serbest bırakma ve düzeltme; bakiye defterden türetilir. Kabul: Eşzamanlı çıkış/rezervasyon eksi stok politikasını aşamaz; bakiyeyi doğrudan değiştiren endpoint bulunmaz.
- M3.4 Toplama ve çıkış: FIFO/FEFO politikası, toplama, paketleme, kısmi sevk, iade ve irsaliye bağı. Kabul: Karantina veya gümrük engelli ürün izinsiz sevk edilemez.
- M3.5 Sayım ve transfer: Kör sayım, fark onayı, hareket kilidi/versiyon kontrolü, depo içi ve depolar arası transfer. Kabul: Sayım sürerken olan hareket fark hesabında kaybolmaz; onaysız düzeltme yapılmaz.
- M3.6 Depo hizmet bedeli: Ardiye/elleçleme için giriş/çıkış tarihi, ücretsiz gün, birim, asgari ücret, dönem ve kademeler. Kabul: Müşteri sözleşmesine göre hesap yeniden üretilebilir; aynı dönem iki kez faturalanmaz.
- M3.7 Gümrük uyum uzantısı: Ülke/rejim/tesis izni, beyanname satırı, eşya statüsü ve belgeye bağlı statü geçişleri. Kabul: Statü sıradan alan düzenleme ile değiştirilemez; genel depo müşterisine gümrük alanları zorunlu değildir.
- M3.8 Gümrük stok/süre/teminat: Beyannameyle stok mutabakatı, süre uyarıları, teminat ilişkisi, resmi rapor taslakları ve düzeltme izi. Kabul: Pilotun gerçek rejim senaryoları uzmanla doğrulanır; “aynı depoda serbest/gümrüklü alan” hukuki doğrulama olmadan etkinleştirilmez.
- M3.9 Saha ve bağlantılar: Barkod/terminal, offline kısıtları, lojistik kabul/çıkış köprüsü ve muhasebe fatura önerisi. Kabul: Barkod tekrarı çift hareket yaratmaz; iş ortağının malları başka portal kullanıcısına görünmez.
M4 — İnsan Kaynakları [DETAYLANDIRMA / temel D3, bordro D4]
Amaç: Sektör bağımsız çalışan yaşam döngüsü. Zorunlu: Kişi, organizasyon, alan yetkisi, onay, dosya. Opsiyonel: Lojistik, muhasebe, web kariyer formu.
- M4.1 İstihdam ve özlük: Ortak kişi bağı, çalışan numarası, işveren şirket, sözleşme, giriş/çıkış, pozisyon ve tarihli görev. Aynı kişinin yeniden işe girişi ayrı istihdam ilişkisi olabilir. Kabul: Kullanıcı hesabı olmayan çalışan ve çalışan olmayan kullanıcı desteklenir.
- M4.2 Organizasyon: Departman, pozisyon, yönetici, maliyet merkezi ve tarihli değişim. Core organizasyon kimlikleri kullanılır; özlük detayları İK'dadır. Kabul: Yönetici değişikliği geçmiş onay merciini yeniden yazmaz.
- M4.3 İzin ve devamsızlık: Tür, hak ediş, bakiye, takvim, yarım gün, çakışma ve onay. Kabul: Aynı dönem iki istek bakiyeyi yarış koşuluyla aşamaz.
- M4.4 Puantaj/vardiya: Planlanan ve gerçekleşen süre, tatil, fazla çalışma ve düzeltme onayı. Kabul: Gece yarısını geçen vardiya ve dönem sınırı doğru hesaplanır; cihaz kaydı doğrudan onaylı puantaj sayılmaz.
- M4.5 Belge ve yetkinlik: Özlük belgesi, eğitim, sertifika ve geçerlilik; sürücünün ehliyet/SRC/psikoteknik özel yetkinlik türleridir. Kabul: Lojistik yalnızca uygunluk/izinli belge bilgisini okur; ücret ve sağlık ayrıntısı açılmaz.
- M4.6 Bordro (D4): Ülke/işveren/dönem, brüt-net, vergi/prim, ek ödeme/kesinti, revizyon ve hesap izi. Kabul: Uzman doğrulanmış örnekler geçer; dönem kapandıktan sonra rastgele yeniden hesaplama yapılmaz.
- M4.7 Yasal teslim ve muhasebe: SGK/vergi dosyası, teknik/yasal kabul, bordro muhasebe fişi ve mutabakat. Kabul: Ç11c hazır olmadan bordro “yasal uçtan uca tamam” sayılmaz; çift gönderim çift tahakkuk yaratmaz.
- M4.8 Self servis/aday: Kendi bordro/izin/belgesi; istenirse web başvurusundan aday yönetimi ve işe alım geçişi. Kabul: Çalışan diğer personelin verisini göremez; adaydan çalışana geçerken saklama ve alan aktarımı kontrollüdür.
M5 — Muhasebe ve finans [DETAYLANDIRMA / D4]
Amaç: Sektör bağımsız finans kayıtları; Türkiye yasal uyumu ayrı doğrulama kapılarıyla. Zorunlu: Şirket, ortak taraf/ürün, dönem, hesaplama, belge ve entegrasyon. Opsiyonel: Diğer tüm iş modülleri.
- M5.1 Cari hesap ve vade: Tarafın şirket bazlı cari uzantısı, hesap/para birimi, risk/vade ve açılış bakiyesi. Kabul: Ortak müşteri kartı farklı işletici şirketlerde ayrı cari hesaba bağlanabilir.
- M5.2 Satış/alış faturası: Satır, vergi, iskonto, döviz, iade, düzeltme, kesinleştirme ve e-belge bağı. Kabul: Dönem/seri/vergi kontrolü olmadan kesinleşmez; geçmiş belge ana karttan etkilenmez.
- M5.3 Tahsilat ve ödeme: Kasa/banka hesabı, dağıtım/mahsup, kısmi ödeme, avans ve dekont. Kabul: Bir ödeme birden çok faturaya dağıtılabilir; toplam eşleme tutarı ödeme tutarını aşamaz.
- M5.4 Banka ve çek/senet: Hareket içeri alma, tekilleştirme, eşleştirme önerisi, mutabakat; çek/senet durum ve vade takibi. Kabul: Aynı banka hareketi ikinci kez işlenmez; AI önerisi onaysız finansal kayıt yapmaz.
- M5.5 Genel muhasebe: Şirket hesap planı, çift taraflı fiş, borç/alacak denklik, kaynak belge, maliyet merkezi ve ters kayıt. Kabul: Denk olmayan fiş işlenmez; kesinleşmiş defter kaydı fiziksel silinmez.
- M5.6 Dönem/kur: Dönem kilidi, kapanış kontrolleri, kur farkı ve gerekli değerleme süreçleri. Kabul: Kapalı döneme API/import dahil yazılamaz; yeniden açma gerekçeli ve yetkilidir.
- M5.7 Vergi/e-belge: KDV/vergi raporları, e-Fatura/e-Arşiv; e-Defter ayrı format, doğrulama ve işletim kapsamıdır. Kabul: Güncel ülke paketinin teknik testleri ve uzman kabulü olmadan uygunluk beyanı verilmez.
- M5.8 Kaynak modül köprüleri: Sevkiyat, depo hizmeti, satış ve bordrodan taslak/tahakkuk önerisi; kaynak kalem–finans kalemi eşlemesi. Kabul: Kısmi faturalama ve iptal desteklenir; aynı kaynak kalemi izin verilen miktar/bedelden fazla faturalanmaz.
M6 — Satış ve sipariş [YENİ / ihtiyaca bağlı D3+]
Gerekçe: CRM fırsatı/teklifi ile bağlayıcı sipariş ve teslim taahhüdü aynı yaşam döngüsü değildir. Zorunlu: Ortak ürün/taraf, hesap, onay, süreç. Opsiyonel: CRM, depo, lojistik, web, muhasebe.
- M6.1 Sipariş: Tekliften veya bağımsız satır oluşturma, sözleşme/fiyat snapshot'ı, müşteri referansı ve revizyon. Kabul: CRM kapalıyken sipariş açılır; CRM teklif modelinin ikinci kopyası oluşturulmaz.
- M6.2 Karşılama: Hizmet/ürün ayrımı, rezervasyon isteği, kısmi teslim, kalan miktar ve iptal. Kabul: Depo yoksa harici/manuel karşılama izlenir; depo varsa stok defterine doğrudan yazılmaz.
- M6.3 Kanal: Web siparişi ve iç satış aynı domain kuralları; kanal kimliği ve kaynak fiyat doğrulaması. Kabul: Aynı web siparişi tek kayıttır; tarayıcıdan gelen indirim nihai kabul edilmez.
- M6.4 Finans ilişkisi: Teslim/faturalama planı, iade ve ödeme durum projeksiyonu. Kabul: Sipariş, teslim ve fatura tutarlarının farkı açıklanabilir.
M7 — Satın alma ve tedarik [YENİ / ihtiyaca bağlı D3+]
Gerekçe: Tedarikçi kartı bulunması satın alma iş sürecini tamamlamaz. Opsiyonel: Depo, lojistik ve muhasebe.
- M7.1 Talep ve onay: İhtiyaç, bütçe/maliyet merkezi, miktar ve onay zinciri. Kabul: Yetkisiz talep siparişe dönüşmez; görev ayrılığı uygulanır.
- M7.2 Teklif ve sipariş: Tedarikçi karşılaştırma, fiyat/vade, sipariş ve değişiklik geçmişi. Kabul: Tedarikçi işi ortak Taraf'a bağlıdır; geçmiş sipariş bedeli kart değişimiyle değişmez.
- M7.3 Kabul/fatura eşlemesi: Sipariş–kabul–alış faturası miktar/tutar toleransları, kısmi kabul/iade. Kabul: Eksik teslim fazla stok yaratmaz; tolerans aşımı onay gerektirir.
- M7.4 Performans: Termin, kalite, fiyat sapması ve açık bakiye. Kabul: Performans metriklerinin hangi olaydan üretildiği izlenir.
M8 — Müşteri destek ve hizmet talepleri [YENİ / ihtiyaca bağlı D2+]
Gerekçe: Web formu teknik altyapıdır; destek talebinin SLA ve çözüm yaşam döngüsü ayrı iş modülüdür. Opsiyonel: CRM, portal ve AI.
- M8.1 Talep kaydı: Web/portal/manuel giriş, taraf, kategori, öncelik, ek ve ekip. Kabul: Public başvuru özel iç notu belirleyemez; tekrar gönderim tek talep olur.
- M8.2 Süreç/SLA: İlk yanıt/çözüm hedefi, çalışma takvimi, bekleme ve eskalasyon. Kabul: SLA saati açık takvim ve bekleme kurallarıyla hesaplanır.
- M8.3 İletişim: İç not/dış yanıt, durum bildirimi ve portal geçmişi. Kabul: İç notlar e-posta veya portal yanıtına sızmaz.
- M8.4 Bilgi tabanı: Onaylı makale, görünürlük, sürüm ve AI kaynak izni. Kabul: Dahili makale anonim bot tarafından kullanılmaz; çözüm raporu kişisel veri sınırlarına uyar.
7. Modüllerin birlikte çalışması
7.1. Veri sahibi ve tüketici matrisi
Kavram	Yazma sahibi	Diğer modüllerin kullanımı
Taraf/kişi/adres	Ortak ana veri servisi	Yetkili komutla oluşturma/değiştirme; minimum projeksiyonla okuma
Giriş kimliği/üyelik	Kimlik ve tenant yönetimi	Çalışan/sürücü hesabına bağ; personel detayını kimlikte tutma
Ürün/hizmet/birim	Ortak katalog	Satış kalemi, stok kalemi ve fatura satırı referansı
Fırsat/teklif	CRM	Satış/lojistik opsiyonel dönüştürme; snapshot
Satış siparişi	Satış	Depoya rezervasyon/karşılama isteği; finans taslağı
Sevkiyat/sefer	Lojistik	Depo kabul/çıkış ve finans taslağı bağlantısı
Stok/rezervasyon/hareket	Depo	Satış/lojistikten yetkili komut; doğrudan bakiye güncelleme yok
İstihdam/ücret/bordro	İK	Lojistiğe uygunluk; muhasebeye izinli tahakkuk özeti
Fatura/defter/ödeme	Muhasebe	Kaynak modüllere izinli durum/tutar projeksiyonu
Web başvurusu	Web entegrasyon katmanı	Hedef modüle kontrollü dönüştürme; dönüşen kayıt hedef modülün mülkiyetinde
Destek talebi	Destek	CRM/portal görünümü; iç not yetkisi korunur


7.2. Örnek uçtan uca senaryolar
Profil	Akış	Ayrı tutulması gereken kural
Lojistik	Site taşıma talebi → CRM değerlendirme (opsiyonel) → sevkiyat → teslim → fatura taslağı	CRM kapalıyken talep→lojistik çalışır; teslim doğrudan kesin fatura anlamına gelmez.
Toptan dağıtım	Site ürün/teklif → CRM → satış siparişi → stok rezervasyonu → depo çıkış → teslim → fatura	Satış fiyatı, fiziksel stok ve finansal gelir farklı sahiplerdedir.
Profesyonel hizmet	Site talebi → fırsat/teklif → hizmet siparişi → hizmet kabulü → fatura	Depo, araç ve sevkiyat zorunlu değildir; hizmet kapsamı doğrulanır.
Depo hizmeti	Müşteri mal kabulü → müşteri bazlı stok → ardiye → hizmet bedeli → fatura taslağı	Müşteri malı işletmenin satılabilir kendi stoku sayılmaz.
İç İK	Kariyer formu → aday → istihdam → izin/puantaj → bordro → muhasebe tahakkuku	CRM müşteri kaydı otomatik açılmaz; aday/personel verisi ayrı erişimdedir.


7.3. Sözleşme örneği
ShipmentDelivered.v1 olayında tenant, sevkiyat kimliği, teslim zamanı, izinli kaynak kalem referansları ve correlation bulunabilir. Olay muhasebe faturasının kendisi değildir. Finans köprüsü olayı inbox ile tekilleştirir, ilgili şirket ve açık modülü doğrular, domain komutuyla taslak önerir. Ret veya eksik eşleme süreç ekranında kalır. Fatura kesinleşirse kaynak sisteme ayrı bir sonuç olayı gönderilir.

Snapshot ilkesi: Güncel Taraf adı her modülde ortak olabilir; geçmiş fatura/teklif üzerindeki yasal ünvan, adres, vergi numarası, kalem ve fiyat dondurulur. Tek kavramı bir kez tanımlamak geçmiş belge içeriğini canlı karta bağlamak anlamına gelmez.
8. Teslim dalgaları ve bağımlılık planı
Buradaki bağımlılıklar, bir epicin bütün ileri sürümlerinin tamamlanması anlamına gelmez. Minimum sözleşme/altyapı kilometre taşı bağımlılığıdır. Örneğin Ç20 hak kontrolü arayüzünü Ç0'da tanımlar; Ç12 plan motoru daha sonra bu arayüzü uygular. Ç6 onay altyapısı Ç21'in tam süreç koordinatörünü beklemez. Karşılıklı referans verilen özellikler bu şekilde döngüsüz alt görevlere ayrılmalıdır.
Dalga	Teslim amacı	Gerekli minimumlar	Çıkış kapısı
D0 — Temel doğrulama	Mevcut yapıyı doğrula ve geliştirme sözleşmesini sabitle	Ç0; Ç20 manifest/bağımlılık minimumu; Ç22 tenant/şirket kararı; Ç23 CI/izolasyon; mevcut Y doğrulaması	Tenant sınırı ve domain sahipliği testli; yanlış modül referansı engelli
D1 — CRM ve web talepleri pilotu	Müşteri verisi ve gelen taleplerle kullanılabilir ürün	Ç1; Ç2 minimum; Ç3a; Ç4; Ç5a; Ç7; Ç11 yalnız dar başvuru API kimliği; Ç12 minimum kota; Ç13; Ç14; Ç15a/dil anahtarları; Ç19.1–5/10; Ç24 minimum; M1 minimum	Web talebi→değerlendirme→fırsat izlenebilir; restore ve yetki testleri geçer
D2 — Lojistik ve ortak operasyon	Gerçek taşıma operasyonu, teklif/belge/onay	Ç2 tam; Ç3b/c minimum/d; Ç5b; Ç6a; Ç8; Ç9; Ç10a; Ç11a/b; Ç15b; Ç18a; Ç20/Ç21 minimum; Ç22 tam; M1 teklif, M2	CRM olmadan lojistik çalışır; teslim/entegratör hataları ve tekrar olaylar testli
D3 — Depo ve İK temel, ticari ölçek	Stok ve personel süreçleri; ikinci sektör doğrulaması	Ç3c tam; Ç5c ihtiyaca bağlı; Ç6b; Ç10b; Ç12 ticari; Ç16; Ç18b araçları; Ç21/Ç24; M3 ve M4 temel; Ç19.6–8 ihtiyaca bağlı	Toptancı/hizmet profilinde core fork'u yok; stok eşzamanlılık ve özlük yetki testleri geçer
D4 — Finans, uyum ve AI	Bordro/muhasebe/ülke paketleri ve kontrollü AI	Ç11c; Ç15c İngilizce; Ç17; Ç18b domain setleri; M4 bordro; M5; Ç19.8–9	Uzman doğrulanmış finans/bordro sonuçları, dönem kapama, yasal adaptör kabulü ve AI güvenlik testleri


M6/M7/M8 sırası: Satış/satın alma, toptan pilot seçilirse D3'e; destek, web destek talebi ürün kapsamına alınırsa D2'ye eklenir. Bu üç modülün tamamı lojistik pilot ön koşulu değildir.
8.1. Önceliklendirilmiş ilk iş listesi
1. Ç0 + Ç23: Kod üzerinden mevcut “tamamlandı” envanteri, RLS ve boru hattı bypass yolları; kanıtlanan/eksik ayrımı.
2. Ç22 minimum: Tenant–işletici şirket–müşteri Taraf ayrımını ve işlem kapsamını kesinleştir.
3. Ç20 minimum: Manifest, sert/yumuşak bağımlılık ve hak kontrolü arayüzü; rol/özellik/abonelik ayrımı.
4. Ç1: Outbox dispatcher + abone inbox + retry/dead-letter; iki worker ve çökme testi.
5. Ç3a kalanlar: Kullanıcı/kişi/üyelik bağı, benzerlik, tekilleştirme ve CRM migration mutabakatı.
6. Ç14 + Ç13: Dağıtım/secret/yedek/restore ve veri yaşam döngüsü; pilot erişim kapısı.
7. Ç4 + Ç7 + Ç5a: Dosya, import ve bildirimle CRM pilotunu kullanılabilir hale getir.
8. Ç19 minimum + Ç11 dar API: Bir gerçek sitede iframe veya PHP formu → başvuru kutusu → CRM; aynı talep tekrarı ve spam testleri.
9. M1 teklif + Ç3c/d + Ç8 + Ç18a: Hizmet/para, teklif revizyonu ve PDF; onay koşulları.
10. Ç21 minimum + M2: Lojistik operasyonu ve opsiyonel CRM köprüsü; mali/yasal uçlar hazır oldukça bağla.
Bu sıra takvim taahhüdü değildir. Tek geliştirici için bütün core'u bitirip ürün açmak yerine her dalgada çalışan bir dikey süreç çıkarılır. Efor tahmini, kaynak kod doğrulaması ve pilot kapsamı sonrası alt görevlere verilir.
9. Modül–core bağımlılık matrisi
Z: Üretim kapsamına alınan ilgili özellikten önce zorunlu minimum. O: İsteğe bağlı bağlantı. K: Seçilen fonksiyon/pakete bağlı. Boşluk o modül için doğrudan gereklilik olmadığı anlamındadır.
Core epic	CRM	Lojistik	Depo	İK	Muhasebe	Web kanalı
Ç0 mimari/komut	Z	Z	Z	Z	Z	Z
Ç1 olay	Z	Z	Z	Z	Z	Z
Ç2 işler	Z min	Z	Z	Z	Z	Z min
Ç3a taraf/kişi	Z	Z	Z	Z	Z	Z
Ç3b adres	K	Z	Z	K	Z	K
Ç3c ürün/birim	K teklif	Z min	Z		Z	K katalog
Ç3d para/kur	K teklif	Z	K bedel	K bordro	Z	K ticaret
Ç4 dosya	Z	Z	Z	Z	Z	Z
Ç5 bildirim	Z a	Z b	K c	Z	K	Z a
Ç6 onay/otomasyon	K teklif	K onay	K	Z izin	K	K
Ç7 import/export	Z	Z	Z	Z	Z	K
Ç8 belge/seri	K teklif	Z	Z	K	Z	K
Ç9 ekran/arama	Z min	Z	Z	Z	Z	K yönetim
Ç10 rapor	K	Z a	Z a	K	Z	K
Ç11 dış entegrasyon	O	K e-belge	K	K yasal	Z	Z min
Ç12 hak/kota	Z min	Z min	Z	Z	Z	Z min
Ç13 güvenlik/mahremiyet	Z	Z	Z	Z	Z	Z
Ç14 canlı/kurtarma	Z	Z	Z	Z	Z	Z
Ç15 UX/dil	Z	Z	Z	Z	Z	Z
Ç16 saha/offline	O	K saha	K terminal	O		O
Ç17 AI	O	O	O	O	O	O
Ç18 hesap	K teklif	Z a	Z a	K bordro	Z	K ticaret
Ç19 web	O	O	O	O	O	Z
Ç20 modül yaşam döngüsü	Z	Z	Z	Z	Z	Z
Ç21 süreç köprüleri	O	K köprü	K köprü	K köprü	K köprü	K
Ç22 şirket/organizasyon	Z min	Z	Z	Z	Z	Z min
Ç23 kalite kapıları	Z	Z	Z	Z	Z	Z
Ç24 performans/izolasyon	Z min	Z min	Z	Z min	Z	Z min


10. Her epic için “bitti” ölçütü
- İş kapsamı, sahibi, zorunlu/opsiyonel bağımlılık ve kapsam dışı davranış yazılmış.
- UI yanında API, import, worker ve entegrasyon yolları aynı yetki/domain kurallarını uyguluyor.
- Tenant/şirket kapsamı ve tablo sınıflandırması belli; yeni tenant tablosunun RLS ve negatif testleri var.
- Transaction, eşzamanlılık, eventId/idempotency ve başarısız işlem davranışı testli.
- Audit var; sır, gereksiz kişisel veri veya yetkisiz alan loglanmıyor.
- Migration ileri/geri uyumluluğu, mevcut veriye etkisi ve kurtarma planı doğrulanmış.
- Modül kapalı, kullanıcı pasif, kota dolu ve dış servis kesik senaryoları anlamlı hata üretiyor.
- Form/listeler erişilebilir, mobil hedefler kapsamına uygun; boş/yükleniyor/hata durumları tamam.
- İlgili metrik/alarm, README/runbook ve gerektiğinde ADR yazılmış; pilot kabul kanıtı eklenmiş.
- Sektör bağımsız epiclerde en az iki farklı sektör senaryosu ve tek modül kullanım senaryosu değerlendirilmiş.
11. Kaynak yol haritasının korunma ve genişletilme eşlemesi
Kaynak başlık	Revizyondaki karşılığı
K3–K10	2.2; düzeltilen anlamlar 1.1; yeni K11–K17
Mevcut durum	3; beyan ile doğrulama ayrımı
Y1–Y9	4; her yönetim yeteneği alt epic ve kabul ölçütüyle
Ç1–Ç4	5; inbox, kimlik ayrımı, merge, dosya karantinası ilaveleri
Ç5a–c	Ç5; kanal/tercih/gönderici ve görünürlük ilaveleri
Ç6a–b	Ç6; sürümlü onay ve güvenli otomasyon
Ç7–Ç10a/b	Ç7–Ç10; telafi, snapshot, güvenli read model ilaveleri
Ç11a/b/c	Ç11; servis kimliği, webhook güvenliği, sağlayıcı ve ülke paketleri
Ç12–Ç14	Ç12–Ç14; minimum kotalar/mahremiyet D1'e; bağımsız yedek ve restore
Ç15a/b/c	Ç15; dil altyapısı D1, İngilizce D4; white-label hakkı Ç12/domain bağı Ç19
Ç16–Ç18a/b	Ç16–Ç18; offline güvenliği, modül agent'leri, domain hesap sınırı
M1–M5	6; bütün kaynak işlevler alt epiclerle detaylandırıldı
Web sitesi talebi	Yeni Ç19; on alt epic, aşamalı teslim
Modül ilişkileri	Yeni Ç20/Ç21; veri sahipliği ve örnek akışlar 7
Şirket/şube ve kalite	Yeni Ç22–Ç24
Yeni iş alanı boşlukları	M6 satış, M7 satın alma, M8 destek; opsiyonel genişleme
D1–D4 ve bağımlılık matrisi	8–9; D0 eklendi, çelişen zamanlamalar düzeltildi


12. Karar bekleyen konular ve şimdilik kullanılan varsayımlar
Konu	Bu belgedeki varsayım	Kesinleştirme zamanı
Tenantta tek/çok şirket	Model şirket kimliğini destekler; pilot ekranı tek şirketle sade kalabilir	D0
Mevcut kodun gerçekten tamamlanan kısımları	Kaynak beyanı; test/inceleme yapılacak	D0
İlk sektör dışında doğrulama	Toptan dağıtım ve hizmet örnekleri domain kontrolü için kullanılır	D1–D3
SQL Server edition/OS/kapasite	Rakam varsayılmaz; gerçek kurulum ve resmi destek matrisi doğrulanacak	Canlı öncesi
Pilot özel entegratörü	Bir sağlayıcıyla başlayıp soyutlama arkasında ilerle	D2 kapsam kesimi
Depo gümrük rejimi	Türkiye pilotu varsayımı; tesis/rejim/raporlar uzmanla belirlenir	M3 tasarımı
İlk web sitesi teknolojisi	PHP veya iframe; WordPress bağlayıcısı talebe bağlı	Ç19 pilotu
İlk web işlemi	Teklif/iletişim başvurusu; sipariş ve ödeme daha sonra	D1
Katalog/sipariş sahibi	Alan bazlı sahiplik bağlayıcı başına belirlenecek	Ç19.6/7 öncesi
Portal dış kullanıcı politikası	Ayrı üyelik ve taraf kapsamı	Ç19.8 öncesi
Bordro ve muhasebe uyum kapsamı	Türkiye paketi; temel İK ile yasal bordro ayrı teslim	D3/D4 kesimi
Tema seçenekleri	Modülix varsayılanı + kontrollü açık/koyu; ek tema ihtiyaca bağlı	D2
SLA ve kurtarma hedefleri	Pilot için önerilen RPO/RTO doğrulanacak; sözleşme değildir	Canlı kabulü


13. Teknik dayanaklar
Bu belgedeki modül sınırları, teslim dalgaları ve kapsam kararları Modülix için mimari öneridir. Aşağıdaki birincil kaynaklar yalnızca belirtilen genel teknik ilkeleri destekler; kaynak kodun uygunluğunu veya Türkiye mevzuatına uyumu doğrulamaz.
1. AWS — Transactional outbox: Transaction ile olayın atomik kaydı ve tekrarlara dayanıklı tüketici yaklaşımı; Ç1.
2. OWASP — Multi-Tenant Security: Tenant sınırının API, worker, cache, dosya ve kurtarmada uygulanması; Ç13/Ç14/Ç24.
3. OWASP — Webhook Security: Webhook imzalama, tekrar ve hedef güvenliği; Ç11.
4. OWASP — SSRF Prevention: Kullanıcı tanımlı callback hedeflerinin ağ sınırı; Ç11.
5. Microsoft — Domain analysis: İş yeteneği ve domain sınırlarının belirlenmesi; Ç0/Ç20. Bu kaynak mikros
