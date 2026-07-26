/**
 * UI zoom tests.
 *
 * Reported as "всё в окне очень мелкое как будто. Не очень удобно" — the
 * interface reads as tiny. The fix is a zoom factor rather than a font-size
 * setting, because padding, row heights and control sizes are px too; scaling
 * only the text would make the UI cramped instead of larger.
 */

import { describe, expect, it } from 'vitest';
import { DEFAULT_ZOOM, ZOOM_STEPS, snapZoom, stepZoom, zoomLabel } from '../src/shared/ui-zoom';

describe('ZOOM_STEPS', () => {
  it('is sorted ascending, which stepZoom relies on', () => {
    const sorted = [...ZOOM_STEPS].sort((a, b) => a - b);
    expect([...ZOOM_STEPS]).toEqual(sorted);
  });

  it('includes 100% and offers something larger — the actual complaint', () => {
    expect(ZOOM_STEPS).toContain(DEFAULT_ZOOM);
    expect(Math.max(...ZOOM_STEPS)).toBeGreaterThan(DEFAULT_ZOOM);
  });

  it('has no duplicates', () => {
    expect(new Set(ZOOM_STEPS).size).toBe(ZOOM_STEPS.length);
  });
});

describe('snapZoom', () => {
  it('returns the default for a missing or unusable value', () => {
    for (const v of [undefined, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(snapZoom(v)).toBe(DEFAULT_ZOOM);
    }
  });

  it('keeps an exact step untouched', () => {
    for (const z of ZOOM_STEPS) expect(snapZoom(z)).toBe(z);
  });

  it('snaps an in-between value to the nearest step', () => {
    // A persisted 1.07 (or a hand-edited settings file) must not blur the UI on
    // half-pixel text.
    expect(snapZoom(1.07)).toBe(1.1);
    expect(snapZoom(1.2)).toBe(1.25);
  });

  it('clamps out-of-range values to the ends rather than returning them', () => {
    expect(snapZoom(0.1)).toBe(ZOOM_STEPS[0]);
    expect(snapZoom(99)).toBe(ZOOM_STEPS[ZOOM_STEPS.length - 1]);
  });
});

describe('stepZoom', () => {
  it('moves one step up and one step down', () => {
    expect(stepZoom(1, 1)).toBe(1.1);
    expect(stepZoom(1.1, -1)).toBe(1);
  });

  it('clamps at the maximum instead of wrapping to the smallest', () => {
    // Ctrl+= at maximum should do nothing; jumping back to 90% would be a
    // genuinely disorienting bug.
    const max = ZOOM_STEPS[ZOOM_STEPS.length - 1];
    expect(stepZoom(max, 1)).toBe(max);
  });

  it('clamps at the minimum instead of wrapping to the largest', () => {
    expect(stepZoom(ZOOM_STEPS[0], -1)).toBe(ZOOM_STEPS[0]);
  });

  it('works from an unsnapped starting value', () => {
    expect(stepZoom(1.07, 1)).toBe(1.25);
  });

  it('starts from the default when the current value is unknown', () => {
    expect(stepZoom(undefined, 1)).toBe(stepZoom(DEFAULT_ZOOM, 1));
  });
});

describe('zoomLabel', () => {
  it('formats as a whole percentage', () => {
    expect(zoomLabel(1)).toBe('100%');
    expect(zoomLabel(1.25)).toBe('125%');
    expect(zoomLabel(0.9)).toBe('90%');
  });

  it('produces a clean label for every offered step', () => {
    for (const z of ZOOM_STEPS) expect(zoomLabel(z)).toMatch(/^\d+%$/);
  });
});
