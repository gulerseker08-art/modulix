namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>
/// Örnek şablon metinleri. Bunlar hukuki inceleme yapılmamış örneklerdir ve kesin geçerli sayılmaz: kurum kendi onaylı metnini kullanmalıdır.
/// Örnek şablon kaydedilirken "örnek metin" işaretlenir; üretilen her belgenin üstünde bu uyarı görünür.
/// </summary>
public static class ContractTemplateSamples
{
    public const string Notice = "Bu metin yalnızca örnektir; hukuki inceleme yapılmamıştır ve kesin geçerli sayılmaz. Yayımlamadan önce kurumunuzun onaylı metniyle değiştirin.";

    public const string ServiceName = "Örnek hizmet sözleşmesi (hukuki inceleme yapılmamıştır)";

    public const string ServiceBody = """
        # HİZMET SÖZLEŞMESİ

        Sözleşme no: {{sozlesme.numara}}
        Sözleşme türü: {{sozlesme.tur}}
        Düzenleme tarihi: {{tarih.bugun}}

        ## 1. Taraflar

        {{taraflar_listesi}}

        Müşteri: {{musteri.unvan}}, vergi dairesi {{musteri.vergi_dairesi}}, vergi no {{musteri.vergi_no}}, adres: {{musteri.adres}}

        ## 2. Konu

        {{sozlesme.baslik}}
        {{sozlesme.aciklama}}

        ## 3. Süre ve yenileme

        Sözleşme {{sozlesme.baslangic_tarihi}} tarihinde başlar ve {{sozlesme.bitis_tarihi}} tarihinde sona erer. Yenileme yöntemi: {{sozlesme.yenileme_yontemi}}. Yenilememe ya da fesih bildirimi, bitişten en az {{sozlesme.bildirim_suresi_gun}} gün önce, en geç {{sozlesme.son_bildirim_tarihi}} tarihine kadar yapılmalıdır.

        ## 4. Kapsam ve bedel

        {{kalem_tablosu}}

        Ödeme koşulları: {{sozlesme.odeme_kosullari}}

        ## 5. İmza

        {{imza_alani}}
        """;
}
