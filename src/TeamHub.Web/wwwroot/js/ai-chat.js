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
    const messagesElement = root.querySelector("#aiChatMessages");
    const documentsElement = root.querySelector("#aiChatDocuments");
    const files = root.querySelector("#aiChatFiles");
    const form = root.querySelector("#aiChatForm");
    const prompt = root.querySelector("#aiChatPrompt");
    const send = root.querySelector("#aiChatSend");
    const error = root.querySelector("#aiChatError");
    const token = form.querySelector('input[name="__RequestVerificationToken"]');
    const storageKey = `teamhub.aiChat.widget.v2:${root.dataset.aiChatUser || "page"}`;
    let history = [];
    let modelsLoaded = model.options.length > 0 && Boolean(model.value);
    let loadingModels = null;
    let pendingModel = null;
    let activeRequest = null;
    const copyIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 7V3h13v13h-4v5H3V7h5Zm2 0h7v7h2V5h-9v2Zm5 2H5v10h10V9Z" /></svg>';
    const copiedIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m9.2 17.2-4.4-4.4 1.4-1.4 3 3L17.8 5.8l1.4 1.4-10 10Z" /></svg>';

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
                    return [{ role: item.role, content: item.content.slice(0, 8000), citations }];
                })
                : [];
            return {
                open: state.open === true,
                fullscreen: state.fullscreen === true,
                mode: state.mode === "General" ? "General" : "TeamHub",
                model: typeof state.model === "string" ? state.model.slice(0, 200) : "",
                history: savedHistory
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
                history: history.slice(-20)
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
            : "Ask a general question or attach your own documents.";
        empty.append(title, detail);
        messagesElement.append(empty);
    }

    function resetConversation() {
        history = [];
        if (files) files.value = "";
        showEmptyConversation();
        error.textContent = "";
        persistState();
    }

    function updateMode(reset = true) {
        const teamHubMode = mode.value === "TeamHub";
        documentsElement.classList.toggle("d-none", teamHubMode);
        note.textContent = teamHubMode
            ? "Uses only TeamHub information authorized for your account. Workflow results are limited to workflows assigned to you."
            : "Isolated from TeamHub. Documents are optional; ordinary questions use the model's general knowledge.";
        if (reset) resetConversation();
    }

    function setGenerating(generating) {
        mode.disabled = generating;
        model.disabled = generating || !modelsLoaded;
        prompt.disabled = generating;
        if (files) files.disabled = generating;
        send.disabled = false;
        send.textContent = generating ? "Cancel" : "Send";
        send.classList.toggle("btn-danger", generating);
        send.classList.toggle("btn-primary", !generating);
        send.setAttribute("aria-label", generating ? "Cancel response generation" : "Send message");
        root.classList.toggle("is-generating", generating);
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

    function addMessage(role, content, citations) {
        const empty = messagesElement.querySelector(".ai-chat-empty");
        if (empty) empty.remove();
        const row = document.createElement("div");
        row.className = `ai-chat-message ${role}`;
        const bubble = document.createElement("div");
        bubble.className = "ai-chat-bubble";
        const answer = document.createElement("div");
        answer.className = "ai-chat-answer";
        answer.textContent = content;
        bubble.append(answer);
        if (Array.isArray(citations) && citations.length) {
            const sourceLine = document.createElement("div");
            sourceLine.className = "ai-chat-citations";
            sourceLine.textContent = `Sources: ${citations.map(item => item.title).join(", ")}`;
            bubble.append(sourceLine);
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
        if (mode.value !== "General") return [];
        const selected = Array.from(files.files || []);
        if (selected.length > 5) throw new Error("Attach no more than five documents.");
        return Promise.all(selected.map(async file => ({
            name: file.name,
            mediaType: file.type || mediaTypeFor(file.name),
            content: await file.text()
        })));
    }

    function mediaTypeFor(name) {
        const extension = name.split(".").pop().toLowerCase();
        if (extension === "md" || extension === "markdown") return "text/markdown";
        if (extension === "csv") return "text/csv";
        if (extension === "json") return "application/json";
        return "text/plain";
    }

    function setWidgetOpen(open) {
        if (!widget) return;
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
        const isFullscreen = root.classList.toggle("is-fullscreen");
        maximize.setAttribute("aria-label", isFullscreen ? "Restore AI Chat" : "Maximize AI Chat");
        maximize.setAttribute("title", isFullscreen ? "Restore" : "Maximize");
        persistState();
    });
    document.addEventListener("keydown", event => {
        if (event.key === "Escape" && widget && !root.hidden) setWidgetOpen(false);
    });

    mode.addEventListener("change", () => updateMode(true));
    model.addEventListener("change", persistState);
    window.addEventListener("beforeunload", persistState);
    form.addEventListener("submit", async event => {
        event.preventDefault();
        if (activeRequest) {
            activeRequest.abort();
            return;
        }
        const content = prompt.value.trim();
        if (!content || !model.value || send.disabled) return;
        error.textContent = "";
        activeRequest = new AbortController();
        setGenerating(true);
        const previousHistory = [...history];
        const requestHistory = [
            ...history.slice(-19).map(item => ({ role: item.role, content: item.content })),
            { role: "user", content }
        ];
        history = requestHistory;
        addMessage("user", content);
        persistState();
        prompt.value = "";

        try {
            const documents = await readDocuments();
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
                    documents
                })
            });
            const payload = await response.json().catch(() => ({}));
            if (!response.ok) throw new Error(payload.error || "The AI request could not be completed.");
            const assistantMessage = {
                role: "assistant",
                content: payload.answer,
                citations: Array.isArray(payload.citations) ? payload.citations : []
            };
            history = [...requestHistory, assistantMessage];
            addMessage("assistant", assistantMessage.content, assistantMessage.citations);
            persistState();
        } catch (exception) {
            history = previousHistory;
            persistState();
            error.textContent = exception.name === "AbortError"
                ? "Response generation was cancelled."
                : exception.message || "The AI request could not be completed.";
        } finally {
            activeRequest = null;
            setGenerating(false);
            prompt.focus();
        }
    });

    const savedState = readState();
    if (savedState) {
        mode.value = savedState.mode;
        pendingModel = savedState.model;
        history = savedState.history;
    }
    updateMode(false);
    messagesElement.replaceChildren();
    if (history.length) {
        for (const item of history) addMessage(item.role, item.content, item.citations);
    } else {
        showEmptyConversation();
    }

    if (widget) {
        root.classList.toggle("is-fullscreen", savedState?.fullscreen === true);
        if (savedState?.fullscreen === true && maximize) {
            maximize.setAttribute("aria-label", "Restore AI Chat");
            maximize.setAttribute("title", "Restore");
        }
        setWidgetOpen(savedState?.open === true);
    } else if (!modelsLoaded) {
        loadModels();
    }
})();
