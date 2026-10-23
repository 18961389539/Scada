// Center the current selection without changing the model, selection or page scroll.
(() => {
    function initCanvasNavigation() {
        const button = document.getElementById('btnEditorLocate');
        const wrapper = document.getElementById('divMimicWrapper');
        const structure = document.getElementById('divStructure');

        if (!button || !wrapper)
            return;

        const zh = (document.documentElement.lang || 'zh').startsWith('zh');
        const locateTitle = zh ? '定位选中组件（F）' : 'Center selected components (F)';
        const nodeTitle = zh ? '双击定位到画布' : 'Double-click to locate on the canvas';
        button.title = locateTitle;
        button.setAttribute('aria-label', locateTitle);
        button.parentElement.title = locateTitle;
        let refreshPending = false;
        const requestedUrl = new URL(window.location.href);
        const requestedValue = requestedUrl.searchParams.get('componentID');
        const requestedNumber = Number(requestedValue);
        let requestedComponentID = /^[1-9]\d*$/.test(requestedValue || '') &&
            Number.isSafeInteger(requestedNumber) && requestedNumber <= 2147483647 ? requestedNumber : null;
        let requestedZoomObserver = null;

        function selection() {
            return typeof selectedComponents !== 'undefined' && Array.isArray(selectedComponents)
                ? selectedComponents : [];
        }

        function refresh() {
            refreshPending = false;
            locateRequestedComponent();
            button.disabled = selection().length === 0;

            if (structure) {
                for (const node of structure.querySelectorAll('.node-comp'))
                    node.title = nodeTitle;
            }
        }

        function scheduleRefresh() {
            if (!refreshPending) {
                refreshPending = true;
                requestAnimationFrame(refresh);
            }
        }

        function locateSelection() {
            const rects = [];

            for (const component of selection()) {
                const elements = component.dom?.nodeType === Node.ELEMENT_NODE
                    ? [component.dom] : Array.from(component.dom || []);

                for (const element of elements) {
                    if (wrapper.contains(element) && element.getClientRects().length > 0)
                        rects.push(element.getBoundingClientRect());
                }
            }

            if (rects.length === 0 || wrapper.clientWidth === 0 || wrapper.clientHeight === 0)
                return false;

            // Rendered bounds include every container offset, rotation and CSS zoom factor.
            const left = Math.min(...rects.map(rect => rect.left));
            const right = Math.max(...rects.map(rect => rect.right));
            const top = Math.min(...rects.map(rect => rect.top));
            const bottom = Math.max(...rects.map(rect => rect.bottom));
            const viewport = wrapper.getBoundingClientRect();
            const scrollLeft = wrapper.scrollLeft + (left + right) / 2 -
                viewport.left - wrapper.clientLeft - wrapper.clientWidth / 2;
            const scrollTop = wrapper.scrollTop + (top + bottom) / 2 -
                viewport.top - wrapper.clientTop - wrapper.clientHeight / 2;

            wrapper.scrollLeft = Math.max(0, Math.min(wrapper.scrollWidth - wrapper.clientWidth, scrollLeft));
            wrapper.scrollTop = Math.max(0, Math.min(wrapper.scrollHeight - wrapper.clientHeight, scrollTop));
            return true;
        }

        function locateRequestedComponent() {
            if (requestedComponentID === null)
                return;

            const canvas = wrapper.querySelector('.mimic');

            // The page restores CSS zoom shortly after loading the model. Wait for that first
            // application so a saved zoom cannot move the requested component out of view again.
            if (!canvas || !canvas.style.zoom || typeof mimic === 'undefined')
                return;

            const componentID = requestedComponentID;
            requestedComponentID = null;
            requestedZoomObserver?.disconnect();
            const component = mimic.componentMap.get(componentID);

            if (component && typeof selectComponent === 'function') {
                selectComponent(component);
                locateSelection();
            }

            // Consume only this navigation parameter; later redraws or a browser refresh retain
            // the user's current navigation instead of repeatedly selecting the same component.
            requestedUrl.searchParams.delete('componentID');
            window.history.replaceState(window.history.state, '', requestedUrl);
        }

        button.addEventListener('click', locateSelection);
        document.addEventListener('keydown', event => {
            const target = event.target;
            const typing = target && (target.matches('input, textarea, select') ||
                target.closest('[contenteditable]:not([contenteditable="false"]), [role="textbox"]'));

            if (event.defaultPrevented || event.isComposing || event.ctrlKey || event.metaKey ||
                event.altKey || event.shiftKey || typing || document.querySelector('.modal.show') ||
                typeof longAction !== 'undefined' && longAction || event.key.toLowerCase() !== 'f') {
                return;
            }

            if (locateSelection())
                event.preventDefault();
        });

        structure?.addEventListener('dblclick', event => {
            // The original click handlers have already selected this node (or preserved a multiselection).
            // Reuse that selection rather than synthesizing clicks or changing component state.
            if (event.target.closest('.node-comp') && !event.ctrlKey && !event.metaKey && !event.altKey &&
                !event.shiftKey && !document.querySelector('.modal.show') &&
                !(typeof longAction !== 'undefined' && longAction)) {
                if (locateSelection())
                    event.preventDefault();
            }
        });

        const observer = new MutationObserver(scheduleRefresh);
        observer.observe(wrapper, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
        if (structure)
            observer.observe(structure, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
        if (requestedComponentID !== null) {
            requestedZoomObserver = new MutationObserver(scheduleRefresh);
            requestedZoomObserver.observe(wrapper, {
                subtree: true, attributes: true, attributeFilter: ['style']
            });
        }
        refresh();
    }

    if (document.readyState === 'loading')
        document.addEventListener('DOMContentLoaded', initCanvasNavigation, { once: true });
    else
        initCanvasNavigation();
})();
