// The designer's pointer work: dragging a menu around the canvas, and dragging from a menu's handle
// onto another one to connect them. Both run entirely in the browser and report once, on release —
// a Blazor Server circuit should not see a message per pointer move.

let view = null;

/** Starts listening on the canvas. `dotnet` receives OnNodeMoved(id, x, y) and OnLink(from, to). */
export function start(canvas, dotnet) {
    stop();
    if (!canvas) return;

    const state = { canvas, dotnet, drag: null, link: null };
    view = state;

    const positionIn = event => {
        const box = canvas.getBoundingClientRect();
        return { x: event.clientX - box.left + canvas.scrollLeft, y: event.clientY - box.top + canvas.scrollTop };
    };

    state.onDown = event => {
        const handle = event.target.closest("[data-link]");
        const node = event.target.closest("[data-menu]");
        if (!node || event.button !== 0) return;
        const id = node.dataset.menu;
        const at = positionIn(event);
        if (handle) {
            // Drawing a new connection: the line follows the pointer until it is dropped on a menu.
            state.link = { from: id, line: canvas.querySelector(".link-preview") };
            canvas.dataset.gesture = `link from ${id}`;
            state.link.line?.setAttribute("d", `M ${at.x} ${at.y} L ${at.x} ${at.y}`);
            state.link.start = at;
            state.link.line?.classList.add("live");
        } else {
            const x = parseFloat(node.dataset.x) || 0;
            const y = parseFloat(node.dataset.y) || 0;
            state.drag = { node, id, grabbedAt: at, from: { x, y } };
            canvas.dataset.gesture = `drag ${id}`;
            node.classList.add("dragging");
        }
        // Capture keeps a real pointer with the canvas; a synthetic one has nothing to capture.
        try {
            canvas.setPointerCapture?.(event.pointerId);
        } catch {
            // No capture: the pointer still reports through the canvas listeners.
        }
        event.preventDefault();
    };

    state.onMove = event => {
        const at = positionIn(event);
        if (state.drag) {
            const x = Math.max(0, state.drag.from.x + at.x - state.drag.grabbedAt.x);
            const y = Math.max(0, state.drag.from.y + at.y - state.drag.grabbedAt.y);
            state.drag.node.setAttribute("transform", `translate(${x} ${y})`);
            state.drag.to = { x, y };
        } else if (state.link) {
            const { start } = state.link;
            state.link.line?.setAttribute("d", `M ${start.x} ${start.y} L ${at.x} ${at.y}`);
        }
    };

    state.onUp = event => {
        try {
            canvas.releasePointerCapture?.(event.pointerId);
        } catch {
            // Nothing was captured — a pointer that vanished, or one the page never held.
        }

        if (state.drag) {
            const { node, id, to } = state.drag;
            node.classList.remove("dragging");
            state.drag = null;
            if (to) {
                node.dataset.x = to.x;
                node.dataset.y = to.y;
                dotnet.invokeMethodAsync("OnNodeMoved", id, Math.round(to.x), Math.round(to.y));
            }
        } else if (state.link) {
            const target = document.elementFromPoint(event.clientX, event.clientY)?.closest("[data-menu]");
            const from = state.link.from;
            state.link.line?.classList.remove("live");
            state.link.line?.setAttribute("d", "");
            state.link = null;
            canvas.dataset.gesture = `drop on ${target?.dataset.menu ?? "nothing"}`;
            if (target && target.dataset.menu !== from) {
                dotnet.invokeMethodAsync("OnLink", from, target.dataset.menu);
            }
        }
    };

    canvas.addEventListener("pointerdown", state.onDown);
    canvas.addEventListener("pointermove", state.onMove);
    canvas.addEventListener("pointerup", state.onUp);
    canvas.addEventListener("pointercancel", state.onUp);
    // Says the canvas is live, which is what a test waits for before it starts dragging.
    canvas.dataset.graph = "ready";
}

/** Stops listening; called when the page goes away. */
export function stop() {
    if (!view) return;
    const { canvas, onDown, onMove, onUp } = view;
    canvas.removeEventListener("pointerdown", onDown);
    canvas.removeEventListener("pointermove", onMove);
    canvas.removeEventListener("pointerup", onUp);
    canvas.removeEventListener("pointercancel", onUp);
    view = null;
}

/** Brings a menu's editor into view when its node is selected. */
export function reveal(id) {
    document.getElementById(`menu-${id}`)?.scrollIntoView({ block: "nearest", behavior: "smooth" });
}
