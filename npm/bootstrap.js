#!/usr/bin/env node
"use strict";

/**
 * One-time bootstrap for the four npm packages.
 *
 * Why this exists: trusted publishing (OIDC from GitHub Actions) needs a trusted-publisher
 * relationship on the package *before* CI can publish — but the npmjs.com settings page only
 * exists once a package has been published. `npm trust github` (npm >= 11.5.1) closes that
 * loop: it creates the relationship from the CLI, so a never-published name can be claimed.
 *
 * Order matters and is not interchangeable:
 *   1. trust   — create the GitHub relationship (needs an interactive 2FA login)
 *   2. publish — must succeed within 2 days, or the trust config expires and must be recreated
 *
 * The 2-day window is npm's rule, not ours: "A newly created trusted publisher configuration
 * must complete its first successful publish within 2 days."
 *
 * Usage:
 *   node npm/bootstrap.js --check          # report state, change nothing
 *   node npm/bootstrap.js --trust          # only create the trust relationships
 *   node npm/bootstrap.js --publish        # only publish (assumes trust is already set)
 *   node npm/bootstrap.js                  # trust, then publish
 */

const { execFileSync } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");

const REPO = path.resolve(__dirname, "..");
const NPM_DIR = path.join(REPO, "npm");
const REPO_SLUG = "ProcessMonitor/adesk-pro";
const WORKFLOW_FILE = "release.yml";
const MIN_NPM = [11, 5, 1];

// The launcher depends on the platform packages, so it must be publisher last.
const PACKAGES = [
  "adesk-pro-darwin-arm64",
  "adesk-pro-darwin-x64",
  "adesk-pro-win32-x64",
  "adesk-pro",
];

const args = process.argv.slice(2);
const checkOnly = args.includes("--check");
const trustOnly = args.includes("--trust");
const publishOnly = args.includes("--publish");
const doTrust = !checkOnly && !publishOnly;
const doPublish = !checkOnly && !trustOnly;

function fail(message) {
  console.error(`\nerror: ${message}`);
  process.exit(1);
}

function note(message) {
  console.log(message);
}

function run(cmd, argv, options = {}) {
  try {
    return execFileSync(cmd, argv, {
      encoding: "utf8",
      // A missing package or an absent login is expected during a check; npm writes a
      // multi-line error block to stderr for both, which would drown the report.
      stdio: options.stdio || ["ignore", "pipe", "pipe"],
      ...options,
    }).trim();
  } catch (error) {
    if (options.allowFailure) return null;
    throw error;
  }
}

/** Compares the npm CLI version against the minimum trusted publishing supports. */
function npmVersionOk() {
  const version = run("npm", ["--version"]);
  const got = version.split(".").map((n) => Number.parseInt(n, 10));
  const ok =
    got[0] > MIN_NPM[0] ||
    (got[0] === MIN_NPM[0] &&
      (got[1] > MIN_NPM[1] || (got[1] === MIN_NPM[1] && got[2] >= MIN_NPM[2])));
  return { version, ok };
}

/** True when the package name is already taken on the registry. */
function packageExists(name) {
  const output = run("npm", ["view", name, "version"], { allowFailure: true });
  return output !== null && output.length > 0;
}

function readVersion(pkgName) {
  return JSON.parse(fs.readFileSync(path.join(NPM_DIR, pkgName, "package.json"), "utf8")).version;
}

function preflight() {
  note("preflight");

  const { version: npmVersion, ok } = npmVersionOk();
  note(`  npm ${npmVersion} ${ok ? "(ok)" : "(TOO OLD)"}`);
  if (!ok) {
    fail(
      `trusted publishing needs npm >= ${MIN_NPM.join(".")}. Upgrade with "npm i -g npm@latest".\n` +
        `  (Node 24 LTS bundles npm 11.19, so a Node upgrade also works.)`
    );
  }

  // A trust relationship is bound to the repo's immutable identity; a mismatched
  // repository.url makes the OIDC exchange fail with an auth error that hides the cause.
  const repoUrl = run("git", ["remote", "get-url", "origin"], { cwd: REPO });
  note(`  origin: ${repoUrl}`);
  if (!repoUrl.includes("ProcessMonitor/adesk-pro")) {
    fail(`origin does not look like ${REPO_SLUG}; trusted publishing binds to the repository`);
  }

  for (const pkg of PACKAGES) {
    const manifest = JSON.parse(
      fs.readFileSync(path.join(NPM_DIR, pkg, "package.json"), "utf8")
    );
    const expected = `git+https://github.com/${REPO_SLUG}.git`;
    if (manifest.repository?.url !== expected) {
      fail(`${pkg}: repository.url is "${manifest.repository?.url}", must be "${expected}"`);
    }
  }
  note(`  all ${PACKAGES.length} manifests have the expected repository.url`);

  const who = run("npm", ["whoami"], { allowFailure: true });
  if (who === null) {
    fail(`not logged in. Run "npm login --auth-type=web" first (2FA is required for npm trust).`);
  }
  note(`  logged in as ${who}`);
  return who;
}

function trust() {
  note("\ncreating trusted publisher relationships (this is the step that lets CI publish)");
  for (const pkg of PACKAGES) {
    note(`\n  ${pkg}`);
    try {
      const output = run("npm", [
        "trust", "github", pkg,
        "--file", WORKFLOW_FILE,
        "--repo", REPO_SLUG,
        "--allow-publish",
        "-y",
      ]);
      note(`  ${output.split("\n")[0]}`);
    } catch (error) {
      const text = `${error.stdout || ""}${error.stderr || ""}${error.message || ""}`;
      if (/already|exists/i.test(text)) {
        note("  already configured — leaving it alone");
        continue;
      }
      fail(
        `${pkg}: npm trust failed.\n${text.trim()}\n\n` +
          `  If this says the package does not exist, publish it once first:\n` +
          `    node npm/bootstrap.js --publish`
      );
    }
  }
  note("\ntrust configured. The first publish must succeed within 2 days or it expires.");
}

function publish() {
  note("\npublishing (must complete within 2 days of the trust step)");

  // Binaries must be staged first; publish.js verifies the architecture of each one.
  const version = readVersion("adesk-pro");
  note(`  version ${version}`);

  for (const pkg of PACKAGES.slice(0, -1)) {
    const spec = {
      "adesk-pro-darwin-arm64": "out/osx-arm64/adesk-pro",
      "adesk-pro-darwin-x64": "out/osx-x64/adesk-pro",
      "adesk-pro-win32-x64": "out/win-x64/adesk-pro.exe",
    }[pkg];
    if (!fs.existsSync(path.join(REPO, spec))) {
      fail(
        `missing ${spec}. Build all three platforms first:\n` +
          `  dotnet publish src/AdeskPro.Mac.Cli/AdeskPro.Mac.Cli.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o out/osx-arm64\n` +
          `  dotnet publish src/AdeskPro.Mac.Cli/AdeskPro.Mac.Cli.csproj -c Release -r osx-x64   --self-contained true -p:PublishSingleFile=true -o out/osx-x64\n` +
          `  dotnet publish src/AdeskPro/AdeskPro.csproj           -c Release -r win-x64   --self-contained true -p:PublishSingleFile=true -p:EnableWindowsTargeting=true -o out/win-x64`
      );
    }
  }

  try {
    run("node", [path.join(NPM_DIR, "publish.js")], { cwd: REPO, stdio: "inherit" });
  } catch {
    fail("publish failed; see the output above");
  }

  note(`\nverify with:  npm view adesk-pro version`);
  note(`then try:    npx -y adesk-pro@${version} doctor`);
}

// --- main ---

if (checkOnly) {
  note("adesk-pro npm bootstrap — check only\n");
  const who = run("npm", ["whoami"], { allowFailure: true });
  note(`npm user: ${who === null ? "NOT LOGGED IN" : who}`);
  const { version: npmVersion, ok } = npmVersionOk();
  note(`npm version: ${npmVersion} ${ok ? "(ok)" : "(too old for trusted publishing)"}`);
  note("");
  for (const pkg of PACKAGES) {
    const exists = packageExists(pkg);
    note(`  ${pkg.padEnd(26)} ${exists ? "PUBLISHED" : "not yet published"}`);
  }
  note("\nThis command changed nothing. Run without --check to proceed.");
  process.exit(0);
}

note("adesk-pro npm bootstrap\n");
preflight();
if (doTrust) trust();
if (doPublish) publish();
note("\ndone.");
