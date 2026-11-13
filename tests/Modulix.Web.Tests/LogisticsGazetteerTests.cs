using Modulix.Web.Services;

namespace Modulix.Web.Tests;

/// <summary>Lojistik panosunun yer kataloğu: şehir metni (serbest metin) harita noktasına çevrilir; bulunamayan yer null döner.</summary>
public class LogisticsGazetteerTests
{
    [Theory]
    [InlineData("Ankara", "Ankara", "TR")]
    [InlineData("istanbul", "İstanbul", "TR")]
    [InlineData("İZMİR", "İzmir", "TR")]
    [InlineData("Izmir", "İzmir", "TR")]
    [InlineData("Gebze, Kocaeli", "Gebze", "TR")]
    [InlineData("Çerkezköy (Tekirdağ)", "Çerkezköy", "TR")]
    [InlineData("Hamburg", "Hamburg", "DE")]
    [InlineData("Munich", "Münih", "DE")]
    [InlineData("Köstence", "Köstence", "RO")]
    public void Known_places_resolve_ignoring_case_diacritics_and_suffixes(string text, string name, string country)
    {
        var place = LogisticsGazetteer.Find(text);
        Assert.NotNull(place);
        Assert.Equal((name, country), (place.Name, place.Country));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bilinmeyen Köy")]
    public void Unknown_or_blank_text_is_not_found(string? text) => Assert.Null(LogisticsGazetteer.Find(text));

    [Fact]
    public void Every_place_has_valid_coordinates_and_all_81_provinces_are_present()
    {
        var provinces = new[]
        {
            "Adana", "Adıyaman", "Afyonkarahisar", "Ağrı", "Aksaray", "Amasya", "Ankara", "Antalya", "Ardahan", "Artvin", "Aydın", "Balıkesir", "Bartın", "Batman",
            "Bayburt", "Bilecik", "Bingöl", "Bitlis", "Bolu", "Burdur", "Bursa", "Çanakkale", "Çankırı", "Çorum", "Denizli", "Diyarbakır", "Düzce", "Edirne", "Elazığ",
            "Erzincan", "Erzurum", "Eskişehir", "Gaziantep", "Giresun", "Gümüşhane", "Hakkari", "Hatay", "Iğdır", "Isparta", "İstanbul", "İzmir", "Kahramanmaraş",
            "Karabük", "Karaman", "Kars", "Kastamonu", "Kayseri", "Kırıkkale", "Kırklareli", "Kırşehir", "Kilis", "Kocaeli", "Konya", "Kütahya", "Malatya", "Manisa",
            "Mardin", "Mersin", "Muğla", "Muş", "Nevşehir", "Niğde", "Ordu", "Osmaniye", "Rize", "Sakarya", "Samsun", "Siirt", "Sinop", "Sivas", "Şanlıurfa", "Şırnak",
            "Tekirdağ", "Tokat", "Trabzon", "Tunceli", "Uşak", "Van", "Yalova", "Yozgat", "Zonguldak",
        };
        Assert.Equal(81, provinces.Length);
        foreach (var province in provinces)
        {
            var place = LogisticsGazetteer.Find(province);
            Assert.NotNull(place);
            Assert.True(place.Country == "TR" && place.Lon is > 25 and < 45 && place.Lat is > 35 and < 43, $"{province}: {place.Lon},{place.Lat}");
        }
    }
}
