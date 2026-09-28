# The build context is the repository root and not this directory. The restore layer needs
# global.json and Directory.Build.props, and both live at the root:
#
#   docker build -f src/Xakpc.SQLiteMCPSidecar/Dockerfile .
#
# The SDK stage stays on the build platform and cross-compiles to $TARGETARCH. Emulating a whole
# .NET build under QEMU is slow, and the sidecar ships for arm64 too: one application with one
# local SQLite file is exactly the shape that runs on a small ARM server.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# The project file alone first, thus a source change does not invalidate the restore layer.
COPY global.json Directory.Build.props ./
COPY src/Xakpc.SQLiteMCPSidecar/Xakpc.SQLiteMCPSidecar.csproj src/Xakpc.SQLiteMCPSidecar/
RUN dotnet restore src/Xakpc.SQLiteMCPSidecar/Xakpc.SQLiteMCPSidecar.csproj -a $TARGETARCH

COPY src/ src/

# Keep -o. Directory.Build.props now applies inside the image as well, thus the default output
# would land in /src/build/bin and the final stage would copy nothing.
#
# -a selects the target runtime, which is what makes the bundled native SQLite of
# Microsoft.Data.Sqlite come from the right architecture. UseAppHost=false leaves the native launcher
# out, thus the entry point is the runtime and the cross-built image needs no host executable.
RUN dotnet publish src/Xakpc.SQLiteMCPSidecar/Xakpc.SQLiteMCPSidecar.csproj \
    --no-restore \
    -c $BUILD_CONFIGURATION \
    -a $TARGETARCH \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# One port. The reverse proxy terminates TLS, thus the sidecar serves plain HTTP and it has no
# HTTPS redirection and no second port.
EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080

# SQLite needs scratch space for a large sort or a spill. The recommended deployment mounts the root
# filesystem read-only with a small writable tmpfs at /tmp.
ENV SQLITE_TMPDIR=/tmp

# The image carries no database, no token and no permission set. SQLITE_SIDECAR_DB and
# SQLITE_SIDECAR_TOKEN are mandatory, thus a container with no configuration fails at startup
# instead of serving in a degraded state.

# The non-root user of the base image.
USER $APP_UID

COPY --from=build /app/publish .

# No HEALTHCHECK instruction. The platform probes /health over HTTP, and an in-image probe would
# need curl in a runtime image that otherwise carries no shell tooling.
ENTRYPOINT ["dotnet", "Xakpc.SQLiteMCPSidecar.dll"]
