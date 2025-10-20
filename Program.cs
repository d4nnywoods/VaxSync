using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using VaxSync.Web;
using VaxSync.Web.Components;
using VaxSync.Web.Components.Account;
using VaxSync.Web.Components.Layout;
using VaxSync.Web.Data;
using VaxSync.Web.Services;

#nullable enable

internal class Program
{
    private static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // ---- Blazor (.NET 9) ----
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // ---- Database (SQLite in App_Data) ----
        var csFolder = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(csFolder);
        var dbPath = Path.Combine(csFolder, "vaxsync_dev.db");

        builder.Services.AddDbContext<ApplicationDbContext>(o =>
            o.UseSqlite($"Data Source={dbPath}"));
        builder.Services.AddDatabaseDeveloperPageExceptionFilter();

        // ---- Identity / Auth ----
        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();

        builder.Services.AddAuthorization();

        builder.Services
            .AddIdentityCore<ApplicationUser>(o =>
            {
                o.SignIn.RequireConfirmedAccount = true;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        /*Commented out login redirect configuration so navigation works without signing in.*/
        builder.Services.ConfigureApplicationCookie(options =>
        {   
            options.LoginPath = "/Account/Login";
            options.LogoutPath = "/Account/Logout";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        });
        

        // Blazor auth helpers for AuthorizeView etc.
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<IdentityUserAccessor>();
        builder.Services.AddScoped<IdentityRedirectManager>();
        builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

        // ---- App services ----
        builder.Services.AddScoped<AuditLogService>();
        builder.Services.AddMudServices();
        builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

        var app = builder.Build();

        // ---- Dev: migrate + optional seed ----
        if (app.Environment.IsDevelopment())
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await db.Database.MigrateAsync();

            // Toggle via appsettings.Development.json
            var seedSection = app.Configuration.GetSection("Seed");
            var seedEnabled = seedSection.GetValue<bool?>("Enabled") ?? true;
            if (seedEnabled)
            {
                var studentCount = seedSection.GetValue<int?>("StudentCount") ?? 500;
                var schoolCount = seedSection.GetValue<int?>("SchoolCount") ?? 25;
                var batchSize = seedSection.GetValue<int?>("BatchSize") ?? DevSeeder.DefaultBatchSize;

                await DevSeeder.SeedAsync(
                    db,
                    targetStudentCount: Math.Clamp(studentCount, 0, 250_000),
                    schoolCount: Math.Clamp(schoolCount, 1, 1_000),
                    batchSize: Math.Clamp(batchSize, 100, 50_000));
            }
        }

        // ---- One-time Identity user creation ----
        if (app.Configuration.GetValue<bool>("OneTimeCreateUsers"))
        {
            using var scope = app.Services.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

            string[] roles = { "Admin", "SchoolNurse", "Viewer" };
            foreach (var r in roles)
                if (!await roleManager.RoleExistsAsync(r))
                    await roleManager.CreateAsync(new IdentityRole(r));

            async Task Ensure(string email, string pass, string role, string? schoolId = null)
            {
                var u = await userManager.FindByEmailAsync(email);
                if (u == null)
                {
                    u = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, SchoolId = schoolId };
                    var res = await userManager.CreateAsync(u, pass);
                    if (!res.Succeeded) throw new Exception(string.Join(", ", res.Errors.Select(e => e.Description)));
                }
                if (!await userManager.IsInRoleAsync(u, role))
                    await userManager.AddToRoleAsync(u, role);
            }

            await Ensure("admin@vaxsync.local", "Admin123!", "Admin");
            await Ensure("nurse@vaxsync.local", "Nurse123!", "SchoolNurse", "SCH0001");
            await Ensure("viewer@vaxsync.local", "Viewer123!", "Viewer");
        }

        // ---- Pipeline ----
        if (app.Environment.IsDevelopment())
        {
            app.UseMigrationsEndPoint();
        }
        else
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();

        app.UseRouting();

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseAntiforgery();

        app.MapRazorComponents<App>()
           .AddInteractiveServerRenderMode();

        // Required for Blazor Identity default pages at /Account/*
        app.MapAdditionalIdentityEndpoints();

        await app.RunAsync();
    }
}