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
        Assert.Contains(HrModule.Permissions.EmployeesRead, staff.Permissions);
        Assert.DoesNotContain(HrModule.Permissions.EmployeesPersonal, staff.Permissions);
        Assert.DoesNotContain(HrModule.Permissions.EmployeesWrite, staff.Permissions);
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

        var audited = Assert.Single(Module.AuditedEntities, a => a.EntityKey == HrModule.Entities.Employee);
        Assert.All(sensitive.Fields, field => Assert.True(audited.Fields.ContainsKey(field), field));
    }

    [Fact]
    public void Default_form_has_several_tabs_keeps_identity_first_and_groups_personal_data_on_its_own_tab()
    {
        Assert.Equal(
            [HrModule.Entities.Employee, HrModule.Entities.LeaveRequest, HrModule.Entities.LeaveBalance, HrModule.Entities.ExpenseClaim, HrModule.Entities.ExpenseLine],
            HrForms.All.Select(f => f.EntityKey));
        foreach (var definition in HrForms.All)
        {
            Assert.True(definition.Tabs.Count >= 2, definition.EntityKey);
            Assert.Equal("genel", definition.Tabs[0].Key);
            Assert.All(definition.Tabs, tab => Assert.Contains(definition.Sections, s => s.Tab == tab.Key));
            Assert.Contains(definition.Sections, s => s.Key == FormDefinition.ExtrasSection);
            var firstTab = definition.Sections.Where(s => s.Tab == definition.Tabs[0].Key).Select(s => s.Key).ToHashSet();
            Assert.All(definition.Fields.Where(f => f is { Required: true, Fixed: true }), f => Assert.Contains(f.Section, firstTab));
            Assert.All(definition.Fields, f => Assert.Contains(f.Span, FormSpans.Allowed));
            Assert.Contains(definition.EntityKey, Module.CustomizableEntities.Select(c => c.EntityKey));
        }

        var form = HrForms.All[0];

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
        Assert.Equal(HrModule.Entities.Employee, Module.Lookups.Single(l => l.Key == HrLookupDefinitions.LeaveRequestEmployee).TargetEntityKey);
        Assert.Equal(HrModule.Entities.Employee, Module.Lookups.Single(l => l.Key == HrLookupDefinitions.LeaveBalanceEmployee).TargetEntityKey);
        Assert.True(Module.Lookups.Single(l => l.Key == HrLookupDefinitions.EmployeeUser).IsMember);
        Assert.Equal(["personId"], Module.SharedRecordReferences.Select(r => r.Field));

        var owner = Assert.Single(Module.CredentialOwners);
        Assert.Equal(CredentialCategories.Employee, owner.Category);
        Assert.Contains(CredentialCategories.Employee, CredentialCategories.All);
    }

    [Fact]
    public void Department_list_has_a_value_for_every_default_and_no_categories()
    {
        var list = Assert.Single(Module.ChoiceLists, l => l.Key == HrModule.Lists.Department);
        Assert.False(list.Required);
        Assert.Empty(list.Categories);
        Assert.NotEmpty(list.Values);
    }

    [Fact]
    public void Leave_types_are_categorised_and_only_the_annual_category_is_the_default()
    {
        var list = Assert.Single(Module.ChoiceLists, l => l.Key == HrModule.Lists.LeaveType);
        Assert.Equal(
            [HrModule.LeaveCategories.Annual, HrModule.LeaveCategories.Excuse, HrModule.LeaveCategories.Sick, HrModule.LeaveCategories.Unpaid],
            list.Categories.Select(c => c.Key));
        Assert.All(list.Values, v => Assert.Contains(v.Category, list.Categories.Select(c => c.Key)));
        Assert.All(list.Categories, c => Assert.Contains(list.Values, v => v.Category == c.Key)); // her kategoride en az bir değer
        Assert.Equal(HrModule.LeaveCategories.Annual, list.Values.Single(v => v.Key == list.Default).Category);
    }

    [Fact]
    public void Expense_types_are_categorised_and_finance_only_pays()
    {
        var list = Assert.Single(Module.ChoiceLists, l => l.Key == HrModule.Lists.ExpenseType);
        Assert.Equal(
            [HrModule.ExpenseCategories.Transport, HrModule.ExpenseCategories.Accommodation, HrModule.ExpenseCategories.Meal, HrModule.ExpenseCategories.Other],
            list.Categories.Select(c => c.Key));
        Assert.All(list.Categories, c => Assert.Contains(list.Values, v => v.Category == c.Key));
        Assert.Contains(list.Values, v => v.Key == list.Default);

        var finance = Module.RoleTemplates.Single(t => t.Key == "hr.finance");
        Assert.Contains(HrModule.Permissions.ExpensesPay, finance.Permissions);
        Assert.DoesNotContain(HrModule.Permissions.ExpensesApprove, finance.Permissions);
        Assert.Equal(Core.Contracts.Authorization.RecordScope.All, finance.Scope);
        Assert.Contains(HrModule.Permissions.ExpensesRequest, Module.RoleTemplates.Single(t => t.Key == "hr.staff").Permissions);
        Assert.Contains(HrModule.Permissions.ExpensesApprove, Module.RoleTemplates.Single(t => t.Key == "hr.manager").Permissions);
        Assert.DoesNotContain(HrModule.Permissions.ExpensesPay, Module.RoleTemplates.Single(t => t.Key == "hr.specialist").Permissions);
        Assert.Equal(HrModule.Entities.Employee, Module.Lookups.Single(l => l.Key == HrLookupDefinitions.ExpenseClaimEmployee).TargetEntityKey);
    }

    [Fact]
    public void Staff_and_managers_see_their_own_records_and_only_the_specialist_manages_leave()
    {
        var staff = Module.RoleTemplates.Single(t => t.Key == "hr.staff");
        var manager = Module.RoleTemplates.Single(t => t.Key == "hr.manager");
        var specialist = Module.RoleTemplates.Single(t => t.Key == "hr.specialist");

        Assert.Equal(Core.Contracts.Authorization.RecordScope.Own, staff.Scope);
        Assert.Equal(Core.Contracts.Authorization.RecordScope.Own, manager.Scope);
        Assert.Contains(HrModule.Permissions.LeavesRequest, staff.Permissions);
        Assert.DoesNotContain(HrModule.Permissions.LeavesApprove, staff.Permissions);
        Assert.Contains(HrModule.Permissions.LeavesApprove, manager.Permissions);
        Assert.DoesNotContain(HrModule.Permissions.LeavesManage, manager.Permissions);
        Assert.Contains(HrModule.Permissions.LeavesManage, specialist.Permissions);
    }

    [Theory]
    [InlineData("2026-11-16", "2026-11-20", false, 5)] // pazartesi–cuma
    [InlineData("2026-11-13", "2026-11-16", false, 2)] // cuma–pazartesi: hafta sonu düşülür
    [InlineData("2026-11-14", "2026-11-15", false, 0)] // yalnızca hafta sonu
    [InlineData("2026-11-17", "2026-11-17", true, 0.5)] // yarım gün
    [InlineData("2026-10-28", "2026-10-30", false, 1.5)] // 28 yarım gün tatil, 29 tam gün tatil, 30 iş günü
    [InlineData("2026-10-29", "2026-10-29", true, 0)] // tatil gününde yarım gün sayılmaz
    [InlineData("2026-11-20", "2026-11-16", false, 0)] // ters aralık
    public void Leave_days_skip_weekends_and_holidays(string start, string end, bool halfDay, double expected)
    {
        var holidays = new Dictionary<DateOnly, bool> { [new DateOnly(2026, 10, 28)] = true, [new DateOnly(2026, 10, 29)] = false };

        Assert.Equal((decimal)expected, LeaveCalendar.WorkingDays(DateOnly.Parse(start), DateOnly.Parse(end), halfDay, holidays));
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
        Assert.Equal(HrModule.Permissions.LeavesRead, HrAccess.LeavesRead);
        Assert.Equal(HrModule.Permissions.LeavesRequest, HrAccess.LeavesRequest);
        Assert.Equal(HrModule.Permissions.LeavesApprove, HrAccess.LeavesApprove);
        Assert.Equal(HrModule.Permissions.LeavesManage, HrAccess.LeavesManage);
        Assert.Equal(HrModule.Entities.LeaveRequest, HrAccess.LeaveRequestEntityKey);
        Assert.Equal(HrModule.Entities.LeaveBalance, HrAccess.LeaveBalanceEntityKey);
        Assert.Equal(HrModule.Entities.Holiday, HrAccess.HolidayEntityKey);
        Assert.Equal(HrModule.Lists.LeaveType, HrAccess.LeaveTypeList);
        Assert.Equal(HrLookupDefinitions.LeaveRequestEmployee, HrAccess.LeaveRequestEmployeeLookup);
        Assert.Equal(HrLookupDefinitions.LeaveBalanceEmployee, HrAccess.LeaveBalanceEmployeeLookup);
        Assert.Equal(HrModule.LeaveCategories.Annual, HrAccess.AnnualCategory);
        Assert.Equal(LeaveRequest.Statuses.All, HrAccess.LeaveStatuses.Select(s => s.Key));
        Assert.Equal(HrModule.Permissions.ExpensesRead, HrAccess.ExpensesRead);
        Assert.Equal(HrModule.Permissions.ExpensesRequest, HrAccess.ExpensesRequest);
        Assert.Equal(HrModule.Permissions.ExpensesApprove, HrAccess.ExpensesApprove);
        Assert.Equal(HrModule.Permissions.ExpensesManage, HrAccess.ExpensesManage);
        Assert.Equal(HrModule.Permissions.ExpensesPay, HrAccess.ExpensesPay);
        Assert.Equal(HrModule.Entities.ExpenseClaim, HrAccess.ExpenseClaimEntityKey);
        Assert.Equal(HrModule.Entities.ExpenseLine, HrAccess.ExpenseLineEntityKey);
        Assert.Equal(HrModule.Lists.ExpenseType, HrAccess.ExpenseTypeList);
        Assert.Equal(HrLookupDefinitions.ExpenseClaimEmployee, HrAccess.ExpenseClaimEmployeeLookup);
        Assert.Equal(ExpenseClaim.Statuses.All, HrAccess.ExpenseStatuses.Select(s => s.Key));
    }
}
