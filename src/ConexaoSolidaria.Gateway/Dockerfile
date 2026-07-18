FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Os arquivos da raiz sao obrigatorios: Directory.Packages.props centraliza as versoes (CPM),
# sem ele os PackageReference ficam sem Version e o restore falha com NU1015.
COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/ConexaoSolidaria.ServiceDefaults/ConexaoSolidaria.ServiceDefaults.csproj", "src/ConexaoSolidaria.ServiceDefaults/"]
COPY ["src/ConexaoSolidaria.Gateway/ConexaoSolidaria.Gateway.csproj", "src/ConexaoSolidaria.Gateway/"]
RUN dotnet restore "src/ConexaoSolidaria.Gateway/ConexaoSolidaria.Gateway.csproj"

COPY . .
RUN dotnet publish "src/ConexaoSolidaria.Gateway/ConexaoSolidaria.Gateway.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
# Roda como usuario non-root (o securityContext do k8s tambem forca runAsNonRoot/runAsUser).
USER $APP_UID
ENTRYPOINT ["dotnet", "ConexaoSolidaria.Gateway.dll"]
