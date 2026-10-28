(() => {
    const root = document.querySelector("[data-ai-chat]");
    if (!root) return;

    const widget = root.hasAttribute("data-ai-chat-widget");
    const launcher = document.getElementById("aiChatLauncher");
    const close = document.getElementById("aiChatClose");
    const maximize = document.getElementById("aiChatMaximize");
    const provider = document.getElementById("aiChatProvider");
    const mode = root.querySelector("#aiChatMode");
    const model = root.querySelector("#aiChatModel");
    const note = root.querySelector("#aiChatModeNote");
    const meetingStudioContext = root.querySelector("#aiChatMeetingStudioContext");
    const meetingStudio = root.querySelector("#aiChatMeetingStudio");
    const messagesElement = root.querySelector("#aiChatMessages");
    const documentsElement = root.querySelector("#aiChatDocuments");
    const files = root.querySelector("#aiChatFiles");
    const filesLabel = root.querySelector("#aiChatFilesLabel");
    const meetingOverrideContext = root.querySelector("#aiChatMeetingOverrideContext");
    const meetingOverride = root.querySelector("#aiChatMeetingOverride");
    const form = root.querySelector("#aiChatForm");
    const prompt = root.querySelector("#aiChatPrompt");
    const send = root.querySelector("#aiChatSend");
    const error = root.querySelector("#aiChatError");
    const usageElement = root.querySelector("#aiChatUsage");
    const meetingActions = root.querySelector("#aiChatMeetingActions");
    const downloadNotes = root.querySelector("#aiChatDownloadNotes");
    const editNotes = root.querySelector("#aiChatEditNotes");
    const meetingEditor = root.querySelector("#aiChatMeetingEditor");
    const closeMeetingEditorButton = root.querySelector("#aiChatCloseMeetingEditor");
    const meetingDraft = root.querySelector("#aiChatMeetingDraft");
    const meetingDestination = root.querySelector("#aiChatMeetingDestination");
    const publishNotes = root.querySelector("#aiChatPublishNotes");
    const meetingStatus = root.querySelector("#aiChatMeetingStatus");
    const token = form.querySelector('input[name="__RequestVerificationToken"]');
    const storageKey = `teamhub.aiChat.widget.v2:${root.dataset.aiChatUser || "page"}`;
    let history = [];
    let modelsLoaded = model.options.length > 0 && Boolean(model.value);
    let loadingModels = null;
    let pendingModel = null;
    let activeRequest = null;
    let conversationRevision = 0;
    let normalBounds = null;
    let meetingDestinationsLoaded = false;
    let meetingStudiosLoaded = false;
    let pendingMeetingStudioId = null;
    let meetingDraftDirty = false;
    const copyIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 7V3h13v13h-4v5H3V7h5Zm2 0h7v7h2V5h-9v2Zm5 2H5v10h10V9Z" /></svg>';
    const copiedIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m9.2 17.2-4.4-4.4 1.4-1.4 3 3L17.8 5.8l1.4 1.4-10 10Z" /></svg>';

    function normalizeTokenUsage(value) {
        if (!value || typeof value !== "object") return null;
        const inputTokens = Number(value.inputTokens);
        const outputTokens = Number(value.outputTokens);
        const reportedTotal = Number(value.totalTokens);
        if (![inputTokens, outputTokens, reportedTotal]
            .every(item => Number.isInteger(item) && item >= 0)) return null;
        const totalTokens = reportedTotal || inputTokens + outputTokens;
        if (totalTokens === 0) return null;
        return { inputTokens, outputTokens, totalTokens };
    }

    function formatTokenCount(value) {
        return new Intl.NumberFormat().format(value);
    }

    function formatTokenUsage(usage) {
        return `${formatTokenCount(usage.totalTokens)} total (${formatTokenCount(usage.inputTokens)} input + ${formatTokenCount(usage.outputTokens)} output)`;
    }

    function updateSessionUsage() {
        if (!usageElement) return;
        let inputTokens = 0;
        let outputTokens = 0;
        let totalTokens = 0;
        let unavailableResponses = 0;
        for (const item of history) {
            if (item.role !== "assistant") continue;
            const usage = normalizeTokenUsage(item.usage);
            if (!usage) {
                unavailableResponses += 1;
                continue;
            }
            inputTokens += usage.inputTokens;
            outputTokens += usage.outputTokens;
            totalTokens += usage.totalTokens;
        }

        if (totalTokens > 0) {
            usageElement.textContent = `Session tokens: ${formatTokenUsage({ inputTokens, outputTokens, totalTokens })}`
                + (unavailableResponses > 0 ? ` · ${unavailableResponses} response not reported` : "");
        } else if (unavailableResponses > 0) {
            usageElement.textContent = "Session tokens: unavailable from this provider";
        } else {
            usageElement.textContent = "Session tokens: 0";
        }
    }

    function readState() {
        if (!widget) return null;
        try {
            const state = JSON.parse(sessionStorage.getItem(storageKey) || "null");
            if (!state || typeof state !== "object") return null;
            const savedHistory = Array.isArray(state.history)
                ? state.history.slice(-20).flatMap(item => {
                    if (!item || !["user", "assistant"].includes(item.role) || typeof item.content !== "string") return [];
                    const citations = Array.isArray(item.citations)
                        ? item.citations.slice(0, 20).flatMap(citation =>
                            citation && typeof citation.title === "string"
                                ? [{ id: String(citation.id || ""), title: citation.title.slice(0, 200) }]
                                : [])
                        : [];
                    const usage = normalizeTokenUsage(item.usage);
                    return [{ role: item.role, content: item.content.slice(0, 8000), citations, usage }];
                })
                : [];
            return {
                open: state.open === true,
                fullscreen: state.fullscreen === true,
                mode: ["General", "TeamHub", "MeetingNotes"].includes(state.mode)
                    ? state.mode
                    : "General",
                model: typeof state.model === "string" ? state.model.slice(0, 200) : "",
                meetingStudioId: typeof state.meetingStudioId === "string"
                    ? state.meetingStudioId.slice(0, 100)
                    : "",
                history: savedHistory,
                bounds: state.bounds
                    && [state.bounds.left, state.bounds.top, state.bounds.width, state.bounds.height]
                        .every(value => Number.isFinite(value))
                    ? {
                        left: state.bounds.left,
                        top: state.bounds.top,
                        width: state.bounds.width,
                        height: state.bounds.height
                    }
                    : null
            };
        } catch {
            return null;
        }
    }

    function persistState() {
        if (!widget) return;
        try {
            sessionStorage.setItem(storageKey, JSON.stringify({
                open: !root.hidden,
                fullscreen: root.classList.contains("is-fullscreen"),
                mode: mode.value,
                model: pendingModel || model.value,
                meetingStudioId: meetingStudio?.value || pendingMeetingStudioId || "",
                history: history.slice(-20),
                bounds: normalBounds
            }));
        } catch {
            // Chat remains usable when browser storage is unavailable.
        }
    }

    function showEmptyConversation() {
        messagesElement.replaceChildren();
        const empty = document.createElement("div");
        empty.className = "ai-chat-empty";
        const title = document.createElement("strong");
        title.textContent = "What would you like to know?";
        const detail = document.createElement("span");
        detail.textContent = mode.value === "TeamHub"
            ? "Ask about TeamHub information available to your account."
            : mode.value === "MeetingNotes"
                ? "Paste a transcript or attach transcript files to generate structured meeting notes."
                : "Ask a general question or attach your own documents.";
        empty.append(title, detail);
        messagesElement.append(empty);
    }

    function resetConversation() {
        conversationRevision += 1;
        history = [];
        if (files) files.value = "";
        if (meetingOverride) meetingOverride.value = "";
        prompt.value = "";
        showEmptyConversation();
        error.textContent = "";
        if (meetingActions) meetingActions.classList.add("d-none");
        closeMeetingEditor(false);
        if (meetingDraft) meetingDraft.value = "";
        meetingDraftDirty = false;
        if (meetingStatus) meetingStatus.textContent = "";
        updateSessionUsage();
        persistState();
    }

    function updateMode(reset = true) {
        const teamHubMode = mode.value === "TeamHub";
        const meetingNotesMode = mode.value === "MeetingNotes";
        documentsElement.classList.toggle("d-none", teamHubMode);
        meetingStudioContext?.classList.toggle("d-none", !meetingNotesMode);
        meetingOverrideContext?.classList.toggle("d-none", !meetingNotesMode);
        if (meetingNotesMode) loadMeetingStudios();
        note.textContent = teamHubMode
            ? "Uses only TeamHub information authorized for your account. Workflow results are limited to workflows assigned to you."
            : meetingNotesMode
                ? "Generates Markdown notes only from the pasted or attached transcript. It does not use TeamHub data or general knowledge for meeting facts."
                : "Isolated from TeamHub. Documents are optional; ordinary questions use the model's general knowledge.";
        if (filesLabel) filesLabel.textContent = meetingNotesMode ? "Attach transcript files" : "Optional documents";
        prompt.placeholder = meetingNotesMode
            ? "Paste the meeting transcript or describe the requested notes…"
            : "Ask a question…";
        if (reset) resetConversation();
    }

    function setGenerating(generating) {
        mode.disabled = generating;
        model.disabled = generating || !modelsLoaded;
        if (meetingStudio) meetingStudio.disabled = generating || !meetingStudiosLoaded;
        prompt.disabled = generating;
        if (files) files.disabled = generating;
        if (meetingOverride) meetingOverride.disabled = generating;
        if (publishNotes) publishNotes.disabled = generating || !meetingDestination?.value;
        send.disabled = false;
        send.textContent = generating ? "Cancel" : "Send";
        send.classList.toggle("btn-danger", generating);
        send.classList.toggle("btn-primary", !generating);
        send.setAttribute("aria-label", generating ? "Cancel response generation" : "Send message");
        root.classList.toggle("is-generating", generating);
    }

    async function loadMeetingStudios() {
        if (!meetingStudio || meetingStudiosLoaded) return;
        meetingStudio.replaceChildren(new Option("Loading studios...", ""));
        meetingStudio.disabled = true;
        try {
            const response = await fetch("/AIChat?handler=MeetingNotesStudios", {
                headers: { "Accept": "application/json" }
            });
            const payload = await response.json().catch(() => ({}));
            if (!response.ok) throw new Error(payload.error || "Meeting studios could not be loaded.");
            meetingStudio.replaceChildren(new Option("Default meeting-note format", ""));
            for (const item of payload.studios || []) {
                meetingStudio.add(new Option(item.label, item.id));
            }
            meetingStudiosLoaded = true;
            if (pendingMeetingStudioId
                && Array.from(meetingStudio.options).some(option => option.value === pendingMeetingStudioId)) {
                meetingStudio.value = pendingMeetingStudioId;
            }
            pendingMeetingStudioId = null;
            meetingStudio.disabled = false;
            persistState();
        } catch (exception) {
            meetingStudio.replaceChildren(new Option("Default meeting-note format", ""));
            meetingStudio.disabled = true;
            error.textContent = exception.message || "Meeting studios could not be loaded.";
        }
    }

    async function loadMeetingDestinations() {
        if (!meetingDestination || meetingDestinationsLoaded) return;
        meetingDestination.replaceChildren(new Option("Loading studios…", ""));
        meetingDestination.disabled = true;
        try {
            const response = await fetch("/AIChat?handler=MeetingNotesDestinations", {
                headers: { "Accept": "application/json" }
            });
            const payload = await response.json().catch(() => ({}));
            if (!response.ok) throw new Error(payload.error || "Teams destinations could not be loaded.");
            meetingDestination.replaceChildren();
            for (const item of payload.destinations || []) {
                meetingDestination.add(new Option(item.label, item.id));
            }
            if (meetingStudio?.value
                && Array.from(meetingDestination.options).some(option => option.value === meetingStudio.value)) {
                meetingDestination.value = meetingStudio.value;
            }
            meetingDestinationsLoaded = true;
            const hasDestinations = meetingDestination.options.length > 0;
            meetingDestination.disabled = !hasDestinations;
            meetingDestination.classList.toggle("d-none", !hasDestinations);
            if (publishNotes) {
                publishNotes.classList.toggle("d-none", !hasDestinations);
                publishNotes.disabled = !meetingDestination.value;
            }
            refreshMeetingActions();
        } catch (exception) {
            meetingDestination.replaceChildren();
            meetingDestination.disabled = true;
            meetingDestination.classList.add("d-none");
            if (publishNotes) publishNotes.classList.add("d-none");
            if (meetingStatus) meetingStatus.textContent = exception.message || "Teams destinations could not be loaded.";
        }
    }

    function currentPublishableResponse() {
        if (mode.value === "TeamHub") return "";
        return [...history].reverse().find(item => item.role === "assistant")?.content || "";
    }

    function closeMeetingEditor(restoreFocus = true) {
        if (!meetingEditor) return;
        meetingEditor.classList.add("d-none");
        root.classList.remove("is-reviewing-notes");
        if (meetingStatus) meetingStatus.textContent = "";
        if (restoreFocus) editNotes?.focus();
    }

    async function openMeetingEditor() {
        if (!meetingEditor) return;
        if (meetingDraft && !meetingDraftDirty) meetingDraft.value = currentPublishableResponse();
        meetingEditor.classList.remove("d-none");
        root.classList.add("is-reviewing-notes");
        if (meetingStatus) meetingStatus.textContent = "";
        await loadMeetingDestinations();
        window.requestAnimationFrame(() => meetingDraft?.focus({ preventScroll: widget }));
    }

    function refreshMeetingActions(replaceDraft = false) {
        if (!meetingActions) return;
        const notes = currentPublishableResponse();
        meetingActions.classList.toggle("d-none", !notes);
        if (!notes) return;
        if (meetingDraft && (replaceDraft || !meetingDraftDirty)) meetingDraft.value = notes;
        if (meetingDestination?.options.length === 0
            && meetingDestinationsLoaded
            && !meetingEditor?.classList.contains("d-none")
            && meetingStatus) {
            meetingStatus.textContent = "No Teams destination is configured; you can download the Markdown file.";
        }
        if (publishNotes) publishNotes.disabled = !meetingDestination?.value;
    }

    function downloadMeetingNotes() {
        const notes = meetingDraftDirty ? meetingDraft?.value.trim() : currentPublishableResponse();
        if (!notes) return;
        const blob = new Blob([notes], { type: "text/markdown;charset=utf-8" });
        const link = document.createElement("a");
        const url = URL.createObjectURL(blob);
        link.href = url;
        link.download = `meeting-notes-${new Date().toISOString().slice(0, 10)}.md`;
        document.body.append(link);
        link.click();
        link.remove();
        window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    async function publishMeetingNotes() {
        const notes = meetingDraft?.value.trim() || "";
        if (!notes || !meetingDestination?.value || !publishNotes) return;
        const destinationLabel = meetingDestination.selectedOptions[0]?.textContent || "the selected studio";
        if (!window.confirm(`Publish these meeting notes to ${destinationLabel}?`)) return;
        publishNotes.disabled = true;
        if (meetingStatus) {
            meetingStatus.className = "small text-secondary";
            meetingStatus.textContent = "Publishing to Teams…";
        }
        try {
            const response = await fetch("/AIChat?handler=PublishMeetingNotes", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "RequestVerificationToken": token.value
                },
                body: JSON.stringify({
                    destinationId: meetingDestination.value,
                    markdown: notes
                })
            });
            const payload = await response.json().catch(() => ({}));
            if (!response.ok) throw new Error(payload.error || "Meeting notes could not be published.");
            if (meetingStatus) {
                meetingStatus.className = "small text-success";
                meetingStatus.textContent = payload.message || "Meeting notes published to Teams.";
            }
        } catch (exception) {
            if (meetingStatus) {
                meetingStatus.className = "small text-danger";
                meetingStatus.textContent = exception.message || "Meeting notes could not be published.";
            }
        } finally {
            publishNotes.disabled = false;
        }
    }

    async function copyResponse(content, button) {
        try {
            if (navigator.clipboard?.writeText) {
                await navigator.clipboard.writeText(content);
            } else {
                const temporary = document.createElement("textarea");
                temporary.value = content;
                temporary.setAttribute("readonly", "");
                temporary.style.position = "fixed";
                temporary.style.opacity = "0";
                document.body.append(temporary);
                temporary.select();
                document.execCommand("copy");
                temporary.remove();
            }
            button.innerHTML = copiedIcon;
            button.setAttribute("aria-label", "Response copied");
            button.title = "Response copied";
            window.setTimeout(() => {
                button.innerHTML = copyIcon;
                button.setAttribute("aria-label", "Copy response");
                button.title = "Copy response";
            }, 1500);
        } catch {
            error.textContent = "The response could not be copied. Select the text and copy it manually.";
        }
    }

    const markdownBlockStart = /^(?:#{1,6}\s+|```|~~~|>\s?|[-+*]\s+|\d+\.\s+|<svg\b|(?:-{3,}|\*{3,}|_{3,})\s*$)/i;
    const safeSvgElements = new Set([
        "svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "tspan",
        "defs", "lineargradient", "radialgradient", "stop", "clippath", "title", "desc"
    ]);
    const safeSvgAttributes = new Set([
        "xmlns", "viewbox", "preserveaspectratio", "width", "height", "x", "y", "x1", "x2", "y1", "y2",
        "cx", "cy", "r", "rx", "ry", "d", "points", "fill", "fill-opacity", "fill-rule", "stroke",
        "stroke-width", "stroke-linecap", "stroke-linejoin", "stroke-opacity", "opacity", "transform",
        "font-family", "font-size", "font-weight", "text-anchor", "dominant-baseline", "offset", "stop-color",
        "stop-opacity", "gradientunits", "gradienttransform", "spreadmethod", "id", "clip-path", "role",
        "aria-label"
    ]);

    function safeLinkUrl(value) {
        try {
            const url = new URL(value, window.location.href);
            return ["http:", "https:", "mailto:"].includes(url.protocol) ? url.href : null;
        } catch {
            return null;
        }
    }

    function safeImageUrl(value) {
        if (/^data:image\/(?:png|jpeg|gif|webp);base64,[a-z0-9+/=\s]+$/i.test(value)) return value;
        try {
            const url = new URL(value, window.location.href);
            return ["http:", "https:"].includes(url.protocol) ? url.href : null;
        } catch {
            return null;
        }
    }

    function appendInlineMarkdown(parent, source) {
        const pattern = /(`[^`\r\n]+`|!\[[^\]\r\n]*\]\([^\s)]+\)|\[[^\]\r\n]+\]\([^\s)]+\)|\*\*\*[^*\r\n]+\*\*\*|\*\*[^*\r\n]+\*\*|~~[^~\r\n]+~~|\*[^*\r\n]+\*)/g;
        let offset = 0;
        for (const match of source.matchAll(pattern)) {
            if (match.index > offset) parent.append(document.createTextNode(source.slice(offset, match.index)));
            const tokenValue = match[0];
            let element = null;
            let text = tokenValue;

            if (tokenValue.startsWith("`")) {
                element = document.createElement("code");
                text = tokenValue.slice(1, -1);
            } else if (tokenValue.startsWith("![")) {
                const parts = /^!\[([^\]]*)\]\(([^)]+)\)$/.exec(tokenValue);
                const sourceUrl = parts ? safeImageUrl(parts[2]) : null;
                if (sourceUrl) {
                    element = document.createElement("img");
                    element.src = sourceUrl;
                    element.alt = parts[1];
                    element.loading = "lazy";
                    element.referrerPolicy = "no-referrer";
                    element.className = "ai-chat-markdown-image";
                    text = "";
                }
            } else if (tokenValue.startsWith("[")) {
                const parts = /^\[([^\]]+)\]\(([^)]+)\)$/.exec(tokenValue);
                const href = parts ? safeLinkUrl(parts[2]) : null;
                if (href) {
                    element = document.createElement("a");
                    element.href = href;
                    element.target = "_blank";
                    element.rel = "noopener noreferrer";
                    text = parts[1];
                }
            } else if (tokenValue.startsWith("***")) {
                const strong = document.createElement("strong");
                element = document.createElement("em");
                strong.append(element);
                parent.append(strong);
                text = tokenValue.slice(3, -3);
            } else if (tokenValue.startsWith("**")) {
                element = document.createElement("strong");
                text = tokenValue.slice(2, -2);
            } else if (tokenValue.startsWith("~~")) {
                element = document.createElement("del");
                text = tokenValue.slice(2, -2);
            } else if (tokenValue.startsWith("*")) {
                element = document.createElement("em");
                text = tokenValue.slice(1, -1);
            }

            if (element) {
                if (text) element.textContent = text;
                if (!element.parentNode) parent.append(element);
            } else {
                parent.append(document.createTextNode(tokenValue));
            }
            offset = match.index + tokenValue.length;
        }
        if (offset < source.length) parent.append(document.createTextNode(source.slice(offset)));
    }

    function markdownTableCells(line) {
        const trimmed = line.trim().replace(/^\|/, "").replace(/\|$/, "");
        return trimmed.split("|").map(cell => cell.trim());
    }

    function isMarkdownTable(lines, index) {
        if (index + 1 >= lines.length || !lines[index].includes("|")) return false;
        const separators = markdownTableCells(lines[index + 1]);
        return separators.length > 0 && separators.every(cell => /^:?-{3,}:?$/.test(cell));
    }

    function sanitizeSvg(source) {
        const parsed = new DOMParser().parseFromString(source, "image/svg+xml");
        const rootElement = parsed.documentElement;
        if (!rootElement || rootElement.localName.toLowerCase() !== "svg"
            || parsed.querySelector("parsererror")) return null;

        const copyElement = original => {
            const tagName = original.localName.toLowerCase();
            if (!safeSvgElements.has(tagName)) return null;
            const clean = document.createElementNS("http://www.w3.org/2000/svg", original.localName);
            for (const attribute of original.attributes) {
                const name = attribute.name.toLowerCase();
                const value = attribute.value.trim();
                if (!safeSvgAttributes.has(name) || name.startsWith("on")) continue;
                if (/url\s*\(/i.test(value) && !/^url\(#[a-z0-9_.:-]+\)$/i.test(value)) continue;
                if (name === "id" && !/^[a-z0-9_.:-]+$/i.test(value)) continue;
                clean.setAttribute(attribute.name, value);
            }
            for (const child of original.childNodes) {
                if (child.nodeType === Node.TEXT_NODE) {
                    clean.append(document.createTextNode(child.textContent || ""));
                } else if (child.nodeType === Node.ELEMENT_NODE) {
                    const cleanChild = copyElement(child);
                    if (cleanChild) clean.append(cleanChild);
                }
            }
            return clean;
        };

        const cleanRoot = copyElement(rootElement);
        if (!cleanRoot) return null;
        cleanRoot.classList.add("ai-chat-generated-svg");
        cleanRoot.setAttribute("role", "img");
        if (!cleanRoot.hasAttribute("aria-label")) cleanRoot.setAttribute("aria-label", "AI-generated image");
        return cleanRoot;
    }

    function appendSvgPreview(parent, source) {
        const svg = sanitizeSvg(source);
        if (!svg) return false;
        const figure = document.createElement("figure");
        figure.className = "ai-chat-generated-image";
        const preview = document.createElement("div");
        preview.className = "ai-chat-generated-image-preview";
        preview.append(svg);
        const caption = document.createElement("figcaption");
        caption.textContent = "AI-generated SVG image";
        const download = document.createElement("a");
        download.className = "btn btn-sm btn-outline-secondary";
        download.download = "generated-image.svg";
        download.textContent = "Download SVG";
        const serialized = new XMLSerializer().serializeToString(svg);
        download.href = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(serialized)}`;
        caption.append(download);
        figure.append(preview, caption);
        parent.append(figure);
        return true;
    }

    function renderMarkdown(parent, content) {
        parent.classList.add("markdown-content", "ai-chat-rich-content");
        const lines = String(content || "").replace(/\r\n?/g, "\n").split("\n");
        let index = 0;
        while (index < lines.length) {
            const line = lines[index];
            if (!line.trim()) {
                index += 1;
                continue;
            }

            const fence = /^(?:```|~~~)\s*([a-z0-9_-]*)\s*$/i.exec(line.trim());
            if (fence) {
                const marker = line.trim().slice(0, 3);
                const language = fence[1].toLowerCase();
                const body = [];
                index += 1;
                while (index < lines.length && !lines[index].trim().startsWith(marker)) body.push(lines[index++]);
                if (index < lines.length) index += 1;
                const code = body.join("\n");
                if (language === "svg" && appendSvgPreview(parent, code)) continue;
                const pre = document.createElement("pre");
                const codeElement = document.createElement("code");
                if (language) codeElement.dataset.language = language;
                codeElement.textContent = code;
                pre.append(codeElement);
                parent.append(pre);
                continue;
            }

            if (/^\s*<svg\b/i.test(line)) {
                const svgLines = [line];
                while (!/<\/svg>\s*$/i.test(svgLines.at(-1)) && index + 1 < lines.length) {
                    index += 1;
                    svgLines.push(lines[index]);
                }
                index += 1;
                const svgSource = svgLines.join("\n").trim();
                if (appendSvgPreview(parent, svgSource)) continue;
                const pre = document.createElement("pre");
                const code = document.createElement("code");
                code.textContent = svgSource;
                pre.append(code);
                parent.append(pre);
                continue;
            }

            if (isMarkdownTable(lines, index)) {
                const headers = markdownTableCells(lines[index]);
                index += 2;
                const table = document.createElement("table");
                const thead = document.createElement("thead");
                const headerRow = document.createElement("tr");
                for (const header of headers) {
                    const cell = document.createElement("th");
                    appendInlineMarkdown(cell, header);
                    headerRow.append(cell);
                }
                thead.append(headerRow);
                const tbody = document.createElement("tbody");
                while (index < lines.length && lines[index].includes("|") && lines[index].trim()) {
                    const row = document.createElement("tr");
                    const values = markdownTableCells(lines[index]);
                    for (let cellIndex = 0; cellIndex < headers.length; cellIndex += 1) {
                        const cell = document.createElement("td");
                        appendInlineMarkdown(cell, values[cellIndex] || "");
                        row.append(cell);
                    }
                    tbody.append(row);
                    index += 1;
                }
                table.append(thead, tbody);
                const wrapper = document.createElement("div");
                wrapper.className = "ai-chat-table-wrap";
                wrapper.append(table);
                parent.append(wrapper);
                continue;
            }

            const heading = /^(#{1,6})\s+(.+)$/.exec(line);
            if (heading) {
                const element = document.createElement(`h${heading[1].length}`);
                appendInlineMarkdown(element, heading[2]);
                parent.append(element);
                index += 1;
                continue;
            }

            if (/^\s*(?:-{3,}|\*{3,}|_{3,})\s*$/.test(line)) {
                parent.append(document.createElement("hr"));
                index += 1;
                continue;
            }

            if (/^\s*>/.test(line)) {
                const quote = document.createElement("blockquote");
                while (index < lines.length && /^\s*>/.test(lines[index])) {
                    if (quote.childNodes.length) quote.append(document.createElement("br"));
                    appendInlineMarkdown(quote, lines[index].replace(/^\s*>\s?/, ""));
                    index += 1;
                }
                parent.append(quote);
                continue;
            }

            const listMatch = /^\s*([-+*]|\d+\.)\s+(.+)$/.exec(line);
            if (listMatch) {
                const ordered = /\d+\./.test(listMatch[1]);
                const list = document.createElement(ordered ? "ol" : "ul");
                while (index < lines.length) {
                    const itemMatch = /^\s*([-+*]|\d+\.)\s+(.+)$/.exec(lines[index]);
                    if (!itemMatch || /\d+\./.test(itemMatch[1]) !== ordered) break;
                    const item = document.createElement("li");
                    appendInlineMarkdown(item, itemMatch[2]);
                    list.append(item);
                    index += 1;
                }
                parent.append(list);
                continue;
            }

            const paragraphLines = [line];
            index += 1;
            while (index < lines.length && lines[index].trim()
                && !markdownBlockStart.test(lines[index].trim())
                && !isMarkdownTable(lines, index)) {
                paragraphLines.push(lines[index]);
                index += 1;
            }
            const paragraph = document.createElement("p");
            paragraphLines.forEach((paragraphLine, lineIndex) => {
                if (lineIndex) paragraph.append(document.createElement("br"));
                appendInlineMarkdown(paragraph, paragraphLine);
            });
            parent.append(paragraph);
        }
    }

    function addMessage(role, content, citations, usage) {
        const empty = messagesElement.querySelector(".ai-chat-empty");
        if (empty) empty.remove();
        const row = document.createElement("div");
        row.className = `ai-chat-message ${role}`;
        const bubble = document.createElement("div");
        bubble.className = "ai-chat-bubble";
        if (role === "assistant") bubble.classList.add("ai-chat-rich-bubble");
        const answer = document.createElement("div");
        answer.className = "ai-chat-answer";
        if (role === "assistant") {
            renderMarkdown(answer, content);
        } else {
            answer.textContent = content;
        }
        bubble.append(answer);
        if (Array.isArray(citations) && citations.length) {
            const sourceLine = document.createElement("div");
            sourceLine.className = "ai-chat-citations";
            sourceLine.textContent = `Sources: ${citations.map(item => item.title).join(", ")}`;
            bubble.append(sourceLine);
        }
        const normalizedUsage = normalizeTokenUsage(usage);
        if (role === "assistant") {
            const usageLine = document.createElement("div");
            usageLine.className = "ai-chat-token-usage";
            usageLine.textContent = normalizedUsage
                ? `Tokens: ${formatTokenUsage(normalizedUsage)}`
                : "Tokens: not reported by provider";
            bubble.append(usageLine);
        }
        if (role === "assistant") {
            const actions = document.createElement("div");
            actions.className = "ai-chat-message-actions";
            const copy = document.createElement("button");
            copy.type = "button";
            copy.className = "ai-chat-copy-button";
            copy.setAttribute("aria-label", "Copy response");
            copy.title = "Copy response";
            copy.innerHTML = copyIcon;
            copy.addEventListener("click", () => copyResponse(content, copy));
            actions.append(copy);
            bubble.append(actions);
        }
        row.append(bubble);
        messagesElement.append(row);
        messagesElement.scrollTop = messagesElement.scrollHeight;
    }

    function addThinkingMessage() {
        const empty = messagesElement.querySelector(".ai-chat-empty");
        if (empty) empty.remove();

        const row = document.createElement("div");
        row.className = "ai-chat-message assistant ai-chat-thinking";
        row.setAttribute("role", "status");
        row.setAttribute("aria-label", "AI is thinking");

        const bubble = document.createElement("div");
        bubble.className = "ai-chat-bubble ai-chat-thinking-bubble";

        const label = document.createElement("span");
        label.className = "ai-chat-thinking-label";
        label.textContent = "Thinking";

        const dots = document.createElement("span");
        dots.className = "ai-chat-thinking-dots";
        dots.setAttribute("aria-hidden", "true");
        for (let index = 0; index < 3; index += 1) {
            dots.append(document.createElement("span"));
        }

        bubble.append(label, dots);
        row.append(bubble);
        messagesElement.append(row);
        messagesElement.scrollTop = messagesElement.scrollHeight;
        return row;
    }

    function requestExternalAccess(payload, signal) {
        return new Promise((resolve, reject) => {
            const sourceNames = Array.isArray(payload.externalSources)
                ? payload.externalSources.filter(source => typeof source === "string")
                : [];
            const row = document.createElement("div");
            row.className = "ai-chat-message assistant ai-chat-consent";

            const bubble = document.createElement("div");
            bubble.className = "ai-chat-bubble ai-chat-consent-bubble";
            const question = document.createElement("div");
            question.className = "ai-chat-answer";
            question.textContent = payload.answer
                || "May TeamHub use your external connections for this question?";

            const actions = document.createElement("div");
            actions.className = "ai-chat-consent-actions";
            actions.setAttribute("role", "group");
            actions.setAttribute("aria-label", "External access choice");
            const approve = document.createElement("button");
            approve.type = "button";
            approve.className = "btn btn-sm btn-primary";
            approve.textContent = sourceNames.length
                ? `Use ${sourceNames.join(" and ")}`
                : "Allow for this question";
            const decline = document.createElement("button");
            decline.type = "button";
            decline.className = "btn btn-sm btn-outline-secondary";
            decline.textContent = "Use TeamHub only";
            actions.append(approve, decline);
            bubble.append(question, actions);
            row.append(bubble);
            messagesElement.append(row);
            messagesElement.scrollTop = messagesElement.scrollHeight;

            const finish = approved => {
                signal.removeEventListener("abort", cancel);
                approve.disabled = true;
                decline.disabled = true;
                actions.textContent = approved
                    ? `${sourceNames.join(" and ")} access approved for this question.`
                    : "Using TeamHub data only for this question.";
                actions.classList.add("is-complete");
                resolve(approved);
            };
            const cancel = () => {
                row.remove();
                reject(new DOMException("Response generation was cancelled.", "AbortError"));
            };
            approve.addEventListener("click", () => finish(true), { once: true });
            decline.addEventListener("click", () => finish(false), { once: true });
            signal.addEventListener("abort", cancel, { once: true });
        });
    }

    async function loadModels(force = false) {
        if (modelsLoaded && !force) return;
        if (loadingModels) return loadingModels;
        const preferredModel = pendingModel || model.value;
        model.disabled = true;
        send.disabled = true;
        error.textContent = "";
        model.replaceChildren(new Option("Loading models…", ""));

        loadingModels = (async () => {
            try {
                const response = await fetch("/AIChat?handler=Models", {
                    headers: { "Accept": "application/json" }
                });
                const payload = await response.json().catch(() => ({}));
                if (!response.ok) throw new Error(payload.error || "Models could not be loaded.");
                model.replaceChildren();
                for (const item of payload.models || []) {
                    const label = item.costTier ? `${item.id} [${item.costTier}]` : item.id;
                    model.add(new Option(label, item.id));
                }
                if (preferredModel && Array.from(model.options).some(option => option.value === preferredModel)) {
                    model.value = preferredModel;
                }
                pendingModel = null;
                if (provider) provider.textContent = payload.provider || "AI provider";
                modelsLoaded = model.options.length > 0;
                if (!modelsLoaded) throw new Error("The active provider did not return any models.");
                model.disabled = false;
                send.disabled = false;
                persistState();
            } catch (exception) {
                model.replaceChildren(new Option("No model available", ""));
                error.textContent = exception.message || "Models could not be loaded.";
            } finally {
                loadingModels = null;
            }
        })();
        return loadingModels;
    }

    async function readDocuments() {
        if (mode.value === "TeamHub") return [];
        const selected = Array.from(files.files || []);
        if (selected.length > 5) throw new Error("Attach no more than five documents.");
        const maximumBinaryFileBytes = 10 * 1024 * 1024;
        const maximumTotalBinaryBytes = 20 * 1024 * 1024;
        let totalBinaryBytes = 0;
        return Promise.all(selected.map(async file => {
            const mediaType = file.type || mediaTypeFor(file.name);
            const extension = file.name.split(".").pop().toLowerCase();
            const binary = ["docx", "pdf", "chm", "png", "jpg", "jpeg", "webp", "gif"].includes(extension);
            if (!binary) {
                return { name: file.name, mediaType, content: await file.text() };
            }
            if (file.size > maximumBinaryFileBytes) {
                throw new Error(`${file.name} exceeds the 10 MB binary file limit.`);
            }
            totalBinaryBytes += file.size;
            if (totalBinaryBytes > maximumTotalBinaryBytes) {
                throw new Error("Binary document and image uploads cannot exceed 20 MB in total.");
            }
            return {
                name: file.name,
                mediaType,
                base64Content: await readFileAsBase64(file)
            };
        }));
    }

    async function readMeetingNotesOverride() {
        if (mode.value !== "MeetingNotes" || !meetingOverride?.files?.length) return null;
        const file = meetingOverride.files[0];
        const extension = file.name.split(".").pop().toLowerCase();
        if (!["md", "markdown"].includes(extension))
            throw new Error("The additional Meeting Notes override must be a Markdown (.md) file.");
        if (file.size > 256 * 1024)
            throw new Error("The additional Meeting Notes override cannot exceed 256 KB.");
        const content = (await file.text()).trim();
        if (!content) throw new Error("The additional Meeting Notes override is empty.");
        return content;
    }

    function readFileAsBase64(file) {
        return new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.addEventListener("load", () => {
                const value = typeof reader.result === "string" ? reader.result : "";
                const separator = value.indexOf(",");
                if (separator < 0) reject(new Error(`${file.name} could not be read.`));
                else resolve(value.slice(separator + 1));
            }, { once: true });
            reader.addEventListener("error", () => reject(
                new Error(`${file.name} could not be read.`)), { once: true });
            reader.readAsDataURL(file);
        });
    }

    function mediaTypeFor(name) {
        const extension = name.split(".").pop().toLowerCase();
        if (extension === "md" || extension === "markdown") return "text/markdown";
        if (extension === "csv") return "text/csv";
        if (extension === "json") return "application/json";
        if (extension === "docx") return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        if (extension === "pdf") return "application/pdf";
        if (extension === "chm") return "application/vnd.ms-htmlhelp";
        if (extension === "png") return "image/png";
        if (extension === "jpg" || extension === "jpeg") return "image/jpeg";
        if (extension === "webp") return "image/webp";
        if (extension === "gif") return "image/gif";
        return "text/plain";
    }

    function clearWidgetBounds() {
        root.style.removeProperty("left");
        root.style.removeProperty("top");
        root.style.removeProperty("right");
        root.style.removeProperty("bottom");
        root.style.removeProperty("width");
        root.style.removeProperty("height");
    }

    function constrainBounds(bounds) {
        const margin = 8;
        const viewportWidth = document.documentElement.clientWidth;
        const viewportHeight = document.documentElement.clientHeight;
        const minimumWidth = Math.min(340, Math.max(1, viewportWidth - (margin * 2)));
        const minimumHeight = Math.min(420, Math.max(1, viewportHeight - (margin * 2)));
        const width = Math.min(Math.max(bounds.width, minimumWidth), viewportWidth - (margin * 2));
        const height = Math.min(Math.max(bounds.height, minimumHeight), viewportHeight - (margin * 2));
        return {
            left: Math.min(Math.max(bounds.left, margin), viewportWidth - width - margin),
            top: Math.min(Math.max(bounds.top, margin), viewportHeight - height - margin),
            width,
            height
        };
    }

    function applyWidgetBounds(bounds) {
        if (!widget || !bounds || window.matchMedia("(max-width: 575.98px)").matches) return;
        const constrained = constrainBounds(bounds);
        root.style.left = `${constrained.left}px`;
        root.style.top = `${constrained.top}px`;
        root.style.right = "auto";
        root.style.bottom = "auto";
        root.style.width = `${constrained.width}px`;
        root.style.height = `${constrained.height}px`;
        normalBounds = constrained;
    }

    function currentWidgetBounds() {
        const rectangle = root.getBoundingClientRect();
        return {
            left: rectangle.left,
            top: rectangle.top,
            width: rectangle.width,
            height: rectangle.height
        };
    }

    function initializeWidgetResize() {
        if (!widget) return;
        const handles = root.querySelectorAll("[data-ai-chat-resize]");
        for (const handle of handles) {
            handle.addEventListener("pointerdown", event => {
                if (root.classList.contains("is-fullscreen")
                    || window.matchMedia("(max-width: 575.98px)").matches
                    || event.button !== 0) return;

                event.preventDefault();
                const direction = handle.dataset.aiChatResize || "";
                const start = currentWidgetBounds();
                const startRight = start.left + start.width;
                const startBottom = start.top + start.height;
                const startX = event.clientX;
                const startY = event.clientY;
                const margin = 8;
                applyWidgetBounds(start);
                handle.setPointerCapture(event.pointerId);
                document.documentElement.classList.add("teamhub-ai-chat-resizing");

                const move = moveEvent => {
                    const viewportWidth = document.documentElement.clientWidth;
                    const viewportHeight = document.documentElement.clientHeight;
                    const minimumWidth = Math.min(340, Math.max(1, viewportWidth - (margin * 2)));
                    const minimumHeight = Math.min(420, Math.max(1, viewportHeight - (margin * 2)));
                    const deltaX = moveEvent.clientX - startX;
                    const deltaY = moveEvent.clientY - startY;
                    let left = start.left;
                    let top = start.top;
                    let right = startRight;
                    let bottom = startBottom;

                    if (direction.includes("w")) {
                        left = Math.min(Math.max(margin, start.left + deltaX), startRight - minimumWidth);
                    } else if (direction.includes("e")) {
                        right = Math.max(
                            Math.min(viewportWidth - margin, startRight + deltaX),
                            start.left + minimumWidth);
                    }
                    if (direction.includes("n")) {
                        top = Math.min(Math.max(margin, start.top + deltaY), startBottom - minimumHeight);
                    } else if (direction.includes("s")) {
                        bottom = Math.max(
                            Math.min(viewportHeight - margin, startBottom + deltaY),
                            start.top + minimumHeight);
                    }

                    applyWidgetBounds({
                        left,
                        top,
                        width: right - left,
                        height: bottom - top
                    });
                };

                const stop = stopEvent => {
                    handle.removeEventListener("pointermove", move);
                    handle.removeEventListener("pointerup", stop);
                    handle.removeEventListener("pointercancel", stop);
                    if (handle.hasPointerCapture(stopEvent.pointerId)) {
                        handle.releasePointerCapture(stopEvent.pointerId);
                    }
                    document.documentElement.classList.remove("teamhub-ai-chat-resizing");
                    normalBounds = currentWidgetBounds();
                    persistState();
                };

                handle.addEventListener("pointermove", move);
                handle.addEventListener("pointerup", stop);
                handle.addEventListener("pointercancel", stop);
            });
        }

        window.addEventListener("resize", () => {
            if (!root.classList.contains("is-fullscreen") && normalBounds) applyWidgetBounds(normalBounds);
        });
    }

    function setWidgetOpen(open) {
        if (!widget) return;
        if (!open) {
            activeRequest?.abort();
            resetConversation();
        }
        root.hidden = !open;
        root.setAttribute("aria-hidden", String(!open));
        launcher.setAttribute("aria-expanded", String(open));
        persistState();
        if (open && activeRequest) {
            setGenerating(true);
        } else if (open) {
            loadModels(true).finally(() => prompt.focus());
        }
    }

    if (launcher) launcher.addEventListener("click", () => setWidgetOpen(root.hidden));
    if (close) close.addEventListener("click", () => setWidgetOpen(false));
    if (maximize) maximize.addEventListener("click", () => {
        const isFullscreen = !root.classList.contains("is-fullscreen");
        if (isFullscreen) {
            if (!window.matchMedia("(max-width: 575.98px)").matches) {
                normalBounds = currentWidgetBounds();
            }
            clearWidgetBounds();
            root.classList.add("is-fullscreen");
        } else {
            root.classList.remove("is-fullscreen");
            if (normalBounds) applyWidgetBounds(normalBounds);
        }
        maximize.setAttribute("aria-label", isFullscreen ? "Restore TeamHub Assistant" : "Maximize TeamHub Assistant");
        maximize.setAttribute("title", isFullscreen ? "Restore" : "Maximize");
        persistState();
    });
    document.addEventListener("keydown", event => {
        if (event.key !== "Escape" || !widget || root.hidden) return;
        if (root.classList.contains("is-reviewing-notes")) closeMeetingEditor();
        else setWidgetOpen(false);
    });

    mode.addEventListener("change", () => updateMode(true));
    model.addEventListener("change", persistState);
    if (meetingStudio) meetingStudio.addEventListener("change", () => {
        if (mode.value === "MeetingNotes") resetConversation();
        persistState();
    });
    window.addEventListener("beforeunload", persistState);
    prompt.addEventListener("keydown", event => {
        if (event.key !== "Enter" || event.shiftKey || event.isComposing) return;
        event.preventDefault();
        form.requestSubmit();
    });
    form.addEventListener("submit", async event => {
        event.preventDefault();
        if (activeRequest) {
            activeRequest.abort();
            return;
        }
        const attachedFiles = mode.value === "TeamHub" ? [] : Array.from(files?.files || []);
        let content = prompt.value.trim();
        if (!content && attachedFiles.length > 0) {
            content = mode.value === "MeetingNotes"
                ? "Generate structured meeting notes from the attached transcript."
                : "Analyze the attached file and provide a concise summary.";
        }
        if (!content) {
            error.textContent = mode.value === "TeamHub"
                ? "Enter a question. TeamHub mode does not accept file attachments."
                : "Enter a message or attach a file.";
            prompt.focus();
            return;
        }
        if (!model.value || send.disabled) return;
        error.textContent = "";
        const requestRevision = conversationRevision;
        activeRequest = new AbortController();
        setGenerating(true);
        const previousHistory = [...history];
        const requestHistory = [
            ...history.slice(-19).map(item => ({ role: item.role, content: item.content })),
            { role: "user", content }
        ];
        history = requestHistory;
        addMessage("user", content);
        let thinkingMessage = addThinkingMessage();
        persistState();
        prompt.value = "";

        try {
            const documents = await readDocuments();
            const meetingNotesOverride = await readMeetingNotesOverride();
            let externalAccessDecision = null;
            let approvedExternalSources = [];
            let payload;
            while (true) {
                const response = await fetch("/AIChat?handler=Message", {
                    method: "POST",
                    signal: activeRequest.signal,
                    headers: {
                        "Content-Type": "application/json",
                        "RequestVerificationToken": token.value
                    },
                    body: JSON.stringify({
                        mode: mode.value,
                        model: model.value,
                        messages: requestHistory,
                        documents,
                        meetingNotesStudioId: mode.value === "MeetingNotes"
                            ? meetingStudio?.value || null
                            : null,
                        meetingNotesOverride,
                        externalAccessDecision,
                        approvedExternalSources
                    })
                });
                payload = await response.json().catch(() => ({}));
                if (!response.ok) throw new Error(payload.error || "The AI request could not be completed.");
                if (!payload.requiresExternalAccess) break;

                thinkingMessage.remove();
                const approved = await requestExternalAccess(payload, activeRequest.signal);
                externalAccessDecision = approved ? "Approved" : "Denied";
                approvedExternalSources = approved && Array.isArray(payload.externalSources)
                    ? payload.externalSources
                    : [];
                thinkingMessage = addThinkingMessage();
            }
            if (requestRevision !== conversationRevision) return;
            const assistantMessage = {
                role: "assistant",
                content: payload.answer,
                citations: Array.isArray(payload.citations) ? payload.citations : [],
                usage: normalizeTokenUsage(payload.usage)
            };
            history = [...requestHistory, assistantMessage];
            thinkingMessage.remove();
            addMessage("assistant", assistantMessage.content, assistantMessage.citations, assistantMessage.usage);
            updateSessionUsage();
            meetingDraftDirty = false;
            closeMeetingEditor(false);
            refreshMeetingActions(true);
            persistState();
        } catch (exception) {
            if (requestRevision === conversationRevision) {
                history = previousHistory;
                persistState();
                error.textContent = exception.name === "AbortError"
                    ? "Response generation was cancelled."
                    : exception.message || "The AI request could not be completed.";
            }
        } finally {
            thinkingMessage.remove();
            activeRequest = null;
            setGenerating(false);
            prompt.focus();
        }
    });

    const savedState = readState();
    if (savedState) {
        mode.value = savedState.mode;
        pendingModel = savedState.model;
        pendingMeetingStudioId = savedState.meetingStudioId;
        history = savedState.history;
        normalBounds = savedState.bounds;
    }
    updateMode(false);
    messagesElement.replaceChildren();
    if (history.length) {
        for (const item of history) addMessage(item.role, item.content, item.citations, item.usage);
    } else {
        showEmptyConversation();
    }
    updateSessionUsage();
    refreshMeetingActions();

    if (downloadNotes) downloadNotes.addEventListener("click", downloadMeetingNotes);
    if (editNotes) editNotes.addEventListener("click", openMeetingEditor);
    if (closeMeetingEditorButton) closeMeetingEditorButton.addEventListener("click", () => closeMeetingEditor());
    if (meetingDraft) meetingDraft.addEventListener("input", () => {
        meetingDraftDirty = true;
    });
    if (publishNotes) publishNotes.addEventListener("click", publishMeetingNotes);
    if (meetingDestination) meetingDestination.addEventListener("change", () => {
        if (publishNotes) publishNotes.disabled = !meetingDestination.value;
        if (meetingStatus) meetingStatus.textContent = "";
    });

    if (widget) {
        root.classList.toggle("is-fullscreen", savedState?.fullscreen === true);
        if (savedState?.fullscreen !== true && normalBounds) applyWidgetBounds(normalBounds);
        if (savedState?.fullscreen === true && maximize) {
            maximize.setAttribute("aria-label", "Restore TeamHub Assistant");
            maximize.setAttribute("title", "Restore");
        }
        initializeWidgetResize();
        setWidgetOpen(savedState?.open === true);
    } else if (!modelsLoaded) {
        loadModels();
    }
})();
