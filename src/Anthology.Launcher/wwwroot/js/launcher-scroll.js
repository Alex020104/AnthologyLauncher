(() => {
    const contentSelector = "#launcher-content-scroll";
    const trackSelector = ".launcher-scroll-track";
    const thumbSelector = ".launcher-scroll-thumb";

    function bindScrollControl() {
        const content = document.querySelector(contentSelector);
        const track = document.querySelector(trackSelector);
        const thumb = track?.querySelector(thumbSelector);
        const control = track?.closest(".launcher-scroll-control");
        if (!content || !track || !thumb || !control || track.dataset.bound === "true") {
            return;
        }

        track.dataset.bound = "true";
        let dragging = false;
        let dragOffset = 0;

        const metrics = () => {
            const trackHeight = track.clientHeight;
            const scrollRange = Math.max(0, content.scrollHeight - content.clientHeight);
            const ratio = content.scrollHeight > 0 ? content.clientHeight / content.scrollHeight : 1;
            const thumbHeight = Math.min(trackHeight, Math.max(58, Math.round(trackHeight * ratio)));
            const travel = Math.max(0, trackHeight - thumbHeight);
            return { trackHeight, scrollRange, thumbHeight, travel };
        };

        const update = () => {
            const { scrollRange, thumbHeight, travel } = metrics();
            const progress = scrollRange > 0 ? content.scrollTop / scrollRange : 0;
            thumb.style.height = `${thumbHeight}px`;
            thumb.style.transform = `translateY(${Math.round(progress * travel)}px)`;
            track.setAttribute("aria-valuenow", `${Math.round(progress * 100)}`);
            control.classList.toggle("disabled", scrollRange === 0);
        };

        const scrollToPointer = (clientY, offset = 0) => {
            const rect = track.getBoundingClientRect();
            const { scrollRange, travel } = metrics();
            const position = Math.max(0, Math.min(travel, clientY - rect.top - offset));
            content.scrollTop = travel > 0 ? (position / travel) * scrollRange : 0;
        };

        thumb.addEventListener("pointerdown", event => {
            dragging = true;
            const rect = thumb.getBoundingClientRect();
            dragOffset = event.clientY - rect.top;
            thumb.classList.add("dragging");
            thumb.setPointerCapture(event.pointerId);
            event.preventDefault();
        });

        thumb.addEventListener("pointermove", event => {
            if (dragging) {
                scrollToPointer(event.clientY, dragOffset);
            }
        });

        const stopDragging = event => {
            if (!dragging) {
                return;
            }

            dragging = false;
            thumb.classList.remove("dragging");
            if (thumb.hasPointerCapture(event.pointerId)) {
                thumb.releasePointerCapture(event.pointerId);
            }
        };
        thumb.addEventListener("pointerup", stopDragging);
        thumb.addEventListener("pointercancel", stopDragging);

        track.addEventListener("pointerdown", event => {
            if (event.target !== thumb) {
                const { thumbHeight } = metrics();
                scrollToPointer(event.clientY, thumbHeight / 2);
            }
        });

        control.querySelector('[data-scroll-direction="up"]')?.addEventListener("click", () => {
            content.scrollBy({ top: -Math.max(140, content.clientHeight * 0.7), behavior: "smooth" });
        });
        control.querySelector('[data-scroll-direction="down"]')?.addEventListener("click", () => {
            content.scrollBy({ top: Math.max(140, content.clientHeight * 0.7), behavior: "smooth" });
        });

        track.addEventListener("keydown", event => {
            if (event.key === "ArrowUp" || event.key === "PageUp") {
                content.scrollBy({ top: event.key === "PageUp" ? -content.clientHeight : -90, behavior: "smooth" });
                event.preventDefault();
            } else if (event.key === "ArrowDown" || event.key === "PageDown") {
                content.scrollBy({ top: event.key === "PageDown" ? content.clientHeight : 90, behavior: "smooth" });
                event.preventDefault();
            } else if (event.key === "Home") {
                content.scrollTo({ top: 0, behavior: "smooth" });
                event.preventDefault();
            } else if (event.key === "End") {
                content.scrollTo({ top: content.scrollHeight, behavior: "smooth" });
                event.preventDefault();
            }
        });

        content.addEventListener("scroll", update, { passive: true });
        new ResizeObserver(update).observe(content);
        new MutationObserver(update).observe(content, { childList: true, subtree: true });
        update();
    }

    new MutationObserver(bindScrollControl).observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener("resize", bindScrollControl);
    window.anthologyScroll = {
        toTop: () => document.querySelector(contentSelector)?.scrollTo({ top: 0, behavior: "auto" })
    };
    bindScrollControl();
})();
