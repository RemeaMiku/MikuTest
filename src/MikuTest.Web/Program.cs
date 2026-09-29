using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Components;
using MikuTest.Web.Data;
using MikuTest.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRazorPages();
builder.Services.AddCascadingAuthenticationState();
var connection = new SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("Quiz") ?? "Data Source=App_Data/mikutest.db"
);
if (!Path.IsPathRooted(connection.DataSource))
    connection.DataSource = Path.Combine(builder.Environment.ContentRootPath, connection.DataSource);
Directory.CreateDirectory(Path.GetDirectoryName(connection.DataSource)!);
builder.Services.AddDbContextFactory<QuizDbContext>(options => options.UseSqlite(connection.ToString()));
var identityConnection = new SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("Identity") ?? "Data Source=App_Data/identity.db"
);

// Cookie validation also runs on parallel asset requests; avoid reusing a SQLite
// connection with active statements when registering Identity's SQL functions.
identityConnection.Pooling = false;
if (!Path.IsPathRooted(identityConnection.DataSource))
    identityConnection.DataSource = Path.Combine(
        builder.Environment.ContentRootPath,
        identityConnection.DataSource
    );
builder.Services.AddDbContext<IdentityDbContext>(options => options.UseSqlite(identityConnection.ToString()));
builder
    .Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = false;
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
    })
    .AddRoles<IdentityRole>()
    .AddSignInManager<ActiveAccountSignInManager>()
    .AddEntityFrameworkStores<IdentityDbContext>();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/access-denied";
});
builder.Services.AddAuthorization(Permissions.Configure);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<PermissionResolver>();
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PermissionHandler>();
builder.Services.AddScoped<IManagementAccess, ManagementAccess>();
builder.Services.AddScoped<ManagementRecords>();
builder.Services.AddScoped<AccountAdminService>();
builder.Services.AddScoped<
    Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider,
    AccountStateProvider
>();
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
builder.Services.AddScoped<QuizService>();
builder.Services.AddScoped<QuestionService>();
builder.Services.AddScoped<QuestionGroupService>();
builder.Services.AddScoped<MediaUploadService>();
builder.Services.AddScoped<QuizAdminService>();
builder.Services.AddScoped<RandomQuizService>();
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    await using var db = await scope
        .ServiceProvider.GetRequiredService<IDbContextFactory<QuizDbContext>>()
        .CreateDbContextAsync();
    await DatabaseUpgrade.ApplyAsync(db);
    await SeedData.InitializeAsync(db);
    await Phase2Seed.InitializeAsync(db);
    await DevelopmentQuestionSeed.InitializeAsync(db);
    await KnowledgeDomainSeed.InitializeAsync(db);
    await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.EnsureCreatedAsync();
    await IdentitySeed.InitializeAsync(scope.ServiceProvider, builder.Configuration);
}
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
var uploadsDirectory = Path.Combine(app.Environment.ContentRootPath, "App_Data", "uploads");
Directory.CreateDirectory(uploadsDirectory);
app.UseStaticFiles(
    new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsDirectory),
        RequestPath = "/uploads",
        OnPrepareResponse = context => context.Context.Response.Headers["X-Content-Type-Options"] = "nosniff",
    }
);
app.MapPost(
        "/account/logout",
        async (HttpContext context, IAntiforgery antiforgery, SignInManager<ApplicationUser> signIn) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await signIn.SignOutAsync();
            return Results.LocalRedirect("/");
        }
    )
    .RequireAuthorization();
app.MapStaticAssets();
app.MapRazorPages();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
