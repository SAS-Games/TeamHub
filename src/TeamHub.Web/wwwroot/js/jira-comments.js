(() => {
    const modalElement = document.querySelector("[data-jira-comments-modal]");
    if (!modalElement) return;
    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
    const title = modalElement.querySelector("[data-jira-comments-title]");
    const status = modalElement.querySelector("[data-jira-comments-status]");
    const error = modalElement.querySelector("[data-jira-comments-error]");
    const shared = modalElement.querySelector("[data-jira-comments-shared]");
    const content = modalElement.querySelector("[data-jira-comments-content]");
    const open = modalElement.querySelector("[data-jira-comments-open]");
    let pending;
    let imageUrls = [];

    const clear = () => {
        pending?.abort();
        for (const url of imageUrls) URL.revokeObjectURL(url);
        imageUrls = [];
        content.replaceChildren();
    };
    modalElement.addEventListener("hidden.bs.modal", clear);

    const readResponse = async response => {
        if (response.redirected || !response.headers.get("content-type")?.includes("application/json")) {
            throw new Error("Your session or permissions may have changed. Refresh the page and try again.");
        }
        const result = await response.json();
        if (!response.ok) throw new Error(result.error || "Comments could not be loaded.");
        return result;
    };
    const safeUrl = value => {
        const url = new URL(value);
        if (!["https:", "http:"].includes(url.protocol) || url.username || url.password) {
            throw new Error("Jira returned an unsafe link.");
        }
        return url.href;
    };

    const createImageJob = (part, commentId, commentsUrl, signal, container) => {
        const figure = document.createElement("figure");
        const caption = document.createElement("figcaption");
        caption.className = "small text-secondary";
        caption.textContent = `Loading image: ${part.text}`;
        figure.append(caption);
        container.append(figure);
        return async () => {
            try {
                const url = new URL(commentsUrl, location.href);
                url.searchParams.set("handler", "Image");
                url.searchParams.set("commentId", commentId);
                url.searchParams.set("attachmentId", part.attachmentId);
                const response = await fetch(url, { signal });
                if (response.redirected) throw new Error("Sign in again to view this image.");
                if (!response.ok) {
                    const result = await readResponse(response);
                    throw new Error(result.error || "Image could not be loaded.");
                }
                if (!["image/png", "image/jpeg", "image/gif", "image/webp"].includes(response.headers.get("content-type"))) {
                    throw new Error("The image response was not a supported image.");
                }
                const blob = await response.blob();
                if (signal.aborted) return;
                const source = URL.createObjectURL(blob);
                imageUrls.push(source);
                const image = document.createElement("img");
                image.className = "img-fluid border rounded my-2";
                image.alt = part.text;
                image.style.maxHeight = "480px";
                image.addEventListener("error", () => {
                    image.remove();
                    caption.textContent = `${part.text}: the image could not be displayed. Open the comment in Jira.`;
                });
                image.src = source;
                figure.prepend(image);
                caption.textContent = part.text;
            } catch (failure) {
                if (signal.aborted) return;
                caption.textContent = `${part.text}: ${failure instanceof Error ? failure.message : "Image could not be loaded."}`;
            }
        };
    };

    document.querySelectorAll("[data-jira-comments]").forEach(button => {
        button.addEventListener("click", async () => {
            clear();
            const controller = new AbortController();
            pending = controller;
            title.textContent = `${button.dataset.ticketId} - Latest Comments`;
            status.textContent = "Loading comments from Jira...";
            error.hidden = true;
            shared.hidden = true;
            open.hidden = true;
            modal.show();
            try {
                const result = await readResponse(await fetch(button.dataset.commentsUrl, {
                    signal: controller.signal,
                    headers: { Accept: "application/json" }
                }));
                if (controller.signal.aborted) return;
                open.href = safeUrl(result.ticketUrl);
                open.hidden = false;
                shared.hidden = !result.isUsingSharedCredential;
                status.textContent = result.comments.length ? `${result.comments.length} comment(s)` : "No comments are visible for this ticket.";
                const imageJobs = [];
                for (const comment of result.comments) {
                    const article = document.createElement("article");
                    article.className = "border rounded p-3 mb-3";
                    const heading = document.createElement("h3");
                    heading.className = "h6";
                    heading.textContent = comment.author;
                    const date = document.createElement("div");
                    date.className = "small text-secondary mb-2";
                    const created = new Date(comment.created);
                    date.textContent = `Created: ${created.toLocaleString()}`;
                    if (comment.updated && new Date(comment.updated).getTime() !== created.getTime()) {
                        date.textContent += ` | Updated: ${new Date(comment.updated).toLocaleString()}`;
                    }
                    const body = document.createElement("div");
                    body.style.whiteSpace = "pre-wrap";
                    body.style.overflowWrap = "anywhere";
                    article.append(heading, date, body);
                    content.append(article);
                    for (const part of comment.parts) {
                        if (part.kind === "image") {
                            imageJobs.push(createImageJob(part, comment.id, button.dataset.commentsUrl, controller.signal, body));
                        } else if (part.kind === "link") {
                            const link = document.createElement("a");
                            link.href = safeUrl(part.url);
                            link.textContent = part.text;
                            link.target = "_blank";
                            link.rel = "noopener noreferrer";
                            body.append(link);
                        } else {
                            body.append(document.createTextNode(part.text));
                        }
                    }
                }
                let nextImage = 0;
                await Promise.all(Array.from({ length: Math.min(4, imageJobs.length) }, async () => {
                    while (!controller.signal.aborted && nextImage < imageJobs.length) {
                        await imageJobs[nextImage++]();
                    }
                }));
            } catch (failure) {
                if (controller.signal.aborted) return;
                status.textContent = "";
                error.textContent = failure instanceof Error ? failure.message : "Comments could not be loaded.";
                error.hidden = false;
            }
        });
    });
})();
