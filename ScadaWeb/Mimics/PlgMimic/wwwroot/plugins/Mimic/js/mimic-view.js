// Handles the mimic view page.
// Depends on jquery, scada-common.js, view-hub.js, main-api.js, mimic-common.js, mimic-model.js, mimic-render.js

class MimicDataProvider extends rs.mimic.DataProvider {
    getCurData = (cnlNum, opt_joinLen) =>
        MainApi.getCurDataFromMap(this.curDataMap, cnlNum, opt_joinLen);
    getPrevData = (cnlNum, opt_joinLen) =>
        MainApi.getCurDataFromMap(this.prevDataMap, cnlNum, opt_joinLen);
}

const viewHub = ViewHub.getInstance();
const mainApi = new MainApi({ rootPath: viewHub.appEnv.rootPath });
mainApi.dataPaused = true;
const mimic = new rs.mimic.Mimic();
const unitedRenderer = new rs.mimic.UnitedRenderer(mimic, false);
const dataProvider = new MimicDataProvider();

// Set in MimicView.cshtml
var viewID = 0;
var controlRight = false;
var refreshRate = 1000;
var fonts = [];
var runtimeOptions = {};
var phrases = {};

let errorElem = $();
let spinnerElem = $();
let mimicWrapperElem = $();
let toolbarElem = $();

let cnlListID = 0;
let scale = new rs.mimic.Scale();
let errorTimeoutID = 0;
let offlineBannerElem = $();
let toastElem = $();
let toastTimeoutID = 0;
let lastToastText = "";
let lastToastAt = 0;
let updateTimerID = 0;
let updateInFlight = false;
let dataStarted = false;

function initElements() {
    errorElem = $("#divError");
    spinnerElem = $("#divSpinner");
    mimicWrapperElem = $("#divMimicWrapper");
    toolbarElem = $("#divToolbar");

    offlineBannerElem = $('<div id="divOfflineBanner" role="status" aria-live="assertive" hidden></div>')
        .appendTo(document.body);
    toastElem = $('<div id="divRuntimeToast" role="alert" aria-live="assertive"></div>')
        .appendTo(document.body).hide();
}

// Plain-language feedback when a click cannot do anything. The operator should never
// wonder whether the system is broken or the click was simply ignored.
function showRuntimeToast(text) {
    if (!toastElem.length)
        return;

    toastElem.text(text).stop(true, true).fadeIn(120);
    clearTimeout(toastTimeoutID);
    toastTimeoutID = setTimeout(() => toastElem.fadeOut(200), 3600);
}

function notifyBlocked(text) {
    const now = Date.now();
    if (text === lastToastText && now - lastToastAt < 4000)
        return;

    lastToastText = text;
    lastToastAt = now;
    showRuntimeToast(text);
}

function bindEvents() {
    mimicWrapperElem[0].addEventListener('click', event => {
        const componentElem = event.target.closest('.comp');

        if (!componentElem || event.target.closest('a[href]')) {
            return; // navigation remains available during a data outage
        }

        const zh = (document.documentElement.lang || 'zh').startsWith('zh');

        if (mainApi.dataPaused) {
            event.preventDefault();
            event.stopImmediatePropagation();
            notifyBlocked(zh
                ? '连接中断，命令未发送。请等数据恢复后再操作。'
                : 'Connection lost; the command was not sent. Retry after the data is back.');
            return;
        }

        if (!controlRight) {
            // The component shows its own read-only hint, but only on small controls.
            notifyBlocked(zh
                ? '当前账号为只读权限，无法下发命令。'
                : 'This account is read-only; commands are disabled.');
        }
    }, true);
    const zh = (document.documentElement.lang || 'zh').startsWith('zh');
    Object.assign(modalPhrases, {
        ok: zh ? '确定' : 'OK', close: zh ? '关闭' : 'Close',
        execute: zh ? '执行命令' : 'Execute', cancel: zh ? '取消' : 'Cancel'
    });
    $('<button type="button" id="btnActualSize" class="tool-item tool-btn">100%</button>')
        .attr('title', zh ? '实际大小，保证文字与按钮可读' : 'Actual size')
        .insertBefore('#spanScaleValue').on('click', () => {
            scale = new rs.mimic.Scale(rs.mimic.ScaleType.NUMERIC, 1);
            updateScale();
        });
    $('<span id="mimicConnectionStatus" role="status" aria-live="polite"></span>').appendTo('#divToolbar');
    $(window).on("resize", function () {
        updateLayout();

        if (scale.type !== rs.mimic.ScaleType.NUMERIC) {
            updateScale();
        }
    });

    $(document).on("keydown", function (event) {
        let targetElem = $(event.target);

        if ((targetElem.is("body") || targetElem.closest("#divToolbar").length > 0) &&
            handleKeyDown(event.code, event.ctrlKey)) {
            event.preventDefault();
        }
    });

    $("#btnFitScreen").on("click", function () {
        scale = new rs.mimic.Scale(rs.mimic.ScaleType.FIT_SCREEN);
        updateScale();
    });

    $("#btnFitWidthBtn").on("click", function () {
        scale = new rs.mimic.Scale(rs.mimic.ScaleType.FIT_WIDTH);
        updateScale();
    });

    $("#btnZoomOutBtn").on("click", function () {
        scale = scale.getPrev();
        updateScale();
    });

    $("#btnZoomInBtn").on("click", function () {
        scale = scale.getNext();
        updateScale();
    });
}

function bindGestureEvents() {
    // when zoom in quickly, glitches may occur because the scrollable area does not expand fast enough
    let initialCenter = null;
    let initialDistance = 0;
    let initialScale = 0;
    let initialScroll = null;

    let getCenter = (event) => {
        let touch1 = event.touches[0];
        let touch2 = event.touches[1];

        return {
            x: (touch1.clientX + touch2.clientX) / 2,
            y: (touch1.clientY + touch2.clientY) / 2
        };
    };

    let getDistance = (event) => {
        let touch1 = event.touches[0];
        let touch2 = event.touches[1];

        return Math.hypot(
            touch2.clientX - touch1.clientX,
            touch2.clientY - touch1.clientY
        );
    };

    mimicWrapperElem
        .on("touchstart", function (event) {
            if (event.touches.length === 2) {
                initialCenter = getCenter(event);
                initialDistance = getDistance(event);
                initialScale = scale.value;
                initialScroll = {
                    x: mimicWrapperElem.scrollLeft(),
                    y: mimicWrapperElem.scrollTop()
                };
                event.preventDefault();
            }
        })
        .on("touchmove", function (event) {
            if (event.touches.length === 2 && initialCenter && initialDistance > 0 && initialScale > 0) {
                // measure gesture
                let currentCenter = getCenter(event);
                let currentDistance = getDistance(event);

                // update scale
                let newScale = initialScale * currentDistance / initialDistance;
                scale = new rs.mimic.Scale(rs.mimic.ScaleType.NUMERIC, newScale);
                updateScale();

                // scroll
                let scaleRatio = scale.value / initialScale;
                let scrollLeft = Math.round((initialScroll.x + initialCenter.x) * scaleRatio - currentCenter.x);
                let scrollTop = Math.round((initialScroll.y + initialCenter.y) * scaleRatio - currentCenter.y);
                mimicWrapperElem.scrollLeft(scrollLeft);
                mimicWrapperElem.scrollTop(scrollTop);
                event.preventDefault();
            }
        });
}

function updateLayout() {
    let h = $(window).height();
    errorElem.outerHeight(h);
    spinnerElem.outerHeight(h);
    mimicWrapperElem.outerHeight(h);
    alignHorizontally();
}

async function loadMimic() {
    spinnerElem.removeClass("d-none");
    let result = await mimic.load(getLoaderUrl(), viewID);

    if (result.ok) {
        try {
            viewHub.setViewTitle(window, mimic.title);
            mimic.mapComponentsByName();
            mimicWrapperElem.append(unitedRenderer.createMimicDom());
            toolbarElem.removeClass("d-none");
            initScale();
            startUpdatingData();
            startBlinking();
        }
        catch (ex) {
            console.error(ex);
            showErrorText(phrases.displayMimicError);
        }
    } else {
        showErrorText(result.msg);
    }

    spinnerElem.addClass("d-none");
}

function getLoaderUrl() {
    return viewHub.appEnv.rootPath + "Api/Mimic/";
}

function startUpdatingData() {
    dataStarted = true;

    // A hidden tab keeps no polling loop. visibilitychange restarts it immediately.
    if (document.hidden || updateInFlight)
        return;

    updateInFlight = true;

    updateData(function () {
        updateInFlight = false;

        if (document.hidden)
            return;

        clearTimeout(updateTimerID);
        updateTimerID = setTimeout(startUpdatingData, refreshRate);
    });
}

function handleVisibilityChange() {
    if (document.hidden) {
        clearTimeout(updateTimerID);
        updateTimerID = 0;
        return;
    }

    if (dataStarted && !updateInFlight) {
        clearTimeout(updateTimerID);
        updateTimerID = 0;
        startUpdatingData();
    }
}

function updateData(callback) {
    mainApi.getCurDataByView(viewID, cnlListID, function (dto) {
        const zh = (document.documentElement.lang || 'zh').startsWith('zh');
        const connectionChanged = mainApi.dataPaused === dto.ok;
        mainApi.dataPaused = !dto.ok;
        mimicWrapperElem.toggleClass('data-paused', !dto.ok);
        document.body.classList.toggle('mimic-offline', !dto.ok);
        offlineBannerElem.prop('hidden', dto.ok).text(dto.ok ? '' : (zh
            ? '连接中断 · 显示上次数据，控制已暂停'
            : 'Disconnected · showing stale data, controls paused'));
        $('#mimicConnectionStatus').text(dto.ok
            ? (zh ? '更新于 ' : 'Updated ') + new Date().toLocaleTimeString()
            : (zh ? '连接中断，显示上次数据，控制已暂停' : 'Disconnected; showing previous data; controls paused'))
            .toggleClass('text-danger', !dto.ok)
            .attr('title', dto.ok ? '' : (dto.msg || ''));
        if (dto.ok) {
            cnlListID = dto.data.cnlListID;
        } else {
            showErrorIcon(dto.msg);
            if (connectionChanged) {
                for (const component of mimic.components) unitedRenderer.updateComponentDom(component);
            }
            callback();
            return;
        }

        dataProvider.prevDataMap = dataProvider.curDataMap;
        dataProvider.curDataMap = MainApi.mapCurData(dto.data);
        unitedRenderer.updateData(dataProvider);
        if (connectionChanged) {
            for (const component of mimic.components) unitedRenderer.updateComponentDom(component);
        }
        callback();
    });
}

function startBlinking() {
    let blinkers = getBlinkers(); // components that can blink

    if (blinkers.length > 0 && runtimeOptions.blinkingRate > 0) {
        let blinkOn = false;
        let blinkIteration = () => {
            blinkOn = !blinkOn;
            blink(blinkers, blinkOn);
            setTimeout(blinkIteration, runtimeOptions.blinkingRate);
        };

        setTimeout(blinkIteration, runtimeOptions.blinkingRate);
    }
}

function getBlinkers() {
    let blinkers = [];

    for (let component of mimic.components) {
        let blinkingState = component.isFaceplate
            ? component.document?.blinkingState
            : component.properties?.blinkingState;

        if (blinkingState?.isSet) {
            blinkers.push(component);
        }
    }

    return blinkers;
}

function blink(blinkers, blinkOn) {
    const EventType = rs.mimic.EventType;

    for (let blinker of blinkers) {
        if (blinker.dom) {
            if (blinkOn) {
                if (blinker.properties.blinking) {
                    blinker.dom.addClass("blink-on");
                    blinker.dom.trigger(EventType.BLINK_ON);
                }
            } else if (blinker.dom.hasClass("blink-on")) {
                blinker.dom.removeClass("blink-on");
                blinker.dom.trigger(EventType.BLINK_OFF);
            }
        }
    }
}

function initScale() {
    if (runtimeOptions.rememberScale) {
        scale.load(localStorage);
    } else {
        scale.type = runtimeOptions.scaleType;
        scale.value = runtimeOptions.scaleValue;
    }

    updateScale(false);
}

function updateScale(saveScale = true) {
    mimic.renderer?.setScale(mimic, scale);
    $("#spanScaleValue").text(Math.round(scale.value * 100) + "%");
    alignHorizontally();

    if (saveScale) {
        scale.save(localStorage);
    }
}

function alignHorizontally() {
    let wrapperPadding = 0;

    if (mimic.dom) {
        let mimicWidth = mimic.dom[0].getBoundingClientRect().width; // taking scaling into account
        let wrapperWidth = mimicWrapperElem.innerWidth();

        if (wrapperWidth > mimicWidth) {
            wrapperPadding = Number.parseInt((wrapperWidth - mimicWidth) / 2);
        }
    }

    mimicWrapperElem.css("padding-left", wrapperPadding);
};

function showErrorText(text) {
    errorElem
        .text(text)
        .removeClass("d-none");
}

function showErrorIcon(message) {
    clearTimeout(errorTimeoutID);
    // Keep the reason on the icon so hovering explains what went wrong.
    $("#spanErrorIcon").attr("title", message || "").removeClass("d-none");

    errorTimeoutID = setTimeout(function () {
        $("#spanErrorIcon").addClass("d-none");
        errorTimeoutID = 0;
    }, ScadaUtils.ERROR_DISPLAY_DURATION);
}

function handleKeyDown(code, ctrlKey) {
    if (ctrlKey) {
        switch (code) {
            case "Minus":
                scale = scale.getPrev();
                updateScale();
                return true;

            case "Equal":
                scale = scale.getNext();
                updateScale();
                return true;

            case "Digit0":
                scale = new rs.mimic.Scale();
                updateScale();
                return true;
        }
    }

    return false;
}


// Shows the keyboard and touch shortcuts; nobody discovers Ctrl+= otherwise.
function initShortcutHelp() {
    const zh = (document.documentElement.lang || 'zh').startsWith('zh');
    const items = zh
        ? [["Ctrl + =", "放大"], ["Ctrl + -", "缩小"], ["Ctrl + 0", "恢复 100%"], ["F", "全屏 / 退出全屏"], ["双指手势", "触摸缩放"], ["?", "打开/关闭本帮助"]]
        : [["Ctrl + =", "Zoom in"], ["Ctrl + -", "Zoom out"], ["Ctrl + 0", "Reset to 100%"], ["F", "Toggle full screen"], ["Two-finger pinch", "Zoom on touch"], ["?", "Toggle this help"]];
    let panel = null;

    function toggle() {
        if (panel) {
            panel.remove();
            panel = null;
            return;
        }

        panel = document.createElement("div");
        panel.style.cssText = "position:fixed;z-index:9999;right:16px;top:52px;background:#1e2a38;" +
            "color:#f2f6fa;padding:12px 14px;border-radius:8px;font-size:12px;line-height:1.9;" +
            "box-shadow:0 8px 24px rgba(0,0,0,.28);min-width:220px;";
        panel.innerHTML = '<div style="font-weight:600;margin-bottom:6px">' +
            (zh ? "快捷键" : "Shortcuts") + "</div>" +
            items.map(([key, text]) => '<div style="display:flex;justify-content:space-between;gap:18px">' +
                '<span style="opacity:.75">' + text + "</span>" +
                '<kbd style="font-family:inherit;background:#33445a;border-radius:4px;padding:0 6px">' +
                key + "</kbd></div>").join("");
        document.body.appendChild(panel);
    }

    window.addEventListener("keydown", function (event) {
        if (event.key === "?" && !/^(INPUT|TEXTAREA|SELECT)$/.test(event.target.tagName)) {
            event.preventDefault();
            toggle();
        } else if (event.key === "Escape" && panel) {
            toggle();
        }
    });

    $("#btnHelp").on("click", toggle);
}


// Full screen toggle. Shop-floor panels are often driven by a keyboard, so a hotkey helps too.
function toggleFullscreen() {
    if (document.fullscreenElement) {
        document.exitFullscreen?.().catch(() => { /* Already left. */ });
        return;
    }

    document.documentElement.requestFullscreen?.().catch(() => {
        const zh = (document.documentElement.lang || 'zh').startsWith('zh');
        notifyBlocked(zh ? '浏览器拒绝了全屏请求，请按 F11。' : 'Full screen was blocked; press F11.');
    });
}

function initFullscreen() {
    const button = $("#btnFullscreen");

    function sync() {
        const active = !!document.fullscreenElement;
        button.attr("aria-pressed", String(active))
            .find("i").toggleClass("fa-expand", !active).toggleClass("fa-compress", active);
    }

    button.on("click", toggleFullscreen);
    document.addEventListener("fullscreenchange", sync);

    $(document).on("keydown", function (event) {
        if (event.ctrlKey || event.metaKey || event.altKey)
            return;
        if (/^(INPUT|TEXTAREA|SELECT)$/.test(event.target.tagName))
            return;
        if (event.code === "KeyF") {
            event.preventDefault();
            toggleFullscreen();
        }
    });

    sync();
}

// Kiosk mode hides the view strip and fades the toolbar so a wall display shows only the mimic.
// Enabled per link with ?kiosk=1; the toolbar reappears on hover so nobody gets locked out.
function initKioskMode() {
    const params = new URLSearchParams(window.location.search);

    if (params.get("kiosk") === "1") {
        document.body.classList.add("mimic-kiosk");
    }
}

// Wall displays rotate through the views. Enabled per link with ?rotate=N (seconds) or the
// toolbar button; the choice is remembered and operator interaction pauses the rotation.
function initRotation() {
    const links = Array.from(document.querySelectorAll('#runtimeNavigation a'));
    const button = $("#btnRotate");

    if (!button.length) {
        return;
    }

    if (links.length < 2) {
        button.hide();
        return;
    }

    const storageKey = "rs.mimic.rotate";
    const defaultSeconds = 15;
    const pauseAfterInteraction = 30000;
    const params = new URLSearchParams(window.location.search);
    const requested = Number(params.get("rotate"));
    const seconds = Number.isFinite(requested) && requested >= 3 ? requested : defaultSeconds;
    const baseTitle = button.attr("title") || "";

    let timerID = 0;
    let resumeAt = 0;

    function nextUrl() {
        const index = links.findIndex(link => link.classList.contains("current"));
        const link = links[index >= 0 ? (index + 1) % links.length : 0];

        if (!link) {
            return null;
        }

        const url = new URL(link.href, window.location.origin);

        // Keep the display mode while rotating through the links.
        if (params.get("kiosk") === "1") {
            url.searchParams.set("kiosk", "1");
        }

        return url.toString();
    }

    function tick() {
        if (!timerID || Date.now() < resumeAt) {
            return;
        }

        const url = nextUrl();
        if (url) {
            // The view may run inside the navigation frame, so navigate the top window.
            window.top.location.href = url;
        }
    }

    function start() {
        clearInterval(timerID);
        timerID = setInterval(tick, seconds * 1000);
        button.attr("aria-pressed", "true").attr("title", baseTitle + " · " + seconds + "s");
    }

    function stop() {
        clearInterval(timerID);
        timerID = 0;
        button.attr("aria-pressed", "false").attr("title", baseTitle);
    }

    function remember(on) {
        try {
            localStorage.setItem(storageKey, on ? "1" : "0");
        } catch (error) {
            // Storage is optional.
        }
    }

    button.on("click", function () {
        if (timerID) {
            stop();
            remember(false);
        } else {
            start();
            remember(true);
        }
    });

    // Operator interaction wins: hold the rotation for half a minute.
    for (const type of ["mousemove", "mousedown", "keydown", "wheel", "touchstart"]) {
        document.addEventListener(type, () => {
            resumeAt = Date.now() + pauseAfterInteraction;
        }, { passive: true });
    }

    let saved = "0";

    try {
        saved = localStorage.getItem(storageKey) || "0";
    } catch (error) {
        // Storage is optional.
    }

    if (requested >= 3) {
        remember(true);
        start();
    } else if (saved === "1") {
        start();
    }
}

// Dark scheme toggle. Preference order: ?theme=dark URL parameter, localStorage, light.
function initColorScheme() {    const storageKey = "rs.mimic.colorScheme";
    const params = new URLSearchParams(window.location.search);
    const requested = params.get("theme");
    let dark = requested ? requested === "dark" : localStorage.getItem(storageKey) === "dark";
    const button = $("#btnTheme");

    function apply() {
        $("body").toggleClass("mimic-dark", dark);
        $("#divMimicWrapper").toggleClass("mimic-dark", dark);
        button.attr("aria-pressed", String(dark))
            .find("i").toggleClass("fa-moon", !dark).toggleClass("fa-sun", dark);
    }

    button.on("click", function () {
        dark = !dark;
        localStorage.setItem(storageKey, dark ? "dark" : "light");
        apply();
    });

    apply();
}

$(async function () {
    unitedRenderer.configure({ viewHub, mainApi, fonts, controlRight });
    initElements();
    bindEvents();
    bindGestureEvents();
    initKioskMode();
    initFullscreen();
    initRotation();
    initColorScheme();
    initShortcutHelp();
    document.addEventListener("visibilitychange", handleVisibilityChange);
    updateLayout();
    await loadMimic();
});
