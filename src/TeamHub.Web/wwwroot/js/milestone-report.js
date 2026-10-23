(() => {
    const form = document.querySelector("[data-milestone-report-form]");
    const modalElement = document.querySelector("[data-milestone-report-modal]");
    if (!form || !modalElement) return;

    const button = form.querySelector("button[type='submit']");
    const progress = modalElement.querySelector("[data-milestone-report-progress]");
    const error = modalElement.querySelector("[data-milestone-report-error]");
    const close = modalElement.querySelector("[data-milestone-report-close]");
    const result = document.querySelector("[data-milestone-report-result]");
    const modal = bootstrap.Modal.getOrCreateInstance(modalElement, { backdrop: "static" });
    let pending;

    modalElement.addEventListener("hidden.bs.modal", () => {
        pending?.abort();
        pending = null;
        button.disabled = false;
    });

    form.addEventListener("submit", async event => {
        event.preventDefault();
        pending?.abort();
        const controller = new AbortController();
        pending = controller;
        button.disabled = true;
        progress.hidden = false;
        error.hidden = true;
        close.hidden = true;
        result.className = "mt-3";
        result.replaceChildren();
        modal.show();

        try {
            const response = await fetch(form.action, {
                method: "POST",
                signal: controller.signal,
                body: new FormData(form),
                headers: { Accept: "application/json" }
            });
            if (response.redirected || !response.headers.get("content-type")?.includes("application/json")) {
                throw new Error("The report could not be generated. Your session may have expired; refresh and try again.");
            }
            const payload = await response.json();
            if (!response.ok) throw new Error(payload.error || "The milestone report could not be generated.");

            const message = document.createElement("span");
            message.textContent = payload.message || "The milestone report was generated successfully.";
            result.className = "alert alert-success mt-3";
            result.append(message);
            if (payload.reportUrl) {
                const link = document.createElement("a");
                link.href = payload.reportUrl;
                link.className = "btn btn-sm btn-outline-primary ms-2";
                link.textContent = "View Report";
                result.append(link);
            }
            modal.hide();
        } catch (failure) {
            if (controller.signal.aborted) return;
            progress.hidden = true;
            error.textContent = failure instanceof Error
                ? failure.message
                : "The milestone report could not be generated.";
            error.hidden = false;
            close.hidden = false;
        } finally {
            if (pending === controller) {
                pending = null;
                button.disabled = false;
            }
        }
    });
})();
