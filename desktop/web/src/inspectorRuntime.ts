import type { InspectorDiff } from "./inspectorModel";

export interface DiffViewer {
  setDiff(diff?: InspectorDiff): void;
  layout(): void;
  dispose(): void;
}

export interface TerminalViewer {
  appendText(text: string): boolean;
  replaceText(snapshot: string): void;
  fit(): void;
  dispose(): void;
}

export interface InspectorRuntime {
  mountDiff(container: HTMLElement): Promise<DiffViewer>;
  mountTerminal(container: HTMLElement): Promise<TerminalViewer>;
}

export const defaultInspectorRuntime: InspectorRuntime = {
  async mountDiff(container) {
    const module = await import("./monacoDiffViewer");
    return module.mountMonacoDiffViewer(container);
  },
  async mountTerminal(container) {
    const module = await import("./xtermViewer");
    return module.mountXtermViewer(container);
  }
};
