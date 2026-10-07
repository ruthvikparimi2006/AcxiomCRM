using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AcxiomCRM.Controllers.Api;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Supplied by user-secrets (development) or environment variables, never appsettings (AUTH-13).
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is not configured. Set it with user-secrets or an environment variable.");

// Add services to the container.
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuditService>();

// §16 password and lockout policy (AUTH-04, AUTH-05, AUTH-07). Identity stores only password hashes.
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireUppercase = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireDigit = true;
        o.Password.RequireNonAlphanumeric = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager<AppSignInManager>()
    .AddDefaultTokenProviders();

// API-04: the web uses the Identity cookie, the REST API uses JWT bearer tokens (§16 #13). Requests under /api are
// authenticated only by the token and everything else only by the cookie, so a browser cookie never authorizes an
// API call (no CSRF exposure) and an unauthenticated API call gets 401 instead of a login-page redirect.
const string WebOrApi = "WebOrApi";
static bool IsApi(HttpContext c) => c.Request.Path.StartsWithSegments("/api");
var jwtKey = TokenService.SigningKey(builder.Configuration["Jwt:Key"]);
builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = WebOrApi;
        o.DefaultAuthenticateScheme = WebOrApi;
        o.DefaultChallengeScheme = WebOrApi;
        o.DefaultForbidScheme = WebOrApi;
    })
    .AddPolicyScheme(WebOrApi, WebOrApi, o =>
        o.ForwardDefaultSelector = c => IsApi(c) ? JwtBearerDefaults.AuthenticationScheme : IdentityConstants.ApplicationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = TokenService.Issuer,
            ValidAudience = TokenService.Issuer,
            IssuerSigningKey = jwtKey,
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        // A token stops working as soon as its user is deactivated or their security stamp changes
        // (password reset, role change), just like the web session.
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                var users = ctx.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                var stampClaim = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<IdentityOptions>>()
                    .Value.ClaimsIdentity.SecurityStampClaimType;
                var user = await users.GetUserAsync(ctx.Principal!);
                if (user is null || !user.IsActive || user.SecurityStamp != ctx.Principal!.FindFirstValue(stampClaim))
                    ctx.Fail("The token is no longer valid.");
            },
        };
    });
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<LoginService>();

// D4 / API-08: 5 API login attempts per minute per client IP address, then 429 Too Many Requests.
// Limitation: partitions by the connection's IP; behind a reverse proxy, forwarded headers must be enabled (Step 14).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
    o.AddPolicy(AuthApiController.LoginRateLimit, c => RateLimitPartition.GetFixedWindowLimiter(
        c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// API-06: API errors are RFC 7807 problem objects (no stack traces, no database details).
builder.Services.AddProblemDetails();

// ---- Deployment settings (Step 14, host-neutral; see docs/DEPLOYMENT.md) ----

// Behind a reverse proxy or load balancer: trust X-Forwarded-For/-Proto only from the proxies listed in configuration,
// so HTTPS detection, the per-IP login limit and audited IP addresses use the real client. With none listed, only a
// proxy on the same machine (loopback) is trusted.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
        o.KnownProxies.Add(IPAddress.Parse(proxy));
});

// AUTH-13 / API-09: HSTS tells browsers to use HTTPS only (sent outside Development).
builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(365);
    o.IncludeSubDomains = true;
});

// AUTH-08: every cookie the app sets is HTTPS-only and unreadable by scripts.
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.HttpOnly = true;
});
builder.Services.Configure<CookieTempDataProviderOptions>(o =>
{
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.HttpOnly = true;
});

// Keys that protect login cookies, anti-forgery tokens and password-reset links. Store them somewhere persistent
// (and shared between instances) in production, or every restart signs everyone out and voids open reset links.
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    builder.Services.AddDataProtection().SetApplicationName("AcxiomCRM").PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// Admin-issued password reset links expire quickly.
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromMinutes(30));

// AUTH-08: secure session cookie.
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.LogoutPath = "/Account/Logout";
    o.AccessDeniedPath = "/Account/AccessDenied";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.ExpireTimeSpan = TimeSpan.FromMinutes(60);
    o.SlidingExpiration = true;
});

// Deactivation and role changes update the security stamp; re-check it every minute so old sessions end quickly.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

// RBAC-01..06: the §7.1 permission matrix as fixed role policies (§16 #11). Record scope is applied by ScopeService.
var managerCanViewTeamUsers = builder.Configuration.GetValue("Manager:CanViewTeamUsers", true);
var managerCanViewAuditLog = builder.Configuration.GetValue("Manager:CanViewAuditLog", false);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin))
    .AddPolicy(Policies.ViewUsers, p => p.RequireAssertion(c =>
        c.User.IsInRole(Roles.Admin) || (managerCanViewTeamUsers && c.User.IsInRole(Roles.Manager))))
    .AddPolicy(Policies.CrmUser, p => p.RequireRole(Roles.All))
    .AddPolicy(Policies.ViewAuditLog, p => p.RequireAssertion(c =>
        c.User.IsInRole(Roles.Admin) || (managerCanViewAuditLog && c.User.IsInRole(Roles.Manager))));
builder.Services.AddScoped<ScopeService>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<LeadService>();
builder.Services.AddScoped<OpportunityService>();
builder.Services.AddScoped<LeadConversionService>();
builder.Services.AddScoped<FollowUpService>();
builder.Services.AddScoped<ActivityService>();

// GEN-03: every controller requires a signed-in user unless it opts out with [AllowAnonymous].
// AUTH-09: every state-changing MVC request must carry a valid anti-forgery token.
builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add(new AuthorizeFilter());
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());

    // VAL-11: consistent, user-friendly wording when a value can't be read (e.g. "abc" for a number).
    var m = o.ModelBindingMessageProvider;
    m.SetAttemptedValueIsInvalidAccessor((_, field) => string.Format(ValidationRules.InvalidValueMessage, field));
    m.SetUnknownValueIsInvalidAccessor(field => string.Format(ValidationRules.InvalidValueMessage, field));
    m.SetValueIsInvalidAccessor(_ => "Enter a valid value.");
    m.SetNonPropertyAttemptedValueIsInvalidAccessor(_ => "Enter a valid value.");
    m.SetNonPropertyUnknownValueIsInvalidAccessor(() => "Enter a valid value.");
    m.SetValueMustBeANumberAccessor(field => $"{field} must be a number.");
    m.SetNonPropertyValueMustBeANumberAccessor(() => "Enter a number.");
    m.SetValueMustNotBeNullAccessor(_ => "A value is required.");
    m.SetMissingBindRequiredValueAccessor(field => $"{field} is required.");
    m.SetMissingKeyOrValueAccessor(() => "A value is required.");
    m.SetMissingRequestBodyRequiredValueAccessor(() => "A request body is required.");
})
.AddJsonOptions(o =>
{
    // Enums travel as their names ("Qualification"), and numbers that are not a defined value are refused.
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    // API-06: malformed JSON gets a generic message instead of parser/type details.
    o.AllowInputFormatterExceptionMessages = false;
});

var app = builder.Build();

await SeedData.SeedAsync(app.Services);

// Configure the HTTP request pipeline.
app.UseForwardedHeaders();

// API-06: under /api, unhandled errors and empty 4xx/5xx responses become problem JSON.
app.UseWhen(IsApi, api =>
{
    api.UseExceptionHandler();
    api.UseStatusCodePages();
});
// GEN-09: on the web, users see friendly error pages, never exception details (Development keeps the developer page).
app.UseWhen(c => !IsApi(c), web =>
{
    if (!app.Environment.IsDevelopment()) web.UseExceptionHandler("/Home/Error");
    web.UseStatusCodePagesWithReExecute("/Home/Status/{0}");
});
if (!app.Environment.IsDevelopment())
{
    app.UseHsts(); // 365 days, configured above
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

public partial class Program;
