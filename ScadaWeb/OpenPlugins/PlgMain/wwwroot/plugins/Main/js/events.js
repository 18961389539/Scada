// Depends on jquery, scada-common.js, view-hub.js, main-api.js, table-common.js

// The variables below are set in Events.cshtml
var phrases = {};

const ALL_EVENTS_KEY = "Events.AllEvents";
const START_DELAY = 550; // ms
const POSTPONE_SCROLL_PERIOD = 10000; // ms

var allEvents = false;
var filterID = 0;
var arcWriteTime = 0;
var eventBeepTime = "";
var scrollTimeoutID = 0;
var eventsTimerID = 0;
var liveMode = true;

function restoreFilter() {
    allEvents = ScadaUtils.getStorageItem(localStorage, ALL_EVENTS_KEY, "false") === "true";

    if (allEvents) {
        $("#spanAllEventsBtn").addClass("selected");
    } else {
        $("#spanEventsByViewBtn").addClass("selected");
    }
}

function initTooltips() {
    new bootstrap.Tooltip(document.getElementById("spanPrintBtn"));
    new bootstrap.Tooltip(document.getElementById("spanExportBtn"));
    new bootstrap.Tooltip(document.getElementById("spanInfoBtn"));
};

function bindEvents() {
    $(window).on("resize", function () {
        updateLayout();
    });

    $("#spanAllEventsBtn").on("click", function () {
        // load all events
        $(this).addClass("selected");
        $("#spanEventsByViewBtn").removeClass("selected");
        $("#spanLiveMode").addClass("selected");

        allEvents = true;
        liveMode = true;
        ScadaUtils.setStorageItem(localStorage, ALL_EVENTS_KEY, "true");
        resetEvents();
        startUpdatingEvents();
    });

    $("#spanEventsByViewBtn").on("click", function () {
        // load events by view
        $(this).addClass("selected");
        $("#spanAllEventsBtn").removeClass("selected");
        $("#spanLiveMode").addClass("selected");

        allEvents = false;
        liveMode = true;
        ScadaUtils.setStorageItem(localStorage, ALL_EVENTS_KEY, "false");
        resetEvents();
        startUpdatingEvents();
    });

    $("#spanQueryRange").on("click", queryRange);
    $("#spanLiveMode").on("click", returnToLive);

    $("#spanPrintBtn").on("click", function () {
        // generate Excel workbook
        location = allEvents
            ? "Print/PrintAllEvents"
            : "Print/PrintEventsByView?viewID=" + viewHub.viewID;
    });

    $("#spanExportBtn").on("click", function () {
        // export what is currently on screen
        if (!TableExport.exportCsv("#tblEvents", "events")) {
            showMessage(phrases.noEvents);
        }
    });

    $("#tblEvents").on("click", function (event) {
        let target = $(event.target);
        if (target.is("td.ack i")) {
            // show event acknowledgement dialog
            let eventID = target.closest(".row-event").attr("data-id");
            viewHub.features.eventAck.show(eventID);
        }
    });

    $("#divTableWrapper").on("mousemove wheel touchstart", function () {
        postponeScroll();
    });
}

function updateLayout() {
    let h = $(window).height() - $("#divToolbar").outerHeight();
    $("#divTableWrapper").outerHeight(h);
    $("#divMessage").outerHeight(h);
};

function startUpdatingEvents() {
    if (!liveMode) {
        return;
    }

    clearTimeout(eventsTimerID);
    updateEvents(function () {
        if (liveMode) {
            eventsTimerID = setTimeout(startUpdatingEvents, pluginOptions.refreshRate);
        }
    });
}

// datetime-local wants local time without a zone; toISOString would shift the value.
function toLocalInputValue(date) {
    const pad = value => String(value).padStart(2, "0");
    return date.getFullYear() + "-" + pad(date.getMonth() + 1) + "-" + pad(date.getDate()) +
        "T" + pad(date.getHours()) + ":" + pad(date.getMinutes());
}

function initRangeInputs() {
    const now = new Date();
    const dayStart = new Date(now.getFullYear(), now.getMonth(), now.getDate(), 0, 0);
    $("#dtRangeStart").val(toLocalInputValue(dayStart));
    $("#dtRangeEnd").val(toLocalInputValue(now));
}

function queryRange() {
    const startValue = $("#dtRangeStart").val();
    const endValue = $("#dtRangeEnd").val();

    if (!startValue || !endValue) {
        showMessage(phrases.rangeRequired);
        return;
    }

    const startTime = new Date(startValue);
    const endTime = new Date(endValue);

    if (isNaN(startTime.getTime()) || isNaN(endTime.getTime()) || endTime <= startTime) {
        showMessage(phrases.rangeInvalid);
        return;
    }

    liveMode = false;
    clearTimeout(eventsTimerID);
    clearTimeout(scrollTimeoutID);
    scrollTimeoutID = 0;
    $("#spanLiveMode").removeClass("selected");
    showMessage(phrases.loading);

    mainApi.getEvents(archiveBit, new TimeRange(startTime.toISOString(), endTime.toISOString(), true),
        function (dto) {
            if (!dto.ok) {
                showErrorBadge();
                showMessage(dto.msg || phrases.rangeLoadError);
                return;
            }

            showEvents(dto.data, false);
            clearTimeout(scrollTimeoutID);
            scrollTimeoutID = 0;
            $("#divTableWrapper").scrollTop(0);
        });
}

function returnToLive() {
    $("#spanLiveMode").addClass("selected");

    if (liveMode) {
        return;
    }

    liveMode = true;
    resetEvents();
    startUpdatingEvents();
}

function updateEvents(callback) {
    if (archiveBit >= 0) {
        mainApi.getArcWriteTime(archiveBit, function (dto) {
            if (dto.ok) {
                let newArcWriteTime = dto.data;

                if (arcWriteTime !== newArcWriteTime) {
                    // request events
                    let retrieveEvents = function (dto) {
                        if (dto.ok) {
                            let oldFilterID = filterID;
                            filterID = dto.data.filterID;
                            arcWriteTime = newArcWriteTime;
                            showEvents(dto.data, oldFilterID > 0);
                        } else {
                            showErrorBadge();
                        }

                        callback();
                    };

                    if (allEvents) {
                        mainApi.getLastAvailableEvents(archiveBit, pluginOptions.eventDepth,
                            pluginOptions.eventCount, filterID,
                            function (dto) { retrieveEvents(dto); });
                    } else {
                        mainApi.getLastEventsByView(archiveBit, pluginOptions.eventDepth,
                            pluginOptions.eventCount, viewHub.viewID, filterID,
                            function (dto) { retrieveEvents(dto); });
                    }
                } else {
                    // data has not changed
                    callback();
                }
            } else {
                showErrorBadge();
                callback();
            }
        });
    } else {
        console.warn("Unable to request events");
        showErrorBadge();
        callback();
    }
}

function showEvents(data, enableEffects) {
    if (data.records.length > 0) {
        let tbodyElem = $("<tbody></tbody>");
        let newEventBeepTime = eventBeepTime;

        for (let record of data.records) {
            let e = record.e;   // event data
            let ef = record.ef; // formatted event

            let row = $("<tr class='row-event' data-id='" + record.id + "'></tr>")
                .append(createCell("time", ef.time))
                .append(createCell("obj", ef.obj))
                .append(createCell("dev", ef.dev))
                .append(createCell("cnl", ef.cnl))
                .append(createCell("descr", ef.descr))
                .append(createCell("sev", getSeverityElem(e.severity, ef.sev)))
                .append(createCell("ack", getAckElem(e, ef)))
                .css("color", MainApi.getColor(record, ColorIndex.MAIN_COLOR));

            if (ef.beep) {
                newEventBeepTime = e.timestamp;
            }

            tbodyElem.append(row);
        }

        $("#divMessage").addClass("hidden");
        $("#divTableWrapper tbody").remove();
        $("#divTableWrapper table").append(tbodyElem);
        $("#divTableWrapper").removeClass("hidden");

        // scroll down
        if (scrollTimeoutID <= 0) {
            scrollDownEvents(enableEffects);
        }

        // beep
        if (eventBeepTime < newEventBeepTime) {
            eventBeepTime = newEventBeepTime;

            if (enableEffects) {
                ScadaUtils.playSound($("#audEvent"));
            }
        }
    } else {
        $("#divTableWrapper tbody").remove();
        showMessage(phrases.noEvents);
    }
}

function createCell(cssClass, content) {
    let cellElem = $(`<td class='${cssClass}'></td>`);

    if (content) {
        if (typeof content === "string") {
            cellElem.text(content);
        } else if (content.jquery) {
            cellElem.append(content);
        }
    }

    return cellElem;
}

function getSeverityElem(severityValue, severityText) {
    switch (Severity.closest(severityValue)) {
        case Severity.CRITICAL:
            return $("<i class='fa-solid fa-circle-exclamation critical'></i>").attr("title", severityText);

        case Severity.MAJOR:
            return $("<i class='fa-solid fa-triangle-exclamation major'></i>").attr("title", severityText);

        case Severity.MINOR:
            return $("<i class='fa-solid fa-triangle-exclamation minor'></i>").attr("title", severityText);

        case Severity.INFO:
            return $("<i class='fa-solid fa-info info'></i>").attr("title", severityText);

        default:
            return null;
    }
}

function getAckElem(e, ef) {
    if (e.ack) {
        return $("<i class='fa-regular fa-square-check ack-yes'></i>").attr("title", ef.ack);
    } else if (e.ackRequired) {
        return $("<i class='fa-regular fa-square ack-no'></i>").attr("title", phrases.ack);
    } else {
        return null;
    }
}

function resetEvents() {
    showMessage(phrases.loading);
    $("#divTableWrapper tbody").remove();

    filterID = 0;
    arcWriteTime = 0;
    eventBeepTime = "";
    clearTimeout(scrollTimeoutID);
    scrollTimeoutID = 0;
}

function showMessage(text) {
    $("#divTableWrapper").addClass("hidden");
    $("#divMessage").text(text).removeClass("hidden");
}

function postponeScroll() {
    clearTimeout(scrollTimeoutID);

    scrollTimeoutID = setTimeout(function () {
        scrollDownEvents(true);
        scrollTimeoutID = 0;
    }, POSTPONE_SCROLL_PERIOD);
}

function scrollDownEvents(animate) {
    let divTableWrapper = $("#divTableWrapper");
    let scrollHeight = divTableWrapper[0].scrollHeight;
    let newScrollTop = scrollHeight - divTableWrapper.innerHeight();

    if (divTableWrapper.scrollTop() < newScrollTop) {
        if (animate) {
            divTableWrapper.animate({ scrollTop: newScrollTop }, "slow");
        } else {
            divTableWrapper.scrollTop(newScrollTop);
        }
    }
}

$(document).ready(function () {
    restoreFilter();
    initTooltips();
    initRangeInputs();
    bindEvents();
    updateLayout();
    showMessage(phrases.loading);
    setTimeout(startUpdatingEvents, START_DELAY); // wait for loading view in cache
});
