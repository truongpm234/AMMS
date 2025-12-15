# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution file and restore dependencies
COPY *.sln .
COPY AMMS.API/*.csproj ./AMMS.API/
COPY AMMS.Application/*.csproj ./AMMS.Application/
COPY AMMS.Domain/*.csproj ./AMMS.Domain/
COPY AMMS.Infrastructure/*.csproj ./AMMS.Infrastructure/
COPY AMMS.Shared/*.csproj ./AMMS.Shared/

RUN dotnet restore

# Copy all source files
COPY . .

# Build the project
WORKDIR /src/AMMS.API
RUN dotnet build -c Release -o /app/build

# Stage 2: Publish
FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Copy published files
COPY --from=publish /app/publish .

# Expose port (Render uses port 8080 by default)
EXPOSE 8080

# Set environment variable for ASP.NET Core
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Entry point
ENTRYPOINT ["dotnet", "AMMS.API.dll"]

