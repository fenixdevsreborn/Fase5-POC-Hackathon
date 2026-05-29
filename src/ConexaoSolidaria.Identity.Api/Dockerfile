FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/ConexaoSolidaria.Shared/ConexaoSolidaria.Shared.csproj", "src/ConexaoSolidaria.Shared/"]
COPY ["src/ConexaoSolidaria.Identity.Api/ConexaoSolidaria.Identity.Api.csproj", "src/ConexaoSolidaria.Identity.Api/"]
RUN dotnet restore "src/ConexaoSolidaria.Identity.Api/ConexaoSolidaria.Identity.Api.csproj"

COPY . .
RUN dotnet publish "src/ConexaoSolidaria.Identity.Api/ConexaoSolidaria.Identity.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ConexaoSolidaria.Identity.Api.dll"]
