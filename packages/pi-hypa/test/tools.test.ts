import test from "node:test";
import { mkdtemp, writeFile } from "node:fs/promises";
import { homedir, tmpdir } from "node:os";
import { join } from "node:path";
import assert from "node:assert/strict";
import {
  buildFindCommand,
  buildGrepCommand,
  buildLsCommand,
  buildReadCommand,
  detectSupportedImageMimeType,
  limitStdoutLines,
  looksLikeOpaqueBinary,
  shellQuote,
  tryBuildImageReadResult,
} from "../extensions/tools.js";

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
  const home = homedir();
  assert.equal(buildReadCommand("src/File.cs"), "cat -- src/File.cs");
  // offset + limit
  assert.equal(buildReadCommand("src/File.cs", 10, 5), "sed -n 10,14p src/File.cs");
  // offset only → end is $ (shellQuote wraps the range because $ is not safe-unquoted)
  assert.equal(buildReadCommand("src/File.cs", 10), `sed -n ${shellQuote("10,$p")} src/File.cs`);
  // limit only → start defaults to 1
  assert.equal(buildReadCommand("src/File.cs", undefined, 5), "sed -n 1,5p src/File.cs");
  // path with spaces requires quoting (shellQuote is platform-aware)
  assert.equal(
    buildReadCommand("src/My File.cs", 10, 5),
    `sed -n 10,14p ${shellQuote("src/My File.cs")}`,
  );
  // leading-dash relative path: BSD sed has no `--`, so prefix ./
  assert.equal(buildReadCommand("-report.txt", 1, 5), "sed -n 1,5p ./-report.txt");
  // absolute paths starting with / need no ./ prefix
  assert.equal(buildReadCommand("/tmp/-report.txt", 1, 5), "sed -n 1,5p /tmp/-report.txt");
  // tilde expands on the sed branch too
  assert.equal(
    buildReadCommand("~/notes.txt", 2, 3),
    `sed -n 2,4p ${shellQuote(`${home}/notes.txt`)}`,
  );
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

// Minimal valid static PNG (1x1 transparent) with IHDR + IDAT + IEND
const MINI_PNG = Buffer.from(
  "89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000a49444154789c63000100000500010d0a2db40000000049454e44ae426082",
  "hex",
);
const MINI_JPEG = Buffer.from([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46, 0x49, 0x46, 0x00, 0x01, 0xff, 0xd9]);
const MINI_GIF = Buffer.from("GIF89a\x01\x00\x01\x00\x00\x00\x00;", "binary");
const MINI_WEBP = Buffer.from(
  // RIFF....WEBP + minimal payload
  Buffer.concat([
    Buffer.from("RIFF"),
    Buffer.from([0x0a, 0x00, 0x00, 0x00]),
    Buffer.from("WEBP"),
    Buffer.from("XXXX"),
  ]),
);

test("detectSupportedImageMimeType sniffs png/jpeg/gif/webp magic bytes", () => {
  assert.equal(detectSupportedImageMimeType(MINI_PNG), "image/png");
  assert.equal(detectSupportedImageMimeType(MINI_JPEG), "image/jpeg");
  assert.equal(detectSupportedImageMimeType(MINI_GIF), "image/gif");
  assert.equal(detectSupportedImageMimeType(MINI_WEBP), "image/webp");
  assert.equal(detectSupportedImageMimeType(Buffer.from("hello world")), null);
  assert.equal(detectSupportedImageMimeType(Buffer.from([0x00, 0x01, 0x02])), null);
});

test("looksLikeOpaqueBinary detects NUL bytes but not text or images", () => {
  assert.equal(looksLikeOpaqueBinary(Buffer.from("plain text\n")), false);
  assert.equal(looksLikeOpaqueBinary(MINI_PNG), false);
  assert.equal(looksLikeOpaqueBinary(Buffer.from([0x7f, 0x45, 0x4c, 0x46, 0x00, 0x01])), true);
});

test("tryBuildImageReadResult returns image content for PNG with and without extension", async () => {
  const dir = await mkdtemp(join(tmpdir(), "pi-hypa-img-"));
  const withExt = join(dir, "pixel.png");
  const noExt = join(dir, "pixel-noext");
  await writeFile(withExt, MINI_PNG);
  await writeFile(noExt, MINI_PNG);

  for (const path of [withExt, noExt]) {
    const result = await tryBuildImageReadResult(path);
    assert.ok(result, `expected image result for ${path}`);
    const types = result!.content.map((c) => c.type);
    assert.deepEqual(types, ["text", "image"]);
    const image = result!.content.find((c) => c.type === "image") as { type: "image"; data: string; mimeType: string };
    assert.equal(image.mimeType, "image/png");
    assert.ok(image.data.length > 0);
    assert.equal(Buffer.from(image.data, "base64").compare(MINI_PNG), 0);
    const text = result!.content.find((c) => c.type === "text") as { type: "text"; text: string };
    assert.match(text.text, /image\/png/);
    assert.doesNotMatch(text.text, /IHDR/);
  }
});

test("tryBuildImageReadResult returns image content for jpeg/gif/webp", async () => {
  const dir = await mkdtemp(join(tmpdir(), "pi-hypa-img-"));
  const cases: Array<[string, Buffer, string]> = [
    ["a.jpg", MINI_JPEG, "image/jpeg"],
    ["a.gif", MINI_GIF, "image/gif"],
    ["a.webp", MINI_WEBP, "image/webp"],
  ];
  for (const [name, bytes, mime] of cases) {
    const path = join(dir, name);
    await writeFile(path, bytes);
    const result = await tryBuildImageReadResult(path);
    assert.ok(result, `expected image for ${name}`);
    const image = result!.content.find((c) => c.type === "image") as { mimeType: string; data: string };
    assert.equal(image.mimeType, mime);
    assert.ok(image.data.length > 0);
  }
});

test("tryBuildImageReadResult notes non-vision models without dumping bytes as text", async () => {
  const dir = await mkdtemp(join(tmpdir(), "pi-hypa-img-"));
  const path = join(dir, "x.png");
  await writeFile(path, MINI_PNG);
  const result = await tryBuildImageReadResult(path, { model: { input: ["text"] } });
  assert.ok(result);
  const text = (result!.content.find((c) => c.type === "text") as { text: string }).text;
  assert.match(text, /does not support images/i);
  // Still include image part (host may strip); never mojibake the payload as text
  assert.ok(result!.content.some((c) => c.type === "image"));
});

test("tryBuildImageReadResult returns binary notice for opaque non-image binary", async () => {
  const dir = await mkdtemp(join(tmpdir(), "pi-hypa-bin-"));
  const path = join(dir, "blob");
  await writeFile(path, Buffer.from([0x00, 0x01, 0x02, 0xff, 0xfe]));
  const result = await tryBuildImageReadResult(path);
  assert.ok(result);
  assert.equal(result!.content.length, 1);
  assert.equal(result!.content[0].type, "text");
  assert.match((result!.content[0] as { text: string }).text, /Binary file detected/i);
});

test("tryBuildImageReadResult returns null for ordinary text so cat path can run", async () => {
  const dir = await mkdtemp(join(tmpdir(), "pi-hypa-txt-"));
  const path = join(dir, "notes.txt");
  await writeFile(path, "hello from hypa_read\n", "utf8");
  const result = await tryBuildImageReadResult(path);
  assert.equal(result, null);
});
