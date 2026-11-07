# Qahwa server with the Desk built in. Build from the repository root:  docker build -t qahwa .
# (docker compose up --build does this for you, see docker-compose.yml)

# 1. The Desk (React) is built first; Vite writes it to src/Qahwa.Server/wwwroot.
FROM node:22-alpine AS desk
WORKDIR /src/src/Qahwa.Desk
COPY src/Qahwa.Desk/package.json src/Qahwa.Desk/package-lock.json ./
RUN npm ci
COPY src/Qahwa.Desk/ ./
COPY tests/expression-vectors /src/tests/expression-vectors
RUN npm run build

# 2. The server is published with the Desk next to it.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /src
COPY . .
COPY --from=desk /src/src/Qahwa.Server/wwwroot src/Qahwa.Server/wwwroot
RUN dotnet publish src/Qahwa.Server -c Release -o /app --no-self-contained \
 && mkdir -p /app/samples && cp -r samples/doctypes /app/samples/doctypes

# 3. A small runtime image that runs as an ordinary user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=server /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Qahwa__DocTypePaths__0=/app/samples/doctypes \
    Qahwa__AutoMigrate=true
RUN mkdir -p /app/data && chown -R $APP_UID /app/data
USER $APP_UID
VOLUME /app/data
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=5 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /api/health HTTP/1.0\r\n\r\n" >&3 && grep -q "\"ok\"" <&3' || exit 1
ENTRYPOINT ["dotnet", "Qahwa.Server.dll"]
