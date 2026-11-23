# Publishing to npm

The four packages publish with **OIDC trusted publishing** — GitHub Actions exchanges a short-lived
identity token for registry credentials. There is no `NPM_TOKEN` secret, and nothing long-lived to
rotate.

## One-time bootstrap (required once, before CI can publish)

Trusted publishing normally needs a relationship configured on the package's settings page — but
that page only exists once a package has been published. `npm trust github` (npm ≥ 11.5.1) closes
the loop from the CLI, so a never-published name can be claimed.

```bash
npm login --auth-type=web          # opens a browser; npm trust requires 2FA
node npm/bootstrap.js --check      # report state, change nothing
node npm/bootstrap.js              # create the trust relationships, then publish
```

`bootstrap.js` does, in this order:

1. **preflight** — npm ≥ 11.5.1, logged in, `repository.url` on all four manifests pointing at
   `ProcessMonitor/adesk-pro` (the trust is bound to the repo's immutable identity; a mismatch
   surfaces later as an opaque auth failure)
2. **trust** — `npm trust github <pkg> --file release.yml --repo ProcessMonitor/adesk-pro --allow-publish`
   for each package
3. **publish** — `node npm/publish.js`

> **The 2-day window.** npm expires a new trust configuration if it is not validated by a
> successful publish within two days: *"A newly created trusted publisher configuration must
> complete its first successful publish within 2 days."* Run `--trust` and `--publish` in the same
> sitting. If the window lapses, delete the configuration and create it again.

Binaries must exist before step 3. Build all three platforms:

```bash
dotnet publish src/AdeskPro.Mac.Cli/AdeskPro.Mac.Cli.csproj -c Release -r osx-arm64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o out/osx-arm64

dotnet publish src/AdeskPro.Mac.Cli/AdeskPro.Mac.Cli.csproj -c Release -r osx-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o out/osx-x64

dotnet publish src/AdeskPro/AdeskPro.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:EnableWindowsTargeting=true -o out/win-x64
```

## Ongoing releases (after bootstrap)

Nothing manual. Push a tag:

```bash
git tag v0.3.0
git push origin v0.3.0
```

`release.yml` then builds the three binaries, runs the tests, publishes a GitHub Release, and
publishes the four npm packages over OIDC.

## Package layout and why the order is fixed

```
npm/adesk-pro                    launcher (bin/adesk-pro.js), no product logic
npm/adesk-pro-darwin-arm64       ┐
npm/adesk-pro-darwin-x64         ├ one self-contained binary each, os/cpu gated
npm/adesk-pro-win32-x64          ┘
```

The launcher's `optionalDependencies` point at the platform packages, so they must reach the
registry **before** it. `publish.js` publishes in that order and refuses to start if a binary is
missing or has the wrong architecture — a single local `publish/` directory otherwise packs an
arm64 build into the x64 tarball, which only end users would discover.

## Verifying a release

```bash
npm view adesk-pro version
npx -y adesk-pro@<version> doctor
```

## Notes

- **Node 24+** is required in CI because trusted publishing needs npm ≥ 11.5.1 and Node ≥ 22.14;
  Node 20 bundles npm 10 and fails at publish time with an auth error that looks unrelated.
- Provenance is generated automatically for a public repo published from a public workflow, so
  `--provenance` is not passed.
- The macOS binaries are ad-hoc signed, not notarised. npm installs do not set the quarantine
  flag, so `npx` paths are unaffected; direct GitHub Release downloads need
  `xattr -d com.apple.quarantine`.
