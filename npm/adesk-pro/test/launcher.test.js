#!/usr/bin/env node
"use strict";

/**
 * Launcher tests. The launcher is glue, and glue fails silently in exactly the case that
 * matters: an agent's JSON-RPC handshake. So these tests drive real child processes over real
 * pipes instead of asserting on strings.
 *
 * Run with:  node npm/adesk-pro/test/launcher.test.js
 * Binary-dependent tests are skipped (not failed) when no binary has been built, so the suite
 * stays useful on a machine that has not built this platform yet.
 */

const { spawnSync, spawn } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");
const assert = require("node:assert");

const LAUNCHER = path.resolve(__dirname, "..", "bin", "adesk-pro.js");
const REPO = path.resolve(__dirname, "..", "..", "..");
const HOST_EXE = process.platform === "win32" ? "adesk-pro.exe" : "adesk-pro";
const HOST_BINARY = path.join(REPO, "publish", HOST_EXE);

const results = { passed: 0, failed: 0, skipped: 0 };

function ok(name) {
  results.passed++;
  console.log(`  ok    ${name}`);
}

function fail(name, message) {
  results.failed++;
  console.log(`  FAIL  ${name}`);
  if (message) console.log(`        ${message.split("\n").join("\n        ")}`);
}

function skip(name, reason) {
  results.skipped++;
  console.log(`  skip  ${name} (${reason})`);
}

function testSync(name, fn) {
  try {
    fn();
    ok(name);
  } catch (error) {
    fail(name, error.message);
  }
}

function runLauncher(args, env = {}, input = undefined) {
  return spawnSync(process.execPath, [LAUNCHER, ...args], {
    encoding: "utf8",
    env: { ...process.env, ...env },
    input,
    timeout: 30_000,
  });
}

/** Drives the launcher in MCP mode and resolves with the first JSON-RPC response line. */
function mcpHandshake(timeoutMs = 20_000) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [LAUNCHER, "mcp"], {
      env: { ...process.env, ADESK_PRO_BINARY: HOST_BINARY },
      stdio: ["pipe", "pipe", "pipe"],
    });

    let stdout = "";
    let stderr = "";
    let settled = false;

    const finish = (error, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      try { child.stdin.end(); } catch {}
      try { child.kill(); } catch {}
      error ? reject(error) : resolve(value);
    };

    const timer = setTimeout(
      () => finish(new Error(`no response within ${timeoutMs}ms (stderr: ${stderr.slice(0, 300)})`)),
      timeoutMs
    );

    child.stderr.on("data", (chunk) => { stderr += chunk.toString("utf8"); });
    child.on("error", (error) => finish(error));

    child.stdout.on("data", (chunk) => {
      stdout += chunk.toString("utf8");
      const newline = stdout.indexOf("\n");
      if (newline < 0) return;
      const line = stdout.slice(0, newline);
      try {
        finish(null, JSON.parse(line));
      } catch (error) {
        finish(new Error(`first stdout line is not JSON (${error.message}): ${line.slice(0, 200)}`));
      }
    });

    child.stdin.write(
      JSON.stringify({
        jsonrpc: "2.0",
        id: 1,
        method: "initialize",
        params: { protocolVersion: "2025-06-18" },
      }) + "\n"
    );
  });
}

async function main() {
  console.log("launcher tests\n");

  // --- static checks, no binary needed ---

  testSync("launcher is valid JavaScript", () => {
    const result = spawnSync(process.execPath, ["--check", LAUNCHER], { encoding: "utf8" });
    assert.strictEqual(result.status, 0, result.stderr);
  });

  testSync("launcher declares a shebang for global bin installs", () => {
    const first = fs.readFileSync(LAUNCHER, "utf8").split("\n")[0];
    assert.strictEqual(first, "#!/usr/bin/env node");
  });

  testSync("launcher uses inherited stdio, never piped", () => {
    const source = fs.readFileSync(LAUNCHER, "utf8");
    // A piped stdio would break the MCP JSON-RPC handshake.
    assert.match(source, /stdio:\s*["']inherit["']/);
    assert.doesNotMatch(source, /stdio:\s*\[\s*["']pipe["']/);
  });

  testSync("unsupported platform fails with an actionable message", () => {
    const stub = `
      Object.defineProperty(process, "platform", { value: "linux" });
      process.argv = [process.argv[0], ${JSON.stringify(LAUNCHER)}, "status"];
      require(${JSON.stringify(LAUNCHER)});
    `;
    const result = spawnSync(process.execPath, ["-e", stub], { encoding: "utf8", timeout: 20_000 });
    assert.notStrictEqual(result.status, 0, "should exit non-zero");
    assert.match(result.stderr, /does not publish a binary for linux/);
    assert.match(result.stderr, /Supported: macOS/);
  });

  testSync("missing optional dependency explains how to repair", () => {
    const stub = `
      process.env.ADESK_PRO_BINARY = "/nonexistent/adesk-pro";
      const Module = require("node:module");
      const realResolve = Module._resolveFilename;
      Module._resolveFilename = function (request, ...rest) {
        if (String(request).startsWith("adesk-pro-")) {
          const error = new Error("Cannot find module " + request);
          error.code = "MODULE_NOT_FOUND";
          throw error;
        }
        return realResolve.call(this, request, ...rest);
      };
      process.argv = [process.argv[0], ${JSON.stringify(LAUNCHER)}, "status"];
      require(${JSON.stringify(LAUNCHER)});
    `;
    const result = spawnSync(process.execPath, ["-e", stub], { encoding: "utf8", timeout: 20_000 });
    assert.notStrictEqual(result.status, 0);
    assert.match(result.stderr, /was not installed|include=optional/);
  });

  // --- behaviour against a real binary ---

  if (!fs.existsSync(HOST_BINARY)) {
    skip("binary passthrough returns the real version", "no binary at publish/");
    skip("exit code propagation", "no binary at publish/");
    skip("MCP initialize handshake survives the launcher", "no binary at publish/");
  } else {
    testSync("binary passthrough returns the real version", () => {
      const result = runLauncher(["--version"], { ADESK_PRO_BINARY: HOST_BINARY });
      assert.strictEqual(result.status, 0, result.stderr);
      assert.match(result.stdout, /^adesk-pro \d+\.\d+\.\d+/, `unexpected stdout: ${result.stdout}`);
    });

    testSync("exit code propagation", () => {
      // An unknown command exits 2 in the real CLI; the launcher must not flatten it to 1.
      const result = runLauncher(["definitely-not-a-command"], { ADESK_PRO_BINARY: HOST_BINARY });
      assert.strictEqual(result.status, 2, `expected exit 2, got ${result.status}`);
      assert.match(result.stdout, /"ok":false/);
    });

    try {
      const response = await mcpHandshake();
      assert.strictEqual(response.jsonrpc, "2.0");
      assert.strictEqual(response.id, 1);
      assert.strictEqual(response.result.serverInfo.name, "adesk-pro");
      assert.ok(response.result.protocolVersion, "protocolVersion missing");
      ok("MCP initialize handshake survives the launcher");
    } catch (error) {
      fail("MCP initialize handshake survives the launcher", error.message);
    }
  }

  console.log(`\n${results.passed} passed, ${results.failed} failed, ${results.skipped} skipped`);
  process.exit(results.failed === 0 ? 0 : 1);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
