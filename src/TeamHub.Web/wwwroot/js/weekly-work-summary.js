(() => {
    const section = document.querySelector("[data-weekly-work-summary]");
    if (!section) return;
    const form = section.querySelector("[data-weekly-summary-form]");
    const button = form.querySelector("button");
    const status = section.querySelector("[data-weekly-summary-status]");
    const error = section.querySelector("[data-weekly-summary-error]");
    const warnings = section.querySelector("[data-weekly-summary-warnings]");
    const preview = section.querySelector("[data-weekly-summary-preview]");
    const rows = section.querySelector("[data-weekly-summary-rows]");
    const copyButton = section.querySelector("[data-weekly-summary-copy]");
    const copyStatus = section.querySelector("[data-weekly-summary-copy-status]");
    const modalElement = section.querySelector("[data-weekly-summary-modal]");
    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
    let pending;

    modalElement.addEventListener("hidden.bs.modal", () => {
        pending?.abort();
        preview.hidden = true;
        copyButton.hidden = true;
        rows.replaceChildren();
        copyStatus.textContent = "";
    });

    copyButton.addEventListener("click", async () => {
        copyButton.disabled = true;
        copyStatus.textContent = "";
        const table = preview.querySelector("table");
        try {
            if (!navigator.clipboard) {
                throw new Error("Clipboard access requires HTTPS or localhost. Select the table and copy it manually.");
            }
            const text = Array.from(table.rows, row =>
                Array.from(row.cells, cell => {
                    const value = cell.textContent;
                    return /[\t\r\n"]/.test(value) ? `"${value.replaceAll('"', '""')}"` : value;
                }).join("\t")
            ).join("\r\n");
            if (typeof ClipboardItem !== "undefined" && navigator.clipboard.write) {
                const report = table.cloneNode(true);
                report.removeAttribute("class");
                report.style.borderCollapse = "collapse";
                for (const cell of report.querySelectorAll("th, td")) {
                    const lines = cell.textContent.split(/\r\n|\r|\n/);
                    cell.replaceChildren();
                    lines.forEach((line, index) => {
                        if (index > 0) cell.append(document.createElement("br"));
                        cell.append(document.createTextNode(line));
                    });
                    cell.style.border = "1px solid #999";
                    cell.style.padding = "8px";
                    cell.style.whiteSpace = "pre-wrap";
                }
                await navigator.clipboard.write([new ClipboardItem({
                    "text/html": new Blob([report.outerHTML], { type: "text/html" }),
                    "text/plain": new Blob([text], { type: "text/plain" })
                })]);
            } else {
                await navigator.clipboard.writeText(text);
            }
            copyStatus.textContent = "Report copied. Paste it into your document, email or spreadsheet.";
        } catch (failure) {
            copyStatus.textContent = failure instanceof Error && !navigator.clipboard
                ? failure.message
                : "The report could not be copied. Allow clipboard access in your browser, or select the table and copy it manually.";
        } finally {
            copyButton.disabled = false;
        }
    });

    form.addEventListener("submit", async event => {
        event.preventDefault();
        pending?.abort();
        const controller = new AbortController();
        pending = controller;
        button.disabled = true;
        error.hidden = true;
        warnings.hidden = true;
        preview.hidden = true;
        copyButton.hidden = true;
        copyStatus.textContent = "";
        rows.replaceChildren();
        status.textContent = "Preparing the weekly summary...";
        modal.show();
        try {
            const response = await fetch(form.action, {
                method: "POST",
                signal: controller.signal,
                body: new FormData(form),
                headers: { Accept: "application/json" }
            });
            if (response.redirected || !response.headers.get("content-type")?.includes("application/json")) {
                throw new Error("The report could not be generated. Your session or permissions may have changed. Refresh the page and try again.");
            }
            const result = await response.json();
            if (controller.signal.aborted) return;
            if (!response.ok) throw new Error(result.error || "The report could not be generated.");
            for (const row of result.rows) {
                const tr = document.createElement("tr");
                for (const value of [row.studio, row.supportActivity]) {
                    const td = document.createElement("td");
                    td.textContent = value;
                    td.style.whiteSpace = "pre-wrap";
                    tr.append(td);
                }
                rows.append(tr);
            }
            warnings.replaceChildren();
            for (const warning of result.warnings) {
                const item = document.createElement("div");
                item.textContent = warning;
                warnings.append(item);
            }
            warnings.hidden = result.warnings.length === 0;
            preview.hidden = false;
            copyButton.hidden = false;
            status.textContent = `Draft generated using ${result.provider} / ${result.model}. Review it against the source before sharing.`;
        } catch (failure) {
            if (controller.signal.aborted) return;
            error.textContent = failure instanceof Error ? failure.message : "The report could not be generated.";
            error.hidden = false;
            status.textContent = "";
        } finally {
            if (pending === controller) button.disabled = false;
        }
    });
})();
