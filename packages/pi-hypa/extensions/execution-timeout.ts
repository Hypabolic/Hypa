/**
 * Propagate Pi bash `timeout` (seconds) onto a rewritten Hypa command as
 * `--timeout-ms`. Insertion is after the leading executable token so quoted
 * absolute paths keep their first token intact.
 *
 * Invalid or non-positive timeouts are left unchanged — never rounded, floored,
 * or clamped to fit Hypa's CLI.
 */

const CLI_TIMEOUT_MS_MAX = 2_147_483_647;

export function injectExecutionTimeout(command: string, timeoutSeconds: unknown): string {
  const timeoutMs = toCliTimeoutMs(timeoutSeconds);
  if (timeoutMs === undefined) return command;

  const split = splitLeadingToken(command);
  if (split === undefined) return command;
  if (!isHypaExecutableToken(split.token)) return command;

  return `${split.prefix}${split.token} --timeout-ms ${timeoutMs}${split.rest}`;
}

function toCliTimeoutMs(timeoutSeconds: unknown): number | undefined {
  if (typeof timeoutSeconds !== "number") return undefined;
  if (!Number.isFinite(timeoutSeconds) || timeoutSeconds <= 0) return undefined;

  const timeoutMs = timeoutSeconds * 1000;
  if (!Number.isInteger(timeoutMs) || timeoutMs <= 0 || timeoutMs > CLI_TIMEOUT_MS_MAX) {
    return undefined;
  }

  return timeoutMs;
}

function splitLeadingToken(command: string): { prefix: string; token: string; rest: string } | undefined {
  const prefixLength = command.length - command.trimStart().length;
  const prefix = command.slice(0, prefixLength);
  const body = command.slice(prefixLength);
  if (body.length === 0) return undefined;

  const quote = body[0];
  if (quote === "'" || quote === '"') {
    const end = body.indexOf(quote, 1);
    if (end === -1) return undefined;
    return { prefix, token: body.slice(0, end + 1), rest: body.slice(end + 1) };
  }

  const whitespace = body.search(/\s/);
  if (whitespace === -1) return { prefix, token: body, rest: "" };
  return { prefix, token: body.slice(0, whitespace), rest: body.slice(whitespace) };
}

function isHypaExecutableToken(token: string): boolean {
  const unquoted =
    (token.startsWith("'") && token.endsWith("'") && token.length >= 2) ||
    (token.startsWith('"') && token.endsWith('"') && token.length >= 2)
      ? token.slice(1, -1)
      : token;
  const base = unquoted.split(/[/\\]/).pop()?.toLowerCase();
  return base === "hypa" || base === "hypa.exe";
}
