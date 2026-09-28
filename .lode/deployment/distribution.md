# Distribution

The image is published to the GitHub Container Registry.

```text
ghcr.io/xakpc/sqlite-mcp-sidecar
```

Code: `.github/workflows/release.yml`.

## Why GHCR and not Docker Hub

`GITHUB_TOKEN` authenticates with `packages: write`, thus the workflow needs **no registry secret**
and no second account. The package follows the visibility of the repository. Docker Hub would add
two secrets and an account to maintain for no gain at this stage.

The image name uses `sqlite-mcp-sidecar`, the repository name, and not the product name
`sqlite-sidecar-mcp`. The two differ. `scripts/token.cs` prints the registry name in its compose
output, thus a generated block matches the real image.

## Triggers and tags

```yaml
on:
  push:
    tags: ['v*']
  workflow_dispatch:
```

`docker/metadata-action` makes the tag set from the git tag:

| Git tag | Image tags |
| --- | --- |
| `v1.4.2` | `1.4.2`, `1.4`, `1`, `latest` |

A `workflow_dispatch` run publishes no `latest`: the `enable` condition tests for a `refs/tags/v`
ref.

## Both architectures in one manifest

```yaml
platforms: linux/amd64,linux/arm64
```

The Dockerfile cross-compiles from the build platform, thus the second architecture costs no
emulation. See [container.md](container.md).

## Provenance

`actions/attest-build-provenance` signs the pushed digest and writes the attestation to the
registry. The product is a security boundary, thus the image states where it was built from. It
costs one step and one permission pair, `id-token: write` and `attestations: write`.

## Related

- [container.md](container.md)
- [summary.md](summary.md)
- [../testing/e2e-harness.md](../testing/e2e-harness.md) — the CI job that tests the same image
