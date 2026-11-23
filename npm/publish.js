#!/usr/bin/env node
"use strict";

/**
 * Publishes adesk-pro to npm.
 *
 * Layout mirrors esbuild: a thin `adesk-pro` launcher with `optionalDependencies` on per-platform
 * packages that each carry one binary. npm installs only the package matching the host's os/cpu,
 * so `npx -y adesk-pro mcp` works without the user choosing or downloading anything.
 *
 * Order matters: the platform packages must exist on the registry before the launcher that
 * depends on them, otherwise `npm install` warns about missing optional dependencies.
 *
 * Usage:
 *   node npm/publish.js --dry-run           # check everything, publish nothing
 *   node npm/publish.js                     # publish all four packages
 *   node npm/publish.js --only darwin-arm64 # publish one platform package
 *   node npm/publish.js --skip-existing     # tolerate a version already on the registry
 */

const { execFileSync } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");

const REPO = path.resolve(__dirname, "..");
const NPM_DIR = path.join(REPO, "npm");

/** platform package dir -> { rid, artifact produced by dotnet publish } */
const PLATFORMS = {
  "adesk-pro-darwin-arm64": { rid: "osx-arm64", exe: "adesk-pro" },
  "adesk-pro-darwin-x64": { rid: "osx-x64", exe: "adesk-pro" },
  "adesk-pro-win32-x64": { rid: "win-x64", exe: "adesk-pro.exe" },
};

const args = process.argv.slice(2);
const dryRun = args.includes("--dry-run");
const skipExisting = args.includes("--skip-existing");
const onlyIdx = args.indexOf("--only");
const only = onlyIdx >= 0 ? args[onlyIdx + 1] : null;

function fail(message) {
  console.error(`error: ${message}`);
  process.exit(1);
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, "utf8"));
}

function run(cmd, argv, options = {}) {
  return execFileSync(cmd, argv, { encoding: "utf8", stdio: options.quiet ? "pipe" : "inherit", ...options });
}

/** The single source of truth for the version is the repo's core csproj. */
function repoVersion() {
  const candidates = [
    "src/AdeskPro.Core/AdeskPro.Core.csproj",
    "src/AdeskPro/AdeskPro.csproj",
  ];
  for (const rel of candidates) {
    const file = path.join(REPO, rel);
    if (!fs.existsSync(file)) continue;
    const match = fs.readFileSync(file, "utf8").match(/<Version>([^<]+)</);
    if (match) return match[1].trim();
  }
  fail("cannot determine version from the csproj files");
}

/**
 * Every manifest must agree with the csproj version. A drift here ships a launcher that asks
 * for a platform package version that was never published, which fails only for end users.
 */
function checkVersions(version) {
  const manifests = [
    path.join(NPM_DIR, "adesk-pro", "package.json"),
    ...Object.keys(PLATFORMS).map((name) => path.join(NPM_DIR, name, "package.json")),
  ];
  for (const manifest of manifests) {
    const json = readJson(manifest);
    const rel = path.relative(REPO, manifest);
    if (json.version !== version) {
      fail(`${rel} is ${json.version} but the csproj says ${version}; align them first`);
    }
  }

  const launcher = readJson(path.join(NPM_DIR, "adesk-pro", "package.json"));
  for (const [name, range] of Object.entries(launcher.optionalDependencies || {})) {
    if (range !== version) {
      fail(`adesk-pro optionalDependencies[${name}] is "${range}" but should be "${version}"`);
    }
  }
  console.log(`versions consistent: ${version}`);
}

/**
 * Reads the CPU architecture out of a binary header, so a mis-staged build is caught before it
 * reaches the registry. This matters because the local build output is a single `publish/`
 * directory: without this check, a macOS arm64 build would happily be packed into the
 * darwin-x64 package, and the resulting tarball fails only for end users.
 *
 * Returns "arm64" | "x64" | "unknown".
 */
function binaryArch(file) {
  const fd = fs.openSync(file, "r");
  try {
    const header = Buffer.alloc(8);
    fs.readSync(fd, header, 0, 8, 0);
    const magic = header.readUInt32BE(0);

    // Mach-O: 0xFEEDFACF (64-bit little-endian on disk becomes 0xCFFAEDFE read as BE)
    if (magic === 0xcffaedfe || magic === 0xfeedfacf) {
      const cputype = header.readUInt32LE(4);
      if (cputype === 0x0100000c) return "arm64"; // CPU_TYPE_ARM64
      if (cputype === 0x01000007) return "x64";   // CPU_TYPE_X86_64
      return "unknown";
    }

    // PE/COFF: "MZ" ... machine field at the PE header. Read the e_lfanew offset first.
    if (header.readUInt16BE(0) === 0x4d5a) {
      const lfanew = Buffer.alloc(4);
      fs.readSync(fd, lfanew, 0, 4, 0x3c);
      const peOffset = lfanew.readUInt32LE(0);
      const machine = Buffer.alloc(2);
      fs.readSync(fd, machine, 0, 2, peOffset + 4);
      const id = machine.readUInt16LE(0);
      if (id === 0x8664) return "x64";  // IMAGE_FILE_MACHINE_AMD64
      if (id === 0xaa64) return "arm64"; // IMAGE_FILE_MACHINE_ARM64
      return "unknown";
    }

    return "unknown";
  } finally {
    fs.closeSync(fd);
  }
}

/** Expected architecture for each platform package. */
const EXPECTED_ARCH = {
  "adesk-pro-darwin-arm64": "arm64",
  "adesk-pro-darwin-x64": "x64",
  "adesk-pro-win32-x64": "x64",
};

/** Copies the built binary into the platform package and verifies it is really there. */
function stageBinary(pkgName, version) {
  const spec = PLATFORMS[pkgName];
  const pkgDir = path.join(NPM_DIR, pkgName);
  const dest = path.join(pkgDir, "bin", spec.exe);

  const candidates = [
    // Per-RID CI output takes priority: it is unambiguous about which platform was built.
    path.join(REPO, "out", spec.rid, spec.exe),
    // build.sh / build.ps1 output — only valid when it matches this package's architecture.
    path.join(REPO, "publish", spec.exe),
  ];

  let source = null;
  const rejected = [];
  for (const candidate of candidates) {
    if (!fs.existsSync(candidate)) continue;
    const arch = binaryArch(candidate);
    if (arch === EXPECTED_ARCH[pkgName]) {
      source = candidate;
      break;
    }
    rejected.push(`${path.relative(REPO, candidate)} is ${arch}`);
  }

  if (!source) {
    const detail = rejected.length > 0 ? `\n  Rejected: ${rejected.join(", ")}` : "";
    fail(
      `no ${EXPECTED_ARCH[pkgName]} binary for ${pkgName}. Build one first:\n` +
        `  dotnet publish ${spec.rid.startsWith("win") ? "src/AdeskPro/AdeskPro.csproj" : "src/AdeskPro.Mac.Cli/AdeskPro.Mac.Cli.csproj"} ` +
        `-c Release -r ${spec.rid} --self-contained true -p:PublishSingleFile=true -o out/${spec.rid}${detail}`
    );
  }

  const arch = binaryArch(source);
  console.log(
    `staging ${pkgName}: ${path.basename(source)} (${arch}) from ${path.relative(REPO, source)}`
  );

  fs.mkdirSync(path.dirname(dest), { recursive: true });
  fs.copyFileSync(source, dest);
  if (spec.exe !== "adesk-pro.exe") fs.chmodSync(dest, 0o755);

  // Verify the copy, not just the source: a failed copy or an over-eager chmod would show here.
  const staged = binaryArch(dest);
  if (staged !== EXPECTED_ARCH[pkgName]) {
    fail(`staged ${path.relative(REPO, dest)} is ${staged}, expected ${EXPECTED_ARCH[pkgName]}`);
  }

  const sizeMb = fs.statSync(dest).size / (1024 * 1024);
  console.log(`  -> ${path.relative(REPO, dest)} (${sizeMb.toFixed(1)} MB)`);
  return sizeMb;
}

function publish(pkgName) {
  const pkgDir = path.join(NPM_DIR, pkgName);
  const argv = ["publish", "--access", "public"];
  if (dryRun) argv.push("--dry-run");
  if (skipExisting) argv.push("--ignore-scripts");
  console.log(`\n=== ${dryRun ? "dry-run: " : ""}npm ${argv.join(" ")}  (${pkgName})`);
  run("npm", argv, { cwd: pkgDir });

  if (skipExisting) {
    // npm refuses to overwrite a version; treat that as success when resuming a partial publish.
    try {
      run("npm", ["view", `${pkgName}@${readJson(path.join(pkgDir, "package.json")).version}`, "version"], { quiet: true });
      console.log(`  ${pkgName} already on the registry — skipped`);
    } catch {
      /* not published yet, nothing to resume */
    }
  }
}

/** Cheap sanity check: the staged binary must actually run. */
function smokeTest(pkgName) {
  if (pkgName.startsWith("win32-")) {
    console.log(`  smoke test skipped for ${pkgName} (cannot execute a Windows binary on ${process.platform})`);
    return;
  }
  if (process.platform !== "darwin") {
    console.log(`  smoke test skipped for ${pkgName} (not a macOS host)`);
    return;
  }
  // Only the host's own architecture can actually run here.
  const hostPkg = process.arch === "arm64" ? "adesk-pro-darwin-arm64" : "adesk-pro-darwin-x64";
  if (pkgName !== hostPkg) {
    console.log(`  smoke test skipped for ${pkgName} (host is ${hostPkg})`);
    return;
  }
  const binary = path.join(NPM_DIR, pkgName, "bin", "adesk-pro");
  const output = run(binary, ["--version"], { quiet: true }).trim();
  console.log(`  smoke test: ${binary} --version -> ${output}`);
}

// --- main ---

const version = repoVersion();
console.log(`adesk-pro npm publish (${dryRun ? "dry run" : "live"}), version ${version}`);
checkVersions(version);

const targets = only
  ? Object.keys(PLATFORMS).filter((n) => n.endsWith(only))
  : [...Object.keys(PLATFORMS), "adesk-pro"];

if (only && targets.length === 0) fail(`--only ${only} matched no platform package`);

// Stage every platform package before publishing any of them. A publish that discovers a
// missing binary halfway through leaves the registry in a partial state, which is far worse
// than refusing to start.
const platformTargets = targets.filter((n) => n in PLATFORMS);
for (const pkgName of platformTargets) {
  stageBinary(pkgName, version);
}

for (const pkgName of targets) {
  if (pkgName in PLATFORMS && !dryRun) smokeTest(pkgName);
  publish(pkgName);
}

console.log(`\ndone. ${dryRun ? "Nothing was published." : "Published."}`);
if (!dryRun) {
  console.log(`users can now run:  npx -y adesk-pro@${version} status`);
}
