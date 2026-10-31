using System.Text;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Tests;

/// <summary>ADR-0022 dilim 6: imza akışı (sıra, tamamlanma, ret), olay sırası/idempotency sınıflaması, dosya doğrulaması ve webhook imzası (saf).</summary>
public class ContractSignatureTests
{
    private static SignatoryInfo S(int position, bool required = true, string status = SignatoryStatus.Waiting) => new(new Guid(position, 0, 0, new byte[8]), position, required, status);

    private static Guid Id(int position) => new(position, 0, 0, new byte[8]);

    [Fact]
    public void Sequential_signing_lets_only_the_first_open_required_signatory_sign()
    {
        var list = new[] { S(1), S(2), S(3) };

        Assert.True(SignatureFlow.CanSign(SignatureMode.Sequential, list, Id(1)).Allowed);
        Assert.False(SignatureFlow.CanSign(SignatureMode.Sequential, list, Id(2)).Allowed);
        Assert.Equal([Id(1)], SignatureFlow.Active(SignatureMode.Sequential, list));

        var afterFirst = new[] { S(1, status: SignatoryStatus.Signed), S(2), S(3) };
        Assert.Equal([Id(2)], SignatureFlow.Active(SignatureMode.Sequential, afterFirst));
    }

    [Fact]
    public void Parallel_signing_activates_everyone_and_optional_signatories_never_block_the_order()
    {
        var list = new[] { S(1), S(2), S(3, required: false) };
        Assert.Equal(3, SignatureFlow.Active(SignatureMode.Parallel, list).Count);

        // Sıralı kipte isteğe bağlı imzacı sırayı bekletmez: ondan sonraki zorunlu imzacı önceki zorunlular tamamsa imzalayabilir.
        var optionalFirst = new[] { S(1, required: false), S(2) };
        Assert.True(SignatureFlow.CanSign(SignatureMode.Sequential, optionalFirst, Id(2)).Allowed);
    }

    [Fact]
    public void Request_is_pending_then_partially_signed_then_completed_when_all_required_have_signed()
    {
        Assert.Equal(SignatureRequestStatus.Pending, SignatureFlow.Evaluate([S(1), S(2)]));
        Assert.Equal(SignatureRequestStatus.PartiallySigned, SignatureFlow.Evaluate([S(1, status: SignatoryStatus.Signed), S(2)]));
        Assert.Equal(SignatureRequestStatus.Completed, SignatureFlow.Evaluate([S(1, status: SignatoryStatus.Signed), S(2, status: SignatoryStatus.Signed)]));
        Assert.Equal(SignatureRequestStatus.Completed, SignatureFlow.Evaluate([S(1, status: SignatoryStatus.Signed), S(2, required: false)]));
    }

    [Fact]
    public void A_required_decline_rejects_the_request_while_an_optional_decline_does_not()
    {
        Assert.Equal(SignatureRequestStatus.Rejected, SignatureFlow.Evaluate([S(1, status: SignatoryStatus.Signed), S(2, status: SignatoryStatus.Declined)]));
        Assert.Equal(SignatureRequestStatus.PartiallySigned, SignatureFlow.Evaluate([S(1, status: SignatoryStatus.Signed), S(2, required: false, status: SignatoryStatus.Declined), S(3)]));
        Assert.Equal(SignatureRequestStatus.Completed, SignatureFlow.Evaluate([S(1, status: SignatoryStatus.Signed), S(2, required: false, status: SignatoryStatus.Declined)]));
    }

    [Fact]
    public void A_signatory_cannot_sign_twice_or_after_declining()
    {
        var done = new[] { S(1, status: SignatoryStatus.Signed), S(2, status: SignatoryStatus.Declined) };

        Assert.Contains("zaten imzaladı", SignatureFlow.CanSign(SignatureMode.Parallel, done, Id(1)).Reason);
        Assert.False(SignatureFlow.CanSign(SignatureMode.Parallel, done, Id(2)).Allowed);
        Assert.False(SignatureFlow.CanSign(SignatureMode.Parallel, done, Guid.NewGuid()).Allowed);
    }

    [Fact]
    public void Provider_events_are_classified_as_duplicate_applied_or_out_of_order()
    {
        Assert.Equal(SignatureEventOutcome.Duplicate, SignatureFlow.ClassifyProviderEvent(eventIdSeen: true, lastSequence: 3, sequence: 4));
        Assert.Equal(SignatureEventOutcome.Applied, SignatureFlow.ClassifyProviderEvent(false, 3, 4));
        Assert.Equal(SignatureEventOutcome.Applied, SignatureFlow.ClassifyProviderEvent(false, 3, 9)); // boşluk (kayıp olay) kabul edilir
        Assert.Equal(SignatureEventOutcome.OutOfOrder, SignatureFlow.ClassifyProviderEvent(false, 3, 3));
        Assert.Equal(SignatureEventOutcome.OutOfOrder, SignatureFlow.ClassifyProviderEvent(false, 3, 2));
    }

    [Fact]
    public void Signed_file_must_be_a_real_pdf_png_or_jpeg_by_its_first_bytes_and_within_the_size_limit()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n...");
        Assert.Null(SignatureFileRules.Validate("imzali.pdf", pdf, out var pdfType));
        Assert.Equal("application/pdf", pdfType);

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 };
        Assert.Null(SignatureFileRules.Validate("tarama.png", png, out var pngType));
        Assert.Equal("image/png", pngType);

        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 };
        Assert.Null(SignatureFileRules.Validate("tarama.jpg", jpeg, out var jpegType));
        Assert.Equal("image/jpeg", jpegType);

        // Uzantı ya da bildirilen tür değil içerik belirler: PDF adlı çalıştırılabilir dosya reddedilir.
        Assert.NotNull(SignatureFileRules.Validate("sahte.pdf", Encoding.ASCII.GetBytes("MZ\u0090\u0000"), out _));
        Assert.NotNull(SignatureFileRules.Validate("html.pdf", Encoding.ASCII.GetBytes("<script>alert(1)</script>"), out _));
        Assert.NotNull(SignatureFileRules.Validate("bos.pdf", [], out _));
        Assert.NotNull(SignatureFileRules.Validate("", pdf, out _));
        Assert.Contains("10 MB", SignatureFileRules.Validate("buyuk.pdf", new byte[SignatureFileRules.MaxBytes + 1], out _));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\x\\imza.pdf", "imza.pdf")]
    [InlineData("a\"b<c>.pdf", "abc.pdf")]
    [InlineData("   ", "imzali-belge")]
    public void Safe_name_drops_paths_and_unsafe_characters(string raw, string expected)
    {
        Assert.Equal(expected, SignatureFileRules.SafeName(raw));
    }

    [Fact]
    public void Webhook_signature_accepts_the_exact_body_and_rejects_tampering_wrong_secrets_and_stale_timestamps()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var body = Encoding.UTF8.GetBytes("{\"eventId\":\"e1\",\"sequence\":1}");
        var signature = SignatureWebhook.Compute("secret", timestamp, body);

        Assert.True(SignatureWebhook.Verify("secret", timestamp, signature, body, now).Valid);
        Assert.False(SignatureWebhook.Verify("secret", timestamp, signature, Encoding.UTF8.GetBytes("{\"eventId\":\"e2\",\"sequence\":1}"), now).Valid);
        Assert.False(SignatureWebhook.Verify("baska", timestamp, signature, body, now).Valid);
        Assert.False(SignatureWebhook.Verify("secret", timestamp, signature, body, now.AddMinutes(6)).Valid);
        Assert.False(SignatureWebhook.Verify("secret", timestamp, signature, body, now.AddMinutes(-6)).Valid);
        Assert.True(SignatureWebhook.Verify("secret", timestamp, signature, body, now.AddMinutes(4)).Valid);
        Assert.False(SignatureWebhook.Verify("secret", null, signature, body, now).Valid);
        Assert.False(SignatureWebhook.Verify("secret", timestamp, null, body, now).Valid);
        Assert.False(SignatureWebhook.Verify("", timestamp, signature, body, now).Valid);
        Assert.False(SignatureWebhook.Verify("secret", "yarin", signature, body, now).Valid);
        // Zaman damgası imzaya dahildir: başka bir zaman damgasıyla aynı imza geçmez (tekrar saldırısında zamanı yenilemek imzayı bozar).
        Assert.False(SignatureWebhook.Verify("secret", (now.ToUnixTimeSeconds() + 1).ToString(), signature, body, now).Valid);
    }

    [Fact]
    public void Simulation_provider_never_talks_to_the_network_and_is_flagged_as_simulation()
    {
        var provider = new SimulationSignatureProvider();
        var request = new ProviderSignatureRequest("ref.1", "sozlesme.pdf", new string('a', 64), [new ProviderSignatory(1, "Ali", "ali@ornek.test", null, true)], DateTimeOffset.UtcNow.AddDays(1), true);

        Assert.True(provider.IsSimulation);
        Assert.Equal("ref.1", provider.CreateRequestAsync(request, CancellationToken.None).Result.ProviderReference);
        Assert.True(provider.CancelRequestAsync("ref.1", CancellationToken.None).IsCompletedSuccessfully);
    }
}
