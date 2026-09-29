using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MikuTest.Web.Data;

namespace MikuTest.Web.Pages.Account;

public class LoginModel(SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "请输入用户名")]
        public string UserName { get; set; } = "";

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = "";
        public bool RememberMe { get; set; }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();
        var result = await signIn.PasswordSignInAsync(
            Input.UserName.Trim(),
            Input.Password,
            Input.RememberMe,
            false
        );
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", "用户名或密码不正确。");
            return Page();
        }
        if (IsLocal(ReturnUrl))
            return LocalRedirect(ReturnUrl!);
        var user = await users.FindByNameAsync(Input.UserName.Trim());
        return LocalRedirect(
            user is not null && await users.IsInRoleAsync(user, AppRoles.SuperAdmin) ? "/superadmin"
            : user is not null && await users.IsInRoleAsync(user, AppRoles.Administrator) ? "/admin"
            : "/me"
        );
    }

    private static bool IsLocal(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith('/')
        && !value.StartsWith("//")
        && !value.StartsWith("/\\");
}
