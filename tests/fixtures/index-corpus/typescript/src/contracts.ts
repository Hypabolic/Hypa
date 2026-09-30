export type WidgetId = string;

export interface Renderable {
  render(): void;
  get label(): string;
}

export abstract class BaseWidget {
  protected abstract seed: number;
  abstract render(): void;
}

export enum Color {
  Red,
  Blue = 2,
}

export namespace Util {
  export function nest(): void {}
}
