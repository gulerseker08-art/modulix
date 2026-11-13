using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Hr.Domain;

namespace Modulix.Modules.Hr.Persistence;

// İK tabloları "hr" şemasındadır ve tenant verisidir: sorgu filtresi otomatik uygulanır, migration'da her tablo Row-Level
// Security politikasına eklenir (TenantIsolationSql.AddTable). Benzersizlik kuralları TenantId ile birlikte tanımlanır.

internal static class HrTables
{
    public const string Schema = HrModule.ModuleKey;

    public static void ConfigureCommon<TEntity>(EntityTypeBuilder<TEntity> builder, string table)
        where TEntity : HrEntity
    {
        builder.ToTable(table, Schema);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();
        builder.Property(e => e.Version).IsConcurrencyToken();
        builder.Property(e => e.CustomFields);
        builder.HasAlternateKey(e => new { e.TenantId, e.Id });
    }
}

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        HrTables.ConfigureCommon(builder, "Employees");
        builder.Property(e => e.EmployeeNumber).HasMaxLength(Employee.EmployeeNumberMaxLength);
        builder.Property(e => e.JobTitle).HasMaxLength(Employee.JobTitleMaxLength);
        builder.Property(e => e.DepartmentKey).HasMaxLength(100);
        builder.Property(e => e.EmploymentType).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(e => e.WorkLocation).HasMaxLength(Employee.WorkLocationMaxLength);
        builder.Property(e => e.Status).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(e => e.NationalId).HasMaxLength(Employee.NationalIdLength).IsFixedLength().IsUnicode(false);
        builder.Property(e => e.HomeAddress).HasMaxLength(Employee.AddressMaxLength);
        builder.Property(e => e.EmergencyContactName).HasMaxLength(Employee.EmergencyNameMaxLength);
        builder.Property(e => e.EmergencyContactPhone).HasMaxLength(Employee.PhoneMaxLength);
        builder.Property(e => e.Iban).HasMaxLength(Employee.IbanLength).IsUnicode(false);
        builder.Property(e => e.Notes).HasMaxLength(Employee.NotesMaxLength);

        builder.HasIndex(e => new { e.TenantId, e.PersonId }).IsUnique().HasDatabaseName("UX_Employees_Person");
        builder.HasIndex(e => new { e.TenantId, e.EmployeeNumber }).IsUnique().HasFilter("[EmployeeNumber] IS NOT NULL").HasDatabaseName("UX_Employees_Number");
        builder.HasIndex(e => new { e.TenantId, e.UserId }).IsUnique().HasFilter("[UserId] IS NOT NULL").HasDatabaseName("UX_Employees_User");
        builder.HasIndex(e => new { e.TenantId, e.NationalId }).IsUnique().HasFilter("[NationalId] IS NOT NULL").HasDatabaseName("UX_Employees_NationalId");
        builder.HasIndex(e => new { e.TenantId, e.Status }).HasDatabaseName("IX_Employees_Status");

        // Yönetici bağı (TenantId, Id) üzerinden: başka tenant'ın çalışanına veritabanı düzeyinde de bağlanılamaz; kart silinmediği için kısıt korur.
        builder.HasOne<Employee>().WithMany().HasForeignKey(e => new { e.TenantId, e.ManagerEmployeeId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.TenantId, e.ManagerEmployeeId }).HasDatabaseName("IX_Employees_Manager");
    }
}

internal sealed class LeaveBalanceConfiguration : IEntityTypeConfiguration<LeaveBalance>
{
    public void Configure(EntityTypeBuilder<LeaveBalance> builder)
    {
        HrTables.ConfigureCommon(builder, "LeaveBalances");
        builder.Property(b => b.EntitledDays).HasPrecision(5, 1);
        builder.Property(b => b.CarriedOverDays).HasPrecision(5, 1);
        builder.Property(b => b.UsedDays).HasPrecision(5, 1);
        builder.Property(b => b.Notes).HasMaxLength(LeaveBalance.NotesMaxLength);
        builder.Ignore(b => b.RemainingDays);

        builder.HasOne<Employee>().WithMany().HasForeignKey(b => new { b.TenantId, b.EmployeeId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(b => new { b.TenantId, b.EmployeeId, b.Year }).IsUnique().HasDatabaseName("UX_LeaveBalances_EmployeeYear");
        builder.HasIndex(b => new { b.TenantId, b.OwnerId }).HasDatabaseName("IX_LeaveBalances_Owner");
    }
}

internal sealed class LeaveRequestConfiguration : IEntityTypeConfiguration<LeaveRequest>
{
    public void Configure(EntityTypeBuilder<LeaveRequest> builder)
    {
        HrTables.ConfigureCommon(builder, "LeaveRequests");
        builder.Property(r => r.LeaveTypeKey).HasMaxLength(100).IsRequired();
        builder.Property(r => r.LeaveCategory).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(r => r.Days).HasPrecision(5, 1);
        builder.Property(r => r.Description).HasMaxLength(LeaveRequest.TextMaxLength);
        builder.Property(r => r.Status).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(r => r.DecisionReason).HasMaxLength(LeaveRequest.ReasonMaxLength);

        builder.HasOne<Employee>().WithMany().HasForeignKey(r => new { r.TenantId, r.EmployeeId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.TenantId, r.EmployeeId, r.StartDate }).HasDatabaseName("IX_LeaveRequests_Employee");
        builder.HasIndex(r => new { r.TenantId, r.Status, r.StartDate }).HasDatabaseName("IX_LeaveRequests_Status");
        builder.HasIndex(r => new { r.TenantId, r.OwnerId }).HasDatabaseName("IX_LeaveRequests_Owner");
        builder.HasIndex(r => new { r.TenantId, r.ApproverUserId }).HasDatabaseName("IX_LeaveRequests_Approver");
    }
}

internal sealed class HolidayConfiguration : IEntityTypeConfiguration<Holiday>
{
    public void Configure(EntityTypeBuilder<Holiday> builder)
    {
        HrTables.ConfigureCommon(builder, "Holidays");
        builder.Property(h => h.Name).HasMaxLength(Holiday.NameMaxLength).IsRequired();
        builder.HasIndex(h => new { h.TenantId, h.Date }).IsUnique().HasDatabaseName("UX_Holidays_Date");
    }
}

internal sealed class ExpenseClaimConfiguration : IEntityTypeConfiguration<ExpenseClaim>
{
    public void Configure(EntityTypeBuilder<ExpenseClaim> builder)
    {
        HrTables.ConfigureCommon(builder, "ExpenseClaims");
        builder.Property(c => c.Title).HasMaxLength(ExpenseClaim.TitleMaxLength).IsRequired();
        builder.Property(c => c.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(ExpenseClaim.TextMaxLength);
        builder.Property(c => c.TotalAmount).HasPrecision(18, 2);
        builder.Property(c => c.Status).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(c => c.DecisionReason).HasMaxLength(ExpenseClaim.ReasonMaxLength);
        builder.Property(c => c.PaymentReference).HasMaxLength(ExpenseClaim.ReasonMaxLength);

        builder.HasOne<Employee>().WithMany().HasForeignKey(c => new { c.TenantId, c.EmployeeId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.TenantId, c.EmployeeId, c.CreatedAt }).HasDatabaseName("IX_ExpenseClaims_Employee");
        builder.HasIndex(c => new { c.TenantId, c.Status }).HasDatabaseName("IX_ExpenseClaims_Status");
        builder.HasIndex(c => new { c.TenantId, c.OwnerId }).HasDatabaseName("IX_ExpenseClaims_Owner");
        builder.HasIndex(c => new { c.TenantId, c.ApproverUserId }).HasDatabaseName("IX_ExpenseClaims_Approver");
    }
}

internal sealed class ExpenseLineConfiguration : IEntityTypeConfiguration<ExpenseLine>
{
    public void Configure(EntityTypeBuilder<ExpenseLine> builder)
    {
        HrTables.ConfigureCommon(builder, "ExpenseLines");
        builder.Property(l => l.ExpenseTypeKey).HasMaxLength(100).IsRequired();
        builder.Property(l => l.Description).HasMaxLength(ExpenseLine.DescriptionMaxLength);
        builder.Property(l => l.Amount).HasPrecision(18, 2);
        builder.Property(l => l.VatAmount).HasPrecision(18, 2);

        builder.HasOne<ExpenseClaim>().WithMany().HasForeignKey(l => new { l.TenantId, l.ClaimId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(l => new { l.TenantId, l.ClaimId, l.ExpenseDate }).HasDatabaseName("IX_ExpenseLines_Claim");
        builder.HasIndex(l => new { l.TenantId, l.OwnerId }).HasDatabaseName("IX_ExpenseLines_Owner");
    }
}

internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        HrTables.ConfigureCommon(builder, "Assets");
        builder.Property(a => a.AssetTag).HasMaxLength(Asset.TagMaxLength).IsRequired();
        builder.Property(a => a.Name).HasMaxLength(Asset.NameMaxLength).IsRequired();
        builder.Property(a => a.CategoryKey).HasMaxLength(100).IsRequired();
        builder.Property(a => a.SerialNumber).HasMaxLength(Asset.SerialMaxLength);
        builder.Property(a => a.Brand).HasMaxLength(Asset.BrandMaxLength);
        builder.Property(a => a.Model).HasMaxLength(Asset.BrandMaxLength);
        builder.Property(a => a.PurchaseCost).HasPrecision(18, 2);
        builder.Property(a => a.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(a => a.Status).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(a => a.Notes).HasMaxLength(Asset.NotesMaxLength);

        builder.HasIndex(a => new { a.TenantId, a.AssetTag }).IsUnique().HasDatabaseName("UX_Assets_Tag");
        builder.HasIndex(a => new { a.TenantId, a.Status }).HasDatabaseName("IX_Assets_Status");
        builder.HasIndex(a => new { a.TenantId, a.CurrentEmployeeId }).HasDatabaseName("IX_Assets_Employee");
        builder.HasIndex(a => new { a.TenantId, a.CurrentUserId }).HasDatabaseName("IX_Assets_User");
    }
}

internal sealed class AssetAssignmentConfiguration : IEntityTypeConfiguration<AssetAssignment>
{
    public void Configure(EntityTypeBuilder<AssetAssignment> builder)
    {
        HrTables.ConfigureCommon(builder, "AssetAssignments");
        builder.Property(a => a.ConditionOut).HasMaxLength(AssetAssignment.TextMaxLength);
        builder.Property(a => a.ConditionIn).HasMaxLength(AssetAssignment.TextMaxLength);
        builder.Property(a => a.Status).HasMaxLength(20).IsUnicode(false).IsRequired();

        builder.HasOne<Asset>().WithMany().HasForeignKey(a => new { a.TenantId, a.AssetId })
            .HasPrincipalKey(a => new { a.TenantId, a.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Employee>().WithMany().HasForeignKey(a => new { a.TenantId, a.EmployeeId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id }).OnDelete(DeleteBehavior.Restrict);

        // Bir demirbaşın aynı anda tek açık zimmeti olur (veritabanı da korur).
        builder.HasIndex(a => new { a.TenantId, a.AssetId }).IsUnique().HasFilter("[Status] = 'active'").HasDatabaseName("UX_AssetAssignments_ActiveAsset");
        builder.HasIndex(a => new { a.TenantId, a.EmployeeId, a.Status }).HasDatabaseName("IX_AssetAssignments_Employee");
        builder.HasIndex(a => new { a.TenantId, a.OwnerId }).HasDatabaseName("IX_AssetAssignments_Owner");
    }
}

internal sealed class ChecklistTemplateConfiguration : IEntityTypeConfiguration<ChecklistTemplate>
{
    public void Configure(EntityTypeBuilder<ChecklistTemplate> builder)
    {
        HrTables.ConfigureCommon(builder, "ChecklistTemplates");
        builder.Property(t => t.Name).HasMaxLength(ChecklistTemplate.NameMaxLength).IsRequired();
        builder.Property(t => t.Kind).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(ChecklistTemplate.TextMaxLength);
        builder.HasIndex(t => new { t.TenantId, t.Kind, t.Name }).IsUnique().HasDatabaseName("UX_ChecklistTemplates_Name");
    }
}

internal sealed class ChecklistTemplateItemConfiguration : IEntityTypeConfiguration<ChecklistTemplateItem>
{
    public void Configure(EntityTypeBuilder<ChecklistTemplateItem> builder)
    {
        HrTables.ConfigureCommon(builder, "ChecklistTemplateItems");
        builder.Property(i => i.Title).HasMaxLength(ChecklistTemplateItem.TitleMaxLength).IsRequired();
        builder.Property(i => i.Description).HasMaxLength(ChecklistTemplateItem.TextMaxLength);
        builder.Property(i => i.ResponsibleRole).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(i => i.TaskType).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.HasOne<ChecklistTemplate>().WithMany().HasForeignKey(i => new { i.TenantId, i.TemplateId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(i => new { i.TenantId, i.TemplateId, i.Sequence }).HasDatabaseName("IX_ChecklistTemplateItems_Template");
    }
}

internal sealed class EmployeeChecklistConfiguration : IEntityTypeConfiguration<EmployeeChecklist>
{
    public void Configure(EntityTypeBuilder<EmployeeChecklist> builder)
    {
        HrTables.ConfigureCommon(builder, "EmployeeChecklists");
        builder.Property(c => c.Kind).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(ChecklistTemplate.NameMaxLength).IsRequired();
        builder.Property(c => c.Status).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(c => c.CancelReason).HasMaxLength(EmployeeChecklist.ReasonMaxLength);
        builder.HasOne<Employee>().WithMany().HasForeignKey(c => new { c.TenantId, c.EmployeeId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ChecklistTemplate>().WithMany().HasForeignKey(c => new { c.TenantId, c.TemplateId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.TenantId, c.EmployeeId, c.Kind }).HasDatabaseName("IX_EmployeeChecklists_Employee");
        builder.HasIndex(c => new { c.TenantId, c.Status }).HasDatabaseName("IX_EmployeeChecklists_Status");
        builder.HasIndex(c => new { c.TenantId, c.OwnerId }).HasDatabaseName("IX_EmployeeChecklists_Owner");
    }
}

internal sealed class ChecklistTaskConfiguration : IEntityTypeConfiguration<ChecklistTask>
{
    public void Configure(EntityTypeBuilder<ChecklistTask> builder)
    {
        HrTables.ConfigureCommon(builder, "ChecklistTasks");
        builder.Property(t => t.Title).HasMaxLength(ChecklistTemplateItem.TitleMaxLength).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(ChecklistTemplateItem.TextMaxLength);
        builder.Property(t => t.ResponsibleRole).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(t => t.TaskType).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(t => t.Status).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(t => t.Note).HasMaxLength(ChecklistTask.NoteMaxLength);
        builder.HasOne<EmployeeChecklist>().WithMany().HasForeignKey(t => new { t.TenantId, t.ChecklistId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => new { t.TenantId, t.ChecklistId, t.Sequence }).HasDatabaseName("IX_ChecklistTasks_Checklist");
        builder.HasIndex(t => new { t.TenantId, t.AssigneeUserId, t.Status }).HasDatabaseName("IX_ChecklistTasks_Assignee");
        builder.HasIndex(t => new { t.TenantId, t.OwnerId }).HasDatabaseName("IX_ChecklistTasks_Owner");
    }
}

internal sealed class AnnouncementConfiguration : IEntityTypeConfiguration<Announcement>
{
    public void Configure(EntityTypeBuilder<Announcement> builder)
    {
        HrTables.ConfigureCommon(builder, "Announcements");
        builder.Property(a => a.Title).HasMaxLength(Announcement.TitleMaxLength).IsRequired();
        builder.Property(a => a.Body).HasMaxLength(Announcement.BodyMaxLength).IsRequired();
        builder.Property(a => a.AudienceDepartmentKey).HasMaxLength(100);
        builder.HasIndex(a => new { a.TenantId, a.IsPublished, a.PublishOn }).HasDatabaseName("IX_Announcements_Published");
    }
}

internal sealed class AnnouncementReadConfiguration : IEntityTypeConfiguration<AnnouncementRead>
{
    public void Configure(EntityTypeBuilder<AnnouncementRead> builder)
    {
        HrTables.ConfigureCommon(builder, "AnnouncementReads");
        builder.HasOne<Announcement>().WithMany().HasForeignKey(r => new { r.TenantId, r.AnnouncementId })
            .HasPrincipalKey(a => new { a.TenantId, a.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.TenantId, r.AnnouncementId, r.UserId }).IsUnique().HasDatabaseName("UX_AnnouncementReads_User");
    }
}

internal sealed class SurveyConfiguration : IEntityTypeConfiguration<Survey>
{
    public void Configure(EntityTypeBuilder<Survey> builder)
    {
        HrTables.ConfigureCommon(builder, "Surveys");
        builder.Property(s => s.Title).HasMaxLength(Survey.TitleMaxLength).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(Survey.DescriptionMaxLength);
        builder.Property(s => s.Status).HasMaxLength(20).IsRequired();
        builder.Property(s => s.AudienceDepartmentKey).HasMaxLength(100);
        builder.HasIndex(s => new { s.TenantId, s.Status }).HasDatabaseName("IX_Surveys_Status");
    }
}

internal sealed class SurveyQuestionConfiguration : IEntityTypeConfiguration<SurveyQuestion>
{
    public void Configure(EntityTypeBuilder<SurveyQuestion> builder)
    {
        HrTables.ConfigureCommon(builder, "SurveyQuestions");
        builder.Property(q => q.Text).HasMaxLength(SurveyQuestion.TextMaxLength).IsRequired();
        builder.Property(q => q.QuestionType).HasMaxLength(20).IsRequired();
        builder.Property(q => q.Options).HasMaxLength(SurveyQuestion.OptionsMaxLength);
        builder.HasOne<Survey>().WithMany().HasForeignKey(q => new { q.TenantId, q.SurveyId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SurveyResponseConfiguration : IEntityTypeConfiguration<SurveyResponse>
{
    public void Configure(EntityTypeBuilder<SurveyResponse> builder)
    {
        HrTables.ConfigureCommon(builder, "SurveyResponses");
        builder.HasOne<Survey>().WithMany().HasForeignKey(r => new { r.TenantId, r.SurveyId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.TenantId, r.SurveyId, r.RespondentUserId }).IsUnique().HasDatabaseName("UX_SurveyResponses_Respondent");
    }
}

internal sealed class SurveyAnswerConfiguration : IEntityTypeConfiguration<SurveyAnswer>
{
    public void Configure(EntityTypeBuilder<SurveyAnswer> builder)
    {
        HrTables.ConfigureCommon(builder, "SurveyAnswers");
        builder.Property(a => a.Choice).HasMaxLength(SurveyQuestion.OptionMaxLength);
        builder.Property(a => a.TextValue).HasMaxLength(SurveyAnswer.TextMaxLength);
        builder.HasOne<SurveyResponse>().WithMany().HasForeignKey(a => new { a.TenantId, a.ResponseId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SurveyQuestion>().WithMany().HasForeignKey(a => new { a.TenantId, a.QuestionId })
            .HasPrincipalKey(q => new { q.TenantId, q.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.TenantId, a.SurveyId, a.QuestionId }).HasDatabaseName("IX_SurveyAnswers_Question");
    }
}
