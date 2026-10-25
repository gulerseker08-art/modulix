# CLAUDE.md

Modulix: modüler, multi-tenant kurumsal iş yönetim platformu. .NET 10 (ASP.NET Core minimal API + Blazor WebAssembly), EF Core, SQL Server 2025 (Row-Level Security). Kod, yorumlar, commit mesajları ve kullanıcıya dönük metinler **Türkçe**.

Ayrıntılı davranış ve uç nokta belgeleri `README.md`'de, mimari kararlar `docs/adr/` altında. Gerekmedikçe README'nin tamamını okuma; ilgili bölümü ara.

## Komutlar

```bash
dotnet build
dotnet test tests/Modulix.ArchitectureTests      # veritabanı gerekmez, hızlı
dotnet test tests/Modulix.Core.Tests             # birim
dotnet test tests/Modulix.Modules.Crm.Tests      # birim
dotnet test tests/Modulix.IntegrationTests       # SQL Server + Mailpit gerekir
dotnet run --project src/Modulix.Api             # API + arayüz; /health
dotnet run --project src/Modulix.Api -- migrate  # migration uygula (db-up.ps1 bunu çağırır)
```

- Yerel veritabanı: `docker compose up -d --wait sqlserver mailpit && docker compose run --rm db-init` (şifreler `.env`'de; Windows'ta `scripts/db-up.ps1`). Port 14333.
- Migration: `dotnet ef migrations add <Ad> --project src/Modulix.Infrastructure --startup-project src/Modulix.Api --output-dir Persistence/Migrations`. `dotnet ef database update` kullanma.
- CI (`.github/workflows/ci.yml`) Release modunda derler, migration uygular, mimari + Core + CRM + entegrasyon testlerini çalıştırır. CI'da yüksek/kritik açıklı NuGet paketi derlemeyi durdurur.
- Değişiklikten sonra en az `dotnet build` ve mimari testleri çalıştır; veritabanı yoksa entegrasyon testlerini çalıştıramadığını açıkça söyle.

## Mimari kurallar (testlerle denetlenir)

- Proje referansları `tests/Modulix.ArchitectureTests` (`ProjectReferenceRulesTests.AllowedReferences`) ile sabit. Modüller (`Modulix.Modules.*`, `Modulix.AI`) yalnızca `Modulix.Core.Contracts`'a başvurur; `Modulix.Web` yalnızca `Modulix.Shared`'a. Yeni proje eklemeden önce kural tablosunu güncelle.
- Her modül tek bir `IModule` uygulamasıyla kaydolur (`Program.cs` → `ModulixModules.Create()`); izin anahtarları modül anahtarıyla başlar (`crm.companies.read`).
- Tenant'a ait her entity `ITenantEntity` uygular; tablo şeması = modül anahtarı. Her yeni tenant tablosu için migration'da `TenantIsolationSql.AddTable` ekle (yoksa RLS testi kırılır).
- Kayıt işlemleri kayıt boru hattından geçer (izin, kapsam, hassas alan, özel alan, audit, outbox). Uç noktada boru hattını atlayıp doğrudan `DbContext`'e yazma.
- Tenant istemciden gelen başlık/parametreden değil, oturumdan belirlenir ve her istekte doğrulanır.
- Uç noktalar `.RequirePermission(...)` / `.RequireModule(...)` ile korunur. Yeni yönetim işlemleri denetim kaydına yazılmalı.
- E-posta `IEmailQueue.Enqueue` ile kuyruğa yazılır; HTML'e giren kullanıcı girdisi `WebUtility.HtmlEncode` ile kodlanır.
- Yeni sektör modülü için CRM'i örnek al (`src/Modulix.Modules.Crm`: `Domain`, `Persistence`, `Records`).

## Kod stili

- `.editorconfig`: file-scoped namespace, primary constructor, `var` yerine açık tip, Allman süslü parantez, 4 boşluk.
- Nullable açık; null uyarıları derleme hatasıdır.
- Çevredeki kodun yorum yoğunluğuna ve adlandırmasına uy. README'de belgelenen bir davranışı değiştirirsen README'yi de güncelle.
