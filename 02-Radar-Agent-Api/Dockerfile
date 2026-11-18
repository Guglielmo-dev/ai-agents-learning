# Fase 1: Build dell'applicazione con SDK .NET 10
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["radar-agent-api.csproj", "./"]
RUN dotnet restore "radar-agent-api.csproj"

COPY . .
RUN dotnet publish "radar-agent-api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Fase 2: Immagine leggera di runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "radar-agent-api.dll"]