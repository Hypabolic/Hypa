import { mkdtemp, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import type { HypaPiConfig } from "./types.js";
import { getExecArgs } from "./rewrite-client.js";

/** Compression must never execute, parse, or change the original Bash script. */
export async function compressBashOutput(
  pi: ExtensionAPI,
  config: HypaPiConfig,
  text: string,
): Promise<string | undefined> {
  if (text.length < 1024) return;
  let directory: string | undefined;
  try {
    directory = await mkdtemp(join(tmpdir(), "pi-hypa-output-"));
    const file = join(directory, "output.txt");
    await writeFile(file, text, { mode: 0o600 });
    const [binary, args] = getExecArgs(config.binary, ["compress", "--kind", "shell-output", "--file", file]);
    const result = await pi.exec(binary, args, { timeout: Math.min(config.rewriteTimeoutMs, 5000) });
    if (!result.killed && result.code === 0 && result.stdout.trim() && result.stdout.length < text.length) {
      return result.stdout;
    }
  } catch {
    // Compression is optional: keep the original result on any failure.
  } finally {
    if (directory) await rm(directory, { recursive: true, force: true }).catch(() => {});
  }
}
