using AMMS.Application.Abstractions;
using AMMS.Application.Services;
using AMMS.Infrastructure.Configurations;
using AMMS.Infrastructure.FileStorage;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configuration
builder.Services.Configure<CloudinaryOptions>(
    builder.Configuration.GetSection("Cloudinary"));
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables();

// Application services
builder.Services.AddScoped<IUploadFileService, UploadFileService>();

// Infrastructure services
builder.Services.AddScoped<ICloudinaryFileStorageService, CloudinaryFileStorageService>();


var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1");
    c.RoutePrefix = string.Empty;
});

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
