using System.Text.RegularExpressions;

namespace Modulix.ArchitectureTests;

/// <summary>
/// Olay aboneleri (ADR-0007): modül aboneliğini <c>EventSubscription.For&lt;Abone&gt;(...)</c> ile bildirir. Abone sınıfı
/// aynı projededir, <c>IEventSubscriber</c> uygular ve modülün <c>ConfigureServices</c>'inde scoped kaydedilir. Abone
/// kayıtları yalnızca kayıt boru hattından değiştirir: veritabanına doğrudan erişen servisleri (<c>IModuleData</c>,
/// <c>DbContext</c>) kullanmaz; yoksa değişikliği izin, hassas alan, denetim kaydı ve olay adımlarını atlar.
/// </summary>
public partial class EventSubscriberRulesTests
{
    private static readonly string[] ForbiddenServices = ["IModuleData", "DbContext"];

    [Fact]
    public void Subscribers_are_declared_registered_and_write_through_the_pipeline()
    {
        var problems = new List<string>();
        foreach (var csproj in RepositoryLayout.SourceProjects())
        {
            var sources = csproj.Directory!
                .EnumerateFiles("*.cs", SearchOption.AllDirectories)
                .Where(f => !IsBuildOutput(f))
                .Select(f => File.ReadAllText(f.FullName))
                .ToList();

            var subscribers = sources
                .SelectMany(text => SubscriptionPattern().Matches(text).Select(m => m.Groups["subscriber"].Value))
                .Distinct()
                .ToList();

            foreach (var subscriber in subscribers)
            {
                var name = $"{RepositoryLayout.ProjectName(csproj)}: {subscriber}";
                var declaration = sources.FirstOrDefault(text => Regex.IsMatch(text, $@"\bclass\s+{subscriber}\b[^{{]*\bIEventSubscriber\b"));
                if (declaration is null)
                {
                    problems.Add($"{name} aynı projede IEventSubscriber uygulayan bir sınıf olarak tanımlı değil.");
                    continue;
                }

                if (!sources.Any(text => text.Contains($"AddScoped<{subscriber}>()", StringComparison.Ordinal)))
                {
                    problems.Add($"{name} modülün ConfigureServices'inde AddScoped<{subscriber}>() ile kaydedilmemiş.");
                }

                var body = ClassBody(declaration, subscriber);
                foreach (var forbidden in ForbiddenServices.Where(f => body.Contains(f, StringComparison.Ordinal)))
                {
                    problems.Add($"{name} {forbidden} kullanıyor; abone kayıtları kayıt boru hattından (IRecordPipeline) değiştirir.");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // Sınıfın bildiriminden dosyanın sonuna kadar; aynı dosyadaki sonraki sınıflar da taranır (daha sıkı, yanlış negatif yok).
    private static string ClassBody(string text, string subscriber) =>
        text[Regex.Match(text, $@"\bclass\s+{subscriber}\b").Index..];

    private static bool IsBuildOutput(FileInfo file) =>
        file.FullName.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");

    [GeneratedRegex(@"EventSubscription\.For<(?<subscriber>\w+)>")]
    private static partial Regex SubscriptionPattern();
}
