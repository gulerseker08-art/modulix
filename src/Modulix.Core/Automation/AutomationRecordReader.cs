using Modulix.Core.Contracts.Records;

namespace Modulix.Core.Automation;

/// <summary>
/// Kural çalışmasının kaydı okuma kapısı (ADR-0027). Okuma, <b>geçerli kimliğin</b> yetkisiyle yapılır: elle çalıştırmada çağıranın,
/// olay yolunda kuralın kimliği (izin, veri kapsamı, hassas alan). Varsayılan uygulama kayıt boru hattıdır; ayrı bir arayüz olması yalnızca
/// testlerin, kayıt türü henüz bir modülde bulunmadan kural altyapısını sınayabilmesi içindir.
/// </summary>
public interface IAutomationRecordReader
{
    Task<RecordResult<RecordData>> ReadAsync(RecordRef record, CancellationToken cancellationToken);
}

public sealed class PipelineAutomationRecordReader(IRecordPipeline pipeline) : IAutomationRecordReader
{
    public Task<RecordResult<RecordData>> ReadAsync(RecordRef record, CancellationToken cancellationToken) =>
        pipeline.GetAsync(record, cancellationToken);
}
