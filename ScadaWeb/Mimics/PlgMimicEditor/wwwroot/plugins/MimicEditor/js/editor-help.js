// Keyboard shortcut reference for the mimic editor.
// The page renders @section Scripts inside <head>, so DOM access starts in $(function).
$(() => {
    const button = document.getElementById('btnEditorHelp');
    if (!button) {
        return;
    }

    const zh = (document.documentElement.lang || 'zh').startsWith('zh');
    const items = zh ? [
        ['方向键', '按网格步长移动选中组件'],
        ['Ctrl + 方向键', '1 px 微调位置'],
        ['Shift + 方向键', '1 px 调整尺寸'],
        ['Ctrl + 点击', '加选 / 取消选择组件'],
        ['Ctrl + S', '保存'],
        ['Ctrl + Z / Ctrl + Y 或 Ctrl + Shift + Z', '撤销 / 重做'],
        ['Ctrl + X / C / V', '剪切 / 复制 / 粘贴'],
        ['Delete', '删除选中组件'],
        ['Esc', '取消放置或拖动，回到指针'],
        ['/', '聚焦组件搜索；搜索中按 Esc 返回画布'],
        ['? 或本按钮', '打开 / 关闭本帮助']
    ] : [
        ['Arrow keys', 'Move the selection by one grid step'],
        ['Ctrl + arrows', 'Nudge by 1 px'],
        ['Shift + arrows', 'Resize by 1 px'],
        ['Ctrl + click', 'Add to / remove from the selection'],
        ['Ctrl + S', 'Save'],
        ['Ctrl + Z / Ctrl + Y or Ctrl + Shift + Z', 'Undo / redo'],
        ['Ctrl + X / C / V', 'Cut / copy / paste'],
        ['Delete', 'Remove the selected components'],
        ['Esc', 'Cancel placing or dragging, back to pointer'],
        ['/', 'Focus component search; Esc returns to canvas'],
        ['? or this button', 'Toggle this help']
    ];

    let panel = null;

    function close() {
        if (panel) {
            panel.remove();
            panel = null;
        }
    }

    function toggle() {
        if (panel) {
            close();
            return;
        }

        panel = document.createElement('div');
        panel.id = 'divEditorShortcuts';
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-label', zh ? '快捷键' : 'Shortcuts');
        panel.style.cssText = 'position:fixed;z-index:2000;right:16px;top:52px;background:#1e2a38;' +
            'color:#f2f6fa;padding:12px 14px;border-radius:8px;font-size:12px;line-height:1.9;' +
            'box-shadow:0 8px 24px rgba(0,0,0,.28);min-width:260px;max-width:min(92vw,420px);';
        panel.innerHTML = '<div style="font-weight:600;margin-bottom:6px">' +
            (zh ? '快捷键' : 'Shortcuts') + '</div>' +
            items.map(([key, text]) => '<div style="display:flex;justify-content:space-between;gap:18px">' +
                '<span style="opacity:.75">' + text + '</span>' +
                '<kbd style="font-family:inherit;background:#33445a;border-radius:4px;padding:0 6px;white-space:nowrap">' +
                key + '</kbd></div>').join('');
        document.body.appendChild(panel);
    }

    button.addEventListener('click', toggle);

    document.addEventListener('keydown', event => {
        const target = event.target;
        const typing = target && (target.matches('input, textarea, select') ||
            target.closest('[contenteditable]:not([contenteditable="false"]), [role="textbox"]'));

        if (event.isComposing || event.ctrlKey || event.metaKey || event.altKey ||
            document.querySelector('.modal.show')) {
            return;
        }

        if (event.key === '/' && !typing) {
            const tab = document.querySelector('[data-bs-target="#divComponents"]');
            if (tab) bootstrap.Tab.getOrCreateInstance(tab).show();
            const search = document.getElementById('componentSearch');
            if (search) {
                event.preventDefault();
                search.focus();
                search.select();
            }
        } else if (event.key === '?' && !typing) {
            event.preventDefault();
            toggle();
        } else if (event.key === 'Escape' && panel) {
            close();
        }
    });

    document.getElementById('componentSearch')?.addEventListener('keydown', event => {
        if (event.key === 'Escape' && !event.isComposing) {
            event.preventDefault();
            event.stopPropagation();
            event.target.value = '';
            event.target.dispatchEvent(new Event('input', { bubbles: true }));
            event.target.blur();
        }
    });
});
