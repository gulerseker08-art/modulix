# Uygulamayı derle
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# API ve bağlı projelerin tamamını kopyala
COPY . .

# API projesini bul ve yayın çıktısını oluştur
RUN dotnet publish "$(find . -name Modulix.Api.csproj -print -quit)" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# Yayınlanan uygulamayı çalıştır
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "Modulix.Api.dll"]