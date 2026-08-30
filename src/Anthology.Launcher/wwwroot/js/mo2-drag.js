(function () {
    let sourceName = null;

    function rowFrom(target) {
        return target instanceof Element ? target.closest(".mo2-mod-row[data-mo2-mod]") : null;
    }

    function clearVisualState() {
        document.querySelectorAll(".mo2-mod-row.native-dragging, .mo2-mod-row.drag-target")
            .forEach(row => row.classList.remove("native-dragging", "drag-target"));
    }

    document.addEventListener("dragstart", event => {
        const row = rowFrom(event.target);
        if (!row || row.getAttribute("draggable") !== "true") {
            return;
        }

        sourceName = row.dataset.mo2Mod || null;
        if (!sourceName) {
            event.preventDefault();
            return;
        }

        event.dataTransfer.effectAllowed = "move";
        event.dataTransfer.setData("text/plain", sourceName);
        row.classList.add("native-dragging");
    }, true);

    document.addEventListener("dragover", event => {
        const row = rowFrom(event.target);
        if (!sourceName || !row || row.dataset.mo2Mod === sourceName) {
            return;
        }

        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
        document.querySelectorAll(".mo2-mod-row.drag-target")
            .forEach(item => item.classList.remove("drag-target"));
        row.classList.add("drag-target");
    }, true);

    document.addEventListener("dragend", () => {
        const endedSource = sourceName;
        clearVisualState();
        window.setTimeout(() => {
            if (sourceName === endedSource) {
                sourceName = null;
            }
        }, 2000);
    }, true);

    window.anthologyMo2Drag = {
        consumeSource: function () {
            const value = sourceName;
            sourceName = null;
            clearVisualState();
            return value;
        }
    };
})();
