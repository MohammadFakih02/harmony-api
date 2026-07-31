# syntax=docker/dockerfile:1
#
# Harmony API — multi-stage build. Produces a small ASP.NET runtime image that
# serves the whole backend (REST + SignalR hubs). Schema/keyspace/bucket all
# self-provision on boot (Postgres via RunMigrationsOnStartup=true, Scylla +
# MinIO on first use), so no manual migration step is needed in the container.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, with only the project files copied, so the restore layer is
# cached until a .csproj (or the shared props) actually changes.
COPY Harmony.slnx ./
COPY src/Directory.Build.props ./src/
COPY src/Harmony.API/Harmony.API.csproj                       ./src/Harmony.API/
COPY src/Harmony.Application/Harmony.Application.csproj       ./src/Harmony.Application/
COPY src/Harmony.Domain/Harmony.Domain.csproj                ./src/Harmony.Domain/
COPY src/Harmony.Infrastructure/Harmony.Infrastructure.csproj ./src/Harmony.Infrastructure/
RUN dotnet restore src/Harmony.API/Harmony.API.csproj

# Copy the rest of the sources and publish the API (its ProjectReferences pull
# in Application/Domain/Infrastructure; the test projects are never referenced).
COPY src/ ./src/
RUN dotnet publish src/Harmony.API/Harmony.API.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is used by the compose healthcheck (the aspnet base image doesn't ship it).
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

# Listen on 8080 inside the container (compose maps it to a host port). No HTTPS
# endpoint is configured, so the app's UseHttpsRedirection is a no-op here.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Harmony.API.dll"]
