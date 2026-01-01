using AMMS.Application.Extensions;
using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Infrastructure.Configurations;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.FileStorage;
using AMMS.Infrastructure.Interceptors;
using AMMS.Infrastructure.Interfaces;
using AMMS.Infrastructure.Repositories;
using AMMS.Shared.DTOs.Email;
using AMMS.Shared.DTOs.PayOS;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions =>
        {
            npgsqlOptions.CommandTimeout(60);

            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(2),
                errorCodesToAdd: null
            );
        });
    options.AddInterceptors(
        sp.GetRequiredService<CostEstimateRoundingInterceptor>()
    );
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.Converters.Add(new DateTimeConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddMemoryCache();
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
builder.Services.Configure<SendGridSettings>(
    builder.Configuration.GetSection("SendGrid"));
builder.Services.AddSingleton<CostEstimateRoundingInterceptor>();
builder.Services.Configure<PayOsOptions>(builder.Configuration.GetSection("PayOS"));


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
builder.Services.AddScoped<IPurchaseRepository, PurchaseRepository>();
builder.Services.AddScoped<IPurchaseService, PurchaseService>();
builder.Services.AddScoped<IOrderLookupService, OrderLookupService>();
builder.Services.AddScoped<IProductTypeProcessRepository, ProductTypeProcessRepository>();
builder.Services.AddScoped<ITaskRepository, TaskRepository>();
builder.Services.AddScoped<ITaskLogRepository, TaskLogRepository>();
builder.Services.AddScoped<IMaterialPerUnitService, MaterialPerUnitService>();
builder.Services.AddScoped<IProductTypeProcessSeedService, ProductTypeProcessSeedService>();
builder.Services.AddScoped<IProductionSchedulingService, ProductionSchedulingService>();
builder.Services.AddScoped<ITaskQrTokenService, TaskQrTokenService>();
builder.Services.AddScoped<ITaskScanService, TaskScanService>();
builder.Services.AddScoped<IMaterialPurchaseRequestService, MaterialPurchaseRequestService>();
builder.Services.AddHttpClient<IPayOsService, PayOsService>();
builder.Services.AddScoped<IPaymentsService, PaymentsService>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IBomRepository, BomRepository>();
builder.Services.AddScoped<IProcessCostRuleService, ProcessCostRuleService>(); 
builder.Services.AddScoped<IProcessCostRuleRepository, ProcessCostRuleRepository>();


// Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

var app = builder.Build();

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