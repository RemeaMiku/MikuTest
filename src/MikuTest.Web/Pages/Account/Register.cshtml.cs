using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MikuTest.Web.Data;

namespace MikuTest.Web.Pages.Account;

public class RegisterModel(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    MikuTest.Web.Services.ManagementRecords records
) : PageModel
{
    public bool RegistrationEnabled { get; private set; } = true;

    public async Task OnGetAsync() =>
        RegistrationEnabled = (await records.SettingsAsync()).RegistrationEnabled;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [
            Required(ErrorMessage = "请输入用户名"),
            RegularExpression(
                @"^[a-zA-Z0-9_-]{3,30}$",
                ErrorMessage = "用户名需为 3–30 位英文字母、数字、下划线或短横线"
            )
        ]
        public string UserName { get; set; } = "";

        [
            Required(ErrorMessage = "请输入密码"),
            MinLength(8, ErrorMessage = "密码至少 8 位"),
            DataType(DataType.Password)
        ]
        public string Password { get; set; } = "";

        [Compare(nameof(Password), ErrorMessage = "两次密码不一致"), DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = "";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        RegistrationEnabled = (await records.SettingsAsync()).RegistrationEnabled;
        if (!RegistrationEnabled)
        {
            ModelState.AddModelError("", "网站暂未开放新账号注册。");
            return Page();
        }
        if (!ModelState.IsValid)
            return Page();
        var user = new ApplicationUser { UserName = Input.UserName, DisplayName = Input.UserName };
        var result = await users.CreateAsync(user, Input.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError(
                    "",
                    e.Code == "DuplicateUserName" ? "这个用户名已被使用，请换一个。" : e.Description
                );
            return Page();
        }
        IdentitySeed.Ensure(await users.AddToRoleAsync(user, AppRoles.User));
        await signIn.SignInAsync(user, false);
        return LocalRedirect("/me");
    }
}
