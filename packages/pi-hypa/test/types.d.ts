declare module "@earendil-works/pi-coding-agent" {
  export interface ExtensionAPI {
    exec(command: string, args: string[], options?: Record<string, unknown>): Promise<{
      stdout: string;
      stderr: string;
      code: number;
      killed?: boolean;
    }>;
    on(event: string, handler: (...args: any[]) => unknown): void;
    registerCommand(name: string, options: { description?: string; handler: (...args: any[]) => unknown }): void;
  }

  export function isToolCallEventType(name: string, event: any): boolean;
}
