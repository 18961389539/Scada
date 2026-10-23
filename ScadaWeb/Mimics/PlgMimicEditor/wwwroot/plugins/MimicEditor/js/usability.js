// Editor conveniences use the normal change queue, undo history and server save path.
(() => {
    const zh = (document.documentElement.lang || 'zh').startsWith('zh');
    const tr = (cn, en) => zh ? cn : en;
    let favorites = new Set();
    try { favorites = new Set(JSON.parse(localStorage.getItem('mimic.componentFavorites') || '[]')); } catch { /* Storage is optional. */ }
    let lastSaved = '', saveFailed = false;

    function filterComponents() {
        const query = ($('#componentSearch').val() || '').trim().toLocaleLowerCase();
        const favoritesOnly = $('#componentFavoritesOnly').prop('checked');
        let count = 0;
        $('#divComponents .component-group').each(function () {
            let groupCount = 0;
            const groupName = $(this).children('.component-group-header').text();
            $(this).find('.component-item').each(function () {
                const type = this.dataset.typeName;
                const label = $(this).find('.component-name').text();
                const visible = (!favoritesOnly || favorites.has(type)) && (type + ' ' + label + ' ' + groupName).toLocaleLowerCase().includes(query);
                this.hidden = !visible;
                if (visible) { count++; groupCount++; }
                if (!$(this).find('.component-favorite').length) {
                    $('<button type="button" class="component-favorite"></button>').appendTo(this);
                    $(this).attr({tabindex: '0', role: 'button'});
                }
                $(this).find('.component-favorite').text(favorites.has(type) ? '★' : '☆')
                    .attr({'aria-label': (favorites.has(type) ? tr('取消收藏 ', 'Unfavorite ') : tr('收藏 ', 'Favorite ')) + label, 'aria-pressed': String(favorites.has(type))});
            });
            this.hidden = groupCount === 0;
        });
        $('#componentSearchEmpty').prop('hidden', count > 0);
    }

    function refreshSaveStatus() {
        const dirty = mimicModified || updateQueue.length > 0 || updateInProgress;
        $('#editorSaveStatus').text(saveFailed ? tr('保存失败，修改尚未保存', 'Save failed; changes are unsaved') : dirty ? tr('有未保存的修改', 'Unsaved changes') : lastSaved || tr('当前文件已载入', 'Current file loaded'))
            .toggleClass('text-danger', saveFailed).toggleClass('text-warning', dirty && !saveFailed);
        $('#btnChannelPicker').prop('disabled', selectedComponents.length !== 1);
    }

    $(document).ready(() => {
        $('#divToolbar').append(document.getElementById('editorToolsTemplate').content.cloneNode(true));
        $('#btnViewRuntime').append(document.createTextNode(' ' + tr('预览运行画面', 'Open runtime view')));
        $('#divComponents').prepend(document.getElementById('componentToolsTemplate').content.cloneNode(true));
        $('#componentSearch, #componentFavoritesOnly').on('input change', filterComponents);
        $('#divComponents').on('click', '.component-favorite', function (event) {
            event.preventDefault(); event.stopPropagation();
            const type = $(this).closest('.component-item').attr('data-type-name');
            if (favorites.has(type)) favorites.delete(type); else favorites.add(type);
            try { localStorage.setItem('mimic.componentFavorites', JSON.stringify([...favorites])); } catch { /* Optional. */ }
            filterComponents();
        }).on('click', '.component-item', function (event) {
            if ($(event.target).closest('.component-favorite').length) return;
            $('#componentHint').text($(this).find('.component-name').text() + ' · ' + tr('已选择，请在画布点击放置。Esc 取消。', 'Selected. Click the canvas to place; Esc cancels.'));
            const type = this.dataset.typeName;
            const factory = rs.mimic.FactorySet.componentFactories.get(type);
            const renderer = rs.mimic.RendererSet.componentRenderers.get(type);
            const preview = $('#componentPreview').empty().prop('hidden', true);
            if (factory && renderer) {
                try {
                    const sample = factory.createComponent();
                    sample.id = -1;
                    const context = new rs.mimic.RenderContext({editMode: true, fontMap: unitedRenderer.fontMap, imageMap: mimic.imageMap, idPrefix: 'preview'});
                    const element = renderer.createDom(sample, context);
                    const scale = Math.min(240 / sample.properties.size.width, 150 / sample.properties.size.height, 1);
                    preview.append(element.css({left: 0, top: 0, transform: `scale(${scale})`, transformOrigin: 'top left'})).prop('hidden', false);
                } catch (error) { console.warn('Component preview unavailable', error); }
            }
        }).on('keydown', '.component-item', function (event) {
            if (event.target !== this) return;
            if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); $(this).trigger('click'); }
        });
        filterComponents();
        $(document).on('keydown.componentTools', event => {
            if (event.key === 'Escape' && $(event.target).closest('#divComponents').length && longAction) {
                pointer();
                $('#componentHint').text(tr('已取消放置。选择组件后可再次放置。', 'Placement cancelled. Select a component to place it again.'));
            }
        });
        new MutationObserver(filterComponents).observe(document.getElementById('divComponents'), {childList: true});
        window.addEventListener('mimic-saved', () => {
            saveFailed = false;
            lastSaved = tr('已保存到当前文件 ', 'Saved to current file ') + new Date().toLocaleTimeString();
            refreshSaveStatus();
        });
        window.addEventListener('mimic-save-failed', () => { saveFailed = true; refreshSaveStatus(); });
        refreshSaveStatus();
        setInterval(refreshSaveStatus, 500);
        updateLayout();

        const catalog = JSON.parse(document.getElementById('editorChannelCatalog').textContent);
        const modal = bootstrap.Modal.getOrCreateInstance(document.getElementById('channelPicker'));
        let component = null;
        function renderChannels() {
            const property = $('#channelPickerProperty').val();
            const query = ($('#channelPickerSearch').val() || '').trim().toLocaleLowerCase();
            const list = $('#channelPickerResults').empty();
            const channels = catalog.filter(c => (['outCnlNum', 'setpointChannel', 'modeOutputChannel'].includes(property) ? c.writable : c.readable) && `${c.number} ${c.name} ${c.unit}`.toLocaleLowerCase().includes(query));
            for (const channel of channels.slice(0, 300)) {
                $('<button type="button" class="list-group-item list-group-item-action"></button>')
                    .text(`${channel.number} · ${channel.name} · ${channel.unit || '—'} · ${channel.writable ? tr('可读写', 'Read/write') : tr('只读', 'Read only')}`)
                    .on('click', () => {
                        if (selectedComponents.length !== 1 || selectedComponents[0] !== component) { modal.hide(); return; }
                        let value = channel.number;
                        if (property === 'channels') {
                            const existing = String(component.properties.channels || '').split(/[,，;；\s]+/).map(Number).filter(n => Number.isInteger(n) && n > 0);
                            value = [...new Set([...existing, channel.number])].slice(0, 16).join(',');
                        }
                        const apply = (name, next) => {
                            component.properties[name] = next;
                            handlePropertyChanged({topObject: component, topPropertyName: name, topPropertyValue: next});
                        };
                        apply(property, value);
                        if (property === 'inCnlNum' && 'unit' in component.properties) apply('unit', channel.unit);
                        propGrid.refresh(true);
                        if (property !== 'channels') modal.hide();
                        else $('#channelPickerSelection').text(component.properties.name + ' · ' + value);
                    }).appendTo(list);
            }
            if (!channels.length) list.text(tr('没有匹配的通道', 'No matching channels'));
            else if (channels.length > 300) $('<p></p>').text(tr('结果较多，请继续输入筛选。', 'Refine the search to see more channels.')).appendTo(list);
        }
        $('#btnChannelPicker').on('click', () => {
            if (selectedComponents.length !== 1) return;
            component = selectedComponents[0];
            const select = $('#channelPickerProperty').empty();
            for (const [property, label] of [['inCnlNum', tr('输入反馈通道', 'Input feedback channel')], ['outCnlNum', tr('输出命令通道', 'Output command channel')], ['channels', tr('通道列表（追加）', 'Channel list (append)')],
                ['permitChannel', tr('控制器最终许可通道', 'Controller permit channel')], ['faultChannel', tr('故障代码通道', 'Fault code channel')],
                ['energizedChannel', tr('独立带电状态通道', 'Independent energization channel')], ['tripChannel', tr('跳闸反馈通道', 'Trip feedback channel')],
                ['feedbackChannel', tr('反馈通道', 'Feedback channel')], ['processChannel', tr('过程值通道', 'Process value channel')],
                ['outputChannel', tr('控制输出反馈通道', 'Control output feedback')], ['setpointChannel', tr('设定输出通道', 'Setpoint output')],
                ['modeChannel', tr('模式反馈通道', 'Mode feedback')], ['modeOutputChannel', tr('模式输出通道', 'Mode output')], ['statusChannel', tr('设备在线状态通道', 'Device online channel')]]) {
                if (property in component.properties) $('<option></option>').val(property).text(label).appendTo(select);
            }
            $('#channelPickerSelection').text(component.properties.name);
            $('#channelPickerSearch').val('');
            renderChannels(); modal.show();
        });
        $('#channelPickerSearch').on('input', renderChannels);
        $('#channelPickerProperty').on('change', renderChannels);
        $('#channelPicker').on('shown.bs.modal', () => $('#channelPickerSearch').trigger('focus'));
    });
})();
