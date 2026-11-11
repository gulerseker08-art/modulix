using System.Text;
using Modulix.Core.Files;

namespace Modulix.Core.Tests.Files;

/// <summary>ADR-0040 K3: yükleme doğrulaması. Tür uzantı + dosya imzasıyla belirlenir; istemcinin bildirdiği tür kullanılmaz.</summary>
public sealed class FileRulesTests
{
    private const long Max = 10 * 1024 * 1024;

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7 gövde");

    [Theory]
    [InlineData("rapor.pdf", "application/pdf")]
    [InlineData("RAPOR.PDF", "application/pdf")]
    [InlineData("C:\\Users\\biri\\Belgeler\\rapor.pdf", "application/pdf")]
    [InlineData("../../etc/rapor.pdf", "application/pdf")]
    public void A_valid_pdf_is_accepted_and_the_name_is_made_safe(string fileName, string contentType)
    {
        var (name, type, error) = FileRules.Validate(fileName, Pdf, Pdf.Length, Max);

        Assert.Null(error);
        Assert.Equal(contentType, type);
        Assert.DoesNotContain('/', name!);
        Assert.DoesNotContain('\\', name!);
        Assert.DoesNotContain("..", name!);
    }

    [Theory]
    [InlineData("zararli.exe")]
    [InlineData("sayfa.html")]
    [InlineData("resim.svg")]
    [InlineData("betik.js")]
    [InlineData("uzantisiz")]
    [InlineData("fatura.exe.pdf")]
    [InlineData("belge.HTML.pdf")]
    public void Disallowed_extensions_and_dangerous_names_are_rejected(string fileName)
    {
        var (_, _, error) = FileRules.Validate(fileName, Pdf, Pdf.Length, Max);

        Assert.NotNull(error);
    }

    [Fact]
    public void The_extension_must_match_the_file_signature()
    {
        var text = Encoding.ASCII.GetBytes("bu bir pdf değil");

        Assert.NotNull(FileRules.Validate("sahte.pdf", text, text.Length, Max).Error);
        Assert.NotNull(FileRules.Validate("sahte.png", Pdf, Pdf.Length, Max).Error);
        Assert.Null(FileRules.Validate("gercek.txt", text, text.Length, Max).Error);
    }

    [Fact]
    public void Text_files_cannot_carry_binary_bytes()
    {
        byte[] binary = [0x41, 0x00, 0x42];

        Assert.NotNull(FileRules.Validate("ikili.csv", binary, binary.Length, Max).Error);
    }

    [Fact]
    public void Empty_oversized_and_unnamed_files_are_rejected()
    {
        Assert.NotNull(FileRules.Validate("bos.pdf", [], 0, Max).Error);
        Assert.NotNull(FileRules.Validate("buyuk.pdf", Pdf, Max + 1, Max).Error);
        Assert.Null(FileRules.Validate("sinirda.pdf", Pdf, Max, Max).Error);
        Assert.NotNull(FileRules.Validate(null, Pdf, Pdf.Length, Max).Error);
        Assert.NotNull(FileRules.Validate("   ", Pdf, Pdf.Length, Max).Error);
        Assert.NotNull(FileRules.Validate("...", Pdf, Pdf.Length, Max).Error);
    }

    [Fact]
    public void A_long_name_is_shortened_but_keeps_its_extension()
    {
        var name = FileRules.SafeName(new string('a', 500) + ".pdf");

        Assert.Equal(StoredFile.FileNameMaxLength, name!.Length);
        Assert.EndsWith(".pdf", name);
    }

    [Fact]
    public void Storage_keys_are_random_64_hex_characters()
    {
        var keys = Enumerable.Range(0, 50).Select(_ => StoredFile.NewStorageKey()).ToList();

        Assert.All(keys, key => Assert.Matches("^[0-9a-f]{64}$", key));
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Only_scanned_clean_or_not_yet_scanned_internal_files_are_downloadable()
    {
        Assert.True(FileScanStatus.IsDownloadable(FileScanStatus.NotScanned));
        Assert.True(FileScanStatus.IsDownloadable(FileScanStatus.Clean));
        Assert.False(FileScanStatus.IsDownloadable(FileScanStatus.Pending));
        Assert.False(FileScanStatus.IsDownloadable(FileScanStatus.Infected));
        Assert.True(FileClassification.IsDownloadable(FileClassification.Internal));
        Assert.False(FileClassification.IsDownloadable("Public"));
    }
}
