(() => {
    const contentSelector = "#launcher-content-scroll";
    const trackSelector = ".launcher-scroll-track";
    const thumbSelector = ".launcher-scroll-thumb";
    const chatSelector = "[data-chat-scroll]";
    const boundChats = new WeakSet();

    function bindChatScrolls(root = document) {
        const chats = [];
        if (root instanceof Element && root.matches(chatSelector)) chats.push(root);
        if (root.querySelectorAll) chats.push(...root.querySelectorAll(chatSelector));

        for (const chat of chats) {
            if (boundChats.has(chat)) continue;
            boundChats.add(chat);
            let stickToBottom = true;

            const isNearBottom = () => chat.scrollHeight - chat.clientHeight - chat.scrollTop <= 72;
            const scrollToBottom = () => requestAnimationFrame(() => {
                if (stickToBottom && chat.isConnected) chat.scrollTop = chat.scrollHeight;
            });

            chat.addEventListener("scroll", () => {
                stickToBottom = isNearBottom();
            }, { passive: true });

            new MutationObserver(records => {
                if (records.some(record => record.addedNodes.length > 0)) scrollToBottom();
            }).observe(chat, { childList: true, subtree: true });

            new ResizeObserver(scrollToBottom).observe(chat);
            scrollToBottom();
        }
    }

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
        let updateFrame = 0;

        const metrics = () => {
            const trackHeight = track.clientHeight;
            const scrollRange = Math.max(0, content.scrollHeight - content.clientHeight);
            const ratio = content.scrollHeight > 0 ? content.clientHeight / content.scrollHeight : 1;
            const thumbHeight = Math.min(trackHeight, Math.max(58, Math.round(trackHeight * ratio)));
            const travel = Math.max(0, trackHeight - thumbHeight);
            return { trackHeight, scrollRange, thumbHeight, travel };
        };

        const update = () => {
            updateFrame = 0;
            const { scrollRange, thumbHeight, travel } = metrics();
            const progress = scrollRange > 0
                ? Math.max(0, Math.min(1, content.scrollTop / scrollRange))
                : 0;
            thumb.style.height = `${thumbHeight}px`;
            thumb.style.transform = `translateY(${Math.round(progress * travel)}px)`;
            track.setAttribute("aria-valuenow", `${Math.round(progress * 100)}`);
            control.classList.toggle("disabled", scrollRange === 0);
        };

        const scheduleUpdate = () => {
            if (!updateFrame) updateFrame = requestAnimationFrame(update);
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
            content.scrollTo({ top: 0, behavior: "smooth" });
        });
        control.querySelector('[data-scroll-direction="down"]')?.addEventListener("click", () => {
            content.scrollTo({ top: content.scrollHeight - content.clientHeight, behavior: "smooth" });
        });

        track.addEventListener("wheel", event => {
            content.scrollBy({ top: event.deltaY, behavior: "auto" });
            event.preventDefault();
        }, { passive: false });

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

        content.addEventListener("scroll", scheduleUpdate, { passive: true });
        content.addEventListener("load", scheduleUpdate, true);

        const observedContent = new WeakSet();
        const resizeObserver = new ResizeObserver(scheduleUpdate);
        const observeSizes = root => {
            if (root instanceof Element && !observedContent.has(root)) {
                observedContent.add(root);
                resizeObserver.observe(root);
            }
            if (root.querySelectorAll) {
                for (const element of root.querySelectorAll("section, article, div, img, iframe, video")) {
                    if (!observedContent.has(element)) {
                        observedContent.add(element);
                        resizeObserver.observe(element);
                    }
                }
            }
        };

        observeSizes(content);
        new MutationObserver(records => {
            for (const record of records) {
                for (const added of record.addedNodes) observeSizes(added);
            }
            scheduleUpdate();
        }).observe(content, { childList: true, subtree: true, characterData: true });

        if (document.fonts?.ready) document.fonts.ready.then(scheduleUpdate);
        scheduleUpdate();
    }

    new MutationObserver(records => {
        bindScrollControl();
        for (const record of records) {
            for (const added of record.addedNodes) bindChatScrolls(added);
        }
    }).observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener("resize", () => {
        bindScrollControl();
        bindChatScrolls();
    });
    window.anthologyScroll = {
        toTop: () => document.querySelector(contentSelector)?.scrollTo({ top: 0, behavior: "auto" })
    };
    bindScrollControl();
    bindChatScrolls();
})();
