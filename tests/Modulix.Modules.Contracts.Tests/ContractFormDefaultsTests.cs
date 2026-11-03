namespace Modulix.Modules.Contracts.Tests;

/// <summary>Arayüz kuralı (CLAUDE.md → Arayüz tasarım kuralları): her kayıt formu sekmelidir; varsayılan düzenler çok sekmelidir (veritabanı gerektirmez).</summary>
public class ContractFormDefaultsTests
{
    [Fact]
    public void Every_default_form_has_several_tabs_and_each_tab_holds_a_section()
    {
        Assert.NotEmpty(ContractsForms.All);
        foreach (var form in ContractsForms.All)
        {
            Assert.True(form.Tabs.Count >= 2, $"{form.EntityKey}: varsayılan form sekmeli olmalı (en az 2 sekme).");
            Assert.Equal("genel", form.Tabs[0].Key);
            Assert.Equal(form.Tabs.Count, form.Tabs.Select(t => t.Key).Distinct().Count());
            foreach (var tab in form.Tabs)
            {
                Assert.True(form.Sections.Any(s => s.Tab == tab.Key), $"{form.EntityKey}: '{tab.Key}' sekmesinde bölüm yok.");
            }
        }
    }

    [Fact]
    public void Required_and_fixed_identity_fields_stay_on_the_first_tab()
    {
        // Zorunlu kimlik alanları ilk sekmede olmalı: kullanıcı kayda başlarken eksik alanı gizli sekmede aramaz.
        foreach (var form in ContractsForms.All)
        {
            var firstTabSections = form.Sections.Where(s => s.Tab == form.Tabs[0].Key).Select(s => s.Key).ToHashSet();
            foreach (var field in form.Fields.Where(f => f is { Required: true, Fixed: true }))
            {
                Assert.True(firstTabSections.Contains(field.Section), $"{form.EntityKey}: zorunlu '{field.Key}' alanı ilk sekmede değil.");
            }
        }
    }
}
