using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MikuTest.Web.Data;

namespace MikuTest.Web.Pages.Account;

[Authorize]
public class ChangePasswordModel(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn)
    : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? SuccessMessage { get; set; }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "请输入当前密码"), DataType(DataType.Password)]
        public string CurrentPassword { get; set; } = "";

        [
            Required(ErrorMessage = "请输入新密码"),
            MinLength(8, ErrorMessage = "密码至少 8 位"),
            DataType(DataType.Password)
        ]
        public string NewPassword { get; set; } = "";

        [Compare(nameof(NewPassword), ErrorMessage = "两次密码不一致"), DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = "";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();
        var user = await users.GetUserAsync(User);
        if (user is null)
            return Challenge();
        var result = await users.ChangePasswordAsync(user, Input.CurrentPassword, Input.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var issue in result.Errors)
                ModelState.AddModelError("", issue.Description);
            return Page();
        }
        await signIn.RefreshSignInAsync(user);
        SuccessMessage = "密码已修改。";
        return RedirectToPage();
    }
}
