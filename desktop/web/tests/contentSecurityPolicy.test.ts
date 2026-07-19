import { describe, expect, it } from "vitest";
import indexHtml from "../index.html?raw";

describe("desktop web content security policy", () => {
  it("allows only packaged assets and blob workers", () => {
    const document = new DOMParser().parseFromString(indexHtml, "text/html");
    const content = document
      .querySelector('meta[http-equiv="Content-Security-Policy"]')
      ?.getAttribute("content");

    expect(content).toBeTruthy();
    const directives = parseDirectives(content!);
    expect(directives.get("default-src")).toEqual(["'none'"]);
    expect(directives.get("script-src")).toEqual(["'self'"]);
    expect(directives.get("style-src")).toEqual(["'self'", "'unsafe-inline'"]);
    expect(directives.get("font-src")).toEqual(["'self'", "data:"]);
    expect(directives.get("img-src")).toEqual(["'self'", "data:", "blob:"]);
    expect(directives.get("worker-src")).toEqual(["'self'", "blob:"]);
    expect(directives.get("connect-src")).toEqual(["'none'"]);
    expect(directives.get("frame-src")).toEqual(["'none'"]);
    expect(directives.get("object-src")).toEqual(["'none'"]);
    expect(directives.get("base-uri")).toEqual(["'none'"]);
    expect(directives.get("form-action")).toEqual(["'none'"]);
  });
});

function parseDirectives(content: string) {
  return new Map(content
    .split(";")
    .map((directive) => directive.trim())
    .filter(Boolean)
    .map((directive) => {
      const [name, ...values] = directive.split(/\s+/);
      return [name, values] as const;
    }));
}
