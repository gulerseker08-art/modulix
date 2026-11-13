using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Records;

/// <summary>
/// Anketin okura görünme kuralı (ADR-0042 dilim 7): yanıta açık, kapanış günü geçmemiş ve hedef departmanı boş ya da okurun departmanı. "Anket
/// yönetimi" izni olan bütün anketleri görür; yanıt verebilmek için ise herkes bu kurala uymalıdır.
/// </summary>
internal static class SurveyAudience
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    public static IQueryable<Survey> Available(IQueryable<Survey> source, string? department)
    {
        var today = Today;
        return source.Where(s => s.Status == Survey.Statuses.Open && (s.ClosesOn == null || s.ClosesOn >= today)
            && (s.AudienceDepartmentKey == null || s.AudienceDepartmentKey == department));
    }

    /// <summary>Kullanıcının etkin çalışan kartındaki departman (kartı yoksa boş; o zaman yalnızca herkese açık anketler).</summary>
    public static Task<string?> DepartmentAsync(HrStoreContext context, CancellationToken cancellationToken)
    {
        var me = context.CurrentUserId ?? Guid.Empty;
        return context.Data.Set<Employee>().AsNoTracking()
            .Where(e => e.UserId == me && e.Status != Employee.Statuses.Terminated).Select(e => e.DepartmentKey).FirstOrDefaultAsync(cancellationToken);
    }
}

/// <summary>
/// Anketler (ADR-0042 dilim 7). Durum yalnızca <see cref="SurveyService"/> komutlarıyla değişir (yanıta aç, kapat); başlık, açıklama, hedef ve kapanış
/// günü her durumda düzenlenir, "kimliği gizli" yalnızca oluştururken seçilir. Yanıtı olan anket silinmez (kapatılır); taslak anket sorularıyla silinir.
/// </summary>
internal sealed class SurveyRecordStore(HrStoreContext context) : HrRecordStore<Survey>(context), IRecordLabelProvider
{
    private readonly Dictionary<Guid, int> responseCounts = [];
    private readonly Dictionary<Guid, int> questionCounts = [];
    private readonly HashSet<Guid> respondedByMe = [];

    public override string EntityKey => HrModule.Entities.Survey;

    protected override string ReadPermission => HrModule.Permissions.SurveysRespond;

    protected override string WritePermission => HrModule.Permissions.SurveysManage;

    public async Task<IReadOnlyDictionary<Guid, string>> LabelsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        Guid[] wanted = [.. ids];
        return await Context.Data.Set<Survey>().AsNoTracking().Where(s => wanted.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Title, cancellationToken);
    }

    protected override IReadOnlyList<RecordField<Survey>> Fields => fields ??=
    [
        Field.Text<Survey>("title", Survey.TitleMaxLength, s => s.Title, (s, v) => s.Title = v!, required: true),
        Field.Text<Survey>("description", Survey.DescriptionMaxLength, s => s.Description, (s, v) => s.Description = v),
        Field.Flag<Survey>("isAnonymous", s => s.IsAnonymous, (s, v) => s.IsAnonymous = v, createOnly: true),
        Field.ListChoice<Survey>("audienceDepartmentKey", HrModule.Lists.Department, s => s.AudienceDepartmentKey, (s, v) => s.AudienceDepartmentKey = v, required: false),
        Field.Date<Survey>("closesOn", s => s.ClosesOn, (s, v) => s.ClosesOn = v),
        Field.Computed<Survey>("status", s => s.Status),
        Field.Computed<Survey>("authorUserId", s => s.AuthorUserId?.ToString()),
        Field.Computed<Survey>("openedAt", s => s.OpenedAt?.ToString("O")),
        Field.Computed<Survey>("responseCount", s => JsonValue.Create(responseCounts.GetValueOrDefault(s.Id))),
        Field.Computed<Survey>("questionCount", s => JsonValue.Create(questionCounts.GetValueOrDefault(s.Id))),
        Field.Computed<Survey>("respondedByMe", s => JsonValue.Create(respondedByMe.Contains(s.Id))),
    ];

    private IReadOnlyList<RecordField<Survey>>? fields;

    protected override IReadOnlyDictionary<string, RecordFilter<Survey>> Filters { get; } = new Dictionary<string, RecordFilter<Survey>>
    {
        ["status"] = (q, v) => v.Split(',') is var s && s.All(Survey.Statuses.All.Contains) ? q.Where(x => s.Contains(x.Status)) : null,

        // Yanıt alan anketler (durum ve kapanış günü); hedef kitle okurlar için ayrıca RestrictAsync'te uygulanır.
        ["available"] = (q, v) => v == "true"
            ? q.Where(s => s.Status == Survey.Statuses.Open && (s.ClosesOn == null || s.ClosesOn >= SurveyAudience.Today))
            : null,
        ["isAnonymous"] = (q, v) => bool.TryParse(v, out var anonymous) ? q.Where(s => s.IsAnonymous == anonymous) : null,
    };

    protected override IOrderedQueryable<Survey> Order(IQueryable<Survey> source) => source.OrderByDescending(s => s.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("title", "status", "closesOn", "createdAt");

    protected override IOrderedQueryable<Survey> Order(IQueryable<Survey> source, string? sort) => sort?.TrimStart('-') switch
    {
        "title" => By(source, s => s.Title, sort.StartsWith('-')).ThenByDescending(s => s.CreatedAt),
        "status" => By(source, s => s.Status, sort.StartsWith('-')).ThenByDescending(s => s.CreatedAt),
        "closesOn" => By(source, s => s.ClosesOn, sort.StartsWith('-')).ThenByDescending(s => s.CreatedAt),
        "createdAt" => By(source, s => s.CreatedAt, sort.StartsWith('-')),
        _ => Order(source),
    };

    protected override IQueryable<Survey> Search(IQueryable<Survey> source, string term) =>
        source.Where(s => s.Title.Contains(term) || (s.Description != null && s.Description.Contains(term)));

    protected override async Task<IQueryable<Survey>> RestrictAsync(IQueryable<Survey> source, CancellationToken cancellationToken) =>
        await Context.Access.HasPermissionAsync(HrModule.Permissions.SurveysManage, cancellationToken)
            ? source
            : SurveyAudience.Available(source, await SurveyAudience.DepartmentAsync(Context, cancellationToken));

    protected override async Task PrepareAsync(IReadOnlyCollection<Survey> entities, CancellationToken cancellationToken)
    {
        Guid[] ids = [.. entities.Select(s => s.Id)];
        var responses = Context.Data.Set<SurveyResponse>().AsNoTracking().Where(r => ids.Contains(r.SurveyId));
        foreach (var row in await responses.GroupBy(r => r.SurveyId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
        {
            responseCounts[row.Key] = row.Count;
        }

        foreach (var row in await Context.Data.Set<SurveyQuestion>().AsNoTracking().Where(q => ids.Contains(q.SurveyId))
            .GroupBy(q => q.SurveyId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
        {
            questionCounts[row.Key] = row.Count;
        }

        var me = Context.CurrentUserId ?? Guid.Empty;
        respondedByMe.UnionWith(await responses.Where(r => r.RespondentUserId == me).Select(r => r.SurveyId).ToListAsync(cancellationToken));
    }

    protected override async Task AppliedAsync(Survey entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (isNew)
        {
            entity.AuthorUserId = Context.CurrentUserId;
            entity.Status = Survey.Statuses.Draft;
            return;
        }

        if (Context.Gate.Status is not { } status)
        {
            return;
        }

        if (status == Survey.Statuses.Open)
        {
            if (!await Context.Data.Set<SurveyQuestion>().AsNoTracking().AnyAsync(q => q.SurveyId == entity.Id, cancellationToken))
            {
                throw new RecordConflictException("Sorusu olmayan anket yanıta açılmaz.");
            }

            if (entity.ClosesOn is { } closes && closes < SurveyAudience.Today)
            {
                throw new RecordConflictException("Kapanış günü geçmiş; önce kapanış gününü değiştirin.");
            }

            entity.OpenedAt ??= DateTimeOffset.UtcNow;
        }

        entity.Status = status;
    }

    protected override async Task BeforeDeleteAsync(Survey entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<SurveyResponse>().AsNoTracking().AnyAsync(r => r.SurveyId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Yanıtı olan anket silinmez; kapatın.");
        }

        Context.Data.Set<SurveyQuestion>().RemoveRange(
            await Context.Data.Set<SurveyQuestion>().Where(q => q.SurveyId == entity.Id).ToListAsync(cancellationToken));
    }
}

/// <summary>
/// Anket soruları (ADR-0042 dilim 7). Okur yalnızca yanıt verebileceği anketlerin sorularını görür. Soru yalnızca taslak ankette eklenir, değişir ve
/// silinir (yanıt toplanmaya başladıktan sonra soru değişirse sonuçlar karşılaştırılamaz). Tek seçim sorusunda 2–20 farklı seçenek gerekir.
/// </summary>
internal sealed class SurveyQuestionRecordStore(HrStoreContext context) : HrRecordStore<SurveyQuestion>(context)
{
    public override string EntityKey => HrModule.Entities.SurveyQuestion;

    protected override string ReadPermission => HrModule.Permissions.SurveysRespond;

    protected override string WritePermission => HrModule.Permissions.SurveysManage;

    protected override IReadOnlyList<RecordField<SurveyQuestion>> Fields { get; } =
    [
        Field.Reference<SurveyQuestion, Survey>("surveyId", "Anket bulunamadı.", q => q.SurveyId, (q, v) => q.SurveyId = v!.Value, required: true, createOnly: true),
        Field.Integer<SurveyQuestion>("sequence", q => q.Sequence, (q, v) => q.Sequence = v ?? 0, 1, 10_000),
        Field.Text<SurveyQuestion>("text", SurveyQuestion.TextMaxLength, q => q.Text, (q, v) => q.Text = v!, required: true),
        Field.Choice<SurveyQuestion>("questionType", SurveyQuestion.Types.All, q => q.QuestionType, (q, v) => q.QuestionType = v),
        Field.Text<SurveyQuestion>("options", SurveyQuestion.OptionsMaxLength, q => q.Options, (q, v) => q.Options = v),
        Field.Flag<SurveyQuestion>("isRequired", q => q.IsRequired, (q, v) => q.IsRequired = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<SurveyQuestion>> Filters { get; } = new Dictionary<string, RecordFilter<SurveyQuestion>>
    {
        ["surveyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(x => x.SurveyId == id) : null,
    };

    protected override IOrderedQueryable<SurveyQuestion> Order(IQueryable<SurveyQuestion> source) => source.OrderBy(q => q.Sequence).ThenBy(q => q.CreatedAt);

    protected override IQueryable<SurveyQuestion> Search(IQueryable<SurveyQuestion> source, string term) => source.Where(q => q.Text.Contains(term));

    protected override async Task<IQueryable<SurveyQuestion>> RestrictAsync(IQueryable<SurveyQuestion> source, CancellationToken cancellationToken)
    {
        if (await Context.Access.HasPermissionAsync(HrModule.Permissions.SurveysManage, cancellationToken))
        {
            return source;
        }

        var visible = SurveyAudience.Available(Context.Data.Set<Survey>().AsNoTracking(), await SurveyAudience.DepartmentAsync(Context, cancellationToken))
            .Select(s => s.Id);
        return source.Where(q => visible.Contains(q.SurveyId));
    }

    protected override async Task AppliedAsync(SurveyQuestion entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        await EnsureDraftAsync(entity.SurveyId, cancellationToken);
        if (entity.QuestionType == SurveyQuestion.Types.Choice)
        {
            var options = entity.OptionList();
            if (options.Count is < 2 or > SurveyQuestion.MaxOptions || options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count
                || options.Any(o => o.Length > SurveyQuestion.OptionMaxLength))
            {
                throw new RecordValidationException([new RecordFieldError("options",
                    $"Tek seçim sorusuna satır başına bir tane olmak üzere 2–{SurveyQuestion.MaxOptions} farklı seçenek yazın (her biri en çok {SurveyQuestion.OptionMaxLength} karakter).")]);
            }

            entity.Options = string.Join('\n', options);
        }
        else
        {
            entity.Options = null;
        }

        if (entity.Sequence == 0)
        {
            var last = await Context.Data.Set<SurveyQuestion>().AsNoTracking().Where(q => q.SurveyId == entity.SurveyId && q.Id != entity.Id)
                .MaxAsync(q => (int?)q.Sequence, cancellationToken);
            entity.Sequence = (last ?? 0) + 10;
        }
    }

    protected override Task BeforeDeleteAsync(SurveyQuestion entity, CancellationToken cancellationToken) => EnsureDraftAsync(entity.SurveyId, cancellationToken);

    private async Task EnsureDraftAsync(Guid surveyId, CancellationToken cancellationToken)
    {
        var status = await Context.Data.Set<Survey>().AsNoTracking().Where(s => s.Id == surveyId).Select(s => s.Status).SingleAsync(cancellationToken);
        if (status != Survey.Statuses.Draft)
        {
            throw new RecordConflictException("Yanıta açılmış anketin soruları değiştirilmez.");
        }
    }
}

/// <summary>
/// Anket katılımları (ADR-0042 dilim 7). Yalnızca <see cref="SurveyService"/> yazar; değişmez, tek başına silinmez. Listesini "anket yönetimi" izni olan
/// görür; kimliği gizli ankette yanıtlayan boş döner (yalnızca sayı ve zaman).
/// </summary>
internal sealed class SurveyResponseRecordStore(HrStoreContext context) : HrRecordStore<SurveyResponse>(context)
{
    private readonly HashSet<Guid> anonymous = [];

    public override string EntityKey => HrModule.Entities.SurveyResponse;

    protected override string ReadPermission => HrModule.Permissions.SurveysManage;

    protected override string WritePermission => HrModule.Permissions.SurveysManage;

    protected override string? CreateBlockedMessage => Context.Gate.IsOpen ? null : "Yanıt /api/hr/surveys/{id}/responses ile gönderilir.";

    protected override string? ImmutableMessage => "Gönderilmiş yanıt değiştirilmez.";

    protected override IReadOnlyList<RecordField<SurveyResponse>> Fields => fields ??=
    [
        Field.Reference<SurveyResponse, Survey>("surveyId", "Anket bulunamadı.", r => r.SurveyId, (r, v) => r.SurveyId = v!.Value, required: true, createOnly: true),
        Field.Computed<SurveyResponse>("respondentUserId", r => anonymous.Contains(r.SurveyId) ? null : r.RespondentUserId.ToString()),
        Field.Computed<SurveyResponse>("submittedAt", r => r.SubmittedAt.ToString("O")),
    ];

    private IReadOnlyList<RecordField<SurveyResponse>>? fields;

    protected override IReadOnlyDictionary<string, RecordFilter<SurveyResponse>> Filters { get; } = new Dictionary<string, RecordFilter<SurveyResponse>>
    {
        ["surveyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(r => r.SurveyId == id) : null,
    };

    protected override IOrderedQueryable<SurveyResponse> Order(IQueryable<SurveyResponse> source) => source.OrderByDescending(r => r.SubmittedAt);

    protected override IQueryable<SurveyResponse> Search(IQueryable<SurveyResponse> source, string term) => source;

    protected override async Task PrepareAsync(IReadOnlyCollection<SurveyResponse> entities, CancellationToken cancellationToken)
    {
        Guid[] ids = [.. entities.Select(r => r.SurveyId).Distinct()];
        anonymous.UnionWith(await Context.Data.Set<Survey>().AsNoTracking().Where(s => ids.Contains(s.Id) && s.IsAnonymous).Select(s => s.Id).ToListAsync(cancellationToken));
    }

    protected override Task AppliedAsync(SurveyResponse entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        entity.RespondentUserId = Context.CurrentUserId ?? throw new RecordConflictException("Aktif kullanıcı yok.");
        entity.SubmittedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Yanıt cevapları (ADR-0042 dilim 7). Yalnızca <see cref="SurveyService"/> yazar; değişmez. Tek tek okunması ayrı bir izne (<c>hr.surveys.answers</c>,
/// hiçbir rol şablonunda yok) bağlıdır; yöneticiler sonuçları <see cref="ISurveyService.ResultsAsync"/> özetinden görür. Kimliği gizli anketin cevapları
/// bu izinle de listelenmez.
/// </summary>
internal sealed class SurveyAnswerRecordStore(HrStoreContext context) : HrRecordStore<SurveyAnswer>(context)
{
    public override string EntityKey => HrModule.Entities.SurveyAnswer;

    protected override string ReadPermission => HrModule.Permissions.SurveysAnswers;

    protected override string WritePermission => HrModule.Permissions.SurveysAnswers;

    protected override string? CreateBlockedMessage => Context.Gate.IsOpen ? null : "Yanıt /api/hr/surveys/{id}/responses ile gönderilir.";

    protected override string? ImmutableMessage => "Gönderilmiş yanıt değiştirilmez.";

    protected override IReadOnlyList<RecordField<SurveyAnswer>> Fields { get; } =
    [
        Field.Reference<SurveyAnswer, SurveyResponse>("responseId", "Yanıt bulunamadı.", a => a.ResponseId, (a, v) => a.ResponseId = v!.Value, required: true, createOnly: true),
        Field.Reference<SurveyAnswer, SurveyQuestion>("questionId", "Soru bulunamadı.", a => a.QuestionId, (a, v) => a.QuestionId = v!.Value, required: true, createOnly: true),
        Field.Integer<SurveyAnswer>("rating", a => a.Rating, (a, v) => a.Rating = v, 1, 5, createOnly: true),
        Field.Text<SurveyAnswer>("choice", SurveyQuestion.OptionMaxLength, a => a.Choice, (a, v) => a.Choice = v, createOnly: true),
        Field.Text<SurveyAnswer>("text", SurveyAnswer.TextMaxLength, a => a.TextValue, (a, v) => a.TextValue = v, createOnly: true),
        Field.Computed<SurveyAnswer>("surveyId", a => a.SurveyId.ToString()),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<SurveyAnswer>> Filters { get; } = new Dictionary<string, RecordFilter<SurveyAnswer>>
    {
        ["questionId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(a => a.QuestionId == id) : null,
        ["responseId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(a => a.ResponseId == id) : null,
    };

    protected override IOrderedQueryable<SurveyAnswer> Order(IQueryable<SurveyAnswer> source) => source.OrderBy(a => a.CreatedAt);

    protected override IQueryable<SurveyAnswer> Search(IQueryable<SurveyAnswer> source, string term) => source;

    protected override Task<IQueryable<SurveyAnswer>> RestrictAsync(IQueryable<SurveyAnswer> source, CancellationToken cancellationToken)
    {
        // Yazan komut kendi yazdığını döndürebilsin diye kapı açıkken süzgeç yok; listelemede gizli anketlerin cevapları görünmez.
        if (Context.Gate.IsOpen)
        {
            return Task.FromResult(source);
        }

        var anonymous = Context.Data.Set<Survey>().AsNoTracking().Where(s => s.IsAnonymous).Select(s => s.Id);
        return Task.FromResult(source.Where(a => !anonymous.Contains(a.SurveyId)));
    }

    protected override async Task AppliedAsync(SurveyAnswer entity, JsonObject values, bool isNew, CancellationToken cancellationToken) =>
        entity.SurveyId = await Context.Data.Set<SurveyResponse>().AsNoTracking().Where(r => r.Id == entity.ResponseId).Select(r => r.SurveyId).SingleAsync(cancellationToken);
}

/// <summary>Yanıt formundaki tek bir sorunun cevabı; sorunun türüne göre puan, seçenek ya da metin.</summary>
public sealed record SurveyAnswerInput(Guid QuestionId, int? Rating, string? Choice, string? Text);

/// <summary>Sorunun özet sonucu: cevap sayısı, puan ortalaması, seçenek/puan dağılımı ve metin cevapları (yanıtlayan bilgisi olmadan, alfabetik).</summary>
public sealed record SurveyQuestionResult(
    Guid QuestionId, int Sequence, string Text, string QuestionType, int AnswerCount, decimal? Average,
    IReadOnlyList<(string Option, int Count)> Counts, IReadOnlyList<string> Texts);

/// <summary>Anket sonucu. Kimliği gizli ankette en az <see cref="Survey.MinAnonymousResponses"/> yanıt yoksa sorular boş ve neden dolu döner.</summary>
public sealed record SurveyResults(Guid SurveyId, int ResponseCount, string? HiddenReason, IReadOnlyList<SurveyQuestionResult> Questions);

/// <summary>Anket komutları (ADR-0042 dilim 7): durum eylemleri, yanıt gönderme ve sonuç özeti.</summary>
public interface ISurveyService
{
    Task<RecordResult<IReadOnlyList<LeaveActionState>>> ActionsAsync(Guid surveyId, CancellationToken cancellationToken);

    Task<RecordResult<RecordData>> RunAsync(Guid surveyId, string action, long version, CancellationToken cancellationToken);

    /// <summary>
    /// Kullanıcı anketi yanıtlar: anket yanıta açık ve kullanıcıya yönelik olmalı (yoksa 404/409), daha önce yanıtlamamış olmalı (409); zorunlu sorular
    /// cevaplanmalı, puan 1–5, seçim seçeneklerden biri olmalı (422). Katılım ve cevaplar tek işlemde yazılır. Anketin güncel hali döner.
    /// </summary>
    Task<RecordResult<RecordData>> SubmitAsync(Guid surveyId, IReadOnlyList<SurveyAnswerInput> answers, CancellationToken cancellationToken);

    /// <summary>Sonuç özeti; yalnızca "anket yönetimi" izniyle.</summary>
    Task<RecordResult<SurveyResults>> ResultsAsync(Guid surveyId, CancellationToken cancellationToken);
}

internal sealed class SurveyService(IRecordPipeline pipeline, HrStoreContext context) : ISurveyService
{
    public static class Actions
    {
        public const string Open = "open";
        public const string Close = "close";
    }

    public async Task<RecordResult<IReadOnlyList<LeaveActionState>>> ActionsAsync(Guid surveyId, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(HrModule.Entities.Survey, surveyId), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<IReadOnlyList<LeaveActionState>>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        if (!await context.Access.HasPermissionAsync(HrModule.Permissions.SurveysManage, cancellationToken))
        {
            return RecordResult<IReadOnlyList<LeaveActionState>>.Ok([]);
        }

        var status = record.Value!.Values["status"]?.GetValue<string>();
        return RecordResult<IReadOnlyList<LeaveActionState>>.Ok(
        [
            new LeaveActionState(Actions.Open, status == Survey.Statuses.Closed ? "Yeniden aç" : "Yanıta aç", status != Survey.Statuses.Open,
                status == Survey.Statuses.Open ? "Anket zaten yanıta açık." : null, RequiresReason: false, Destructive: false),
            new LeaveActionState(Actions.Close, "Kapat", status == Survey.Statuses.Open, status == Survey.Statuses.Open ? null : "Yalnızca yanıta açık anket kapatılır.",
                RequiresReason: false, Destructive: true),
        ]);
    }

    public async Task<RecordResult<RecordData>> RunAsync(Guid surveyId, string action, long version, CancellationToken cancellationToken)
    {
        var to = action switch
        {
            Actions.Open => Survey.Statuses.Open,
            Actions.Close => Survey.Statuses.Closed,
            _ => null,
        };
        if (to is null)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Bilinmeyen eylem.", [new RecordFieldError("action", "Bilinmeyen eylem.")]);
        }

        var record = await pipeline.GetAsync(new RecordRef(HrModule.Entities.Survey, surveyId), cancellationToken);
        if (!record.IsSuccess)
        {
            return record;
        }

        if (!await context.Access.HasPermissionAsync(HrModule.Permissions.SurveysManage, cancellationToken))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, "Bu eylem için yetkiniz yok.");
        }

        var status = record.Value!.Values["status"]?.GetValue<string>();
        if ((to == Survey.Statuses.Open && status == Survey.Statuses.Open) || (to == Survey.Statuses.Closed && status != Survey.Statuses.Open))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict,
                to == Survey.Statuses.Open ? "Anket zaten yanıta açık." : "Yalnızca yanıta açık anket kapatılır.");
        }

        using (context.Gate.Open(HrModule.Permissions.SurveysManage, to, reason: null))
        {
            return await pipeline.UpdateAsync(new RecordCommand(HrModule.Entities.Survey, surveyId, new JsonObject { ["status"] = to }, version), cancellationToken);
        }
    }

    public async Task<RecordResult<RecordData>> SubmitAsync(Guid surveyId, IReadOnlyList<SurveyAnswerInput> answers, CancellationToken cancellationToken)
    {
        if (!await context.Access.HasPermissionAsync(HrModule.Permissions.SurveysRespond, cancellationToken))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, "Anket yanıtlama yetkiniz yok.");
        }

        var survey = await pipeline.GetAsync(new RecordRef(HrModule.Entities.Survey, surveyId), cancellationToken);
        if (!survey.IsSuccess)
        {
            return survey;
        }

        // Yönetici bütün anketleri görür; yanıt için o da hedef kitlede ve anket yanıta açık olmalıdır.
        var department = await SurveyAudience.DepartmentAsync(context, cancellationToken);
        if (!await SurveyAudience.Available(context.Data.Set<Survey>().AsNoTracking(), department).AnyAsync(s => s.Id == surveyId, cancellationToken))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, "Bu anket şu an size yönelik yanıta açık değil.");
        }

        if (survey.Value!.Values["respondedByMe"]?.GetValue<bool>() == true)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, "Bu anketi daha önce yanıtladınız.");
        }

        var questions = await context.Data.Set<SurveyQuestion>().AsNoTracking().Where(q => q.SurveyId == surveyId).ToListAsync(cancellationToken);
        var (clean, errors) = Validate(questions, answers);
        if (errors.Count > 0)
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.ValidationFailed, "Yanıtta hatalar var.", errors);
        }

        return await pipeline.RunAtomicAsync(async ct =>
        {
            using (context.Gate.Open(HrModule.Permissions.SurveysRespond, "submit", reason: null))
            {
                var response = await pipeline.CreateAsync(new RecordCommand(HrModule.Entities.SurveyResponse, null, new JsonObject
                {
                    ["surveyId"] = surveyId.ToString(),
                }), ct);
                if (!response.IsSuccess)
                {
                    return response;
                }

                foreach (var answer in clean)
                {
                    var written = await pipeline.CreateAsync(new RecordCommand(HrModule.Entities.SurveyAnswer, null, new JsonObject
                    {
                        ["responseId"] = response.Value!.Id.ToString(),
                        ["questionId"] = answer.QuestionId.ToString(),
                        ["rating"] = answer.Rating,
                        ["choice"] = answer.Choice,
                        ["text"] = answer.Text,
                    }), ct);
                    if (!written.IsSuccess)
                    {
                        return written;
                    }
                }
            }

            return await pipeline.GetAsync(new RecordRef(HrModule.Entities.Survey, surveyId), ct);
        }, cancellationToken);
    }

    public async Task<RecordResult<SurveyResults>> ResultsAsync(Guid surveyId, CancellationToken cancellationToken)
    {
        var survey = await pipeline.GetAsync(new RecordRef(HrModule.Entities.Survey, surveyId), cancellationToken);
        if (!survey.IsSuccess)
        {
            return RecordResult<SurveyResults>.Fail(survey.Error!.Code, survey.Error.Message, survey.Error.FieldErrors);
        }

        if (!await context.Access.HasPermissionAsync(HrModule.Permissions.SurveysManage, cancellationToken))
        {
            return RecordResult<SurveyResults>.Fail(RecordErrorCode.PermissionDenied, "Anket sonuçlarını görme yetkiniz yok.");
        }

        var responseCount = await context.Data.Set<SurveyResponse>().AsNoTracking().CountAsync(r => r.SurveyId == surveyId, cancellationToken);
        if (survey.Value!.Values["isAnonymous"]?.GetValue<bool>() == true && responseCount < Survey.MinAnonymousResponses)
        {
            return RecordResult<SurveyResults>.Ok(new SurveyResults(surveyId, responseCount,
                $"Kimliği gizli ankette sonuçlar en az {Survey.MinAnonymousResponses} yanıttan sonra gösterilir.", []));
        }

        var questions = await context.Data.Set<SurveyQuestion>().AsNoTracking().Where(q => q.SurveyId == surveyId)
            .OrderBy(q => q.Sequence).ThenBy(q => q.CreatedAt).ToListAsync(cancellationToken);
        var answers = await context.Data.Set<SurveyAnswer>().AsNoTracking().Where(a => a.SurveyId == surveyId)
            .Select(a => new { a.QuestionId, a.Rating, a.Choice, a.TextValue }).ToListAsync(cancellationToken);
        var byQuestion = answers.ToLookup(a => a.QuestionId);
        var results = questions.Select(q =>
        {
            var mine = byQuestion[q.Id].ToList();
            IReadOnlyList<(string, int)> counts = q.QuestionType switch
            {
                SurveyQuestion.Types.Rating => [.. Enumerable.Range(1, 5).Select(r => (r.ToString(CultureInfo.InvariantCulture), mine.Count(a => a.Rating == r)))],
                SurveyQuestion.Types.Choice => [.. q.OptionList().Select(o => (o, mine.Count(a => a.Choice == o)))],
                _ => [],
            };
            var ratings = mine.Where(a => a.Rating is not null).Select(a => (decimal)a.Rating!.Value).ToList();
            return new SurveyQuestionResult(q.Id, q.Sequence, q.Text, q.QuestionType, mine.Count,
                ratings.Count == 0 ? null : Math.Round(ratings.Average(), 2),
                counts, [.. mine.Where(a => a.TextValue is not null).Select(a => a.TextValue!).Order(StringComparer.Ordinal)]);
        }).ToList();
        return RecordResult<SurveyResults>.Ok(new SurveyResults(surveyId, responseCount, null, results));
    }

    private static (List<SurveyAnswerInput> Clean, List<RecordFieldError> Errors) Validate(IReadOnlyList<SurveyQuestion> questions, IReadOnlyList<SurveyAnswerInput> answers)
    {
        var clean = new List<SurveyAnswerInput>();
        var errors = new List<RecordFieldError>();
        var byId = questions.ToDictionary(q => q.Id);
        foreach (var group in answers.GroupBy(a => a.QuestionId))
        {
            if (!byId.TryGetValue(group.Key, out var question))
            {
                errors.Add(new RecordFieldError($"answers.{group.Key}", "Bu ankette böyle bir soru yok."));
                continue;
            }

            if (group.Count() > 1)
            {
                errors.Add(new RecordFieldError($"answers.{group.Key}", "Soru bir kez cevaplanır."));
                continue;
            }

            var answer = group.Single();
            var text = string.IsNullOrWhiteSpace(answer.Text) ? null : answer.Text.Trim();
            var choice = string.IsNullOrWhiteSpace(answer.Choice) ? null : answer.Choice.Trim();
            SurveyAnswerInput? value = question.QuestionType switch
            {
                SurveyQuestion.Types.Rating when answer.Rating is null => null,
                SurveyQuestion.Types.Rating when answer.Rating is < 1 or > 5 => Fail("Puan 1 ile 5 arasında olmalıdır."),
                SurveyQuestion.Types.Rating => new SurveyAnswerInput(question.Id, answer.Rating, null, null),
                SurveyQuestion.Types.Choice when choice is null => null,
                SurveyQuestion.Types.Choice when !question.OptionList().Contains(choice) => Fail("Seçeneklerden birini seçin."),
                SurveyQuestion.Types.Choice => new SurveyAnswerInput(question.Id, null, choice, null),
                _ when text is null => null,
                _ when text.Length > SurveyAnswer.TextMaxLength => Fail($"En çok {SurveyAnswer.TextMaxLength} karakter yazın."),
                _ => new SurveyAnswerInput(question.Id, null, null, text),
            };
            if (value is not null && value.QuestionId != Guid.Empty)
            {
                clean.Add(value);
            }

            SurveyAnswerInput Fail(string message)
            {
                errors.Add(new RecordFieldError($"answers.{question.Id}", message));
                return new SurveyAnswerInput(Guid.Empty, null, null, null);
            }
        }

        foreach (var question in questions.Where(q => q.IsRequired && clean.All(a => a.QuestionId != q.Id) && errors.All(e => e.Field != $"answers.{q.Id}")))
        {
            errors.Add(new RecordFieldError($"answers.{question.Id}", "Bu soru zorunludur."));
        }

        return (clean, errors);
    }
}
