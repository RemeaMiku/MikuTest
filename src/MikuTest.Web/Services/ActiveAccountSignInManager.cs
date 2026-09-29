using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MikuTest.Web.Data;

namespace MikuTest.Web.Services;

public sealed class ActiveAccountSignInManager(
    UserManager<ApplicationUser> users,
    IHttpContextAccessor context,
    IUserClaimsPrincipalFactory<ApplicationUser> factory,
    IOptions<IdentityOptions> options,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation
) : SignInManager<ApplicationUser>(users, context, factory, options, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(ApplicationUser user) =>
        user.Status == AccountStatus.Active && await base.CanSignInAsync(user);
}
