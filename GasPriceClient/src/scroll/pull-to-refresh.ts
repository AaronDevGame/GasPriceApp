export const pullRefreshThreshold = 64;
export const refreshHoldDistance = 64;

/** Increasing resistance rather than a hard stop as the finger travels farther. */
export function rubberBandDistance(travel: number) {
  return 120 * (1 - Math.exp(-Math.max(0, travel) / 180));
}

/** Attach only to the screen's scroll container, never the document or map. */
export function attachPullToRefresh(
  node: HTMLElement,
  options: {
    canRefresh(): boolean;
    onRefresh(): Promise<unknown> | void;
    onPull(distance: number, pulling: boolean): void;
    onBusy(busy: boolean): void;
  },
) {
  let start: { x: number; y: number; id: number } | null = null;
  let distance = 0;
  let busy = false;
  let disposed = false;
  const previousOverscroll = node.style.overscrollBehaviorY;
  node.style.overscrollBehaviorY = 'none';
  const reset = () => { start = null; distance = 0; options.onPull(0, false); };
  const touchStart = (event: TouchEvent) => {
    reset();
    if (busy || !options.canRefresh() || node.scrollTop > 0 || event.touches.length !== 1) return;
    // Text nodes and SVG descendants can be touch targets too. Resolve their
    // owning element without disabling pointer events on the content.
    const target = event.target instanceof Element ? event.target :
      event.target instanceof Node ? event.target.parentElement : null;
    if (!target || target.closest('input, textarea, select, button, a, [contenteditable]:not([contenteditable="false"]), [role="button"], [role="link"], [role="tab"], [role="slider"], [role="checkbox"], [role="switch"], [data-pull-refresh-ignore]')) return;
    // Preserve gestures inside nested lists and horizontal selectors.
    for (let child: Element | null = target; child && child !== node; child = child.parentElement) {
      const style = getComputedStyle(child);
      if ((child.scrollHeight > child.clientHeight && /auto|scroll/.test(style.overflowY)) ||
          (child.scrollWidth > child.clientWidth && /auto|scroll/.test(style.overflowX))) return;
    }
    const touch = event.touches[0];
    start = { x: touch.clientX, y: touch.clientY, id: touch.identifier };
  };
  const touchMove = (event: TouchEvent) => {
    if (!start) return;
    const touch = event.touches[0];
    if (event.touches.length !== 1 || touch.identifier !== start.id || !options.canRefresh() || node.scrollTop > 0) { reset(); return; }
    const dx = Math.abs(touch.clientX - start.x);
    const dy = touch.clientY - start.y;
    if (dy < 0 || dx > Math.max(10, dy)) { reset(); return; }
    if (dy === 0) { distance = 0; options.onPull(0, false); return; }
    if (!event.cancelable) { reset(); return; }
    // Claim the first downward move, even below the visual threshold. Waiting
    // here can let the browser start scrolling and make later moves uncancelable.
    event.preventDefault();
    if (dy < 8) { distance = 0; options.onPull(0, true); return; }
    distance = rubberBandDistance(dy);
    options.onPull(distance, true);
  };
  const touchEnd = () => {
    const refresh = start !== null && distance >= pullRefreshThreshold && options.canRefresh() && !busy;
    reset();
    if (!refresh) return;
    busy = true;
    options.onBusy(true);
    // Screen callbacks retain their existing error presentation.
    void Promise.resolve().then(() => options.onRefresh()).catch(() => {}).finally(() => {
      busy = false;
      if (!disposed) options.onBusy(false);
    });
  };
  node.addEventListener('touchstart', touchStart, { passive: true, capture: true });
  node.addEventListener('touchmove', touchMove, { passive: false, capture: true });
  node.addEventListener('touchend', touchEnd, true);
  node.addEventListener('touchcancel', reset, true);
  return () => {
    disposed = true;
    node.removeEventListener('touchstart', touchStart, true);
    node.removeEventListener('touchmove', touchMove, true);
    node.removeEventListener('touchend', touchEnd, true);
    node.removeEventListener('touchcancel', reset, true);
    node.style.overscrollBehaviorY = previousOverscroll;
  };
}
