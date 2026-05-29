FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/ConexaoSolidaria.Shared/ConexaoSolidaria.Shared.csproj", "src/ConexaoSolidaria.Shared/"]
COPY ["src/ConexaoSolidaria.Campaigns.Api/ConexaoSolidaria.Campaigns.Api.csproj", "src/ConexaoSolidaria.Campaigns.Api/"]
RUN dotnet restore "src/ConexaoSolidaria.Campaigns.Api/ConexaoSolidaria.Campaigns.Api.csproj"

COPY . .
RUN dotnet publish "src/ConexaoSolidaria.Campaigns.Api/ConexaoSolidaria.Campaigns.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ConexaoSolidaria.Campaigns.Api.dll"]
