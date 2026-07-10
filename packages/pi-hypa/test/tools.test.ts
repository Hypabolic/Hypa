import test from "node:test";
import assert from "node:assert/strict";
import { buildFindCommand, buildGrepCommand, buildLsCommand, buildReadCommand, shellQuote } from "../extensions/tools.js";

test("shellQuote protects spaces and single quotes", () => {
  assert.equal(shellQuote("simple/path"), "simple/path");
  assert.equal(shellQuote("a b"), "'a b'");
  assert.equal(shellQuote("it's"), "'it'\"'\"'s'");
});

test("buildReadCommand uses cat by default and sed for line slices", () => {
  assert.equal(buildReadCommand("src/File.cs"), "cat -- src/File.cs");
  assert.equal(buildReadCommand("src/File.cs", 10, 5), "sed -n 10,14p -- src/File.cs");
});

test("buildGrepCommand includes safe ripgrep options", () => {
  assert.equal(
    buildGrepCommand({ pattern: "hello world", path: "src", glob: "*.ts", ignoreCase: true, literal: true, context: 2, limit: 3 }),
    "rg --heading --line-number --color=never --ignore-case --fixed-strings --context 2 --max-count 3 --glob '*.ts' -e 'hello world' -- src",
  );
});

test("buildGrepCommand treats dash-leading patterns as data via -e", () => {
  const command = buildGrepCommand({ pattern: "--help", path: "src" });
  assert.equal(
    command,
    "rg --heading --line-number --color=never -e --help -- src",
  );
  // Pattern must not appear as a bare positional that ripgrep could parse as a flag
  assert.match(command, /\s-e\s--help\s--\s/);
});

test("buildFindCommand applies optional limit", () => {
  assert.equal(buildFindCommand({ pattern: "*.cs", path: "src", limit: 10 }), "find src -type f -name '*.cs' | head -n 10");
});

test("buildLsCommand defaults to long listing", () => {
  assert.equal(buildLsCommand({ path: ".", all: true }), "ls -la -- .");
  assert.equal(buildLsCommand({ path: "src", long: false }), "ls -- src");
});
