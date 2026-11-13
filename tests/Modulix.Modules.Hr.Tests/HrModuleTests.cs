using Modulix.Core.Contracts.Automation;
using Modulix.Core.Contracts.Credentials;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Modules;
using Modulix.Modules.Hr.Domain;
using Modulix.Modules.Hr.Records;
using Modulix.Shared.Credentials;
using Modulix.Shared.Hr;

namespace Modulix.Modules.Hr.Tests;

/// <summary>
/// ADR-0042 dilim 1: İK modülü Core ve başka modül olmadan, yalnızca sözleşmeyle kaydolur; özlük alanları hassastır; varsayılan form
/// sekmelidir; T.C. kimlik no ve IBAN denetimi doğrudur; arayüzün kullandığı anahtarlar sunucudaki modülle aynıdır (veritabanı gerektirmez).
/// </summary>
public class HrModuleTests
{
    private static readonly IModule Module = new HrModule();

    [Fact]
    public void Catalog_with_only_core_and_hr_registers_the_module_and_its_employee_rules()
    {
        var catalog = ModuleCatalog.Create(new HrModule());

        Assert.Equal(["core", "hr"], catalog.Modules.Select(m => m.Key));
        var employee = catalog.FindAutomationEntity(HrModule.Entities.Employee)!.Value;
        Assert.Equal(HrModule.ModuleKey, employee.ModuleKey);
        Assert.Equal(HrModule.Permissions.EmployeesRead, employee.Entity.ReadPermission);
        Assert.Equal(Employee.Statuses.All, catalog.FindAutomationField("hr.employee.status")!.Options.Select(o => o.Key));
        Assert.Equal(AutomationFieldType.Date, catalog.FindAutomationField("hr.employee.hireDate")!.Type);
    }

    [Fact]
    public void Permissions_and_role_templates_belong_to_the_module_and_staff_cannot_see_personal_data()
    {
        var own = Module.Permissions.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        Assert.All(own, key => Assert.StartsWith(HrModule.ModuleKey + ".", key, StringComparison.Ordinal));
        Assert.All(Module.RoleTemplates, template =>
        {
            Assert.StartsWith(HrModule.ModuleKey + ".", template.Key, StringComparison.Ordinal);
            Assert.All(template.Permissions, permission => Assert.Contains(permission, own));
        });

        var staff = Assert.Single(Module.RoleTemplates, t => t.Key == "hr.staff");
        Assert.Equal([HrModule.Permissions.EmployeesRead], staff.Permissions);
    }

    [Fact]
    public void Personal_fields_are_sensitive_and_audited_with_their_own_permission()
    {
        var sensitive = Assert.Single(Module.SensitiveFields);
        Assert.Equal(HrModule.Entities.Employee, sensitive.EntityKey);
        Assert.Equal(HrModule.Permissions.EmployeesPersonal, sensitive.Permission);
        Assert.Equal(HrModule.PersonalFields, sensitive.Fields);
        Assert.Contains("nationalId", sensitive.Fields);
        Assert.Contains("iban", sensitive.Fields);

        var audited = Assert.Single(Module.AuditedEntities);
        Assert.All(sensitive.Fields, field => Assert.True(audited.Fields.ContainsKey(field), field));
    }

    [Fact]
    public void Default_form_has_several_tabs_keeps_identity_first_and_groups_personal_data_on_its_own_tab()
    {
        var form = Assert.Single(HrForms.All);
        Assert.Equal(HrModule.Entities.Employee, form.EntityKey);
        Assert.True(form.Tabs.Count >= 2);
        Assert.Equal("genel", form.Tabs[0].Key);
        Assert.All(form.Tabs, tab => Assert.Contains(form.Sections, s => s.Tab == tab.Key));
        Assert.Contains(form.Sections, s => s.Key == FormDefinition.ExtrasSection);
        var firstTab = form.Sections.Where(s => s.Tab == form.Tabs[0].Key).Select(s => s.Key).ToHashSet();
        Assert.All(form.Fields.Where(f => f is { Required: true, Fixed: true }), f => Assert.Contains(f.Section, firstTab));
        Assert.All(form.Fields, f => Assert.Contains(f.Span, FormSpans.Allowed));
        Assert.Contains(form.EntityKey, Module.CustomizableEntities.Select(c => c.EntityKey));

        // Özlük alanları ayrı sekmede: izni olmayan kullanıcıda bu sekme boş kalır, diğerleri etkilenmez.
        var personalSections = form.Fields.Where(f => HrModule.PersonalFields.Contains(f.Key)).Select(f => f.Section).ToHashSet();
        Assert.Equal(HrModule.PersonalFields.Count, form.Fields.Count(f => HrModule.PersonalFields.Contains(f.Key)));
        Assert.All(form.Sections.Where(s => personalSections.Contains(s.Key)), s => Assert.Equal("ozluk", s.Tab));
    }

    [Fact]
    public void Lookups_point_at_people_employees_or_members_and_employees_are_credential_owners()
    {
        Assert.All(Module.Lookups, lookup => Assert.StartsWith(HrModule.ModuleKey + ".", lookup.Key, StringComparison.Ordinal));
        Assert.Equal(SharedEntities.Person, Module.Lookups.Single(l => l.Key == HrLookupDefinitions.EmployeePerson).TargetEntityKey);
        Assert.Equal(HrModule.Entities.Employee, Module.Lookups.Single(l => l.Key == HrLookupDefinitions.EmployeeManager).TargetEntityKey);
        Assert.True(Module.Lookups.Single(l => l.Key == HrLookupDefinitions.EmployeeUser).IsMember);
        Assert.Equal(["personId"], Module.SharedRecordReferences.Select(r => r.Field));

        var owner = Assert.Single(Module.CredentialOwners);
        Assert.Equal(CredentialCategories.Employee, owner.Category);
        Assert.Contains(CredentialCategories.Employee, CredentialCategories.All);
    }

    [Fact]
    public void Department_list_has_a_value_for_every_default_and_no_categories()
    {
        var list = Assert.Single(Module.ChoiceLists);
        Assert.Equal(HrModule.Lists.Department, list.Key);
        Assert.False(list.Required);
        Assert.Empty(list.Categories);
        Assert.NotEmpty(list.Values);
    }

    [Theory]
    [InlineData("10000000146", true)]
    [InlineData("12345678950", true)]
    [InlineData("10000000147", false)] // son hane yanlış
    [InlineData("01234567890", false)] // ilk hane 0 olamaz
    [InlineData("1000000014", false)] // 10 hane
    [InlineData("1000000014A", false)]
    public void National_id_check_digits_are_validated(string value, bool valid) =>
        Assert.Equal(valid, EmployeeRecordStore.IsValidNationalId(value));

    [Theory]
    [InlineData("TR330006100519786457841326", true)]
    [InlineData("TR330006100519786457841327", false)] // denetim tutmuyor
    [InlineData("DE89370400440532013000", false)] // Türkiye dışı
    [InlineData("TR33000610051978645784132", false)] // 25 karakter
    public void Iban_is_validated_with_mod_97(string value, bool valid) =>
        Assert.Equal(valid, EmployeeRecordStore.IsValidIban(value));

    [Fact]
    public void Client_access_constants_match_the_server_module()
    {
        Assert.Equal(HrModule.ModuleKey, HrAccess.ModuleKey);
        Assert.Equal(HrModule.Permissions.EmployeesRead, HrAccess.EmployeesRead);
        Assert.Equal(HrModule.Permissions.EmployeesWrite, HrAccess.EmployeesWrite);
        Assert.Equal(HrModule.Permissions.EmployeesPersonal, HrAccess.EmployeesPersonal);
        Assert.Equal(HrModule.Entities.Employee, HrAccess.EmployeeEntityKey);
        Assert.Equal(HrModule.Lists.Department, HrAccess.DepartmentList);
        Assert.Equal(HrLookupDefinitions.EmployeePerson, HrAccess.EmployeePersonLookup);
        Assert.Equal(HrLookupDefinitions.EmployeeManager, HrAccess.EmployeeManagerLookup);
        Assert.Equal(HrLookupDefinitions.EmployeeUser, HrAccess.EmployeeUserLookup);
        Assert.Equal(Employee.Statuses.All, HrAccess.Statuses.Select(s => s.Key));
        Assert.Equal(Employee.EmploymentTypes.All, HrAccess.EmploymentTypes.Select(s => s.Key));
        Assert.Equal(Employee.Statuses.Terminated, HrAccess.Terminated);
        Assert.Equal(HrModule.PersonalFields, HrAccess.PersonalFields);
        Assert.Equal(CredentialCategories.Employee, CredentialAccess.Employee);
    }
}
