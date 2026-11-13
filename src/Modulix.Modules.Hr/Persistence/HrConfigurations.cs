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
