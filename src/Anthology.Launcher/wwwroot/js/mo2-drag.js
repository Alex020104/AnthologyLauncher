(function () {
    let bridge = null;
    let drag = null;
    let suppressClickUntil = 0;

    function findRow(target) {
        return target instanceof Element
            ? target.closest('.mo2-mod-row[data-mo2-mod][data-mo2-draggable="true"]')
            : null;
    }

    function isControl(target) {
        return target instanceof Element && !!target.closest('button, input, select, textarea, a');
    }

    function clearTargets() {
        document.querySelectorAll('.mo2-mod-row.pointer-dragging, .mo2-mod-row.drag-target')
            .forEach(row => row.classList.remove('pointer-dragging', 'drag-target'));
    }

    function removeGhost() {
        document.querySelector('.mo2-pointer-ghost')?.remove();
    }

    function positionGhost(clientX, clientY) {
        const ghost = document.querySelector('.mo2-pointer-ghost');
        if (ghost) {
            ghost.style.transform = `translate3d(${clientX + 18}px, ${clientY + 14}px, 0)`;
        }
    }

    function beginVisualDrag(event) {
        drag.started = true;
        drag.sourceRow.classList.add('pointer-dragging');
        document.body.classList.add('mo2-pointer-drag-active');

        const ghost = document.createElement('div');
        ghost.className = 'mo2-pointer-ghost';
        const icon = document.createElement('span');
        icon.textContent = '↕';
        const title = document.createElement('strong');
        title.textContent = drag.sourceRow.querySelector('strong')?.textContent || drag.sourceName;
        ghost.append(icon, title);
        document.body.appendChild(ghost);
        positionGhost(event.clientX, event.clientY);
    }

    function updateTarget(event) {
        positionGhost(event.clientX, event.clientY);
        const element = document.elementFromPoint(event.clientX, event.clientY);
        const target = findRow(element);

        if (drag.targetRow !== target) {
            drag.targetRow?.classList.remove('drag-target');
            drag.targetRow = target && target !== drag.sourceRow ? target : null;
            drag.targetRow?.classList.add('drag-target');
        }

        const list = drag.sourceRow.closest('.mo2-mod-list');
        if (list) {
            const rect = list.getBoundingClientRect();
            if (event.clientY < rect.top + 42) {
                list.scrollBy({ top: -24, behavior: 'auto' });
            } else if (event.clientY > rect.bottom - 42) {
                list.scrollBy({ top: 24, behavior: 'auto' });
            }
        }
    }

    function cleanup() {
        if (drag?.sourceRow?.hasPointerCapture?.(drag.pointerId)) {
            drag.sourceRow.releasePointerCapture(drag.pointerId);
        }
        clearTargets();
        removeGhost();
        document.body.classList.remove('mo2-pointer-drag-active');
        drag = null;
    }

    document.addEventListener('pointerdown', event => {
        if (event.button !== 0 || isControl(event.target)) {
            return;
        }

        const row = findRow(event.target);
        if (!row) {
            return;
        }

        drag = {
            pointerId: event.pointerId,
            sourceRow: row,
            sourceName: row.dataset.mo2Mod,
            targetRow: null,
            startX: event.clientX,
            startY: event.clientY,
            started: false
        };
        row.setPointerCapture(event.pointerId);
    }, true);

    document.addEventListener('pointermove', event => {
        if (!drag || drag.pointerId !== event.pointerId) {
            return;
        }

        if (!drag.started) {
            const distance = Math.hypot(event.clientX - drag.startX, event.clientY - drag.startY);
            if (distance < 6) {
                return;
            }
            beginVisualDrag(event);
        }

        event.preventDefault();
        updateTarget(event);
    }, { capture: true, passive: false });

    document.addEventListener('pointerup', event => {
        if (!drag || drag.pointerId !== event.pointerId) {
            return;
        }

        const sourceName = drag.sourceName;
        const targetName = drag.targetRow?.dataset.mo2Mod;
        const wasDragging = drag.started;
        cleanup();

        if (!wasDragging) {
            return;
        }

        suppressClickUntil = performance.now() + 450;
        event.preventDefault();
        event.stopPropagation();
        if (bridge && sourceName && targetName && sourceName !== targetName) {
            bridge.invokeMethodAsync('MoveMo2ModFromPointerAsync', sourceName, targetName)
                .catch(error => console.error('MO2 pointer move failed', error));
        }
    }, true);

    document.addEventListener('pointercancel', cleanup, true);
    document.addEventListener('click', event => {
        if (performance.now() < suppressClickUntil && event.target instanceof Element && event.target.closest('.mo2-mod-row')) {
            event.preventDefault();
            event.stopPropagation();
        }
    }, true);

    window.anthologyMo2Drag = {
        initialize: function (dotNetBridge) {
            bridge = dotNetBridge;
        }
    };
})();
