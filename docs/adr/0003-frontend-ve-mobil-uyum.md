# ADR-0003: Frontend ve mobil uyum

- **Durum:** Kabul edildi, 16 Ekim 2026
- **Karar:** Blazor WebAssembly (.NET 10), tek responsive arayüz

## Problem

Form ve listeler layout JSON'undan render edilecek, aynı ekranlar telefonda da kullanılacak; bunu yalnızca C# bilen tek kişi, minimum bütçeyle sürdürecek.

## Seçenekler

| Seçenek | Artı | Eksi |
| --- | --- | --- |
| Blazor WebAssembly (seçildi) | Tek dil; DTO ve doğrulama API ile paylaşılır; sunucuda kullanıcı başına bellek tutmaz; kısa bağlantı kopmalarında ekran düşmez | İlk açılışta .NET çalışma zamanı indirilir |
| Blazor Server | Hızlı ilk açılış | Kullanıcı başına sürekli bağlantı ve sunucu belleği; mobil ağda kopunca ekran donar |
| Blazor Web App, Auto modu | İkisinin artıları | Her bileşen iki ortamda çalışmak zorunda; tek kişi için karmaşık |
| React + TypeScript | En küçük ilk yükleme | İkinci dil; Faz 1'i yaklaşık 3 hafta uzatır |

## Mobil uyum

- Mobile-first CSS; kırılımlar: telefon 360 px+, tablet 768 px+, masaüstü 1200 px+.
- Layout renderer dar ekranda formu tek sütuna, tabloyu kart listesine çevirir; kartta görünen alanları layout'taki `priority` belirler.
- Menü telefonda çekmeceye döner; dokunma hedefleri en az 44 px.
- Bileşen kütüphanesi Sprint 1'de seçilir (aday: MudBlazor). Ölçütler: mobil/dokunmatik destek, erişilebilirlik, lisans maliyeti olmaması.
- İlk yükleme için trimming, sıkıştırma ve tarayıcı önbelleği; boyut Sprint 1'de ölçülür.
- Desteklenen tarayıcılar: Chrome, Edge, Safari, Firefox güncel sürümleri; mobil Safari ve mobil Chrome dahil.

## Sonuçlar

- `Modulix.Shared` API sözleşmelerini ve doğrulama kurallarını taşır; sunucu doğrulaması yine zorunludur.
- `Modulix.Web` yalnızca HTTP API ile konuşur; Core'a referans veremez (mimari testle denetlenir).
- 360 px görünüm testi Playwright'ın .NET sürümüyle yazılır.
- İlk açılış boyutu kabul edilemez çıkarsa karar, arayüz kodu henüz azken yeniden açılır.
