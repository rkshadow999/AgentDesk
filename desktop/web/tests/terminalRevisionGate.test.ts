import { describe, expect, it } from "vitest";
import { TerminalRevisionGate } from "../src/terminalRevisionGate";

describe("TerminalRevisionGate", () => {
  it("rejects a delta already included by a delayed mount snapshot", () => {
    const gate = new TerminalRevisionGate();

    gate.markSnapshot(7);

    expect(gate.takeRevision(7)).toBe("skip");
    expect(gate.takeRevision(8)).toBe("append");
    expect(gate.takeRevision(8)).toBe("skip");
    expect(gate.takeRevision(10)).toBe("replace");
  });

  it("accepts the current revision after a viewer reset", () => {
    const gate = new TerminalRevisionGate();
    gate.markSnapshot(4);

    gate.reset();

    expect(gate.takeRevision(4)).toBe("replace");
  });
});
