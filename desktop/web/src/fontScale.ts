export const fontScalePercentValues = [90, 100, 110, 125, 140] as const;

export type FontScalePercent = (typeof fontScalePercentValues)[number];

export const defaultFontScalePercent: FontScalePercent = 110;

export function isFontScalePercent(value: unknown): value is FontScalePercent {
  return typeof value === "number" &&
    fontScalePercentValues.some((candidate) => candidate === value);
}

export function applyFontScalePercent(
  percent: FontScalePercent,
  root: HTMLElement = document.documentElement
) {
  root.style.fontSize = `${percent / 10}px`;
  root.dataset.fontScalePercent = String(percent);
}

export function codeFontSizeForScale(percent: FontScalePercent): number {
  return 12 * percent / 100;
}

export function sessionRowHeightForScale(percent: FontScalePercent): number {
  return Math.max(54, Math.ceil(60 * percent / defaultFontScalePercent));
}
