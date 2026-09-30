import type { WidgetId } from "./contracts";
import { BaseWidget, Renderable, Color } from "./contracts";

export class Widget extends BaseWidget implements Renderable {
  fieldValue: number = 1;
  private _label: string = "w";

  constructor(name: string) {
    super();
    this.fieldValue = name.length;
  }

  get label(): string {
    return this._label;
  }

  set label(value: string) {
    this._label = value;
  }

  override render(): void {
    draw(this.fieldValue);
    console.log(Color.Red);
  }
}

export function draw(n: number): void {
  require("fs");
}

export const helper = (): WidgetId => "id";

export type Pair<T, U> = [T, U];

export class Box<T> {
  value: T;
  constructor(v: T) {
    this.value = v;
  }
}
