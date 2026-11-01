using System.Text;
using Modulix.Modules.Support.Lifecycle;

namespace Modulix.Modules.Support.Tests;

/// <summary>ADR-0024 dilim 4: ek doğrulaması, gelen mesaj sınıflandırması ve anket kuralları (saf sınıflar).</summary>
public class ChannelRulesTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7 içerik");

    [Theory]
    [InlineData("fatura.pdf", "application/pdf")]
    [InlineData("FOTO.JPG", "image/jpeg")]
    [InlineData("rapor.txt", "text/plain")]
    public void Allowed_files_are_accepted_with_a_server_chosen_type(string name, string contentType)
    {
        var content = name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "düz metin"u8.ToArray()
            : name.EndsWith(".JPG", StringComparison.OrdinalIgnoreCase) ? [0xFF, 0xD8, 0xFF, 0xE0, 0x00]
            : Pdf;
        var (fileName, type, error) = AttachmentRules.Validate(name, content);
        Assert.Null(error);
        Assert.Equal((name, contentType), (fileName, type));
    }

    [Theory]
    [InlineData("program.exe")]
    [InlineData("sayfa.html")]
    [InlineData("resim.svg")]
    [InlineData("arsiv.zip")]
    [InlineData("uzantisiz")]
    [InlineData("fatura.exe.pdf")]
    [InlineData("betik.js.pdf")]
    public void Dangerous_or_unknown_names_are_rejected(string name) => Assert.NotNull(AttachmentRules.Validate(name, Pdf).Error);

    [Fact]
    public void The_signature_must_match_the_extension_and_text_files_cannot_be_binary()
    {
        Assert.NotNull(AttachmentRules.Validate("sahte.pdf", "MZ......"u8.ToArray()).Error);
        Assert.NotNull(AttachmentRules.Validate("sahte.png", Pdf).Error);
        Assert.NotNull(AttachmentRules.Validate("sahte.txt", [0x41, 0x00, 0x42]).Error);
        Assert.NotNull(AttachmentRules.Validate("bos.pdf", []).Error);
    }

    [Fact]
    public void The_size_limit_is_inclusive_and_names_are_made_safe()
    {
        var atLimit = new byte[AttachmentRules.MaxBytes];
        Encoding.ASCII.GetBytes("%PDF").CopyTo(atLimit, 0);
        Assert.Null(AttachmentRules.Validate("sinir.pdf", atLimit).Error);
        Assert.NotNull(AttachmentRules.Validate("asim.pdf", new byte[AttachmentRules.MaxBytes + 1]).Error);

        Assert.Equal("kayit.pdf", AttachmentRules.SafeName(@"C:\Users\x\kayit.pdf"));
        Assert.Equal("a_b.pdf", AttachmentRules.SafeName("../../a\"b.pdf"));
        Assert.Null(AttachmentRules.SafeName("   "));
        Assert.Null(AttachmentRules.SafeName("..."));
        Assert.True(AttachmentRules.SafeName(new string('x', 400) + ".pdf")!.Length <= 200);
        Assert.EndsWith(".pdf", AttachmentRules.SafeName(new string('x', 400) + ".pdf"));
    }

    private static InboundMessage Message(string? from = "kisi@firma.test", string? autoSubmitted = null, string? precedence = null, bool suppress = false, bool nullPath = false) =>
        new("Email", "<id@x>", null, null, from, null, "Konu", "Gövde", autoSubmitted, precedence, suppress, nullPath);

    [Theory]
    [InlineData("auto-replied")]
    [InlineData("auto-generated")]
    public void Auto_submitted_headers_mark_a_message_as_automatic(string value) => Assert.True(InboundRules.IsAutomatic(Message(autoSubmitted: value)));

    [Fact]
    public void Human_mail_is_not_automatic_but_loop_signals_are()
    {
        Assert.False(InboundRules.IsAutomatic(Message()));
        Assert.False(InboundRules.IsAutomatic(Message(autoSubmitted: "no")));
        Assert.False(InboundRules.IsAutomatic(Message(precedence: "normal")));
        Assert.True(InboundRules.IsAutomatic(Message(precedence: "Bulk")));
        Assert.True(InboundRules.IsAutomatic(Message(suppress: true)));
        Assert.True(InboundRules.IsAutomatic(Message(nullPath: true)));
        Assert.True(InboundRules.IsAutomatic(Message(from: "MAILER-DAEMON@mx.test")));
        Assert.True(InboundRules.IsAutomatic(Message(from: "no-reply@firma.test")));
        Assert.False(InboundRules.IsAutomatic(Message(from: "reply@firma.test")));
    }

    [Theory]
    [InlineData("Re: Faturam hatalı", "Faturam hatalı")]
    [InlineData("RE: Fwd: ynt: Konu", "Konu")]
    [InlineData("Re: [TK-20261101-AB12CD] Gecikme", "Gecikme")]
    [InlineData("  ", "")]
    public void Subjects_lose_reply_prefixes_and_ticket_tags(string subject, string expected) => Assert.Equal(expected, InboundRules.CleanSubject(subject));

    [Fact]
    public void Ticket_numbers_are_read_from_bracketed_tags_only()
    {
        Assert.Equal("TK-20261101-AB12CD", InboundRules.TicketNumberFromSubject("Re: [TK-20261101-AB12CD] Gecikme"));
        Assert.Null(InboundRules.TicketNumberFromSubject("TK-20261101-AB12CD numaralı talep"));
        Assert.Null(InboundRules.TicketNumberFromSubject("[TK-yarım"));
        Assert.Null(InboundRules.TicketNumberFromSubject(null));
    }

    [Fact]
    public void Thread_ids_put_in_reply_to_first_then_newest_references_without_repeats()
    {
        var message = Message() with { InReplyTo = " <c> ", References = ["<a>", "<b>", "<c>", " "] };
        Assert.Equal(["<c>", "<b>", "<a>"], InboundRules.ThreadIds(message));
        Assert.Empty(InboundRules.ThreadIds(Message()));
        var many = Message() with { References = [.. Enumerable.Range(0, 100).Select(i => $"<{i}>")] };
        Assert.Equal(InboundRules.MaxReferences, InboundRules.ThreadIds(many).Count);
    }

    [Fact]
    public void Ratings_are_one_to_five_and_low_scores_are_flagged()
    {
        Assert.All(new[] { 1, 2, 3, 4, 5 }, r => Assert.True(SurveyRules.IsValidRating(r)));
        Assert.All(new[] { -1, 0, 6, 100 }, r => Assert.False(SurveyRules.IsValidRating(r)));
        Assert.True(SurveyRules.IsLow(1));
        Assert.True(SurveyRules.IsLow(2));
        Assert.False(SurveyRules.IsLow(3));
    }

    [Fact]
    public void A_survey_can_be_answered_once_and_only_before_it_expires()
    {
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.True(SurveyRules.CanAnswer(null, now.AddMinutes(1), now).Open);
        Assert.False(SurveyRules.CanAnswer(null, now, now).Open); // süre dolduğu an kapanır
        Assert.False(SurveyRules.CanAnswer(null, now.AddDays(-1), now).Open);
        var answered = SurveyRules.CanAnswer(now.AddHours(-1), now.AddDays(5), now);
        Assert.False(answered.Open);
        Assert.Contains("yanıtlandı", answered.Reason);
    }
}
