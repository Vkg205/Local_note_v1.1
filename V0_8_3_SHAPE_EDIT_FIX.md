# LocalNote V0.8.3 — Shape editing fix

Fixes shape selection/manipulation after creation.

- Shape click no longer consumes the mouse-down routed event needed by WPF Thumb.
- Body drag now selects and moves the shape.
- Eight resize handles are available when selected.
- Shapes embedded in text/table/image child drawing layers remain hit-testable in Select mode.
- Selecting an existing shape synchronizes shape kind/stroke/fill/thickness back into PageCanvasView before toolbar edits.
- Right-click selects the shape before showing its context menu.
