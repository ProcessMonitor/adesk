#!/usr/bin/env node
"use strict";

/**
 * adesk-pro launcher.
 *
 * This file contains no product logic. It exists so the agent config says
 *
 *     { "command": "npx", "args": ["-y", "adesk-pro", "mcp"] }
 *
 * instead of asking a human to download a platform binary and hand-type its absolute path.
 * It resolves the binary that npm installed as an optional dependency for this platform and
 * hands the process over to it.
 *
 * Why a launcher and not a reimplementation: the accessibility work (Windows UIA COM objects,
 * macOS AX/CoreFoundation references) is already one tested C# codebase. Rewriting it per
 * platform in JS would fork the ref-resolution logic, which is the part that actually matters.
 *
 * Critical detail: stdio is inherited, never piped. In `mcp` mode the parent agent speaks
 * JSON-RPC over this process's stdin/stdout, so any buffering or transformation here breaks
 * the handshake. `stdio: "inherit"` makes this process a transparent stand-in.
 */

const { spawn } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");

const BINARY = process.platform === "win32" ? "adesk-pro.exe" : "adesk-pro";

/** npm optional-dependency package name for the running platform, or null when unsupported. */
function platformPackage() {
  const { platform, arch } = process;
  if (platform === "darwin") {
    if (arch === "arm64") return "adesk-pro-darwin-arm64";
    if (arch === "x64") return "adesk-pro-darwin-x64";
    return null;
  }
  if (platform === "win32" && arch === "x64") return "adesk-pro-win32-x64";
  return null;
}

/** Absolute path to the packaged binary, or null when the platform package is not installed. */
function resolveBinary() {
  // An explicit override makes it possible to run a locally built binary against the npm
  // launcher, which is how this gets tested before a release is published.
  const override = process.env.ADESK_PRO_BINARY;
  if (override && fs.existsSync(override)) return override;

  const pkg = platformPackage();
  if (!pkg) return null;

  try {
    // require.resolve on the package.json is the only reliable way to find a dependency's
    // root: it works with hoisting, pnpm's symlinked store, and nested installs alike.
    const packageJson = require.resolve(`${pkg}/package.json`);
    const candidate = path.join(path.dirname(packageJson), "bin", BINARY);
    return fs.existsSync(candidate) ? candidate : null;
  } catch {
    return null;
  }
}

function unsupportedMessage() {
  const { platform, arch } = process;
  const pkg = platformPackage();
  if (!pkg) {
    return (
      `adesk-pro does not publish a binary for ${platform} ${arch}.\n` +
      `Supported: macOS (arm64, x64) and Windows (x64).\n` +
      `If you are on a different platform, build from source instead: https://github.com/ProcessMonitor/adesk-pro`
    );
  }
  return (
    `The ${pkg} package is missing, so the adesk-pro binary was not installed.\n` +
    `This usually means npm was run with --no-optional, or the download was skipped.\n` +
    `Fix it with one of:\n` +
    `  npm install adesk-pro --include=optional\n` +
    `  npm rebuild adesk-pro\n` +
    `Or point at a binary you built yourself: ADESK_PRO_BINARY=/abs/path/to/adesk-pro`
  );
}

const binary = resolveBinary();
if (!binary) {
  process.stderr.write(unsupportedMessage() + "\n");
  process.exit(1);
}

// npm tarballs normally preserve the executable bit, but a manual copy (or a git checkout of
// the platform package) may not. Repairing it here costs one stat and removes a class of
// "permission denied" reports.
if (process.platform !== "win32") {
  try {
    fs.accessSync(binary, fs.constants.X_OK);
  } catch {
    try {
      fs.chmodSync(binary, 0o755);
    } catch {
      // Read-only install (global npm prefix owned by root): the spawn below will report it.
    }
  }
}

const child = spawn(binary, process.argv.slice(2), {
  stdio: "inherit",
  // Windows needs shell=false here; spawn with an absolute .exe path does not need a shell.
  windowsHide: true,
  env: {
    ...process.env,
    // Tells the binary it was reached through npm, so `install-mcp` emits an `npx` command
    // instead of a path into node_modules that breaks on the next `npm update`.
    ADESK_PRO_NPM_LAUNCH: "adesk-pro",
  },
});

child.on("error", (error) => {
  process.stderr.write(`failed to start adesk-pro: ${error.message}\n`);
  process.exit(1);
});

child.on("exit", (code, signal) => {
  if (signal) {
    // Re-raise the signal so the caller sees the real cause (Ctrl-C, SIGTERM) rather than a
    // generic exit code.
    process.kill(process.pid, signal);
    return;
  }
  process.exit(code === null ? 1 : code);
});
