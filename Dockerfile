# Imagen de la API para cualquier host que ejecute contenedores (ver docs/DESPLIEGUE.md).
# Construir desde la raíz del repo: docker build -t heartbeat-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Heartbeat.sln ./
COPY src/ src/
COPY tests/ tests/
COPY contracts/ contracts/
RUN dotnet publish src/Heartbeat.Api/Heartbeat.Api.csproj -c Release -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
# La base SQLite vive en App_Data: montar un volumen aquí para que sobreviva a los redespliegues.
VOLUME /app/App_Data
EXPOSE 8080
# Secretos obligatorios en tiempo de ejecución (nunca en la imagen): Jwt__Key (32+ caracteres).
ENTRYPOINT ["dotnet", "Heartbeat.Api.dll"]
