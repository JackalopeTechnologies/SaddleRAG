// timezone.js
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.
// Copyright (c) 2012-Present Jackalope Technologies, Inc. and Doug Gerard.
// Converts <span data-utc="..." data-fmt="..."> markers emitted by the
// TimeDisplay Blazor component into the browser's local time, with an
// optional UTC offset suffix.

(function () {
    "use strict";

    var DATA_UTC = "data-utc";
    var DATA_FMT = "data-fmt";
    var DATA_ZONE = "data-show-zone";
    var DATA_RENDERED = "data-utc-rendered";

    function pad(value, width) {
        var w = width || 2;
        var s = String(value);
        while (s.length < w) {
            s = "0" + s;
        }
        return s;
    }

    function formatLocal(date, fmt) {
        var year = date.getFullYear();
        var month = pad(date.getMonth() + 1);
        var day = pad(date.getDate());
        var hour = pad(date.getHours());
        var minute = pad(date.getMinutes());
        var second = pad(date.getSeconds());
        var milli = pad(date.getMilliseconds(), 3);
        return fmt
            .replace(/yyyy/g, year)
            .replace(/MM/g, month)
            .replace(/dd/g, day)
            .replace(/HH/g, hour)
            .replace(/mm/g, minute)
            .replace(/ss/g, second)
            .replace(/fff/g, milli);
    }

    function offsetSuffix(date) {
        var off = -date.getTimezoneOffset();
        var sign = off >= 0 ? "+" : "-";
        var abs = Math.abs(off);
        var hh = pad(Math.floor(abs / 60));
        var mm = pad(abs % 60);
        return "UTC" + sign + hh + ":" + mm;
    }

    function localize(el) {
        var iso = el.getAttribute(DATA_UTC);
        var rendered = el.getAttribute(DATA_RENDERED);
        var skip = !iso || rendered === iso;
        if (!skip) {
            var date = new Date(iso);
            var ok = !isNaN(date.getTime());
            if (ok) {
                var fmt = el.getAttribute(DATA_FMT) || "yyyy-MM-dd HH:mm";
                var showZone = el.getAttribute(DATA_ZONE) === "true";
                var text = formatLocal(date, fmt);
                if (showZone) {
                    text = text + " " + offsetSuffix(date);
                }
                el.textContent = text;
                el.setAttribute(DATA_RENDERED, iso);
            }
        }
    }

    function localizeAll(root) {
        var scope = root || document;
        if (scope.querySelectorAll) {
            var nodes = scope.querySelectorAll("[" + DATA_UTC + "]");
            for (var i = 0; i < nodes.length; i++) {
                localize(nodes[i]);
            }
        }
    }

    function visit(node) {
        if (node.nodeType === 1) {
            if (node.hasAttribute && node.hasAttribute(DATA_UTC)) {
                localize(node);
            }
            localizeAll(node);
        }
    }

    function start() {
        localizeAll(document);

        var observer = new MutationObserver(function (mutations) {
            for (var i = 0; i < mutations.length; i++) {
                var m = mutations[i];
                if (m.type === "attributes" && m.target && m.target.hasAttribute && m.target.hasAttribute(DATA_UTC)) {
                    localize(m.target);
                }
                if (m.addedNodes) {
                    for (var j = 0; j < m.addedNodes.length; j++) {
                        visit(m.addedNodes[j]);
                    }
                }
            }
        });

        observer.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: [DATA_UTC]
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", start);
    }
    else {
        start();
    }
}());
