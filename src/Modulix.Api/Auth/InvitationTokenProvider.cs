using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Users;

namespace Modulix.Api.Auth;

public sealed class InvitationTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public InvitationTokenProviderOptions()
    {
        Name = AccountTokens.InvitationProvider;
        TokenLifespan = AccountTokens.InvitationLifespan;
    }
}

/// <summary>Davet token'ları; şifre sıfırlamadan ayrı süre ve koruma anahtarıyla.</summary>
public sealed class InvitationTokenProvider(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<InvitationTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<ModulixUser>> logger)
    : DataProtectorTokenProvider<ModulixUser>(dataProtectionProvider, options, logger);
