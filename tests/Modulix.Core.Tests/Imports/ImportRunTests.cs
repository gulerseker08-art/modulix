using Modulix.Core.Imports;

namespace Modulix.Core.Tests.Imports;

public class ImportRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = new('a', ImportRun.FileHashLength);

    private static ImportRun NewRun(ImportFailurePolicy policy = ImportFailurePolicy.SkipInvalidRows, string fileName = "musteriler.xlsx") =>
        ImportRun.Create(Guid.NewGuid(), Guid.NewGuid(), "crm.lead", fileName, 2048, Hash, 1, ImportRunMode.Apply, policy, "corr-1", Now);

    private static ImportRun Running(int total, ImportFailurePolicy policy = ImportFailurePolicy.SkipInvalidRows)
    {
        var run = NewRun(policy);
        run.Start(total, Now);
        return run;
    }

    [Fact]
    public void A_new_run_is_pending_and_not_terminal()
    {
        var run = NewRun();

        Assert.Equal(ImportRunStatus.Pending, run.Status);
        Assert.False(run.IsTerminal);
        Assert.Equal(0, run.TotalRows);
        Assert.Null(run.CompletedAt);
    }

    [Fact]
    public void A_run_without_failed_rows_succeeds()
    {
        var run = Running(10);

        run.Complete(created: 6, updated: 3, skipped: 1, failed: 0, note: null, Now.AddMinutes(1));

        Assert.Equal(ImportRunStatus.Succeeded, run.Status);
        Assert.True(run.IsTerminal);
        Assert.Equal(Now.AddMinutes(1), run.CompletedAt);
    }

    [Fact]
    public void Accepted_and_failed_rows_together_make_a_partial_success()
    {
        var run = Running(10);

        run.Complete(created: 7, updated: 0, skipped: 0, failed: 3, note: null, Now);

        Assert.Equal(ImportRunStatus.PartiallySucceeded, run.Status);
        Assert.Equal(3, run.FailedCount);
    }

    [Fact]
    public void When_nothing_is_accepted_the_run_fails()
    {
        var run = Running(4);

        run.Complete(created: 0, updated: 0, skipped: 0, failed: 4, note: null, Now);

        Assert.Equal(ImportRunStatus.Failed, run.Status);
    }

    [Fact]
    public void All_or_nothing_cannot_accept_rows_when_any_row_failed()
    {
        var run = Running(10, ImportFailurePolicy.AllOrNothing);

        Assert.Throws<InvalidOperationException>(() => run.Complete(created: 9, updated: 0, skipped: 0, failed: 1, note: null, Now));

        // Hiçbir satır alınmadıysa aynı kural başarısız bir koşuyu doğru kaydeder.
        run.Complete(created: 0, updated: 0, skipped: 0, failed: 1, note: "Hatalı satır var; hiçbir kayıt alınmadı.", Now);
        Assert.Equal(ImportRunStatus.Failed, run.Status);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, 0, 0, -1)]
    [InlineData(6, 3, 1, 1)]
    public void Counts_must_be_non_negative_and_cannot_exceed_the_row_total(int created, int updated, int skipped, int failed)
    {
        var run = Running(10);

        Assert.Throws<ArgumentException>(() => run.Complete(created, updated, skipped, failed, note: null, Now));
    }

    [Fact]
    public void A_run_must_be_started_before_it_completes_and_cannot_complete_twice()
    {
        var run = NewRun();
        Assert.Throws<InvalidOperationException>(() => run.Complete(0, 0, 0, 0, note: null, Now));

        run.Start(1, Now);
        Assert.Throws<InvalidOperationException>(() => run.Start(1, Now));
        run.Complete(1, 0, 0, 0, note: null, Now);
        Assert.Throws<InvalidOperationException>(() => run.Complete(1, 0, 0, 0, note: null, Now));
        Assert.Throws<InvalidOperationException>(() => run.Cancel(Now));
        Assert.Throws<InvalidOperationException>(() => run.Fail("x", Now));
    }

    [Fact]
    public void A_file_level_failure_ends_the_run_with_a_note_and_zero_counts()
    {
        var run = NewRun();

        run.Fail("Şablon sürümü uyuşmuyor.", Now);

        Assert.Equal(ImportRunStatus.Failed, run.Status);
        Assert.Equal("Şablon sürümü uyuşmuyor.", run.Note);
        Assert.Equal(0, run.TotalRows);
    }

    [Fact]
    public void A_pending_or_running_run_can_be_cancelled()
    {
        var pending = NewRun();
        pending.Cancel(Now);
        Assert.Equal(ImportRunStatus.Cancelled, pending.Status);

        var running = Running(5);
        running.Cancel(Now);
        Assert.Equal(ImportRunStatus.Cancelled, running.Status);
    }

    [Fact]
    public void Long_file_names_and_notes_are_truncated_to_the_column_size()
    {
        var run = ImportRun.Create(
            Guid.NewGuid(), Guid.NewGuid(), "crm.lead", new string('x', 400), 1, Hash, 1,
            ImportRunMode.DryRun, ImportFailurePolicy.SkipInvalidRows, new string('c', 200), Now);
        run.Fail(new string('n', 900), Now);

        Assert.Equal(ImportRun.FileNameMaxLength, run.FileName.Length);
        Assert.Equal(ImportRun.CorrelationMaxLength, run.CorrelationId!.Length);
        Assert.Equal(ImportRun.NoteMaxLength, run.Note!.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public void The_file_hash_must_be_a_lowercase_sha256_hex_string(string hash)
    {
        Assert.Throws<ArgumentException>(() => ImportRun.Create(
            Guid.NewGuid(), Guid.NewGuid(), "crm.lead", "a.csv", 1, hash, 1,
            ImportRunMode.Apply, ImportFailurePolicy.SkipInvalidRows, null, Now));
    }

    [Fact]
    public void Issues_accept_only_known_codes_and_never_carry_a_cell_value()
    {
        var runId = Guid.NewGuid();

        var issue = ImportRowIssue.Create(Guid.NewGuid(), runId, 12, "email", ImportIssueCodes.FormatInvalid, ImportIssueSeverity.Error, "E-posta biçimi geçersiz.", Now);

        Assert.Equal(12, issue.RowNo);
        Assert.Equal("email", issue.ColumnKey);
        Assert.Equal(runId, issue.RunId);
        Assert.Throws<ArgumentException>(() =>
            ImportRowIssue.Create(Guid.NewGuid(), runId, 1, null, "kendi-kodum", ImportIssueSeverity.Error, "x", Now));
        Assert.Throws<ArgumentException>(() =>
            ImportRowIssue.Create(Guid.NewGuid(), runId, 1, null, ImportIssueCodes.SystemError, ImportIssueSeverity.Error, " ", Now));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ImportRowIssue.Create(Guid.NewGuid(), runId, -1, null, ImportIssueCodes.SystemError, ImportIssueSeverity.Error, "x", Now));
    }

    [Fact]
    public void Every_issue_code_is_known_and_unique()
    {
        Assert.Equal(ImportIssueCodes.All.Count, ImportIssueCodes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ImportIssueCodes.All, code => Assert.True(ImportIssueCodes.IsKnown(code)));
        Assert.False(ImportIssueCodes.IsKnown("Required-Missing"));
    }
}
