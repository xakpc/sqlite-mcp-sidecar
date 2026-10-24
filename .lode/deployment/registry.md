# MCP registry listing

The sidecar is listed in the official MCP Registry as `io.github.xakpc/sqlite-mcp-sidecar`. Other
MCP directories (Glama, mcp.directory, PulseMCP, the GitHub MCP Registry) import from the official
registry, thus this one entry is the source for most listings.

Code: `server.json` at the repository root, and one `LABEL` in
`src/Xakpc.SQLiteMCPSidecar/Dockerfile`.

```mermaid
flowchart LR
    tag[git tag vX.Y.Z] --> rel[release.yml]
    rel --> img[ghcr.io image X.Y.Z with label]
    img --> pub[mcp-publisher publish]
    sj[server.json version X.Y.Z] --> pub
    pub --> reg[(Official MCP Registry)]
    reg --> dirs[Glama, mcp.directory, PulseMCP, GitHub registry]
```

## Ownership proof

The registry proves that the publisher owns an OCI package with an image label. The value must be
equal to `name` in `server.json`:

```dockerfile
LABEL io.modelcontextprotocol.server.name="io.github.xakpc/sqlite-mcp-sidecar"
```

The `io.github.xakpc/` namespace needs a GitHub login of the account `xakpc`:

```bash
mcp-publisher login github
mcp-publisher publish
```

## The package entry

One `oci` package with the transport `streamable-http`. The sidecar is an HTTP server and not a
stdio process, thus the entry gives the URL that the client calls after it starts the container:

```json
"transport": {
  "type": "streamable-http",
  "url": "http://localhost:8080/db/mcp",
  "headers": [{ "name": "Authorization", "value": "Bearer {token}", "isSecret": true }]
}
```

The `runtimeArguments` repeat the hardened `docker run` of `README.md`: loopback port, a writable
`/data` mount, `--read-only`, `--tmpfs /tmp`, `--cap-drop ALL`. The `environmentVariables` name the
two mandatory values and the permission set with its read-only default.

## Invariants

- `version` in `server.json`, the tag in `identifier`, and the git tag are the same value. A version
  in the registry is immutable, thus each release needs a new `server.json` version.
- Publish only after `release.yml` pushed the image. The registry reads the label from the tag in
  `identifier`; an image that does not exist yet fails the check.
- `description` is at most 100 characters.
- `mcp-publisher validate` checks the file before a publish.

## Lessons

- An image from before the label (`0.1.0`, `0.1.1`) cannot be listed. The first listed version is
  `0.1.2`.

## Related

- [distribution.md](distribution.md): how a tag becomes an image
- [container.md](container.md): the Dockerfile
