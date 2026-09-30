// AOT smoke fixture — TS constructs regex is weaker on (interface, type-alias, ParentId).
export interface Renderable {
  render(): void;
}

export type WidgetId = string;

export class Widget {
  fieldValue: number = 1;

  constructor(name: string) {
    this.fieldValue = name.length;
  }

  render(): void {
    draw(this.fieldValue);
  }
}

function draw(n: number): void {}
