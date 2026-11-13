using System.Globalization;
using System.Text;

namespace Modulix.Web.Services;

/// <summary>Yer kataloğundaki bir nokta: boylam, enlem ve ISO ülke kodu (TR: Türkiye).</summary>
public sealed record GeoPlace(string Name, double Lon, double Lat, string Country);

/// <summary>
/// Lojistik panosunun yer kataloğu: sevkiyattaki şehir metnini (serbest metin) harita noktasına çevirir. Kayıtta ülke ve koordinat alanı yoktur
/// (ADR-0038); katalog projeyle gelir (dış geocoding servisine adres gönderilmez, on-prem'de de çalışır). Eşleşme ad büyük/küçük harf, Türkçe
/// karakter ve parantez/virgül sonrası ek (ör. "Gebze, Kocaeli") yok sayılarak yapılır; bulunamayan yer haritada çizilmez, panoda sayılır.
/// Kayıt biçimi: <c>boylam,enlem,ülke,Ad/Takma ad/...</c>.
/// </summary>
public static class LogisticsGazetteer
{
    private static readonly string[] Entries =
    [
        // Türkiye: 81 il
        "35.32,37.00,TR,Adana", "38.28,37.76,TR,Adıyaman", "30.54,38.76,TR,Afyonkarahisar/Afyon", "43.05,39.72,TR,Ağrı", "34.03,38.37,TR,Aksaray",
        "35.83,40.65,TR,Amasya", "32.86,39.93,TR,Ankara", "30.71,36.90,TR,Antalya", "42.70,41.11,TR,Ardahan", "41.82,41.18,TR,Artvin",
        "27.84,37.85,TR,Aydın", "27.89,39.65,TR,Balıkesir", "32.34,41.64,TR,Bartın", "41.13,37.89,TR,Batman", "40.23,40.26,TR,Bayburt",
        "29.98,40.14,TR,Bilecik", "40.50,38.88,TR,Bingöl", "42.12,38.40,TR,Bitlis", "31.61,40.73,TR,Bolu", "30.29,37.72,TR,Burdur",
        "29.06,40.19,TR,Bursa", "26.41,40.15,TR,Çanakkale", "33.62,40.60,TR,Çankırı", "34.95,40.55,TR,Çorum", "29.09,37.78,TR,Denizli",
        "40.22,37.91,TR,Diyarbakır", "31.16,40.84,TR,Düzce", "26.56,41.68,TR,Edirne", "39.22,38.67,TR,Elazığ", "39.50,39.75,TR,Erzincan",
        "41.27,39.90,TR,Erzurum", "30.52,39.78,TR,Eskişehir", "37.38,37.07,TR,Gaziantep/Antep", "38.39,40.91,TR,Giresun", "39.48,40.46,TR,Gümüşhane",
        "43.74,37.58,TR,Hakkari", "36.16,36.20,TR,Hatay/Antakya", "44.04,39.92,TR,Iğdır", "30.55,37.76,TR,Isparta", "28.98,41.01,TR,İstanbul/Istanbul",
        "27.14,38.42,TR,İzmir/Izmir", "36.94,37.58,TR,Kahramanmaraş/Maraş", "32.63,41.20,TR,Karabük", "33.22,37.18,TR,Karaman", "43.09,40.60,TR,Kars",
        "33.78,41.38,TR,Kastamonu", "35.49,38.72,TR,Kayseri", "33.51,39.85,TR,Kırıkkale", "27.22,41.74,TR,Kırklareli", "34.17,39.15,TR,Kırşehir",
        "37.12,36.72,TR,Kilis", "29.94,40.77,TR,Kocaeli/İzmit/Izmit", "32.49,37.87,TR,Konya", "29.98,39.42,TR,Kütahya", "38.31,38.35,TR,Malatya",
        "27.43,38.61,TR,Manisa", "40.74,37.31,TR,Mardin", "34.64,36.81,TR,Mersin/İçel", "28.36,37.22,TR,Muğla", "41.49,38.74,TR,Muş",
        "34.71,38.62,TR,Nevşehir", "34.68,37.97,TR,Niğde", "37.88,40.98,TR,Ordu", "36.25,37.07,TR,Osmaniye", "40.52,41.02,TR,Rize",
        "30.40,40.76,TR,Sakarya/Adapazarı", "36.33,41.29,TR,Samsun", "41.94,37.93,TR,Siirt", "35.15,42.03,TR,Sinop", "37.02,39.75,TR,Sivas",
        "38.79,37.16,TR,Şanlıurfa/Urfa", "42.46,37.52,TR,Şırnak", "27.51,40.98,TR,Tekirdağ", "36.55,40.31,TR,Tokat", "39.72,41.00,TR,Trabzon",
        "39.55,39.11,TR,Tunceli", "29.41,38.68,TR,Uşak", "43.38,38.50,TR,Van", "29.27,40.65,TR,Yalova", "34.81,39.82,TR,Yozgat", "31.80,41.45,TR,Zonguldak",
        // Türkiye: sık kullanılan lojistik merkezleri, limanlar ve sınır kapıları
        "29.43,40.80,TR,Gebze", "28.00,41.29,TR,Çerkezköy", "29.15,40.43,TR,Gemlik", "26.97,38.80,TR,Aliağa", "28.69,40.97,TR,Ambarlı", "29.30,40.82,TR,Tuzla",
        "29.00,40.90,TR,Pendik", "26.32,41.72,TR,Kapıkule", "42.34,37.18,TR,Habur", "44.08,39.38,TR,Gürbulak", "36.60,36.22,TR,Cilvegözü", "41.55,41.51,TR,Sarp",
        "35.95,36.60,TR,İskenderun/Iskenderun", "27.10,38.50,TR,Bornova", "29.10,40.30,TR,Nilüfer",
        // Avrupa
        "23.32,42.70,BG,Sofya/Sofia", "26.10,44.43,RO,Bükreş/Bucharest", "28.63,44.18,RO,Köstence/Constanta", "27.91,43.21,BG,Varna", "23.73,37.98,GR,Atina/Athens",
        "22.94,40.64,GR,Selanik/Thessaloniki", "20.46,44.79,RS,Belgrad/Belgrade", "19.04,47.50,HU,Budapeşte/Budapest", "16.37,48.21,AT,Viyana/Vienna",
        "14.42,50.09,CZ,Prag/Prague", "21.01,52.23,PL,Varşova/Warsaw", "13.40,52.52,DE,Berlin", "11.58,48.14,DE,Münih/Munich", "9.99,53.55,DE,Hamburg",
        "6.96,50.94,DE,Köln/Cologne", "8.68,50.11,DE,Frankfurt", "4.48,51.92,NL,Rotterdam", "4.40,51.22,BE,Anvers/Antwerp", "2.35,48.86,FR,Paris",
        "4.84,45.76,FR,Lyon", "4.35,50.85,BE,Brüksel/Brussels", "4.90,52.37,NL,Amsterdam", "-0.13,51.51,GB,Londra/London", "-3.70,40.42,ES,Madrid",
        "2.17,41.39,ES,Barselona/Barcelona", "-0.38,39.47,ES,Valensiya/Valencia", "9.19,45.46,IT,Milano/Milan", "12.50,41.90,IT,Roma/Rome", "7.69,45.07,IT,Torino/Turin",
        "8.93,44.41,IT,Cenova/Genoa", "13.73,45.55,SI,Koper", "8.54,47.38,CH,Zürih/Zurich", "12.57,55.68,DK,Kopenhag/Copenhagen", "18.07,59.33,SE,Stokholm/Stockholm",
        "10.75,59.91,NO,Oslo", "24.94,60.17,FI,Helsinki", "37.62,55.76,RU,Moskova/Moscow", "30.52,50.45,UA,Kiev/Kyiv", "30.73,46.48,UA,Odessa",
        "18.41,43.86,BA,Saraybosna/Sarajevo", "21.43,42.00,MK,Üsküp/Skopje", "19.82,41.33,AL,Tiran/Tirana", "15.98,45.81,HR,Zagreb", "17.11,48.15,SK,Bratislava",
        "28.86,47.01,MD,Kişinev/Chisinau", "27.57,53.90,BY,Minsk", "-9.14,38.72,PT,Lizbon/Lisbon", "-6.26,53.35,IE,Dublin",
        // Kafkasya, Orta Doğu, Orta Asya, Afrika, diğer
        "44.80,41.72,GE,Tiflis/Tbilisi", "49.87,40.41,AZ,Bakü/Baku", "44.51,40.18,AM,Erivan/Yerevan", "51.39,35.69,IR,Tahran/Tehran", "44.37,33.31,IQ,Bağdat/Baghdad",
        "44.01,36.19,IQ,Erbil", "47.78,30.51,IQ,Basra/Basrah", "55.27,25.20,AE,Dubai", "39.17,21.49,SA,Cidde/Jeddah", "46.72,24.71,SA,Riyad/Riyadh",
        "47.98,29.38,KW,Kuveyt/Kuwait", "35.50,33.89,LB,Beyrut/Beirut", "36.29,33.51,SY,Şam/Damascus", "35.93,31.95,JO,Amman", "31.24,30.04,EG,Kahire/Cairo",
        "29.92,31.20,EG,İskenderiye/Alexandria", "69.24,41.30,UZ,Taşkent/Tashkent", "76.89,43.24,KZ,Almatı/Almaty", "-7.59,33.57,MA,Kazablanka/Casablanca",
        "3.06,36.75,DZ,Cezayir/Algiers", "10.18,36.81,TN,Tunus/Tunis", "3.38,6.52,NG,Lagos", "116.41,39.90,CN,Pekin/Beijing", "121.47,31.23,CN,Şanghay/Shanghai",
        "72.88,19.08,IN,Mumbai", "-74.01,40.71,US,New York", "-118.24,34.05,US,Los Angeles", "-95.37,29.76,US,Houston",
    ];

    private static readonly Dictionary<string, GeoPlace> Index = Build();

    /// <summary>Şehir metnini katalogda arar; yer bulunamazsa null.</summary>
    public static GeoPlace? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (Index.TryGetValue(Normalize(text), out var place))
        {
            return place;
        }

        // "Gebze, Kocaeli", "Gebze (Kocaeli)", "Gebze - Kocaeli": önce ilk parça, sonra sondaki parçalar denenir.
        var parts = text.Split([',', '(', ')', '/', '-', '–', '→'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (Index.TryGetValue(Normalize(part), out place))
            {
                return place;
            }
        }

        return null;
    }

    /// <summary>Küçük harfe çevirir ve Türkçe karakterleri ASCII'ye indirger (karşılaştırma anahtarı).</summary>
    internal static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Trim().Replace('İ', 'I').ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(c switch { 'ı' => 'i', 'ğ' => 'g', 'ş' => 's', 'ç' => 'c', 'ö' => 'o', 'ü' => 'u', _ => c });
        }

        return sb.ToString();
    }

    private static Dictionary<string, GeoPlace> Build()
    {
        var index = new Dictionary<string, GeoPlace>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            var f = entry.Split(',', 4);
            var names = f[3].Split('/');
            var place = new GeoPlace(names[0], double.Parse(f[0], CultureInfo.InvariantCulture), double.Parse(f[1], CultureInfo.InvariantCulture), f[2]);
            foreach (var name in names)
            {
                index[Normalize(name)] = place;
            }
        }

        return index;
    }
}
