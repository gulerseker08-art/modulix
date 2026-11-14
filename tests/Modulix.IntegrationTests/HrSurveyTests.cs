using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Modules.Hr.Domain;
using Modulix.Shared.Auth;
using Modulix.Shared.Hr;
using Modulix.Shared.Records;
using static Modulix.Modules.Hr.HrModule.Permissions;
using Scope = Modulix.Core.Contracts.Authorization.RecordScope;

namespace Modulix.IntegrationTests;

/// <summary>
/// ADR-0042 dilim 7: anketler. Mutlu yol (hazırlama, soru türleri, yanıta açma, yanıt, sonuç özeti, kapatma), doğrulama (sorusuz anket açılmaz,
/// seçenek kuralları, açılmış anketin sorusu değişmez, zorunlu soru, puan ve seçenek, tekrar yanıt, doğrudan yazma, yanıtlı anket silinmez), kimliği
/// gizli anket (yanıtlayan görünmez, az yanıtta sonuç gizli, cevaplar tek tek listelenmez), tenant izolasyonu ve yetki (hedef kitle, izinsiz kullanıcı).
/// </summary>
public class HrSurveyTests(ModulixApiFactory factory) : IClassFixture<ModulixApiFactory>
{
    private const string Employees = "/api/records/hr.employee";
    private const string People = "/api/records/core.person";
    private const string Surveys = "/api/records/hr.survey";
    private const string Questions = "/api/records/hr.survey-question";
    private const string Responses = "/api/records/hr.survey-response";
    private const string Answers = "/api/records/hr.survey-answer";

    [Fact]
    public async Task Survey_is_prepared_opened_answered_summarised_and_closed()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);

        var survey = await PostAsync(hr, Surveys, new JsonObject { ["title"] = "Memnuniyet", ["description"] = "Kısa anket." });
        Assert.Equal(("draft", s.Hr.Id.ToString(), 0), (Text(survey, "status"), Text(survey, "authorUserId"), Number(survey, "questionCount")));
        var rating = await QuestionAsync(hr, survey.Id, "İş yükü nasıl?", "rating", null, required: true);
        var choice = await QuestionAsync(hr, survey.Id, "Ofise kaç gün?", "choice", "Hiç\nİki gün\nHer gün", required: true);
        var text = await QuestionAsync(hr, survey.Id, "Önerin?", "text", "yok sayılır", required: false);
        Assert.Equal([10, 20, 30], new[] { rating, choice, text }.Select(q => Number(q, "sequence")));
        Assert.Null(Text(text, "options"));

        // Taslak çalışana görünmez; açılınca görünür.
        Assert.Empty(await ListAsync(staff, Surveys, "available=true"));
        Assert.Equal("open", Text(await ActAsync(hr, survey.Id, "open"), "status"));
        var visible = Assert.Single(await ListAsync(staff, Surveys, "available=true"));
        Assert.Equal((3, false), (Number(visible, "questionCount"), Flag(visible, "respondedByMe")));
        Assert.Equal(3, (await ListAsync(staff, Questions, $"surveyId={survey.Id}")).Count);

        var answered = await SubmitAsync(staff, survey.Id, new SurveyAnswerItem(rating.Id, 4, null, null), new SurveyAnswerItem(choice.Id, null, "İki gün", null),
            new SurveyAnswerItem(text.Id, null, null, "  Daha çok eğitim  "));
        Assert.Equal((true, 1), (Flag(answered, "respondedByMe"), Number(answered, "responseCount")));
        await SubmitAsync(hr, survey.Id, new SurveyAnswerItem(rating.Id, 2, null, null), new SurveyAnswerItem(choice.Id, null, "İki gün", null));

        // Sonuç özeti; kimliği açık ankette yanıtlayanlar listelenir.
        var results = (await hr.GetFromJsonAsync<SurveyResultsDto>($"/api/hr/surveys/{survey.Id}/results"))!;
        Assert.Equal((2, (string?)null), (results.ResponseCount, results.HiddenReason));
        var ratingResult = results.Questions[0];
        Assert.Equal((2, 3m), (ratingResult.AnswerCount, ratingResult.Average));
        Assert.Equal([0, 1, 0, 1, 0], ratingResult.Counts.Select(c => c.Count));
        Assert.Equal([("Hiç", 0), ("İki gün", 2), ("Her gün", 0)], results.Questions[1].Counts.Select(c => (c.Option, c.Count)));
        Assert.Equal(["Daha çok eğitim"], results.Questions[2].Texts);
        Assert.Equal(new[] { s.Hr.Id.ToString(), s.Staff.Id.ToString() }.Order(),
            (await ListAsync(hr, Responses, $"surveyId={survey.Id}")).Select(r => Text(r, "respondentUserId")).Order());

        // Kapanınca çalışana görünmez ve yanıt alınmaz; yeniden açılabilir.
        Assert.Equal("closed", Text(await ActAsync(hr, survey.Id, "close"), "status"));
        Assert.Empty(await ListAsync(staff, Surveys, null));
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Surveys}/{survey.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostResponseAsync(staff, survey.Id, new SurveyAnswerItem(rating.Id, 5, null, null))).StatusCode);
        Assert.Equal("open", Text(await ActAsync(hr, survey.Id, "open"), "status"));
    }

    [Fact]
    public async Task Invalid_surveys_questions_and_answers_are_rejected()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);

        var survey = await PostAsync(hr, Surveys, new JsonObject { ["title"] = "Kurallar" });
        Assert.Equal(HttpStatusCode.Conflict, (await PostActionAsync(hr, survey.Id, "open")).StatusCode); // sorusuz
        Assert.Equal(HttpStatusCode.Conflict, (await PostActionAsync(hr, survey.Id, "close")).StatusCode); // açık değil
        Assert.True((await ValidationProblemAsync(await hr.PostAsJsonAsync($"/api/hr/surveys/{survey.Id}/actions", new RunHrActionRequest("sil", survey.Version, null))))
            .Errors.ContainsKey("action"));

        var oneOption = await ValidationProblemAsync(await hr.PostAsJsonAsync(Questions, new RecordWriteRequest(Question(survey.Id, "Tek", "choice", "Evet", true))));
        Assert.True(oneOption.Errors.ContainsKey("options"));
        var duplicate = await ValidationProblemAsync(await hr.PostAsJsonAsync(Questions, new RecordWriteRequest(Question(survey.Id, "Çift", "choice", "Evet\nevet", true))));
        Assert.True(duplicate.Errors.ContainsKey("options"));
        Assert.True((await ValidationProblemAsync(await hr.PostAsJsonAsync(Questions, new RecordWriteRequest(new JsonObject { ["surveyId"] = survey.Id.ToString() }))))
            .Errors.ContainsKey("text"));

        var rating = await QuestionAsync(hr, survey.Id, "Puan", "rating", null, required: true);
        var choice = await QuestionAsync(hr, survey.Id, "Seçim", "choice", "A\nB", required: false);
        await ActAsync(hr, survey.Id, "open");

        // Açılmış anketin sorusu eklenmez, değişmez, silinmez.
        Assert.Equal(HttpStatusCode.Conflict, (await hr.PostAsJsonAsync(Questions, new RecordWriteRequest(Question(survey.Id, "Geç", "text", null, false)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await hr.PutAsJsonAsync($"{Questions}/{rating.Id}",
            new RecordWriteRequest(new JsonObject { ["text"] = "Değişti" }, rating.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await hr.DeleteAsync($"{Questions}/{rating.Id}?version={rating.Version}")).StatusCode);

        // Cevap kuralları.
        var missing = await ValidationProblemAsync(await PostResponseAsync(staff, survey.Id, new SurveyAnswerItem(choice.Id, null, "A", null)));
        Assert.True(missing.Errors.ContainsKey($"answers.{rating.Id}"));
        var wrong = await ValidationProblemAsync(await PostResponseAsync(staff, survey.Id,
            new SurveyAnswerItem(rating.Id, 6, null, null), new SurveyAnswerItem(choice.Id, null, "C", null), new SurveyAnswerItem(Guid.NewGuid(), 1, null, null)));
        Assert.True(wrong.Errors.ContainsKey($"answers.{rating.Id}"));
        Assert.True(wrong.Errors.ContainsKey($"answers.{choice.Id}"));
        Assert.Equal(3, wrong.Errors.Count);

        await SubmitAsync(staff, survey.Id, new SurveyAnswerItem(rating.Id, 5, null, null));
        Assert.Equal(HttpStatusCode.Conflict, (await PostResponseAsync(staff, survey.Id, new SurveyAnswerItem(rating.Id, 1, null, null))).StatusCode);

        // Katılım ve cevap yalnızca komutla yazılır; yanıtı olan anket silinmez.
        Assert.Equal(HttpStatusCode.Conflict, (await hr.PostAsJsonAsync(Responses,
            new RecordWriteRequest(new JsonObject { ["surveyId"] = survey.Id.ToString() }))).StatusCode);
        var current = await GetAsync(hr, $"{Surveys}/{survey.Id}");
        Assert.Equal(HttpStatusCode.Conflict, (await hr.DeleteAsync($"{Surveys}/{survey.Id}?version={current.Version}")).StatusCode);

        // Taslak anket sorularıyla birlikte silinir.
        var draft = await PostAsync(hr, Surveys, new JsonObject { ["title"] = "Silinecek" });
        await QuestionAsync(hr, draft.Id, "Soru", "rating", null, required: true);
        Assert.Equal(HttpStatusCode.NoContent, (await hr.DeleteAsync($"{Surveys}/{draft.Id}?version={draft.Version}")).StatusCode);
        Assert.Empty(await ListAsync(hr, Questions, $"surveyId={draft.Id}"));
    }

    [Fact]
    public async Task Anonymous_survey_hides_respondents_and_shows_results_only_after_enough_responses()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);
        using var colleague = await LoginAsync(s.Colleague);

        var survey = await PostAsync(hr, Surveys, new JsonObject { ["title"] = "Nabız", ["isAnonymous"] = true });
        var rating = await QuestionAsync(hr, survey.Id, "Bu hafta nasılsın?", "rating", null, required: true);
        await ActAsync(hr, survey.Id, "open");

        // Kimliği gizli seçimi sonradan değişmez.
        var current = await GetAsync(hr, $"{Surveys}/{survey.Id}");
        var change = await hr.PutAsJsonAsync($"{Surveys}/{survey.Id}", new RecordWriteRequest(new JsonObject { ["isAnonymous"] = false }, current.Version));
        Assert.NotEqual(HttpStatusCode.OK, change.StatusCode);
        Assert.True(Flag(await GetAsync(hr, $"{Surveys}/{survey.Id}"), "isAnonymous"));

        await SubmitAsync(staff, survey.Id, new SurveyAnswerItem(rating.Id, 2, null, null));
        await SubmitAsync(colleague, survey.Id, new SurveyAnswerItem(rating.Id, 4, null, null));
        var hidden = (await hr.GetFromJsonAsync<SurveyResultsDto>($"/api/hr/surveys/{survey.Id}/results"))!;
        Assert.Equal((2, true), (hidden.ResponseCount, hidden.HiddenReason is not null));
        Assert.Empty(hidden.Questions);

        await SubmitAsync(hr, survey.Id, new SurveyAnswerItem(rating.Id, 3, null, null));
        var shown = (await hr.GetFromJsonAsync<SurveyResultsDto>($"/api/hr/surveys/{survey.Id}/results"))!;
        Assert.Equal((3, (string?)null, 3m), (shown.ResponseCount, shown.HiddenReason, Assert.Single(shown.Questions).Average));

        // Yanıtlayan hiçbir uçta görünmez; cevaplar tek tek listelenmez (yönetici rolünde bile).
        var responses = await ListAsync(hr, Responses, $"surveyId={survey.Id}");
        Assert.Equal(3, responses.Count);
        Assert.All(responses, r => Assert.Null(Text(r, "respondentUserId")));
        Assert.Empty(await ListAsync(hr, Answers, $"questionId={rating.Id}"));
    }

    /// <summary>
    /// Bölüm 24: tam anonim anket. Kimliği gizli ankette cevap ne katılım satırına ne yazma anına bağlıdır: cevapta katılım kimliği yok, pusula
    /// kimliği rastgele ve başka yerde yok, satır kimliği rastgele (v4), zamanı anketin açıldığı gün; cevabın denetim satırı kişisiz, kayıtsız,
    /// değersiz ve iz kimliksiz; olay yok; cevap metni denetim kaydının hiçbir yerinde yok. Katılım (kim yanıtladı) tutulur ve tekrar yanıt
    /// engellenir. Kimliği açık ankette cevap katılıma bağlı ve denetim kaydı kişiseldir.
    /// </summary>
    [Fact]
    public async Task Anonymous_answers_are_linked_to_no_one_in_the_database_audit_log_or_events()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);

        var anonymous = await PostAsync(hr, Surveys, new JsonObject { ["title"] = "Gizli nabız", ["isAnonymous"] = true });
        var rating = await QuestionAsync(hr, anonymous.Id, "Puanınız", "rating", null, required: true);
        var comment = await QuestionAsync(hr, anonymous.Id, "Görüşünüz", "text", null, required: false);
        await ActAsync(hr, anonymous.Id, "open");
        await SubmitAsync(staff, anonymous.Id, new SurveyAnswerItem(rating.Id, 5, null, null), new SurveyAnswerItem(comment.Id, null, null, "cok gizli dusunce 7f3a"));
        Assert.Equal(HttpStatusCode.Conflict, (await PostResponseAsync(staff, anonymous.Id, new SurveyAnswerItem(rating.Id, 1, null, null))).StatusCode);

        var open = await GetAsync(hr, $"{Surveys}/{anonymous.Id}");
        var named = await PostAsync(hr, Surveys, new JsonObject { ["title"] = "Açık anket" });
        var namedQuestion = await QuestionAsync(hr, named.Id, "Puan", "rating", null, required: true);
        await ActAsync(hr, named.Id, "open");
        await SubmitAsync(staff, named.Id, new SurveyAnswerItem(namedQuestion.Id, 3, null, null));

        using var scope = factory.OpenScope(s.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var participation = await db.Set<SurveyResponse>().AsNoTracking().SingleAsync(r => r.SurveyId == anonymous.Id);
        Assert.Equal(s.Staff.Id, participation.RespondentUserId);

        var answers = await db.Set<SurveyAnswer>().AsNoTracking().Where(a => a.SurveyId == anonymous.Id).ToListAsync();
        Assert.Equal(2, answers.Count);
        Assert.All(answers, a => Assert.Null(a.ResponseId));
        Assert.Single(answers.Select(a => a.BallotId).Distinct());
        var ballot = answers[0].BallotId;
        Assert.NotEqual(participation.Id, ballot);
        Assert.False(await db.Set<SurveyResponse>().AsNoTracking().AnyAsync(r => r.Id == ballot));
        Assert.All(answers, a => Assert.Equal(4, a.Id.Version));
        var openedDay = DateTimeOffset.Parse(Text(open, "openedAt")!).UtcDateTime.Date;
        Assert.All(answers, a => Assert.Equal(openedDay, a.CreatedAt.UtcDateTime));

        // Denetim kaydı: anonim cevabın satırı kişisiz, kayıtsız, değersiz, iz kimliksiz ve gün hassasiyetinde; cevap ve metni hiçbir satırda yok.
        var answerAudit = await db.AuditLog.AsNoTracking().Where(a => a.EntityKey == "hr.survey-answer").ToListAsync();
        var anonymousAudit = answerAudit.Where(a => a.UserId == Guid.Empty).ToList();
        Assert.Equal(2, anonymousAudit.Count);
        Assert.All(anonymousAudit, a => Assert.Equal((Guid.Empty, "{}", (string?)null, TimeSpan.Zero),
            (a.RecordId, a.Changes, a.CorrelationId, a.OccurredAt.UtcDateTime.TimeOfDay)));
        Guid[] answerIds = [.. answers.Select(a => a.Id)];
        Assert.False(await db.AuditLog.AsNoTracking().AnyAsync(a => answerIds.Contains(a.RecordId) || a.RecordId == ballot));
        Assert.False(await db.AuditLog.AsNoTracking().AnyAsync(a => a.Changes.Contains("cok gizli dusunce 7f3a")));
        Assert.False(await db.OutboxMessages.AsNoTracking().AnyAsync(o => o.TenantId == s.TenantId && o.EntityKey == "hr.survey-answer"
            && (answerIds.Contains(o.RecordId) || o.RecordId == Guid.Empty)));

        // Kimliği açık ankette cevap katılıma bağlıdır ve denetim satırı kişiseldir.
        var namedAnswer = await db.Set<SurveyAnswer>().AsNoTracking().SingleAsync(a => a.SurveyId == named.Id);
        var namedParticipation = await db.Set<SurveyResponse>().AsNoTracking().SingleAsync(r => r.SurveyId == named.Id);
        Assert.Equal((namedParticipation.Id, namedParticipation.Id), (namedAnswer.ResponseId, namedAnswer.BallotId));
        Assert.Contains(answerAudit, a => a.RecordId == namedAnswer.Id && a.UserId == s.Staff.Id);

        // Sonuç sayımı pusulaya göre: kimliği gizli ankette yanıt az olduğu için sonuç gizli, açık ankette bir cevaplayan.
        var namedResults = (await hr.GetFromJsonAsync<SurveyResultsDto>($"/api/hr/surveys/{named.Id}/results"))!;
        Assert.Equal(1, Assert.Single(namedResults.Questions).AnswerCount);
    }

    [Fact]
    public async Task Audience_permissions_and_tenants_are_enforced()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var hrA = await LoginAsync(a.Hr);
        using var hrB = await LoginAsync(b.Hr);
        using var staff = await LoginAsync(a.Staff);
        using var outsider = await LoginAsync(a.Outsider);

        var finance = await PostAsync(hrA, Surveys, new JsonObject { ["title"] = "Finans", ["audienceBusinessUnitId"] = a.Finance.ToString() });
        var financeQuestion = await QuestionAsync(hrA, finance.Id, "Soru", "rating", null, required: true);
        await ActAsync(hrA, finance.Id, "open");
        var management = await PostAsync(hrA, Surveys, new JsonObject { ["title"] = "Yönetim", ["audienceBusinessUnitId"] = a.Management.ToString() });
        var managementQuestion = await QuestionAsync(hrA, management.Id, "Soru", "rating", null, required: true);
        await ActAsync(hrA, management.Id, "open");

        // Hedef kitle: çalışan yalnızca kendi departmanının anketini görür ve yanıtlar; başka departmanın anketi ve sorusu yoktur.
        Assert.Equal([finance.Id], (await ListAsync(staff, Surveys, null)).Select(x => x.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Surveys}/{management.Id}")).StatusCode);
        Assert.Empty(await ListAsync(staff, Questions, $"surveyId={management.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, (await PostResponseAsync(staff, management.Id, new SurveyAnswerItem(managementQuestion.Id, 3, null, null))).StatusCode);
        await SubmitAsync(staff, finance.Id, new SurveyAnswerItem(financeQuestion.Id, 3, null, null));

        // Yönetici bütün anketleri görür ama hedef kitlede değilse yanıtlayamaz.
        Assert.Equal(HttpStatusCode.Conflict, (await PostResponseAsync(hrA, finance.Id, new SurveyAnswerItem(financeQuestion.Id, 3, null, null))).StatusCode);

        // Çalışan anket hazırlayamaz, durum değiştiremez, sonuç ve katılım göremez.
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync(Surveys, new RecordWriteRequest(new JsonObject { ["title"] = "Benim" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostActionAsync(staff, finance.Id, "close")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync($"/api/hr/surveys/{finance.Id}/results")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync(Responses)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync(Answers)).StatusCode);

        // İzinsiz kullanıcı.
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Surveys)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostResponseAsync(outsider, finance.Id, new SurveyAnswerItem(financeQuestion.Id, 3, null, null))).StatusCode);

        // B tenant'ı A'nın anketini, sorularını, katılımlarını ve sonucunu görmez, yanıtlayamaz.
        Assert.Empty(await ListAsync(hrB, Surveys, null));
        Assert.Empty(await ListAsync(hrB, Questions, null));
        Assert.Empty(await ListAsync(hrB, Responses, null));
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"{Surveys}/{finance.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync($"/api/hr/surveys/{finance.Id}/results")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostResponseAsync(hrB, finance.Id, new SurveyAnswerItem(financeQuestion.Id, 3, null, null))).StatusCode);
        Assert.Equal(1, Number(await GetAsync(hrA, $"{Surveys}/{finance.Id}"), "responseCount"));
    }

    /// <summary>Bölüm 23b: çoklu seçim sorusu seçenek başına sayılır; sonuç CSV olarak (formül enjeksiyonuna karşı korumalı) indirilir.</summary>
    [Fact]
    public async Task Multi_choice_answers_are_counted_per_option_and_results_export_as_csv()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);
        using var colleague = await LoginAsync(s.Colleague);

        var survey = await PostAsync(hr, Surveys, new JsonObject { ["title"] = "İkram" });
        var drinks = await QuestionAsync(hr, survey.Id, "Hangi içecekler olsun?", "multiChoice", "Kahve\nÇay\nSu", required: true);
        var note = await QuestionAsync(hr, survey.Id, "Notunuz", "text", null, required: false);
        await ActAsync(hr, survey.Id, "open");

        await SubmitAsync(staff, survey.Id, new SurveyAnswerItem(drinks.Id, null, null, null, ["Kahve", "Çay"]));
        await SubmitAsync(colleague, survey.Id,
            new SurveyAnswerItem(drinks.Id, null, null, null, ["Çay"]), new SurveyAnswerItem(note.Id, null, null, "=1+1 çay demli olsun"));

        var results = (await hr.GetFromJsonAsync<SurveyResultsDto>($"/api/hr/surveys/{survey.Id}/results"))!;
        var counted = Assert.Single(results.Questions, q => q.QuestionId == drinks.Id);
        Assert.Equal(2, counted.AnswerCount);
        Assert.Equal([("Kahve", 1), ("Çay", 2), ("Su", 0)], counted.Counts.Select(c => (c.Option, c.Count)));

        var export = await hr.GetAsync($"/api/hr/surveys/{survey.Id}/results/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        var bytes = await export.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var csv = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("Sıra;Soru;Tür;Cevaplayan;Ortalama;Seçenek / puan;Sayı;Metin cevabı", csv);
        Assert.Contains("1;Hangi içecekler olsun?;Çoklu seçim;2;;Çay;2;", csv);
        Assert.Contains(";'=1+1 çay demli olsun", csv);
        Assert.DoesNotContain(s.Staff.Email!, csv);
    }

    [Fact]
    public async Task Multi_choice_answers_and_questions_are_validated()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);

        var survey = await PostAsync(hr, Surveys, new JsonObject { ["title"] = "Doğrulama" });
        var oneOption = await hr.PostAsJsonAsync(Questions, new RecordWriteRequest(Question(survey.Id, "Tek seçenek", "multiChoice", "Yalnız", required: true)));
        Assert.True((await ValidationProblemAsync(oneOption)).Errors.ContainsKey("options"));
        var question = await QuestionAsync(hr, survey.Id, "Hangileri?", "multiChoice", "A\nB\nC", required: true);
        await ActAsync(hr, survey.Id, "open");

        foreach (string[] choices in new[] { new[] { "A", "Z" }, ["A", "A"], [] })
        {
            var rejected = await PostResponseAsync(staff, survey.Id, new SurveyAnswerItem(question.Id, null, null, null, choices));
            Assert.True((await ValidationProblemAsync(rejected)).Errors.ContainsKey($"answers.{question.Id}"));
        }

        Assert.Equal(0, Number(await GetAsync(hr, $"{Surveys}/{survey.Id}"), "responseCount"));
    }

    [Fact]
    public async Task Result_export_needs_survey_management_stays_in_the_tenant_and_respects_the_anonymous_threshold()
    {
        var a = await SetUpAsync();
        var b = await SetUpAsync();
        using var hr = await LoginAsync(a.Hr);
        using var hrB = await LoginAsync(b.Hr);
        using var staff = await LoginAsync(a.Staff);
        using var colleague = await LoginAsync(a.Colleague);
        using var outsider = await LoginAsync(a.Outsider);

        var survey = await PostAsync(hr, Surveys, new JsonObject { ["title"] = "Gizli nabız", ["isAnonymous"] = true });
        var question = await QuestionAsync(hr, survey.Id, "Puanınız", "rating", null, required: true);
        await ActAsync(hr, survey.Id, "open");
        await SubmitAsync(staff, survey.Id, new SurveyAnswerItem(question.Id, 4, null, null));
        await SubmitAsync(colleague, survey.Id, new SurveyAnswerItem(question.Id, 2, null, null));

        var url = $"/api/hr/surveys/{survey.Id}/results/export";
        Assert.Equal(HttpStatusCode.Conflict, (await hr.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hrB.GetAsync(url)).StatusCode);
    }

    /// <summary>Bölüm 23c: kişiye hedeflenmiş anketi yalnızca o kişi görür ve yanıtlar; departman hedefiyle birlikte birleşim kuralı geçerlidir.</summary>
    [Fact]
    public async Task Surveys_targeted_at_people_are_only_available_to_them()
    {
        var s = await SetUpAsync();
        using var hr = await LoginAsync(s.Hr);
        using var staff = await LoginAsync(s.Staff);
        using var colleague = await LoginAsync(s.Colleague);

        var survey = await PostAsync(hr, Surveys, new JsonObject
        {
            ["title"] = "Yalnız Selin ve yönetim",
            ["audienceBusinessUnitIds"] = new JsonArray(JsonValue.Create(s.Management.ToString())),
            ["audienceUserIds"] = new JsonArray(JsonValue.Create(s.Colleague.Id.ToString())),
        });
        var question = await QuestionAsync(hr, survey.Id, "Puan", "rating", null, required: true);
        await ActAsync(hr, survey.Id, "open");

        Assert.Empty(await ListAsync(staff, Surveys, null));
        Assert.Empty(await ListAsync(staff, Questions, $"surveyId={survey.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, (await PostResponseAsync(staff, survey.Id, new SurveyAnswerItem(question.Id, 3, null, null))).StatusCode);
        Assert.Equal([survey.Id], (await ListAsync(colleague, Surveys, null)).Select(x => x.Id));
        await SubmitAsync(colleague, survey.Id, new SurveyAnswerItem(question.Id, 3, null, null));
        Assert.Equal(1, Number(await GetAsync(hr, $"{Surveys}/{survey.Id}"), "responseCount"));
    }

    // --- yardımcılar ---

    private sealed record Setup(Guid TenantId, ModulixUser Hr, ModulixUser Staff, ModulixUser Colleague, ModulixUser Outsider, Guid Finance, Guid Management);

    /// <summary>
    /// İK (yönetici rolü, çalışan kartı yok, kök departmanda), Core'da Finans departmanındaki iki çalışan (yalnızca yanıtlama izni) ve izinsiz kullanıcı.
    /// </summary>
    private async Task<Setup> SetUpAsync()
    {
        var tenant = await factory.CreateTenantAsync();
        await factory.SetModuleAsync(tenant.Id, "hr", enabled: true);
        var hrUser = await factory.CreateUserAsync(tenant);
        var staffUser = await factory.CreateUserAsync(tenant);
        var colleagueUser = await factory.CreateUserAsync(tenant);
        var outsider = await factory.CreateUserAsync(tenant);
        await MakeAdministratorAsync(tenant.Id, hrUser);
        await factory.CreateRoleAsync(tenant.Id, [SurveysRespond], Scope.Own, staffUser, colleagueUser);

        using var hr = await LoginAsync(hrUser);
        var finance = await HrDepartments.CreateAsync(hr, "Finans");
        var management = await HrDepartments.CreateAsync(hr, "Yönetim");
        foreach (var (user, name) in new[] { (staffUser, "Sinan"), (colleagueUser, "Selin") })
        {
            await HrDepartments.MoveAsync(hr, factory, user.Id, tenant.Id, finance);
            var person = await PostAsync(hr, People, new JsonObject { ["firstName"] = name, ["lastName"] = "Yanıtlar" });
            await PostAsync(hr, Employees, new JsonObject
            {
                ["personId"] = person.Id.ToString(), ["hireDate"] = "2020-01-01", ["userId"] = user.Id.ToString(),
            });
        }

        return new Setup(tenant.Id, hrUser, staffUser, colleagueUser, outsider, finance, management);
    }

    private static JsonObject Question(Guid surveyId, string text, string type, string? options, bool required) => new()
    {
        ["surveyId"] = surveyId.ToString(), ["text"] = text, ["questionType"] = type, ["options"] = options, ["isRequired"] = required,
    };

    private static Task<RecordResponse> QuestionAsync(HttpClient client, Guid surveyId, string text, string type, string? options, bool required) =>
        PostAsync(client, Questions, Question(surveyId, text, type, options, required));

    private static async Task<HttpResponseMessage> PostActionAsync(HttpClient client, Guid surveyId, string action)
    {
        var current = await client.GetAsync($"{Surveys}/{surveyId}");
        var version = current.IsSuccessStatusCode ? (await current.Content.ReadFromJsonAsync<RecordResponse>())!.Version : 1;
        return await client.PostAsJsonAsync($"/api/hr/surveys/{surveyId}/actions", new RunHrActionRequest(action, version, null));
    }

    private static async Task<RecordResponse> ActAsync(HttpClient client, Guid surveyId, string action)
    {
        var response = await PostActionAsync(client, surveyId, action);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static Task<HttpResponseMessage> PostResponseAsync(HttpClient client, Guid surveyId, params SurveyAnswerItem[] answers) =>
        client.PostAsJsonAsync($"/api/hr/surveys/{surveyId}/responses", new SubmitSurveyRequest(answers));

    private static async Task<RecordResponse> SubmitAsync(HttpClient client, Guid surveyId, params SurveyAnswerItem[] answers)
    {
        var response = await PostResponseAsync(client, surveyId, answers);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static string? Text(RecordResponse record, string field) => record.Values[field]?.GetValue<string>();

    private static int Number(RecordResponse record, string field) => record.Values[field]!.GetValue<int>();

    private static bool Flag(RecordResponse record, string field) => record.Values[field]!.GetValue<bool>();

    private async Task MakeAdministratorAsync(Guid tenantId, ModulixUser user)
    {
        using var scope = factory.OpenScope(tenantId);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();
        var system = await roles.EnsureSystemRoleAsync(CancellationToken.None);
        Assert.True(await roles.SetMembershipRolesAsync(await factory.GetMembershipIdAsync(user.Id, tenantId), [system.Id], CancellationToken.None));
    }

    private async Task<HttpClient> LoginAsync(ModulixUser user)
    {
        var client = factory.CreateBrowserClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email!, ModulixApiFactory.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<RecordResponse> GetAsync(HttpClient client, string url) => (await client.GetFromJsonAsync<RecordResponse>(url))!;

    private static async Task<IReadOnlyList<RecordResponse>> ListAsync(HttpClient client, string url, string? query) =>
        (await client.GetFromJsonAsync<RecordPageResponse>(query is null ? url : $"{url}?{query}"))!.Items;

    private static async Task<RecordResponse> PostAsync(HttpClient client, string url, JsonObject values)
    {
        var response = await client.PostAsJsonAsync(url, new RecordWriteRequest(values));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RecordResponse>())!;
    }

    private static async Task<ValidationProblemDetails> ValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
    }
}
