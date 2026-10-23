// Filters the existing structure tree so its selection and Ctrl-click behaviour stay intact.
(() => {
    function initStructureSearch() {
        const panel = document.getElementById('divStructure');
        const search = document.getElementById('structureSearch');
        const clear = document.getElementById('structureSearchClear');
        const count = document.getElementById('structureSearchCount');
        const empty = document.getElementById('structureSearchEmpty');

        if (!panel || !search || !clear || !count || !empty)
            return;

        let refreshPending = false;

        function filterTree() {
            refreshPending = false;
            const query = search.value.trim().toLocaleLowerCase();
            const tree = panel.querySelector('.list-top');
            clear.hidden = search.value.length === 0;

            if (!tree) {
                count.textContent = '';
                empty.hidden = true;
                return;
            }

            const items = Array.from(tree.querySelectorAll('.item-comp'));
            const visible = new Set();
            let matches = 0;

            // Children precede their parents so a matching descendant keeps its whole path visible.
            for (let index = items.length - 1; index >= 0; index--) {
                const item = items[index];
                const node = item.querySelector(':scope > .node-comp');
                const matched = !query || (node?.textContent || '').toLocaleLowerCase().includes(query);

                if (matched) {
                    matches++;
                    visible.add(item);
                }

                const shown = visible.has(item);
                item.hidden = !shown;
                item.classList.toggle('structure-search-match', Boolean(query && matched));

                if (shown) {
                    const parent = item.parentElement?.closest('.item-comp');
                    if (parent)
                        visible.add(parent);
                }
            }

            for (const item of tree.querySelectorAll('.item-dependencies, .item-images'))
                item.hidden = Boolean(query);

            const mimicItem = tree.querySelector('.item-mimic');
            if (mimicItem)
                mimicItem.hidden = Boolean(query && matches === 0);

            const template = query ? count.dataset.filtered : count.dataset.total;
            count.textContent = template.replace('{0}', matches).replace('{1}', items.length);
            empty.textContent = query ? empty.dataset.noMatches : empty.dataset.noComponents;
            empty.hidden = matches > 0;
        }

        function scheduleRefresh() {
            if (!refreshPending) {
                refreshPending = true;
                requestAnimationFrame(filterTree);
            }
        }

        function clearSearch() {
            search.value = '';
            filterTree();
            search.focus();
        }

        search.addEventListener('input', filterTree);
        search.addEventListener('keydown', event => {
            if (event.key === 'Escape') {
                event.preventDefault();
                event.stopPropagation();
                clearSearch();
            }
        });
        clear.addEventListener('click', clearSearch);

        new MutationObserver(records => {
            // Watch tree content only. Our status text, hidden attributes and match classes do not
            // retrigger filtering; rebuilding, renaming, adding or removing tree nodes does.
            const treeChanged = records.some(record => {
                const target = record.target.nodeType === Node.ELEMENT_NODE
                    ? record.target : record.target.parentElement;
                return target?.closest('.list-top') ||
                    [...record.addedNodes, ...record.removedNodes].some(node =>
                        node.nodeType === Node.ELEMENT_NODE && node.matches('.list-top'));
            });

            if (treeChanged)
                scheduleRefresh();
        }).observe(panel, { childList: true, characterData: true, subtree: true });

        filterTree();
    }

    if (document.readyState === 'loading')
        document.addEventListener('DOMContentLoaded', initStructureSearch, { once: true });
    else
        initStructureSearch();
})();
