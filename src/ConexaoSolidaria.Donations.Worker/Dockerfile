FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore por projeto: copiar TODOS os .csproj referenciados (transitivamente) antes do restore
# para maximizar o cache de camadas. Donations.Worker -> Contracts + Shared + ServiceDefaults.
COPY ["src/ConexaoSolidaria.Contracts/ConexaoSolidaria.Contracts.csproj", "src/ConexaoSolidaria.Contracts/"]
COPY ["src/ConexaoSolidaria.Shared/ConexaoSolidaria.Shared.csproj", "src/ConexaoSolidaria.Shared/"]
COPY ["src/ConexaoSolidaria.ServiceDefaults/ConexaoSolidaria.ServiceDefaults.csproj", "src/ConexaoSolidaria.ServiceDefaults/"]
COPY ["src/ConexaoSolidaria.Donations.Worker/ConexaoSolidaria.Donations.Worker.csproj", "src/ConexaoSolidaria.Donations.Worker/"]
RUN dotnet restore "src/ConexaoSolidaria.Donations.Worker/ConexaoSolidaria.Donations.Worker.csproj"

COPY . .
RUN dotnet publish "src/ConexaoSolidaria.Donations.Worker/ConexaoSolidaria.Donations.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ConexaoSolidaria.Donations.Worker.dll"]
