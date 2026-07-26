/**
 * UI zoom steps.
 *
 * The reported problem was "всё в окне очень мелкое" — everything in the window
 * feels tiny. Raising font sizes alone does not fix that: the padding, row
 * heights, icon sizes and border radii are all in px too, so bigger text in
 * unchanged boxes just looks cramped. Chromium's zoom factor scales the whole
 * layout, which is why this is a zoom control and not a font-size setting.
 *
 * Discrete steps rather than a free number, matching what Chrome itself offers:
 * a slider invites values like 1.07 that land text on half-pixels and make it
 * blurry, and there is no real use for that precision.
 */

/** Zoom levels offered, smallest first. 1 = 100%. */
export const ZOOM_STEPS = [0.9, 1, 1.1, 1.25, 1.4, 1.5, 1.75, 2] as const;

export const DEFAULT_ZOOM = 1;

/** Snap an arbitrary number to the nearest offered step. */
export function snapZoom(value: number | undefined): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return DEFAULT_ZOOM;
  let best: number = ZOOM_STEPS[0];
  let bestDist = Math.abs(value - best);
  for (const step of ZOOM_STEPS) {
    const d = Math.abs(value - step);
    if (d < bestDist) {
      best = step;
      bestDist = d;
    }
  }
  return best;
}

/**
 * Step up or down from the current zoom.
 *
 * Clamps at the ends rather than wrapping — Ctrl+= at maximum should do nothing,
 * not jump back to the smallest size.
 */
export function stepZoom(current: number | undefined, direction: 1 | -1): number {
  const snapped = snapZoom(current);
  const i = ZOOM_STEPS.indexOf(snapped as (typeof ZOOM_STEPS)[number]);
  const next = i + direction;
  if (next < 0) return ZOOM_STEPS[0];
  if (next >= ZOOM_STEPS.length) return ZOOM_STEPS[ZOOM_STEPS.length - 1];
  return ZOOM_STEPS[next]!;
}

/** "125%" for the settings dropdown. */
export function zoomLabel(value: number): string {
  return `${Math.round(value * 100)}%`;
}
