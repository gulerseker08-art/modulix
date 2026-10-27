using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Infrastructure.Audit;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Identity;

/// <summary>
/// Açık oturum (Y7a). Çerez oturumun kimliğini taşır; her istekte oturumun kapatılmadığı denetlenir. Kullanıcıya
/// bağlıdır, firmaya değil: kişi oturum içinde firma değiştirebilir.
/// </summary>
public sealed class UserSession
{
    public const int IpAddressMaxLength = 45;

    private UserSession()
    {
    }

    public UserSession(Guid userId, AccountClient client, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        CreatedAt = now;
        LastSeenAt = now;
        IpAddress = client.IpAddress is { Length: > IpAddressMaxLength } ip ? ip[..IpAddressMaxLength] : client.IpAddress;
        UserAgent = client.UserAgent is { Length: > AccountClient.UserAgentMaxLength } agent
            ? agent[..AccountClient.UserAgentMaxLength]
            : client.UserAgent;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Son istek; en fazla <see cref="UserSessions.TouchInterval"/> aralıkla güncellenir.</summary>
    public DateTimeOffset LastSeenAt { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>, IPlatformConfiguration
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("UserSessions", DatabaseSchemas.Platform);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.IpAddress).HasMaxLength(UserSession.IpAddressMaxLength).IsUnicode(false);
        builder.Property(s => s.UserAgent).HasMaxLength(AccountClient.UserAgentMaxLength);
        builder.HasIndex(s => s.UserId);
        builder.HasOne<ModulixUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
