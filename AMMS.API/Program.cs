using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Domain;
using AMMS.Infrastructure.Repositories;
using AMMS.Infrastructure.DBContext;
using Microsoft.EntityFrameworkCore;
using AMMS.Infrastructure.Configurations;
using AMMS.Infrastructure.Interfaces;
using AMMS.Infrastructure.FileStorage;

var builder = WebApplication.CreateBuilder(args);
Console.WriteLine(builder.Configuration.GetConnectionString("DefaultConnection"));

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// DbContext
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions =>
        {
            npgsqlOptions.CommandTimeout(60); // ✅ tăng timeout
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorCodesToAdd: null);
        }));

// Configuration
builder.Services.Configure<CloudinaryOptions>(
    builder.Configuration.GetSection("Cloudinary"));


builder.Services.AddScoped<IUploadFileService, UploadFileService>();
builder.Services.AddScoped<ICloudinaryFileStorageService, CloudinaryFileStorageService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run();
