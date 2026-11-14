using System.Globalization;
using System.Text;
using Modulix.Modules.Hr.Records;
using Modulix.Shared.Hr;

namespace Modulix.Api.Hr;

/// <summary>
/// Anket sonucunun CSV dışa aktarımı (ADR-0042 bölüm 23b). İçerik sonuç ekranıyla aynı özettir: soru başına cevaplayan sayısı, puan ortalaması,
/// seçenek/puan dağılımı ve metin cevapları; yanıtlayan bilgisi ve ham yanıt satırı yoktur (kimliği gizli ankette kişiyi açığa çıkarmaz).
/// Excel'in Türkçe ayarına uygun: UTF-8 BOM, ";" ayraç, virgüllü ondalık. "=", "+", "-", "@" ile başlayan hücre formül olarak çalışmasın diye
/// kesme işaretiyle başlar (denetim kaydı dışa aktarımıyla aynı kural).
/// </summary>
internal static class SurveyResultsCsv
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static byte[] Render(SurveyResults results)
    {
        var csv = new StringBuilder();
        AppendRow(csv, ["Sıra", "Soru", "Tür", "Cevaplayan", "Ortalama", "Seçenek / puan", "Sayı", "Metin cevabı"]);
        var number = 0;
        foreach (var question in results.Questions)
        {
            number++;
            string?[] common =
            [
                number.ToString(Turkish),
                question.Text,
                HrAccess.QuestionTypes.FirstOrDefault(t => t.Key == question.QuestionType).Label ?? question.QuestionType,
                question.AnswerCount.ToString(Turkish),
                question.Average?.ToString("0.##", Turkish),
            ];

            if (question.Counts.Count == 0 && question.Texts.Count == 0)
            {
                AppendRow(csv, [.. common, null, null, null]);
            }

            foreach (var (option, count) in question.Counts)
            {
                AppendRow(csv, [.. common, option, count.ToString(Turkish), null]);
            }

            foreach (var text in question.Texts)
            {
                AppendRow(csv, [.. common, null, null, text]);
            }
        }

        return [.. new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    private static void AppendRow(StringBuilder csv, IEnumerable<string?> cells)
    {
        csv.Append(string.Join(';', cells.Select(Cell)));
        csv.Append("\r\n");
    }

    internal static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
