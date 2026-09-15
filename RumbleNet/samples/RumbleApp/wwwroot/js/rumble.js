// RumbleApp interop: keyboard push-to-talk and chat scrolling.
window.rumble = (() => {
    let dotnet = null;
    let held = false;

    const typing = () => {
        const el = document.activeElement;
        return el && (el.tagName === "INPUT" || el.tagName === "TEXTAREA" || el.tagName === "SELECT" || el.isContentEditable);
    };

    const set = (value) => {
        if (held === value || !dotnet) return;
        held = value;
        dotnet.invokeMethodAsync("SetPushToTalk", value);
    };

    const onDown = (e) => {
        if (e.code === "Space" && !typing()) { e.preventDefault(); set(true); }
    };
    const onUp = (e) => {
        if (e.code === "Space") { set(false); }
    };
    const onBlur = () => set(false);

    return {
        attachPushToTalk(ref) {
            dotnet = ref;
            window.addEventListener("keydown", onDown);
            window.addEventListener("keyup", onUp);
            window.addEventListener("blur", onBlur);
        },
        detachPushToTalk() {
            window.removeEventListener("keydown", onDown);
            window.removeEventListener("keyup", onUp);
            window.removeEventListener("blur", onBlur);
            dotnet = null;
            held = false;
        },
        scrollToEnd(el) {
            if (el) el.scrollTop = el.scrollHeight;
        }
    };
})();
