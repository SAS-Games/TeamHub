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
    const subtitle = modalElement.querySelector("[data-jira-comments-subtitle]");
    const copyButton = modalElement.querySelector("[data-jira-comments-copy]");
    const copyStatus = modalElement.querySelector("[data-jira-comments-copy-status]");
    const composer = modalElement.querySelector("[data-jira-comment-composer]");
    const composerBody = modalElement.querySelector("[data-jira-comment-body]");
    const addButton = modalElement.querySelector("[data-jira-comment-add]");
    let pending;
    let imageUrls = [];

    const clear = () => {
        pending?.abort();
        for (const url of imageUrls) URL.revokeObjectURL(url);
        imageUrls = [];
        content.replaceChildren();
        if (composer) composer.hidden = true;
        if (composerBody) composerBody.value = "";
        if (composer) composer.onsubmit = null;
        if (copyButton) copyButton.hidden = true;
        if (copyStatus) copyStatus.textContent = "";
    };
    modalElement.addEventListener("hidden.bs.modal", clear);

    if (copyButton) {
        copyButton.addEventListener("click", async () => {
            try {
                if (!navigator.clipboard?.writeText) {
                    throw new Error("Clipboard copy is not supported in this browser. Please select and copy the text manually.");
                }
                const articles = content.querySelectorAll("article");
                const pieces = [];
                for (const article of articles) {
                    const author = article.querySelector("h3")?.textContent?.trim() || "";
                    const date = article.querySelector(".text-secondary")?.textContent?.trim() || "";
                    const bodyElement = article.querySelector("[data-comment-body]");
                    const body = (bodyElement?.dataset.copyText ?? bodyElement?.textContent)?.trim() || "";
                    pieces.push(`${author}${date ? ` (${date})` : ""}\n\n${body}`);
                }
                const fullText = pieces.join("\n\n---\n\n");
                await navigator.clipboard.writeText(fullText);
                if (copyStatus) {
                    copyStatus.className = "small text-success me-auto";
                    copyStatus.textContent = "Copied to clipboard!";
                    setTimeout(() => {
                        if (copyStatus && copyStatus.textContent === "Copied to clipboard!") {
                            copyStatus.textContent = "";
                        }
                    }, 3000);
                }
            } catch (err) {
                if (copyStatus) {
                    copyStatus.className = "small text-danger me-auto";
                    copyStatus.textContent = err instanceof Error ? err.message : "Failed to copy.";
                    setTimeout(() => {
                        if (copyStatus) {
                            copyStatus.className = "small text-success me-auto";
                            copyStatus.textContent = "";
                        }
                    }, 4000);
                }
            }
        });
    }

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
            const ticketId = button.dataset.ticketId;
            const commentsUrl = button.dataset.commentsUrl;
            const modalTitle = button.dataset.modalTitle;
            title.textContent = modalTitle
                ? (ticketId ? `${modalTitle} (${ticketId})` : modalTitle)
                : (ticketId ? `${ticketId} - Jira Comments` : "Provided Feedback");
            if (subtitle) {
                subtitle.textContent = ticketId
                    ? "Add comments or edit them using Markdown. Newest comments are shown first."
                    : "Feedback recorded for this milestone review.";
            }
            status.textContent = ticketId ? "Loading comments from Jira..." : "Loading feedback...";
            error.hidden = true;
            shared.hidden = true;
            open.hidden = true;
            if (copyButton) copyButton.hidden = true;
            if (copyStatus) copyStatus.textContent = "";
            modal.show();

            const saveComment = async (commentId, body, submitButton) => {
                const originalText = submitButton.textContent;
                submitButton.disabled = true;
                submitButton.textContent = "Saving...";
                error.hidden = true;
                try {
                    const url = new URL(commentsUrl, location.href);
                    url.searchParams.set("handler", "Save");
                    const formData = new URLSearchParams();
                    const token = composer?.querySelector('input[name="__RequestVerificationToken"]')?.value;
                    if (token) formData.set("__RequestVerificationToken", token);
                    if (commentId) formData.set("commentId", commentId);
                    formData.set("body", body);
                    await readResponse(await fetch(url, {
                        method: "POST",
                        signal: controller.signal,
                        headers: {
                            Accept: "application/json",
                            "Content-Type": "application/x-www-form-urlencoded;charset=UTF-8"
                        },
                        body: formData
                    }));
                    if (composerBody) composerBody.value = "";
                    await loadComments();
                } catch (failure) {
                    if (controller.signal.aborted) return;
                    error.textContent = failure instanceof Error ? failure.message : "The Jira comment could not be saved.";
                    error.hidden = false;
                } finally {
                    submitButton.disabled = false;
                    submitButton.textContent = originalText;
                }
            };

            const addLegacyParts = (comment, body, imageJobs) => {
                for (const part of comment.parts || []) {
                    if (!ticketId && part.kind === "markdown" && typeof part.html === "string") {
                        const formatted = document.createElement("div");
                        formatted.className = "markdown-content";
                        formatted.innerHTML = part.html;
                        body.dataset.copyText = part.text;
                        body.append(formatted);
                    } else if (part.kind === "strong") {
                        const strong = document.createElement("strong");
                        strong.textContent = part.text;
                        body.append(strong);
                    } else if (part.kind === "image") {
                        imageJobs.push(createImageJob(part, comment.id, commentsUrl, controller.signal, body));
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
            };

            const addEditor = (article, comment, body) => {
                const edit = document.createElement("button");
                edit.type = "button";
                edit.className = "btn btn-sm btn-outline-primary mt-2";
                edit.textContent = "Edit comment";
                const editor = document.createElement("div");
                editor.className = "mt-3 d-none";
                const textarea = document.createElement("textarea");
                textarea.className = "form-control";
                textarea.rows = 5;
                textarea.maxLength = 20000;
                textarea.value = comment.bodyMarkdown || "";
                textarea.setAttribute("aria-label", `Edit comment by ${comment.author}`);
                const hint = document.createElement("div");
                hint.className = "form-text";
                hint.textContent = "Markdown formatting is supported.";
                const actions = document.createElement("div");
                actions.className = "d-flex gap-2 mt-2";
                const discard = document.createElement("button");
                discard.type = "button";
                discard.className = "btn btn-sm btn-outline-secondary";
                discard.textContent = "Discard";
                const save = document.createElement("button");
                save.type = "button";
                save.className = "btn btn-sm btn-primary";
                save.textContent = "Save";
                actions.append(discard, save);
                editor.append(textarea, hint, actions);
                article.append(edit, editor);
                edit.addEventListener("click", () => {
                    body.classList.add("d-none");
                    edit.classList.add("d-none");
                    editor.classList.remove("d-none");
                    textarea.focus();
                });
                discard.addEventListener("click", () => {
                    textarea.value = comment.bodyMarkdown || "";
                    editor.classList.add("d-none");
                    body.classList.remove("d-none");
                    edit.classList.remove("d-none");
                });
                save.addEventListener("click", () => saveComment(comment.id, textarea.value, save));
            };

            const renderComments = async result => {
                for (const url of imageUrls) URL.revokeObjectURL(url);
                imageUrls = [];
                content.replaceChildren();
                if (result.ticketUrl) {
                    open.href = safeUrl(result.ticketUrl);
                    open.hidden = false;
                } else {
                    open.hidden = true;
                }
                shared.hidden = !result.isUsingSharedCredential;
                if (composer) composer.hidden = !ticketId || !result.canWriteComments;
                if (result.comments && result.comments.length) {
                    status.textContent = ticketId
                        ? `${result.comments.length} comment(s)`
                        : `${result.comments.length} contributor entry(s)`;
                    if (copyButton) copyButton.hidden = false;
                } else {
                    status.textContent = ticketId
                        ? "No comments are visible for this ticket."
                        : "No feedback was recorded for this review.";
                    if (copyButton) copyButton.hidden = true;
                }

                const imageJobs = [];
                for (const comment of result.comments || []) {
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
                    body.setAttribute("data-comment-body", "");
                    body.style.overflowWrap = "anywhere";
                    article.append(heading, date, body);
                    content.append(article);
                    if (ticketId && typeof comment.bodyHtml === "string") {
                        body.className = "markdown-content";
                        body.dataset.copyText = comment.bodyMarkdown || "";
                        // This HTML is produced by the server's SafeMarkdownRenderer.
                        body.innerHTML = comment.bodyHtml;
                        for (const part of comment.parts || []) {
                            if (part.kind === "image") {
                                imageJobs.push(createImageJob(part, comment.id, commentsUrl, controller.signal, body));
                            }
                        }
                    } else {
                        body.style.whiteSpace = "pre-wrap";
                        addLegacyParts(comment, body, imageJobs);
                    }
                    if (ticketId && result.canWriteComments) addEditor(article, comment, body);
                }
                let nextImage = 0;
                await Promise.all(Array.from({ length: Math.min(4, imageJobs.length) }, async () => {
                    while (!controller.signal.aborted && nextImage < imageJobs.length) {
                        await imageJobs[nextImage++]();
                    }
                }));
            };

            const loadComments = async () => {
                status.textContent = ticketId ? "Loading comments from Jira..." : "Loading feedback...";
                error.hidden = true;
                const result = await readResponse(await fetch(commentsUrl, {
                    signal: controller.signal,
                    headers: { Accept: "application/json" }
                }));
                if (!controller.signal.aborted) await renderComments(result);
            };

            if (composer) {
                composer.onsubmit = async event => {
                    event.preventDefault();
                    await saveComment(null, composerBody?.value || "", addButton);
                };
            }

            try {
                await loadComments();
            } catch (failure) {
                if (controller.signal.aborted) return;
                status.textContent = "";
                error.textContent = failure instanceof Error ? failure.message : "Comments could not be loaded.";
                error.hidden = false;
            }
        });
    });
})();
