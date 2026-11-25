# Multi-stage production build: serves both .NET 11 API and React SPA from a single ~115MB container
FROM node:22-alpine AS web-build
WORKDIR /web
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:11.0-alpine AS api-build
WORKDIR /src
COPY backend/ ./
RUN dotnet publish src/FrictionLens.Api/FrictionLens.Api.csproj -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:11.0-alpine AS runtime
WORKDIR /app
RUN apk add --no-cache icu-libs tzdata wget
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
USER $APP_UID
COPY --from=api-build /app/publish ./
COPY --from=web-build /web/dist ./wwwroot
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s CMD wget -qO- http://localhost:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "FrictionLens.Api.dll"]
