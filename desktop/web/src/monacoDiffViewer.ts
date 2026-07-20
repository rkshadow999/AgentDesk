import * as monaco from "monaco-editor/esm/vs/editor/editor.api";
import EditorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";
import "monaco-editor/min/vs/editor/editor.main.css";
import type { DiffViewer } from "./inspectorRuntime";
import type { InspectorDiff } from "./inspectorModel";

type MonacoGlobal = typeof globalThis & {
  MonacoEnvironment?: { getWorker: () => Worker };
};

const monacoGlobal = globalThis as MonacoGlobal;
monacoGlobal.MonacoEnvironment ??= { getWorker: () => new EditorWorker() };

monaco.editor.defineTheme("agentdesk-dark", {
  base: "vs-dark",
  inherit: true,
  rules: [],
  colors: {
    "editor.background": "#1e211f",
    "editorGutter.background": "#1e211f",
    "diffEditor.insertedTextBackground": "#24483a88",
    "diffEditor.removedTextBackground": "#5a302e88",
    "diffEditor.insertedLineBackground": "#203a3088",
    "diffEditor.removedLineBackground": "#442a2888"
  }
});

export function mountMonacoDiffViewer(container: HTMLElement, fontSize = 12): DiffViewer {
  const editor = monaco.editor.createDiffEditor(container, {
    automaticLayout: true,
    readOnly: true,
    originalEditable: false,
    renderSideBySide: false,
    enableSplitViewResizing: false,
    minimap: { enabled: false },
    scrollBeyondLastLine: false,
    wordWrap: "on",
    lineNumbersMinChars: 3,
    fontFamily: '"Cascadia Code", Consolas, monospace',
    fontSize,
    padding: { top: 10, bottom: 10 },
    theme: "agentdesk-dark"
  });
  let original: monaco.editor.ITextModel | undefined;
  let modified: monaco.editor.ITextModel | undefined;

  return {
    setDiff(diff?: InspectorDiff) {
      original?.dispose();
      modified?.dispose();
      original = undefined;
      modified = undefined;
      if (!diff) {
        editor.setModel(null);
        return;
      }

      const language = languageForPath(diff.path);
      original = monaco.editor.createModel(diff.oldText, language);
      modified = monaco.editor.createModel(diff.newText, language);
      editor.setModel({ original, modified });
    },
    setFontSize(nextFontSize) {
      editor.updateOptions({ fontSize: nextFontSize });
    },
    layout() {
      editor.layout();
    },
    dispose() {
      editor.dispose();
      original?.dispose();
      modified?.dispose();
    }
  };
}

function languageForPath(path: string): string {
  const extension = path.split(".").at(-1)?.toLowerCase();
  return ({
    cs: "csharp",
    csproj: "xml",
    json: "json",
    md: "markdown",
    ps1: "powershell",
    rs: "rust",
    ts: "typescript",
    tsx: "typescript",
    xaml: "xml",
    xml: "xml"
  } as Record<string, string>)[extension ?? ""] ?? "plaintext";
}
