# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY AMMS.sln .

COPY AMMS.API/*.csproj ./AMMS.API/
COPY AMMS.Application/*.csproj ./AMMS.Application/
COPY AMMS.Infrastructure/*.csproj ./AMMS.Infrastructure/
COPY AMMS.Shared/*.csproj ./AMMS.Shared/

RUN dotnet restore

COPY . .

WORKDIR /src/AMMS.API
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

COPY --from=publish /app/publish .

RUN echo "{}" > appsettings.json

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "AMMS.API.dll"]