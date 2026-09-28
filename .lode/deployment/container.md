# The container

The sidecar ships as a Linux OCI image for `linux/amd64` and `linux/arm64`. Both architectures come
from one cross-compiled build. Distribution is in [distribution.md](distribution.md), the platform
recipes are in [platforms.md](platforms.md).

Code: `src/Xakpc.SQLiteMCPSidecar/Dockerfile`, `.dockerignore`, `compose.yaml`.

## The build context is the repository root

**Invariant.** The context is the repository root and not the project directory:

```bash
docker build -f src/Xakpc.SQLiteMCPSidecar/Dockerfile .
```

The restore layer needs `global.json` and `Directory.Build.props`, and both live at the root. With
the old `src` context neither reached the image, and the root `.dockerignore` was never read at all,
because Docker reads it from the context.

`Directory.Build.props` now applies inside the image and redirects output to `/src/build/bin`. The
explicit `-o /app/publish` wins, thus the final stage still finds the files. **Do not remove that
`-o`**: without it the publish output moves and the final stage copies nothing.

The csproj carries `<DockerfileContext>..\..</DockerfileContext>`, thus Visual Studio uses the same
context.

## Cross-compilation, not emulation

```dockerfile
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
RUN dotnet restore src/…/Xakpc.SQLiteMCPSidecar.csproj -a $TARGETARCH
RUN dotnet publish … -a $TARGETARCH -o /app/publish /p:UseAppHost=false
```

The SDK stage stays on the build platform. `-a $TARGETARCH` selects the target runtime, which is
what makes the bundled native SQLite of `Microsoft.Data.Sqlite` come from the right architecture:
the arm64 image carries an AArch64 `libe_sqlite3.so`. `UseAppHost=false` leaves out the native
launcher, thus the entry point is `dotnet Xakpc.SQLiteMCPSidecar.dll` and no host executable has to
match the architecture.

Emulating the whole .NET build under QEMU works and it is slow. One application with one local
SQLite file is exactly the shape that runs on a small ARM server, thus arm64 is not optional.

## The final stage

```text
mcr.microsoft.com/dotnet/aspnet:10.0
USER $APP_UID                    # uid 1654, non-root
EXPOSE 8080                      # one port only
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV SQLITE_TMPDIR=/tmp
```

- **One port.** The proxy terminates TLS, thus the template `EXPOSE 8081` is gone.
- **`SQLITE_TMPDIR=/tmp`.** SQLite needs scratch space for a large sort or a spill, and the
  recommended run mounts the root filesystem read-only with a small `tmpfs` at `/tmp`.
- **No `HEALTHCHECK` instruction.** `/health` answers over HTTP and a platform probes it directly. An
  in-image probe would need `curl` in a runtime image that otherwise carries no shell tooling.
- **No configuration.** The image carries no database, no token and no permission set.
  `SQLITE_SIDECAR_DB` and `SQLITE_SIDECAR_TOKEN` are mandatory, thus a container with no
  configuration fails at startup instead of serving in a degraded state.

The base image is the `aspnet` runtime image. A smaller base waits on the NativeAOT result. See
[../plans/open-questions.md](../plans/open-questions.md).

## Runtime hardening

`compose.yaml` and every recipe carry the same four settings:

```yaml
read_only: true
tmpfs: [/tmp]
cap_drop: [ALL]
security_opt: [no-new-privileges:true]
```

## Filesystem access

| Path | Access |
| --- | --- |
| root filesystem | read-only |
| `/data` | read **and write**, also for a read-only permission set |
| `/backups` | writable when the `backup` permission is on |
| `/tmp` | a small writable `tmpfs`, and `SQLITE_TMPDIR` points at it |

**A read-only deployment still needs write access to `/data`.** SQLite opens the `-shm`
shared-memory file read-write also for a read-only connection to a WAL database. A read-only mount
therefore fails, and the failure reads like a permission problem and not like a configuration
problem.

The process deletes stale `*.db.partial` files in the backup directory at startup. See
[../database/backups.md](../database/backups.md).

**The container user is uid 1654 and the host does not know it.** A bind mount from a host
directory that the host user owns is not writable for that uid. CI opens the mount with
`chmod -R 0777 build/dev` before it starts the container. A named volume has no such problem.

## compose.yaml

`compose.yaml` at the repository root is both the local run and the copy-paste example. It builds
the local Dockerfile and carries the published image name in a comment.

```powershell
./scripts/seed-dev-db.ps1        # one time. The sidecar never creates the database.
docker compose up --build
```

It serves `http://localhost:8080/db/mcp` with the token `dev-token`, which is what the launch
profiles use, thus `src/Xakpc.SQLiteMCPSidecar/mcp.http` reaches the container with no change. The
permission set is `${SIDECAR_PERMISSIONS:-schema,read}`:

```bash
SIDECAR_PERMISSIONS=schema,read,danger-raw-write docker compose up
```

**Invariant.** `danger-raw-write` is not in the file. It must be an explicit operator decision, and
an example that an operator copies without thought is the wrong place for it. The same rule holds
for the compose block in the README.

`deploy.replicas: 1` is documentary under `docker compose up`, and it states the rule that matters:
the request budget, the write budget and the idempotency cache are process memory, thus a second
replica makes all three weaker and reports nothing.

## The container launch profile

`src/Xakpc.SQLiteMCPSidecar/Properties/launchSettings.json` has a fourth profile,
`container (schema,read)`, with `commandName: "Docker"`. It mounts `build/dev` at `/data`, publishes
8080 and sets no HTTPS port. `commandName: "DockerCompose"` would need a `.dcproj`, which this
repository does not have and does not need.

## Why this matters more than developer comfort

The SQLite sandbox depends on the native SQLite build, thus a Windows developer run does not prove
the shipped behaviour. The container is the only target that does, and `SidecarHarness` reaches it
with no test change. CI runs the suite against the image on every push. See
[../testing/e2e-harness.md](../testing/e2e-harness.md).

That run has already paid for itself: it found the busy-database defect in
[../decisions/0009-a-locked-database-is-not-a-caller-mistake.md](../decisions/0009-a-locked-database-is-not-a-caller-mistake.md),
which no in-process test could reach, because the in-process harness gives each test its own
database file and never has two clients on one file.

## Related

- [summary.md](summary.md)
- [platforms.md](platforms.md) — Kamal, Coolify, a plain VPS, Fly.io
- [distribution.md](distribution.md) — the published image
- [../configuration/options.md](../configuration/options.md)
- [../security/public-endpoint.md](../security/public-endpoint.md)
- [../testing/e2e-harness.md](../testing/e2e-harness.md)
