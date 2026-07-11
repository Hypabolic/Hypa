import test from "node:test";
import assert from "node:assert/strict";
import { homedir } from "node:os";
import { buildFindCommand, buildGrepCommand, buildLsCommand, buildReadCommand, limitStdoutLines, shellQuote } from "../extensions/tools.js";

test("shellQuote protects spaces and single quotes on POSIX", () => {
  assert.equal(shellQuote("simple/path", "linux"), "simple/path");
  assert.equal(shellQuote("a b", "linux"), "'a b'");
  assert.equal(shellQuote("it's", "darwin"), "'it'\"'\"'s'");
  assert.equal(shellQuote("", "linux"), "''");
});

test("shellQuote uses cmd-style double quotes on Windows", () => {
  assert.equal(shellQuote("", "win32"), '""');
  assert.equal(shellQuote("simple/path", "win32"), "simple/path");
  assert.equal(shellQuote("a b", "win32"), '"a b"');
  assert.equal(shellQuote('say "hi"', "win32"), '"say ""hi"""');
  assert.equal(shellQuote("*.py", "win32"), '"*.py"');
  assert.equal(shellQuote("%TEMP%", "win32"), '"%TEMP%"');
  assert.equal(shellQuote("^escape", "win32"), '"^escape"');
  // Trailing backslash before closing " is a known ShellLexer/StripQuotes limitation
  // outside this function's scope; do not use MSVC list2cmdline escaping here.
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

test("buildFindCommand lists files with ripgrep", () => {
  assert.equal(buildFindCommand({}), "rg --files --glob '*' .");
  assert.equal(buildFindCommand({ pattern: "*.cs", path: "src" }), "rg --files --glob '*.cs' src");
});

test("buildFindCommand ignores limit and never pipes to head", () => {
  const unlimited = buildFindCommand({ pattern: "*.cs", path: "src" });
  const limited = buildFindCommand({ pattern: "*.cs", path: "src", limit: 10 });
  assert.equal(limited, unlimited);
  assert.equal(limited, "rg --files --glob '*.cs' src");
  assert.doesNotMatch(limited, /\|/);
  assert.doesNotMatch(limited, /\bhead\b/);
});

test("limitStdoutLines returns stdout unchanged when limit is undefined", () => {
  assert.equal(limitStdoutLines(""), "");
  assert.equal(limitStdoutLines("a\nb\n"), "a\nb\n");
  assert.equal(limitStdoutLines("a\nb\n", undefined), "a\nb\n");
});
test("limitStdoutLines keeps first N non-empty lines", () => {
  assert.equal(limitStdoutLines("", 5), "");
  assert.equal(limitStdoutLines("a\nb\n", 5), "a\nb\n");
  assert.equal(limitStdoutLines("a\nb\nc\nd\n", 2), "a\nb\n");
  assert.equal(limitStdoutLines("only\n", 1), "only\n");
});

test("limitStdoutLines handles CRLF and empty lines", () => {
  assert.equal(limitStdoutLines("a\r\nb\r\nc\r\n", 2), "a\nb\n");
  // empty lines are skipped when counting paths
  assert.equal(limitStdoutLines("a\n\nb\n\nc\n", 2), "a\nb\n");
  assert.equal(limitStdoutLines("\n\n", 3), "");
});

test("limitStdoutLines floors and clamps limit to at least 1", () => {
  assert.equal(limitStdoutLines("a\nb\nc\n", 2.9), "a\nb\n");
  assert.equal(limitStdoutLines("a\nb\n", 0), "a\n");
  assert.equal(limitStdoutLines("a\nb\n", -3), "a\n");
});

test("normalizePathArg expands a leading ~ to the home directory", () => {
  const home = homedir();
  assert.equal(buildReadCommand("~/notes.txt"), `cat -- ${shellQuote(`${home}/notes.txt`)}`);
  assert.equal(buildReadCommand("~"), `cat -- ${shellQuote(home)}`);
  assert.equal(buildLsCommand({ path: "~" }), `ls -l -- ${shellQuote(home)}`);
  assert.equal(
    buildGrepCommand({ pattern: "needle", path: "~/src" }),
    `rg --heading --line-number --color=never -e needle -- ${shellQuote(`${home}/src`)}`,
  );
  assert.equal(
    buildFindCommand({ pattern: "*.ts", path: "~/src" }),
    `rg --files --glob '*.ts' ${shellQuote(`${home}/src`)}`,
  );
});

test("normalizePathArg strips a leading @ before expanding ~ (pi file-mention syntax)", () => {
  const home = homedir();
  assert.equal(buildReadCommand("@~/notes.txt"), `cat -- ${shellQuote(`${home}/notes.txt`)}`);
  assert.equal(buildReadCommand("@/etc/hosts"), "cat -- /etc/hosts");
});

test("normalizePathArg leaves ~user, absolute, and relative paths untouched", () => {
  assert.equal(buildReadCommand("~otheruser/file"), "cat -- '~otheruser/file'");
  assert.equal(buildReadCommand("/etc/hosts"), "cat -- /etc/hosts");
  assert.equal(buildReadCommand("./rel"), "cat -- ./rel");
});

test("buildLsCommand defaults to long listing", () => {
  assert.equal(buildLsCommand({ path: ".", all: true }), "ls -la -- .");
  assert.equal(buildLsCommand({ path: "src", long: false }), "ls -- src");
});
