import { describe, expect, it } from "vitest";
import {
  applyFontScalePercent,
  codeFontSizeForScale,
  defaultFontScalePercent,
  fontScalePercentValues,
  sessionRowHeightForScale
} from "../src/fontScale";

describe("font scale", () => {
  it("exposes only the persisted desktop font scale contract", () => {
    expect(fontScalePercentValues).toEqual([90, 100, 110, 125, 140]);
    expect(defaultFontScalePercent).toBe(110);
  });

  it("applies the scale as a ten-pixel rem root without multiplying Windows DPI", () => {
    applyFontScalePercent(125, document.documentElement);

    expect(document.documentElement.style.fontSize).toBe("12.5px");
    expect(document.documentElement.dataset.fontScalePercent).toBe("125");
    expect(codeFontSizeForScale(125)).toBe(15);
  });

  it("grows virtualized session rows enough for three scaled text lines", () => {
    expect(sessionRowHeightForScale(90)).toBeLessThan(sessionRowHeightForScale(110));
    expect(sessionRowHeightForScale(140)).toBeGreaterThanOrEqual(72);
  });
});
