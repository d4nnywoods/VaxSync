using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using VaxSync.Web.Components.Account;
using VaxSync.Web.Components;
using VaxSync.Web.Data;
using VaxSync.Web.Services;

internal class Program
{
    private static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // ---- UI / Blazor ----
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // ---- Database ----
        var csFolder = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(csFolder); // ensures folder exists even on new branches
        var dbPath = Path.Combine(csFolder, "vaxsync_dev2.db");

        builder.Services.AddDbContext<ApplicationDbContext>(o =>
            o.UseSqlite($"Data Source={dbPath}"));
        builder.Services.AddDatabaseDeveloperPageExceptionFilter();

        // ---- AuthN / AuthZ for Blazor Identity components ----
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = IdentityConstants.ApplicationScheme;
            options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        })
        .AddIdentityCookies();

        builder.Services.AddAuthorization();

        // ---- Identity stores ----
        builder.Services
            .AddIdentityCore<ApplicationUser>(o =>
            {
                o.SignIn.RequireConfirmedAccount = true;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        // Cookie paths aligned with component endpoints
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Account/Login";
            options.LogoutPath = "/Account/Logout";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        });

        // ---- Blazor auth state helpers ----
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<IdentityUserAccessor>();
        builder.Services.AddScoped<IdentityRedirectManager>();
        builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

        // ---- App services ----
        builder.Services.AddScoped<AuditLogService>();
        builder.Services.AddMudServices();
        builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

        var app = builder.Build();

        // dev errors visible
        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        // ensure schema exists BEFORE user bootstrap (one scope)
        using (var scope = app.Services.CreateScope())
        {
            var services = scope.ServiceProvider;

            var db = services.GetRequiredService<ApplicationDbContext>();
            await db.Database.MigrateAsync();   // fresh DB filename is fine (vaxsync_dev2.db)

            if (app.Configuration.GetValue<bool>("OneTimeCreateUsers"))
            {
                var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
                var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();

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

            app.MapAdditionalIdentityEndpoints();


            app.Run();
        }
    }
