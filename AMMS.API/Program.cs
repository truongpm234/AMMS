using AMMS.Application.Extensions;
using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Infrastructure.Configurations;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.FileStorage;
using AMMS.Infrastructure.Interfaces;
using AMMS.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions =>
        {
            npgsqlOptions.CommandTimeout(30);
            npgsqlOptions.EnableRetryOnFailure(0);
            // ✅ Thêm options để tránh connection hanging
            npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery);
        })
        // ✅ Important: Đảm bảo dispose connection đúng cách
        .EnableSensitiveDataLogging()
        .EnableDetailedErrors(),
    // ✅ ServiceLifetime phải là Scoped
    ServiceLifetime.Scoped);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.Converters.Add(new DateTimeConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        builder =>
        {
            builder.AllowAnyOrigin()
                   .AllowAnyMethod()
                   .AllowAnyHeader();
        });
});

// Configuration
builder.Services.Configure<CloudinaryOptions>(
    builder.Configuration.GetSection("Cloudinary"));

// Services
builder.Services.AddScoped<IUploadFileService, UploadFileService>();
builder.Services.AddScoped<ICloudinaryFileStorageService, CloudinaryFileStorageService>();
builder.Services.AddScoped<IRequestService, RequestService>();
builder.Services.AddScoped<IRequestRepository, RequestRepository>();
builder.Services.AddScoped<IMaterialRepository, MaterialRepository>();
builder.Services.AddScoped<IPaperEstimateService, PaperEstimateService>();
builder.Services.AddScoped<IProductionService, ProductionService>();
builder.Services.AddScoped<IProductionRepository, ProductionRepository>();

// Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information); // Đổi từ Debug sang Information
NpgsqlConnection.ClearAllPools();

var app = builder.Build();

// Middleware
app.Use(async (context, next) =>
{
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var requestId = Guid.NewGuid().ToString();

    context.Response.Headers.Add("X-Request-ID", requestId);
    Console.WriteLine($"[{requestId}] Request: {context.Request.Method} {context.Request.Path}");

    try
    {
        await next();
        stopwatch.Stop();
        Console.WriteLine($"[{requestId}] Response: {context.Response.StatusCode} ({stopwatch.ElapsedMilliseconds}ms)");
    }
    catch (Exception ex)
    {
        stopwatch.Stop();
        Console.WriteLine($"[{requestId}] ERROR after {stopwatch.ElapsedMilliseconds}ms: {ex.Message}");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new
        {
            Success = false,
            Message = "Internal server error",
            RequestId = requestId,
            Error = ex.Message
        });
    }
});

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "AMMS API V1");
    c.RoutePrefix = "swagger";
    c.DefaultModelsExpandDepth(-1);
    c.DisplayRequestDuration();
});

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();
app.Run();