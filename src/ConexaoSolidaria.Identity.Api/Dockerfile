FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore por projeto: copiar TODOS os .csproj referenciados (transitivamente) antes do restore
# para maximizar o cache de camadas. Identity.Api -> Contracts + ServiceDefaults.
# Os arquivos da raiz sao obrigatorios: Directory.Packages.props centraliza as versoes (CPM),
# sem ele os PackageReference ficam sem Version e o restore falha com NU1015.
COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/ConexaoSolidaria.Contracts/ConexaoSolidaria.Contracts.csproj", "src/ConexaoSolidaria.Contracts/"]
COPY ["src/ConexaoSolidaria.ServiceDefaults/ConexaoSolidaria.ServiceDefaults.csproj", "src/ConexaoSolidaria.ServiceDefaults/"]
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
