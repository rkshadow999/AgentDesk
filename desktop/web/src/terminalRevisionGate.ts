export type TerminalRevisionAction = "skip" | "append" | "replace";

export class TerminalRevisionGate {
  private appliedRevision: number | undefined;

  markSnapshot(revision: number): void {
    this.appliedRevision = revision;
  }

  takeRevision(revision: number): TerminalRevisionAction {
    if (this.appliedRevision !== undefined && revision <= this.appliedRevision) {
      return "skip";
    }
    const action = this.appliedRevision !== undefined &&
      revision === this.appliedRevision + 1
      ? "append"
      : "replace";
    this.appliedRevision = revision;
    return action;
  }

  reset(): void {
    this.appliedRevision = undefined;
  }
}
