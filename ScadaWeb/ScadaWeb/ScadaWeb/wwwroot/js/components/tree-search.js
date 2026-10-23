// Adds search and a recently-opened list to the view explorer tree.
// Page scripts are rendered inside <head>, so DOM access starts in $(function).
$(() => {
    const treeElem = document.getElementById('Main_divViewExplorer');
    const input = document.getElementById('Main_txtViewSearch');
    const recentBox = document.getElementById('Main_divRecentViews');

    if (!treeElem || !input) {
        return;
    }

    const zh = (document.documentElement.lang || 'zh').startsWith('zh');
    const storageKey = 'rs.web.recentViews';
    const hiddenClass = 'rs-node-hidden';
    let snapshot = null;

    function childBox(node) {
        const next = node.nextElementSibling;
        return next && next.classList.contains('child-nodes') ? next : null;
    }

    function nodeText(node) {
        return (node.querySelector('.text')?.textContent || '').trim().toLowerCase();
    }

    function subtreeMatches(node, query) {
        if (nodeText(node).includes(query)) {
            return true;
        }

        const box = childBox(node);
        return box
            ? Array.from(box.querySelectorAll('.node')).some(child => subtreeMatches(child, query))
            : false;
    }

    // Search changes the expand state, so the original one is kept for the clear action.
    function takeSnapshot() {
        snapshot = Array.from(treeElem.querySelectorAll('.node')).map(node => {
            const box = childBox(node);
            const expander = node.querySelector('.expander');
            return {
                node,
                boxHidden: box ? box.classList.contains('hidden') : false,
                expanded: expander ? expander.classList.contains('expanded') : false
            };
        });
    }

    function restore() {
        if (!snapshot) {
            return;
        }

        for (const item of snapshot) {
            item.node.classList.remove(hiddenClass);

            const box = childBox(item.node);
            if (box) {
                box.classList.toggle('hidden', item.boxHidden);
            }

            const expander = item.node.querySelector('.expander');
            if (expander) {
                expander.classList.toggle('expanded', item.expanded);
            }
        }

        snapshot = null;
    }

    function apply() {
        const query = input.value.trim().toLowerCase();

        if (!query) {
            restore();
            toggleRecent(true);
            return;
        }

        if (!snapshot) {
            takeSnapshot();
        }

        toggleRecent(false);

        for (const node of treeElem.querySelectorAll('.node')) {
            const selfMatch = nodeText(node).includes(query);
            const box = childBox(node);
            const childMatch = box
                ? Array.from(box.querySelectorAll('.node')).some(child => subtreeMatches(child, query))
                : false;

            node.classList.toggle(hiddenClass, !selfMatch && !childMatch);

            if (box) {
                box.classList.toggle('hidden', !childMatch);
                node.querySelector('.expander')?.classList.toggle('expanded', childMatch);
            }
        }
    }

    function readRecent() {
        try {
            return JSON.parse(localStorage.getItem(storageKey) || '[]');
        } catch (error) {
            return [];
        }
    }

    function renderRecent() {
        if (!recentBox) {
            return;
        }

        const list = readRecent();
        recentBox.innerHTML = '';

        if (list.length === 0) {
            recentBox.hidden = true;
            return;
        }

        const title = document.createElement('div');
        title.className = 'recent-title';
        title.textContent = zh ? '最近访问' : 'Recently opened';
        recentBox.appendChild(title);

        for (const item of list) {
            const link = document.createElement('a');
            link.className = 'recent-item';
            link.href = item.url;
            link.textContent = item.title;
            recentBox.appendChild(link);
        }
    }

    function toggleRecent(show) {
        if (!recentBox) {
            return;
        }

        const hasItems = readRecent().length > 0;
        recentBox.hidden = !(show && hasItems && document.activeElement === input);
    }

    function recordRecent(node) {
        const title = (node.querySelector('.text')?.textContent || '').trim();
        const url = node.getAttribute('href');

        if (!title || !url || url === '#') {
            return;
        }

        const list = readRecent().filter(item => item.url !== url);
        list.unshift({ title, url });

        try {
            localStorage.setItem(storageKey, JSON.stringify(list.slice(0, 5)));
        } catch (error) {
            // Storage is optional.
        }

        renderRecent();
    }

    input.addEventListener('input', apply);
    input.addEventListener('focus', () => {
        renderRecent();
        toggleRecent(true);
    });
    input.addEventListener('blur', () => setTimeout(() => toggleRecent(false), 150));
    input.addEventListener('keydown', event => {
        if (event.key === 'Escape') {
            event.stopPropagation();
            input.value = '';
            apply();
            input.blur();
        }
    });

    // Remember where the user navigates from; the expander arrow is not a navigation.
    treeElem.addEventListener('click', event => {
        const node = event.target.closest('.node');
        if (node && !event.target.closest('.expander')) {
            recordRecent(node);
        }
    }, true);

    renderRecent();
    toggleRecent(false);
});
