// Search and "modified only" filter for the property panel.
// Wraps the Tweakpane DOM only, prop-grid.js is not modified.
// Note: the page renders @section Scripts inside <head>, so nothing may touch the DOM here.
$(() => {
    const pane = document.getElementById('tweakpane');
    const tools = document.getElementById('divPropTools');
    const searchInput = document.getElementById('txtPropSearch');
    const modifiedCheck = document.getElementById('chkPropModified');
    const counter = document.getElementById('spanPropCount');

    if (!pane || !tools || !searchInput || !modifiedCheck || !counter) {
        return;
    }

    const searchingClass = 'rs-prop-searching';
    let lastObject = null;
    let displayToName = new Map();
    let defaultProps = null;
    let targetProps = null;
    let pending = false;

    // The grid exposes the object being edited; a component has typed defaults to compare against.
    function selectedObject() {
        try {
            return propGrid && propGrid._selectedObject ? propGrid._selectedObject : null;
        } catch (error) {
            return null;
        }
    }

    function rebuildContext() {
        displayToName = new Map();
        defaultProps = null;
        targetProps = null;

        const obj = selectedObject();
        if (!obj) {
            return;
        }

        try {
            const descriptor = PropGridHelper.getObjectDescriptor(obj);
            if (descriptor && descriptor.propertyDescriptors) {
                for (const propertyDescriptor of descriptor.propertyDescriptors.values()) {
                    if (propertyDescriptor.displayName) {
                        displayToName.set(String(propertyDescriptor.displayName).trim(), propertyDescriptor.name);
                    }
                }
            }

            targetProps = PropGridHelper.getTargetObject(obj);

            if (obj instanceof rs.mimic.Component) {
                const factory = rs.mimic.FactorySet.componentFactories.get(obj.typeName);
                if (factory) {
                    defaultProps = factory.createComponent().properties;
                }
            }
        } catch (error) {
            console.warn('Property search: context unavailable', error);
        }
    }

    function ensureContext() {
        const obj = selectedObject();
        if (obj !== lastObject) {
            lastObject = obj;
            rebuildContext();
        }
    }

    function isModified(label) {
        if (!defaultProps || !targetProps) {
            return true;
        }

        const propertyName = displayToName.get(label);

        if (!propertyName || targetProps[propertyName] === undefined) {
            return true;
        }

        const current = targetProps[propertyName];
        const initial = defaultProps[propertyName];

        if (current && typeof current === 'object') {
            return JSON.stringify(current) !== JSON.stringify(initial);
        }

        return current !== initial;
    }

    function folderTitle(folder) {
        const header = folder.querySelector(':scope > .tp-fldv_b');
        return header ? header.textContent.trim() : '';
    }

    function hide(elem) {
        elem.style.display = 'none';
        elem.setAttribute('data-rs-hidden', '');
    }

    function show(elem) {
        elem.style.display = '';
        elem.removeAttribute('data-rs-hidden');
    }

    function clearFilter() {
        pane.classList.remove(searchingClass);
        for (const elem of pane.querySelectorAll('[data-rs-hidden]')) {
            show(elem);
        }
        counter.textContent = '';
    }

    function apply() {
        ensureContext();

        const query = searchInput.value.trim().toLocaleLowerCase();
        const onlyModified = modifiedCheck.checked;

        if (!query && !onlyModified) {
            clearFilter();
            return;
        }

        pane.classList.toggle(searchingClass, query.length > 0);

        // A folder whose own title matches keeps every row it contains.
        const folders = Array.from(pane.querySelectorAll('.tp-fldv'));
        const matchedFolders = new Set(query
            ? folders.filter(folder => folderTitle(folder).toLocaleLowerCase().includes(query))
            : []);

        const inMatchedFolder = elem => {
            for (let node = elem.parentElement; node && node !== pane; node = node.parentElement) {
                if (matchedFolders.has(node)) {
                    return true;
                }
            }
            return false;
        };

        const rows = Array.from(pane.querySelectorAll('.tp-lblv'));
        const labelled = [];
        let visible = 0;

        for (const row of rows) {
            const labelElem = row.querySelector('.tp-lblv_l');
            const label = labelElem ? labelElem.textContent.trim() : '';

            if (!label) {
                hide(row); // decided below, once the folder visibility is known
                continue;
            }

            labelled.push(row);

            const textMatch = !query || label.toLocaleLowerCase().includes(query) || inMatchedFolder(row);
            const modifiedMatch = !onlyModified || isModified(label);

            if (textMatch && modifiedMatch) {
                show(row);
                visible++;
            } else {
                hide(row);
            }
        }

        // Rows without a label (colour and slider blades) follow their folder.
        for (const row of rows) {
            const labelElem = row.querySelector('.tp-lblv_l');
            if (labelElem && labelElem.textContent.trim()) {
                continue;
            }

            const folder = row.closest('.tp-fldv');
            const folderHasRow = folder
                ? Array.from(folder.querySelectorAll('.tp-lblv')).some(item => !item.hasAttribute('data-rs-hidden'))
                : false;

            if (folderHasRow) {
                show(row);
            } else {
                hide(row);
            }
        }

        // Hide folders left empty, and drop separators that only make the result noisier.
        for (const folder of folders) {
            const hasRow = Array.from(folder.querySelectorAll('.tp-lblv'))
                .some(item => !item.hasAttribute('data-rs-hidden'));

            if (hasRow || matchedFolders.has(folder)) {
                show(folder);
            } else {
                hide(folder);
            }
        }

        for (const separator of pane.querySelectorAll('.tp-sprv')) {
            if (query) {
                hide(separator);
            }
        }

        counter.textContent = labelled.length
            ? visible + ' / ' + labelled.length
            : '';
        counter.title = visible === 0
            ? (document.documentElement.lang || '').startsWith('zh') ? '没有匹配的属性' : 'No matching properties'
            : '';
    }

    function refreshTools() {
        const hasRows = !!pane.querySelector('.tp-lblv');
        tools.hidden = !hasRows;

        if (!hasRows) {
            searchInput.value = '';
            modifiedCheck.checked = false;
            clearFilter();
        }
    }

    // A union edit silently applies to every selected component; say so, otherwise the
    // panel looks like it edits one object.
    function refreshMultiHint() {
        const hint = document.getElementById('divMultiHint');
        if (!hint) {
            return;
        }

        const count = typeof selectedComponents !== 'undefined' && Array.isArray(selectedComponents)
            ? selectedComponents.length
            : 0;

        if (count > 1) {
            hint.textContent = (hint.dataset.template || '').replace('{0}', String(count));
            hint.hidden = false;
        } else {
            hint.hidden = true;
        }
    }

    function scheduleApply() {
        if (pending) {
            return;
        }

        pending = true;
        requestAnimationFrame(() => {
            pending = false;
            refreshTools();
            refreshMultiHint();
            apply();
        });
    }

    searchInput.addEventListener('input', apply);
    modifiedCheck.addEventListener('change', apply);
    searchInput.addEventListener('keydown', event => {
        if (event.key === 'Escape') {
            event.stopPropagation(); // do not cancel the pending component placement
            searchInput.value = '';
            apply();
        }
    });

    new MutationObserver(scheduleApply).observe(pane, {childList: true, subtree: true});
    refreshTools();
    refreshMultiHint();
});
