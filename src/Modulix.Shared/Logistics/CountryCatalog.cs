namespace Modulix.Shared.Logistics;

/// <summary>Ülke kataloğu satırı: ISO 3166-1 alfa-2 kodu ile Türkçe ve İngilizce ad.</summary>
public sealed record CountryInfo(string Code, string NameTr, string NameEn)
{
    /// <summary>Arayüz diline göre ad ("en" ise İngilizce, diğerlerinde Türkçe).</summary>
    public string Name(string? language) => string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? NameEn : NameTr;
}

/// <summary>
/// Ülke kataloğu (ADR-0043 K3): ISO 3166-1 alfa-2'deki 249 ülke ve bölge. Salt okunur platform kataloğudur; tenant ülke eklemez. Kod hiçbir yerde
/// ada bakmaz, yalnızca koda bakar. Sunucu doğrulaması aynı kod kümesini <c>Modulix.Core.Contracts.Records.IsoCountryCodes</c> ile yapar (proje
/// referans kuralı iki projenin birbirini görmesine izin vermez); iki listenin aynı olduğu entegrasyon testlerinde denetlenir.
/// </summary>
public static class CountryCatalog
{
    public const int CodeLength = 2;

    /// <summary>Yeni kayıtta ön seçili ülke (firma ayarı <c>logistics.default-country</c> sunucuda uygulanır; bu yalnızca formun ilk değeridir).</summary>
    public const string DefaultCode = "TR";

    private const string Data = """
        AD|Andorra|Andorra
        AE|Birleşik Arap Emirlikleri|United Arab Emirates
        AF|Afganistan|Afghanistan
        AG|Antigua ve Barbuda|Antigua and Barbuda
        AI|Anguilla|Anguilla
        AL|Arnavutluk|Albania
        AM|Ermenistan|Armenia
        AO|Angola|Angola
        AQ|Antarktika|Antarctica
        AR|Arjantin|Argentina
        AS|Amerikan Samoası|American Samoa
        AT|Avusturya|Austria
        AU|Avustralya|Australia
        AW|Aruba|Aruba
        AX|Åland Adaları|Åland Islands
        AZ|Azerbaycan|Azerbaijan
        BA|Bosna-Hersek|Bosnia and Herzegovina
        BB|Barbados|Barbados
        BD|Bangladeş|Bangladesh
        BE|Belçika|Belgium
        BF|Burkina Faso|Burkina Faso
        BG|Bulgaristan|Bulgaria
        BH|Bahreyn|Bahrain
        BI|Burundi|Burundi
        BJ|Benin|Benin
        BL|Saint Barthélemy|Saint Barthélemy
        BM|Bermuda|Bermuda
        BN|Brunei|Brunei
        BO|Bolivya|Bolivia
        BQ|Karayip Hollandası|Caribbean Netherlands
        BR|Brezilya|Brazil
        BS|Bahamalar|Bahamas
        BT|Bhutan|Bhutan
        BV|Bouvet Adası|Bouvet Island
        BW|Botsvana|Botswana
        BY|Belarus|Belarus
        BZ|Belize|Belize
        CA|Kanada|Canada
        CC|Cocos (Keeling) Adaları|Cocos (Keeling) Islands
        CD|Kongo Demokratik Cumhuriyeti|Democratic Republic of the Congo
        CF|Orta Afrika Cumhuriyeti|Central African Republic
        CG|Kongo Cumhuriyeti|Republic of the Congo
        CH|İsviçre|Switzerland
        CI|Fildişi Sahili|Côte d'Ivoire
        CK|Cook Adaları|Cook Islands
        CL|Şili|Chile
        CM|Kamerun|Cameroon
        CN|Çin|China
        CO|Kolombiya|Colombia
        CR|Kosta Rika|Costa Rica
        CU|Küba|Cuba
        CV|Yeşil Burun|Cabo Verde
        CW|Curaçao|Curaçao
        CX|Christmas Adası|Christmas Island
        CY|Kıbrıs|Cyprus
        CZ|Çekya|Czechia
        DE|Almanya|Germany
        DJ|Cibuti|Djibouti
        DK|Danimarka|Denmark
        DM|Dominika|Dominica
        DO|Dominik Cumhuriyeti|Dominican Republic
        DZ|Cezayir|Algeria
        EC|Ekvador|Ecuador
        EE|Estonya|Estonia
        EG|Mısır|Egypt
        EH|Batı Sahra|Western Sahara
        ER|Eritre|Eritrea
        ES|İspanya|Spain
        ET|Etiyopya|Ethiopia
        FI|Finlandiya|Finland
        FJ|Fiji|Fiji
        FK|Falkland Adaları|Falkland Islands
        FM|Mikronezya|Micronesia
        FO|Faroe Adaları|Faroe Islands
        FR|Fransa|France
        GA|Gabon|Gabon
        GB|Birleşik Krallık|United Kingdom
        GD|Grenada|Grenada
        GE|Gürcistan|Georgia
        GF|Fransız Guyanası|French Guiana
        GG|Guernsey|Guernsey
        GH|Gana|Ghana
        GI|Cebelitarık|Gibraltar
        GL|Grönland|Greenland
        GM|Gambiya|Gambia
        GN|Gine|Guinea
        GP|Guadeloupe|Guadeloupe
        GQ|Ekvator Ginesi|Equatorial Guinea
        GR|Yunanistan|Greece
        GS|Güney Georgia ve Güney Sandwich Adaları|South Georgia and the South Sandwich Islands
        GT|Guatemala|Guatemala
        GU|Guam|Guam
        GW|Gine-Bissau|Guinea-Bissau
        GY|Guyana|Guyana
        HK|Hong Kong|Hong Kong
        HM|Heard Adası ve McDonald Adaları|Heard Island and McDonald Islands
        HN|Honduras|Honduras
        HR|Hırvatistan|Croatia
        HT|Haiti|Haiti
        HU|Macaristan|Hungary
        ID|Endonezya|Indonesia
        IE|İrlanda|Ireland
        IL|İsrail|Israel
        IM|Man Adası|Isle of Man
        IN|Hindistan|India
        IO|Britanya Hint Okyanusu Toprakları|British Indian Ocean Territory
        IQ|Irak|Iraq
        IR|İran|Iran
        IS|İzlanda|Iceland
        IT|İtalya|Italy
        JE|Jersey|Jersey
        JM|Jamaika|Jamaica
        JO|Ürdün|Jordan
        JP|Japonya|Japan
        KE|Kenya|Kenya
        KG|Kırgızistan|Kyrgyzstan
        KH|Kamboçya|Cambodia
        KI|Kiribati|Kiribati
        KM|Komorlar|Comoros
        KN|Saint Kitts ve Nevis|Saint Kitts and Nevis
        KP|Kuzey Kore|North Korea
        KR|Güney Kore|South Korea
        KW|Kuveyt|Kuwait
        KY|Cayman Adaları|Cayman Islands
        KZ|Kazakistan|Kazakhstan
        LA|Laos|Laos
        LB|Lübnan|Lebanon
        LC|Saint Lucia|Saint Lucia
        LI|Liechtenstein|Liechtenstein
        LK|Sri Lanka|Sri Lanka
        LR|Liberya|Liberia
        LS|Lesotho|Lesotho
        LT|Litvanya|Lithuania
        LU|Lüksemburg|Luxembourg
        LV|Letonya|Latvia
        LY|Libya|Libya
        MA|Fas|Morocco
        MC|Monako|Monaco
        MD|Moldova|Moldova
        ME|Karadağ|Montenegro
        MF|Saint Martin|Saint Martin
        MG|Madagaskar|Madagascar
        MH|Marshall Adaları|Marshall Islands
        MK|Kuzey Makedonya|North Macedonia
        ML|Mali|Mali
        MM|Myanmar|Myanmar
        MN|Moğolistan|Mongolia
        MO|Makao|Macao
        MP|Kuzey Mariana Adaları|Northern Mariana Islands
        MQ|Martinik|Martinique
        MR|Moritanya|Mauritania
        MS|Montserrat|Montserrat
        MT|Malta|Malta
        MU|Mauritius|Mauritius
        MV|Maldivler|Maldives
        MW|Malavi|Malawi
        MX|Meksika|Mexico
        MY|Malezya|Malaysia
        MZ|Mozambik|Mozambique
        NA|Namibya|Namibia
        NC|Yeni Kaledonya|New Caledonia
        NE|Nijer|Niger
        NF|Norfolk Adası|Norfolk Island
        NG|Nijerya|Nigeria
        NI|Nikaragua|Nicaragua
        NL|Hollanda|Netherlands
        NO|Norveç|Norway
        NP|Nepal|Nepal
        NR|Nauru|Nauru
        NU|Niue|Niue
        NZ|Yeni Zelanda|New Zealand
        OM|Umman|Oman
        PA|Panama|Panama
        PE|Peru|Peru
        PF|Fransız Polinezyası|French Polynesia
        PG|Papua Yeni Gine|Papua New Guinea
        PH|Filipinler|Philippines
        PK|Pakistan|Pakistan
        PL|Polonya|Poland
        PM|Saint Pierre ve Miquelon|Saint Pierre and Miquelon
        PN|Pitcairn Adaları|Pitcairn Islands
        PR|Porto Riko|Puerto Rico
        PS|Filistin|Palestine
        PT|Portekiz|Portugal
        PW|Palau|Palau
        PY|Paraguay|Paraguay
        QA|Katar|Qatar
        RE|Réunion|Réunion
        RO|Romanya|Romania
        RS|Sırbistan|Serbia
        RU|Rusya|Russia
        RW|Ruanda|Rwanda
        SA|Suudi Arabistan|Saudi Arabia
        SB|Solomon Adaları|Solomon Islands
        SC|Seyşeller|Seychelles
        SD|Sudan|Sudan
        SE|İsveç|Sweden
        SG|Singapur|Singapore
        SH|Saint Helena|Saint Helena
        SI|Slovenya|Slovenia
        SJ|Svalbard ve Jan Mayen|Svalbard and Jan Mayen
        SK|Slovakya|Slovakia
        SL|Sierra Leone|Sierra Leone
        SM|San Marino|San Marino
        SN|Senegal|Senegal
        SO|Somali|Somalia
        SR|Surinam|Suriname
        SS|Güney Sudan|South Sudan
        ST|São Tomé ve Príncipe|São Tomé and Príncipe
        SV|El Salvador|El Salvador
        SX|Sint Maarten|Sint Maarten
        SY|Suriye|Syria
        SZ|Esvatini|Eswatini
        TC|Turks ve Caicos Adaları|Turks and Caicos Islands
        TD|Çad|Chad
        TF|Fransız Güney Toprakları|French Southern Territories
        TG|Togo|Togo
        TH|Tayland|Thailand
        TJ|Tacikistan|Tajikistan
        TK|Tokelau|Tokelau
        TL|Doğu Timor|Timor-Leste
        TM|Türkmenistan|Turkmenistan
        TN|Tunus|Tunisia
        TO|Tonga|Tonga
        TR|Türkiye|Türkiye
        TT|Trinidad ve Tobago|Trinidad and Tobago
        TV|Tuvalu|Tuvalu
        TW|Tayvan|Taiwan
        TZ|Tanzanya|Tanzania
        UA|Ukrayna|Ukraine
        UG|Uganda|Uganda
        UM|ABD Küçük Dış Adaları|U.S. Minor Outlying Islands
        US|Amerika Birleşik Devletleri|United States
        UY|Uruguay|Uruguay
        UZ|Özbekistan|Uzbekistan
        VA|Vatikan|Vatican City
        VC|Saint Vincent ve Grenadinler|Saint Vincent and the Grenadines
        VE|Venezuela|Venezuela
        VG|Britanya Virjin Adaları|British Virgin Islands
        VI|ABD Virjin Adaları|U.S. Virgin Islands
        VN|Vietnam|Vietnam
        VU|Vanuatu|Vanuatu
        WF|Wallis ve Futuna|Wallis and Futuna
        WS|Samoa|Samoa
        YE|Yemen|Yemen
        YT|Mayotte|Mayotte
        ZA|Güney Afrika|South Africa
        ZM|Zambiya|Zambia
        ZW|Zimbabve|Zimbabwe
        """;

    /// <summary>Bütün ülkeler, koda göre sıralı.</summary>
    public static IReadOnlyList<CountryInfo> All { get; } = Parse();

    private static readonly Dictionary<string, CountryInfo> ByCode = All.ToDictionary(c => c.Code, StringComparer.Ordinal);

    /// <summary>Kod geçerli bir ISO 3166-1 alfa-2 kodu mu (büyük harf).</summary>
    public static bool IsValid(string? code) => code is not null && ByCode.ContainsKey(code);

    /// <summary>Koda göre ülke; bilinmeyen kod için null.</summary>
    public static CountryInfo? Find(string? code) => code is not null && ByCode.TryGetValue(code, out var country) ? country : null;

    /// <summary>Kodun görünen adı; bilinmeyen kod olduğu gibi döner (eski ya da bozuk veriyi gizlemez).</summary>
    public static string NameOf(string? code, string? language = null) => Find(code)?.Name(language) ?? code ?? string.Empty;

    private static List<CountryInfo> Parse() =>
    [
        .. Data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|'))
            .Select(f => new CountryInfo(f[0], f[1], f[2])),
    ];
}
