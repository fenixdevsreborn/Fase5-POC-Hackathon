FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/ConexaoSolidaria.ServiceDefaults/ConexaoSolidaria.ServiceDefaults.csproj", "src/ConexaoSolidaria.ServiceDefaults/"]
COPY ["src/ConexaoSolidaria.Web/ConexaoSolidaria.Web.csproj", "src/ConexaoSolidaria.Web/"]
RUN dotnet restore "src/ConexaoSolidaria.Web/ConexaoSolidaria.Web.csproj"

COPY . .
RUN dotnet publish "src/ConexaoSolidaria.Web/ConexaoSolidaria.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
# Roda como usuario non-root (o securityContext do k8s tambem forca runAsNonRoot/runAsUser).
USER $APP_UID
ENTRYPOINT ["dotnet", "ConexaoSolidaria.Web.dll"]
