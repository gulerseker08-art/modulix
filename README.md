# Modulix

[![CI](https://github.com/gulerseker08-art/modulix/actions/workflows/ci.yml/badge.svg)](https://github.com/gulerseker08-art/modulix/actions/workflows/ci.yml)

AI destekli, modüler, multi-tenant kurumsal iş yönetim platformu. Faz 1: Core + CRM + Lojistik, tüm ekranlar mobil uyumlu.

Mimari kararlar `docs/adr` klasöründedir.

## Gereksinimler

| Araç | Neden | Kontrol komutu |
| --- | --- | --- |
| .NET 10 SDK | Derleme ve testler | `dotnet --version` (10. ile başlamalı) |
| Git | Sürüm kontrolü | `git --version` |
| Docker Desktop | Yerel SQL Server | `docker --version` |

## İlk kurulum (yalnızca bir kez)

Bu klasörün içeriğini boş repo klasörünüze kopyalayın, sonra repo kökünde çalıştırın:

**Windows**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\init-solution.ps1
```

**macOS / Linux**

```bash
bash scripts/init-solution.sh
```

Betik solution'ı, 9 kaynak ve 6 test projesini oluşturur, referansları kurar, derler ve mimari testleri çalıştırır. İnternet bağlantısı gerekir (NuGet paketleri iner).

Beklenen son durum: derleme hatasız biter ve `Modulix.ArchitectureTests` içindeki tüm testler başarılı olur.

## Veritabanı (Görev 2)

SQL Server 2025 Developer, Docker içinde çalışır; bilgisayarınıza ayrıca SQL Server kurmanız gerekmez.

| Ayar | Değer |
| --- | --- |
| Sunucu | `localhost,14333` (1433 değil; yerel SQL Server'ınızla çakışmasın diye) |
| Veritabanı | `Modulix` |
| Şemalar | `platform`, `core`, `meta`, `crm`, `logistics`, `ai` |
| `modulix_app` | Uygulamanın kullanıcısı. Veri okur/yazar; tablo veya güvenlik politikası değiştiremez. |
| `modulix_migrator` | Yalnızca migration komutunun kullanıcısı (db_owner). |
| `sa` | Yönetici; SSMS ile bakmak için. |

Şifreler repo kökündeki `.env` dosyasındadır. Bu dosya ilk çalıştırmada rastgele şifrelerle oluşturulur ve git'e gönderilmez. Uygulama bağlantı cümlelerini `dotnet user-secrets` üzerinden okur; kod deposunda şifre bulunmaz.

**SSMS ile bağlanmak:** Sunucu `localhost,14333`, SQL Server Authentication, kullanıcı `sa`, şifre `.env` içindeki `MSSQL_SA_PASSWORD`. Bağlantı penceresinde "Trust server certificate" seçin.

**Yeni migration eklemek:**

```powershell
dotnet ef migrations add <Ad> --project src/Modulix.Infrastructure --startup-project src/Modulix.Api --output-dir Persistence/Migrations
powershell -ExecutionPolicy Bypass -File scripts\db-up.ps1
```

Migration'ları `dotnet ef database update` ile değil, `db-up.ps1` (içinde `dotnet run --project src/Modulix.Api -- migrate`) ile uygulayın; böylece yalnızca migration kullanıcısı şema değiştirir.

## Sürekli entegrasyon (Görev 3)

`main` dalına her push'ta ve her pull request'te GitHub Actions (`.github/workflows/ci.yml`):

1. SQL Server'ı yereldekiyle aynı `compose.yaml` ve `init.sql` ile açar; şifreler her çalışmada yeniden üretilir.
2. Release modunda derler. Yüksek veya kritik güvenlik açığı olan bir paket varsa derleme durur.
3. Migration'ları uygular, mimari ve entegrasyon testlerini çalıştırır.

Sonuç GitHub'da reponun **Actions** sekmesinde ve README'deki rozette görünür. Bir çalışma yaklaşık 5–8 dakika sürer; özel repolarda bu süre aylık ücretsiz dakika kotasından düşer.

## Günlük komutlar

```powershell
# Bilgisayar açıldıktan veya git pull sonrası: veritabanını başlat ve güncelle
powershell -ExecutionPolicy Bypass -File scripts\db-up.ps1

dotnet build
dotnet test tests/Modulix.ArchitectureTests
dotnet test tests/Modulix.IntegrationTests

# API'yi çalıştır; sağlık kontrolü: /health ("Healthy" dönmeli)
dotnet run --project src/Modulix.Api
```

## Proje yapısı ve bağımlılık kuralları

| Proje | Görev | Referans verebileceği projeler |
| --- | --- | --- |
| `Modulix.Core.Contracts` | Modüllerin görebildiği tek Core projesi | — |
| `Modulix.Core` | Tenant, yetki, entitlement, kayıt boru hattı, audit, outbox, metadata | Core.Contracts |
| `Modulix.Infrastructure` | EF Core, SQL Server, RLS, dosya, e-posta | Core, Core.Contracts |
| `Modulix.Modules.Crm` | Firma, kişi, fırsat, aktivite | Core.Contracts |
| `Modulix.Modules.Logistics` | Sevkiyat, taşıyıcı, maliyet | Core.Contracts |
| `Modulix.AI` | LLM katmanı, tool sözleşmesi | Core.Contracts |
| `Modulix.Shared` | API DTO'ları ve doğrulama kuralları | — |
| `Modulix.Api` | Host, endpoint'ler | Hepsi |
| `Modulix.Web` | Blazor WebAssembly, responsive arayüz | Shared |

Bu kurallar `tests/Modulix.ArchitectureTests` ile her test çalışmasında denetlenir. Örneğin CRM projesine Lojistik referansı eklenirse test kırılır. Yeni bir proje eklemeden önce kural tablosunu (`ProjectReferenceRulesTests.AllowedReferences`) güncelleyin.

## Sorun giderme

- **"A compatible .NET SDK was not found"**: `global.json` .NET 10 SDK istiyor. `dotnet --list-sdks` ile kurulu sürümleri kontrol edin.
- **PowerShell betik çalıştırmayı engelliyor**: Komutu yukarıdaki gibi `-ExecutionPolicy Bypass` ile çalıştırın.
- **Docker "Engine" çalışmıyor**: Docker Desktop'ı açın, "Engine running" yazısını bekleyin.
- **`.env` silindi, SQL Server artık açılmıyor**: Veritabanı eski şifreyle kalır. Yerel veriyi silip baştan kurmak için `docker compose down -v`, ardından `scripts\db-up.ps1`.
- **Port 14333 dolu**: `.env` içindeki `MSSQL_PORT` değerini değiştirip `scripts\db-up.ps1` çalıştırın.
- **"Solution zaten var"**: Betik yalnızca boş repoda çalışır. Yeniden denemek için oluşan `src`, `tests`, `Modulix.sln*`, `.gitignore`, `.editorconfig` dosyalarını silin.
