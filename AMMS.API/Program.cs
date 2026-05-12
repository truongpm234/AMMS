using AMMS.API;
using AMMS.API.Jobs;
using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Infrastructure.Configurations;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.FileStorage;
using AMMS.Infrastructure.Interfaces;
using AMMS.Infrastructure.Repositories;
using AMMS.Shared.DTOs.Email;
using AMMS.Shared.DTOs.PayOS;
using AMMS.Shared.Helpers;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Npgsql;
using QuestPDF.Infrastructure;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Twilio.Types;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.GetCurrentDirectory()
});

builder.Configuration.Sources.Clear();

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

if (args is { Length: > 0 })
{
    builder.Configuration.AddCommandLine(args);
}
var postgresConnStr =
    builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");

var hangfireConnStr =
    builder.Configuration.GetConnectionString("HangfireConnection")
    ?? postgresConnStr;

var hangfireEnableDashboard = builder.Configuration.GetValue("Hangfire:EnableDashboard", true);
var hangfireSchema = builder.Configuration["Hangfire:SchemaName"] ?? "hangfire";
var hangfireRunServer = builder.Configuration.GetValue("Hangfire:RunServer", true);
var hangfireWorkerCount = builder.Configuration.GetValue("Hangfire:WorkerCount", 2);
var hangfireQueues = builder.Configuration.GetSection("Hangfire:Queues").Get<string[]>() ?? new[] { "default", "emails" };
var hangfireSchedulePollingSeconds = builder.Configuration.GetValue("Hangfire:SchedulePollingSeconds", 15);
var hangfireQueuePollSeconds = builder.Configuration.GetValue("Hangfire:QueuePollSeconds", 15);
var hangfireInvisibilityTimeoutMinutes = builder.Configuration.GetValue("Hangfire:InvisibilityTimeoutMinutes", 30);
var hangfireDashboardPath = builder.Configuration["Hangfire:DashboardPath"] ?? "/hangfire";

var autoSendDealEnabled = builder.Configuration.GetValue("AutoSendDeal:Enabled", true);
var autoSendDealCron = builder.Configuration["AutoSendDeal:Cron"] ?? "*/15 * * * *";
var autoStartProductionEnabled = builder.Configuration.GetValue("AutoStartProduction:Enabled", true);
var autoStartProductionCron = builder.Configuration["AutoStartProduction:Cron"] ?? "* * * * *";
var autoCompleteDeliveredEnabled = builder.Configuration.GetValue("AutoCompleteDelivered:Enabled", true);
var autoCompleteDeliveredCron = builder.Configuration["AutoCompleteDelivered:Cron"] ?? "0 1 * * *";
var autoCancelPendingRequestEnabled = builder.Configuration.GetValue("AutoCancelPendingRequest:Enabled", true);
var autoCancelPendingRequestCron = builder.Configuration["AutoCancelPendingRequest:Cron"] ?? "0 0 * * *";



var dataSourceBuilder = new NpgsqlDataSourceBuilder(postgresConnStr);

dataSourceBuilder.EnableDynamicJson();

var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    options.UseNpgsql(
        dataSource,
        npgsqlOptions =>
        {
            npgsqlOptions.CommandTimeout(60);
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 1,
                maxRetryDelay: TimeSpan.FromSeconds(1),
                errorCodesToAdd: null
            );
        });
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.JsonSerializerOptions.Converters.Add(new NullableDateTimeConverter());
    });
builder.Services.Configure<JsonOptions>(options =>
{
    options.JsonSerializerOptions.Converters.Add(new NullableDateTimeConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddMemoryCache();
builder.Services.AddSignalR();
var feOrigins = new[]
{
    "http://localhost:3000",
    "http://192.168.2.220:3000",
    "https://sep490-fe.vercel.app",
    "https://daiphuchai.vercel.app"
};

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.WithOrigins(feOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,

        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"])
        ),

        ClockSkew = TimeSpan.Zero
    };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;

            if (!string.IsNullOrEmpty(accessToken) &&
                path.StartsWithSegments("/hubs/realtime"))
            {
                context.Token = accessToken;
            }

            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("admin", policy =>
        policy.RequireClaim("roleid", "1"));
    options.AddPolicy("consultant", policy =>
        policy.RequireClaim("roleid", "2"));
    options.AddPolicy("manager", policy =>
        policy.RequireClaim("roleid", "3"));
    options.AddPolicy("warehouse manager", policy =>
        policy.RequireClaim("roleid", "4"));
    options.AddPolicy("customer", policy =>
        policy.RequireClaim("roleid", "5"));
    options.AddPolicy("staff", policy =>
        policy.RequireClaim("roleid", "6"));
});


builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "My API",
        Version = "v1"
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Nhập JWT dạng: Bearer {token}"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// Configuration
builder.Services.Configure<CloudinaryOptions>(
builder.Configuration.GetSection("Cloudinary"));
builder.Services.Configure<PayOsOptions>(builder.Configuration.GetSection("PayOS"));
builder.Services.Configure<SendGridSettings>(builder.Configuration.GetSection("EmailSender"));
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
});

builder.Services.AddHangfire(config =>
{
    config.SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
          .UseSimpleAssemblyNameTypeSerializer()
          .UseRecommendedSerializerSettings()
          .UsePostgreSqlStorage(
              hangfireConnStr,
              new PostgreSqlStorageOptions
              {
                  SchemaName = hangfireSchema,
                  PrepareSchemaIfNecessary = true,
                  QueuePollInterval = TimeSpan.FromSeconds(hangfireQueuePollSeconds),
                  InvisibilityTimeout = TimeSpan.FromMinutes(hangfireInvisibilityTimeoutMinutes),
              });
});

if (hangfireRunServer)
{
    builder.Services.AddHangfireServer(options =>
    {
        options.WorkerCount = hangfireWorkerCount;
        options.Queues = hangfireQueues;
        options.SchedulePollingInterval = TimeSpan.FromSeconds(hangfireSchedulePollingSeconds);
    });
}

builder.Services.AddHttpClient("Resend", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Resend:BaseUrl"] ?? "https://api.resend.com/");
    client.Timeout = TimeSpan.FromSeconds(90);
});

builder.Services.AddScoped<QuoteExpiryJob>();
builder.Services.AddScoped<AutoSendDealAfterVerifiedJob>();
builder.Services.Configure<SchedulingOptions>(
builder.Configuration.GetSection("Scheduling"));
builder.Services.AddScoped<WorkCalendar>();
builder.Services.AddScoped<AutoCompleteDeliveredAfter7DaysJob>();
builder.Services.AddScoped<AutoCancelPendingRequestAfter3DaysJob>();
// Services
builder.Services.AddScoped<IUploadFileService, UploadFileService>();
builder.Services.AddScoped<ICloudinaryFileStorageService, CloudinaryFileStorageService>();
builder.Services.AddScoped<IRequestService, RequestService>();
builder.Services.AddScoped<IRequestRepository, RequestRepository>();
builder.Services.AddScoped<IMaterialRepository, MaterialRepository>();
builder.Services.AddScoped<IMaterialService, MaterialService>();
builder.Services.AddScoped<IEstimateService, EstimateService>();
builder.Services.AddScoped<ICostEstimateRepository, CostEstimateRepository>();
builder.Services.AddScoped<IProductionService, ProductionService>();
builder.Services.AddScoped<IProductionRepository, ProductionRepository>();
builder.Services.AddScoped<IMachineService, MachineService>();
builder.Services.AddScoped<IMachineRepository, MachineRepository>();
builder.Services.AddScoped<IProductTypeRepository, ProductTypeRepository>();
builder.Services.AddScoped<IProductTypeService, ProductTypeService>();
builder.Services.AddScoped<IDealService, DealService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<IQuoteRepository, QuoteRepository>();
builder.Services.AddScoped<ISupplierRepository, SupplierRepository>();
builder.Services.AddScoped<ISupplierService, SupplierService>();
builder.Services.AddScoped<ILookupService, LookupService>();
builder.Services.AddScoped<IProductTypeProcessRepository, ProductTypeProcessRepository>();
builder.Services.AddScoped<ITaskRepository, TaskRepository>();
builder.Services.AddScoped<ITaskLogRepository, TaskLogRepository>();
builder.Services.AddScoped<IProductionSchedulingService, ProductionSchedulingService>();
builder.Services.AddScoped<ITaskQrTokenService, TaskQrTokenService>();
builder.Services.AddScoped<ITaskScanService, TaskScanService>();
builder.Services.AddHttpClient<IPayOsService, PayOsService>();
builder.Services.AddScoped<IPaymentsService, PaymentsService>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IBomRepository, BomRepository>();
builder.Services.AddScoped<IProductTemplateRepository, ProductTemplateRepository>();
builder.Services.AddScoped<IProductTemplateService, ProductTemplateService>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<JWTService>();
builder.Services.AddScoped<GoogleAuthService>();
builder.Services.AddScoped<ISmsOtpService, TwilioSmsOtpService>();
builder.Services.AddScoped<IBaseConfigRepository, BaseConfigRepository>();
builder.Services.AddScoped<IBaseConfigService, BaseConfigService>();
builder.Services.AddScoped<IMissingMaterialService, MissingMaterialService>();
builder.Services.AddScoped<IMissingMaterialRepository, MissingMaterialRepository>();
builder.Services.AddScoped<IOrderPlanningService, OrderPlanningService>();
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<NotificationsRepository>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddSingleton<IRealtimePublisher, RealtimePublisher>();
builder.Services.AddSingleton<IEmailBackgroundQueue, EmailBackgroundQueue>();
builder.Services.AddScoped<AutoStartProductionJob>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAccessService, AccessService>();
builder.Services.AddScoped<IContractCompareService, ContractCompareService>();
builder.Services.AddScoped<DeliveryHandoverEmailJob>();
builder.Services.AddScoped<IEstimateConfigRepository, EstimateConfigRepository>();
builder.Services.AddScoped<ITaskService, TaskService>();
builder.Services.AddScoped<IProductionCalendarRepository, ProductionCalendarRepository>();
builder.Services.AddScoped<IProductionCalendarService, ProductionCalendarService>();
builder.Services.AddScoped<ISubProductRepository, SubProductRepository>();
builder.Services.AddScoped<ISubProductService, SubProductService>();
builder.Services.AddScoped<IGroupProductionService, GroupProductionService>();
// Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

var runEmailDispatcher = builder.Configuration.GetValue("App:RunEmailDispatcher", false);

if (runEmailDispatcher)
{
    builder.Services.AddHostedService<EmailDispatcherHostedService>();
}

QuestPDF.Settings.License = LicenseType.Community;

var app = builder.Build();

if (hangfireEnableDashboard)
{
    app.UseHangfireDashboard(hangfireDashboardPath, new DashboardOptions
    {
        Authorization = new[] { new AllowAllDashboardAuthorizationFilter() }
    });
}

var vnTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

if (hangfireRunServer)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            RecurringJob.AddOrUpdate<QuoteExpiryJob>(
                "quote-expiry-hourly",
                job => job.RunAsync(CancellationToken.None),
                Cron.Hourly(),
                vnTz
            );

            if (autoSendDealEnabled)
            {
                RecurringJob.AddOrUpdate<AutoSendDealAfterVerifiedJob>(
                    "auto-send-deal-after-verified-24h",
                    job => job.RunAsync(CancellationToken.None),
                    autoSendDealCron,
                    vnTz
                );
            }

            if (autoStartProductionEnabled)
            {
                RecurringJob.AddOrUpdate<AutoStartProductionJob>(
                    "auto-start-production-by-planned-start",
                    job => job.RunAsync(CancellationToken.None),
                    autoStartProductionCron,
                    vnTz
                );
            }

            if (autoCompleteDeliveredEnabled)
            {
                RecurringJob.AddOrUpdate<AutoCompleteDeliveredAfter7DaysJob>(
                    "auto-complete-delivered-after-7-days",
                    job => job.RunAsync(CancellationToken.None),
                    autoCompleteDeliveredCron,
                    vnTz
                );
            }
            if (autoCancelPendingRequestEnabled)
            {
                RecurringJob.AddOrUpdate<AutoCancelPendingRequestAfter3DaysJob>(
                    "auto-cancel-pending-request-after-3-days",
                    job => job.RunAsync(CancellationToken.None),
                    autoCancelPendingRequestCron,
                    vnTz
                );
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Failed to register Hangfire recurring jobs");
        }
    });
}

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (UnauthorizedAccessException ex)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                message = ex.Message
            });
        }
    }
});

app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "MES API");
    c.RoutePrefix = "swagger";
    c.DefaultModelsExpandDepth(-1);
    c.DisplayRequestDuration();
});

app.UseRouting();

app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<RealtimeHub>("/hubs/realtime");

app.MapGet("/health", (IConfiguration cfg) =>
{
    var isHangfire = cfg.GetValue("Hangfire:RunServer", true);
    return Results.Ok(new
    {
        ok = true,
        mode = isHangfire ? "hangfire" : "api"
    });
});

app.MapGet("/internal/ping", (HttpContext ctx, IConfiguration cfg, ILogger<Program> logger) =>
{
    var expected = cfg["KeepAlive:Secret"];
    var actual = ctx.Request.Headers["X-Internal-Key"].ToString();

    if (string.IsNullOrWhiteSpace(expected) || actual != expected)
    {
        logger.LogWarning("[KeepAlive] Unauthorized ping");
        return Results.Unauthorized();
    }

    var isHangfire = cfg.GetValue("Hangfire:RunServer", true);

    logger.LogInformation("[KeepAlive] Authorized ping received at {UtcNow}", DateTime.UtcNow);

    return Results.Ok(new
    {
        ok = true,
        mode = isHangfire ? "hangfire" : "api",
        time = AppTime.NowVnUnspecified()
    });
});

app.Run();