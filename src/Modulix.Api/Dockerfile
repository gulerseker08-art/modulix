# Uygulamayı derle
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# API ve bağlı projelerin tamamını kopyala
COPY . .

# API yayın çıktısını oluştur
RUN dotnet publish "src/Modulix.Api/Modulix.Api.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# Blazor arayüzünü ayrıca yayınla
RUN dotnet publish "src/Modulix.Web/Modulix.Web.csproj" \
    -c Release \
    -o /app/web-publish

# İşlenmiş arayüz dosyalarını API yayın klasörüne aktar
RUN cp -a /app/web-publish/wwwroot/. /app/publish/wwwroot/

# Yayınlanan uygulamayı çalıştır
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "Modulix.Api.dll"]