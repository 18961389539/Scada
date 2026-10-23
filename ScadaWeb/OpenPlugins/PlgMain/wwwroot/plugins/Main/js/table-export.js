// Exports a rendered HTML table to CSV.
// The BOM keeps Excel happy with Chinese text, and quoting follows RFC 4180.
const TableExport = (() => {
    function cellText(cell) {
        let text = (cell.innerText || cell.textContent || '').replace(/\s+/g, ' ').trim();

        if (!text) {
            // Icon-only cells (severity, acknowledgement) keep the meaning in the tooltip.
            const titled = cell.querySelector('[title], [aria-label]');
            text = titled ? (titled.getAttribute('title') || titled.getAttribute('aria-label') || '').trim() : '';
        }

        return text;
    }

    function escapeCell(value) {
        const text = String(value ?? '');
        return /[",\r\n]/.test(text) ? '"' + text.replace(/"/g, '""') + '"' : text;
    }

    function timestamp() {
        const now = new Date();
        const pad = value => String(value).padStart(2, '0');
        return now.getFullYear() + pad(now.getMonth() + 1) + pad(now.getDate()) + '-' +
            pad(now.getHours()) + pad(now.getMinutes());
    }

    function download(content, fileName) {
        const blob = new Blob(['\uFEFF' + content], { type: 'text/csv;charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    return {
        /**
         * Exports the table matched by the selector. Returns false when there is nothing to export.
         */
        exportCsv(tableSelector, filePrefix) {
            const table = document.querySelector(tableSelector);
            if (!table) {
                return false;
            }

            const rows = [];
            const headerCells = table.querySelectorAll('thead th');

            if (headerCells.length > 0) {
                rows.push(Array.from(headerCells, cellText));
            }

            for (const row of table.querySelectorAll('tbody tr')) {
                const cells = Array.from(row.children, cellText);

                if (cells.some(value => value.length > 0)) {
                    rows.push(cells);
                }
            }

            if (rows.length === 0) {
                return false;
            }

            const csv = rows.map(row => row.map(escapeCell).join(',')).join('\r\n');
            download(csv, (filePrefix || 'table') + '-' + timestamp() + '.csv');
            return true;
        }
    };
})();
