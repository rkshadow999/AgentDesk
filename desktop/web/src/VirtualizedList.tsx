import {
  type FocusEvent,
  type KeyboardEvent,
  type ReactNode,
  useEffect,
  useLayoutEffect,
  useRef,
  useState
} from "react";

type VirtualizedListProps<T> = {
  ariaLabel: string;
  className?: string;
  footer?: ReactNode;
  getKey: (item: T) => string;
  items: readonly T[];
  overscan: number;
  renderItem: (item: T, index: number) => ReactNode;
  rowHeight: number;
};

type FocusedItem = {
  index: number;
  key: string;
};

export function VirtualizedList<T>({
  ariaLabel,
  className,
  footer,
  getKey,
  items,
  overscan,
  renderItem,
  rowHeight
}: VirtualizedListProps<T>) {
  const viewportRef = useRef<HTMLDivElement>(null);
  const pendingFocusIndexRef = useRef<number | undefined>(undefined);
  const focusedItemRef = useRef<FocusedItem | undefined>(undefined);
  const [, setFocusVersion] = useState(0);
  const [scrollTop, setScrollTop] = useState(0);
  const [viewportHeight, setViewportHeight] = useState(rowHeight * 8);

  useEffect(() => {
    const viewport = viewportRef.current;
    if (!viewport) {
      return;
    }
    const measure = () => {
      if (viewport.clientHeight > 0) {
        setViewportHeight(viewport.clientHeight);
      }
    };
    measure();
    const resizeObserver = typeof ResizeObserver === "undefined"
      ? undefined
      : new ResizeObserver(measure);
    resizeObserver?.observe(viewport);
    window.addEventListener("resize", measure);
    return () => {
      resizeObserver?.disconnect();
      window.removeEventListener("resize", measure);
    };
  }, []);

  const firstVisibleIndex = Math.min(
    Math.max(0, items.length - 1),
    Math.floor(Math.max(0, scrollTop) / rowHeight)
  );
  const visibleRowCount = Math.max(1, Math.ceil(viewportHeight / rowHeight));
  const startIndex = Math.max(0, firstVisibleIndex - overscan);
  const endIndex = Math.min(
    items.length,
    firstVisibleIndex + visibleRowCount + overscan
  );
  const focusedItem = focusedItemRef.current;
  const focusedItemIndex = focusedItem
    ? focusedItem.index >= 0 && focusedItem.index < items.length &&
        getKey(items[focusedItem.index]) === focusedItem.key
      ? focusedItem.index
      : items.findIndex((item) => getKey(item) === focusedItem.key)
    : -1;

  useLayoutEffect(() => {
    if (!focusedItem) {
      return;
    }
    if (focusedItemIndex >= 0) {
      focusedItem.index = focusedItemIndex;
    } else if (focusedItemRef.current === focusedItem) {
      focusedItemRef.current = undefined;
    }
  }, [focusedItem, focusedItemIndex]);

  useLayoutEffect(() => {
    const pendingFocusIndex = pendingFocusIndexRef.current;
    if (pendingFocusIndex === undefined ||
        pendingFocusIndex < startIndex || pendingFocusIndex >= endIndex) {
      return;
    }
    const target = viewportRef.current?.querySelector<HTMLElement>(
      `[data-virtual-index="${pendingFocusIndex}"]`
    );
    if (target) {
      pendingFocusIndexRef.current = undefined;
      target.focus();
    }
  }, [endIndex, startIndex]);

  function focusItem(index: number) {
    const viewport = viewportRef.current;
    if (!viewport || items.length === 0) {
      return;
    }
    const boundedIndex = Math.max(0, Math.min(items.length - 1, index));
    const rowTop = boundedIndex * rowHeight;
    const rowBottom = rowTop + rowHeight;
    let nextScrollTop = viewport.scrollTop;
    if (rowTop < nextScrollTop) {
      nextScrollTop = rowTop;
    } else if (rowBottom > nextScrollTop + viewportHeight) {
      nextScrollTop = rowBottom - viewportHeight;
    }

    const visibleTarget = viewport.querySelector<HTMLElement>(
      `[data-virtual-index="${boundedIndex}"]`
    );
    if (visibleTarget) {
      visibleTarget.focus();
    } else {
      pendingFocusIndexRef.current = boundedIndex;
    }
    if (nextScrollTop !== viewport.scrollTop) {
      viewport.scrollTop = nextScrollTop;
    }
    setScrollTop(nextScrollTop);
  }

  function handleKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const indexedTarget = event.target instanceof Element
      ? event.target.closest<HTMLElement>("[data-virtual-index]")
      : null;
    if (!indexedTarget || !event.currentTarget.contains(indexedTarget)) {
      return;
    }
    const currentIndex = Number(indexedTarget.dataset.virtualIndex);
    if (!Number.isInteger(currentIndex)) {
      return;
    }
    let nextIndex: number | undefined;
    if (event.key === "ArrowUp") {
      nextIndex = currentIndex - 1;
    } else if (event.key === "ArrowDown") {
      nextIndex = currentIndex + 1;
    } else if (event.key === "PageUp") {
      nextIndex = currentIndex - visibleRowCount;
    } else if (event.key === "PageDown") {
      nextIndex = currentIndex + visibleRowCount;
    } else if (event.key === "Home") {
      nextIndex = 0;
    } else if (event.key === "End") {
      nextIndex = items.length - 1;
    }
    if (nextIndex === undefined) {
      return;
    }
    event.preventDefault();
    focusItem(nextIndex);
  }

  function handleFocusCapture(event: FocusEvent<HTMLDivElement>) {
    const row = event.target instanceof Element
      ? event.target.closest<HTMLElement>("[data-virtual-row-key]")
      : null;
    const index = Number(row?.dataset.virtualRowIndex);
    const key = row?.dataset.virtualRowKey;
    if (row && key && Number.isInteger(index) && event.currentTarget.contains(row)) {
      const focusedItem = focusedItemRef.current;
      if (!focusedItem || focusedItem.index !== index || focusedItem.key !== key) {
        focusedItemRef.current = { index, key };
        setFocusVersion((version) => version + 1);
      }
    }
  }

  function handleBlurCapture(event: FocusEvent<HTMLDivElement>) {
    if (!(event.relatedTarget instanceof Node) ||
        !event.currentTarget.contains(event.relatedTarget)) {
      if (focusedItemRef.current) {
        focusedItemRef.current = undefined;
        setFocusVersion((version) => version + 1);
      }
    }
  }

  const visibleIndices = Array.from(
    { length: Math.max(0, endIndex - startIndex) },
    (_, offset) => startIndex + offset
  );
  if (focusedItemIndex >= 0 &&
      (focusedItemIndex < startIndex || focusedItemIndex >= endIndex)) {
    visibleIndices.push(focusedItemIndex);
    visibleIndices.sort((left, right) => left - right);
  }
  return (
    <div
      ref={viewportRef}
      className={className}
      data-virtualized-list="true"
      onBlurCapture={handleBlurCapture}
      onFocusCapture={handleFocusCapture}
      onKeyDownCapture={handleKeyDown}
      onScroll={(event) => setScrollTop(event.currentTarget.scrollTop)}
    >
      <div
        className="session-list-spacer"
        role="list"
        aria-label={ariaLabel}
        style={{ height: items.length * rowHeight }}
      >
        {visibleIndices.map((index) => {
          const item = items[index];
          const itemKey = getKey(item);
          return (
            <div
              className="session-virtual-row"
              data-virtual-row-index={index}
              data-virtual-row-key={itemKey}
              role="listitem"
              aria-posinset={index + 1}
              aria-setsize={items.length}
              key={itemKey}
              style={{
                height: rowHeight,
                transform: `translateY(${index * rowHeight}px)`
              }}
            >
              {renderItem(item, index)}
            </div>
          );
        })}
      </div>
      {footer}
    </div>
  );
}
