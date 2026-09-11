(function () {
    "use strict";

    const root = document.getElementById("flowDesigner");
    if (!root) return;

    const byId = id => document.getElementById(id);
    const flowId = root.dataset.flowId;
    const canEdit = root.dataset.canEdit === "true";
    const nameInput = byId("flowName");
    const saveState = byId("saveState");
    const saveButton = byId("saveButton");
    const saveButtonText = byId("saveButtonText");
    const shareButton = byId("shareButton");
    const shareButtonText = byId("shareButtonText");
    const publishButton = byId("publishButton");
    const publishButtonText = byId("publishButtonText");
    const saveTemplateForm = byId("saveTemplateForm");
    const nodeForm = byId("nodeProperties");
    const connectionForm = byId("connectionProperties");
    const workflowForm = byId("workflowProperties");
    const emptyProperties = byId("emptyProperties");
    const nodeTypes = window.FlowDesignerAdapters.nodeTypes;
    const diagramTypes = window.FlowDesignerAdapters.diagramTypes;
    const palettes = window.FlowDesignerAdapters.palettes;
    let flow = null;
    let selectedNode = null;
    let selectedConnection = null;
    let dirty = false;
    let isDraft = false;
    let saving = false;
    let historyTimer = null;
    let toastTimer = null;
    let clipboardNode = null;
    let history = [];
    let historyIndex = -1;
    let applyingHistory = false;
    let workflowKeyTouched = false;
    let presentationMode = false;

    const adapter = new window.FlowDesignerAdapters.DrawflowAdapter(byId("drawflow"), {
        onChange: handleCanvasChange,
        onSelectNode: showNodeProperties,
        onSelectConnection: showConnectionProperties,
        onClearSelection: clearProperties,
        onZoom: updateZoom
    });

    initialize().catch(error => {
        console.error(error);
        setSaveState("error", "Could not load diagram");
        showToast("Could not load the diagram.");
    });

    async function initialize() {
        const response = await fetch(`/api/flows/${flowId}`);
        if (!response.ok) throw new Error(`Load failed (${response.status})`);
        flow = await response.json();
        isDraft = Number(flow.version) <= 0;
        nameInput.value = flow.name;
        root.dataset.diagramType = flow.diagramType;
        root.classList.add(`fd-mode-${flow.diagramType.toLowerCase()}`);
        adapter.setGraph(flow);
        adapter.setReadOnly(!canEdit);
        nameInput.readOnly = !canEdit;
        initializeWorkflowProperties();
        history = [graphSnapshot()];
        historyIndex = 0;
        updateHistoryButtons();
        updateCanvasHint();
        applyValidation(localValidation());
        updateShareButton();
        setSaveState(canEdit ? (isDraft ? "unsaved" : "saved") : "saved", canEdit ? (isDraft ? "Not saved yet" : "Saved") : "View only");
        bindUi();
        clearProperties();
    }

    function bindUi() {
        buildPalette();

        document.querySelectorAll("[data-node-type]").forEach(tool => {
            tool.addEventListener("dragstart", event => {
                event.dataTransfer.setData("application/x-flow-node", tool.dataset.nodeType);
                event.dataTransfer.effectAllowed = "copy";
            });
            tool.addEventListener("click", () => addAtCenter(tool.dataset.nodeType));
        });

        const canvas = byId("drawflow");
        canvas.addEventListener("pointerdown", finalizeSelectedNodeTitle, true);
        canvas.addEventListener("dragover", event => { event.preventDefault(); event.dataTransfer.dropEffect = "copy"; });
        canvas.addEventListener("drop", event => {
            event.preventDefault();
            if (!canEdit || presentationMode) return;
            const type = event.dataTransfer.getData("application/x-flow-node");
            if (!activePalette().nodes.includes(type)) return;
            const point = adapter.screenToCanvas(event.clientX, event.clientY);
            const position = nodePosition(type, point);
            adapter.addNode(type, position.x, position.y);
        });
        canvas.addEventListener("dblclick", event => {
            if (!canEdit || presentationMode) return;
            if (event.target !== canvas && !event.target.classList.contains("drawflow")) return;
            const point = adapter.screenToCanvas(event.clientX, event.clientY);
            const position = nodePosition(activePalette().primary, point);
            adapter.addNode(activePalette().primary, position.x, position.y);
        });

        saveButton.addEventListener("click", () => save(true));
        shareButton?.addEventListener("click", toggleSharing);
        publishButton?.addEventListener("click", publish);
        saveTemplateForm?.addEventListener("submit", saveAsTemplate);
        byId("saveTemplateModal")?.addEventListener("show.bs.modal", () => {
            if (!byId("templateName").value) byId("templateName").value = nameInput.value.trim();
            if (!byId("templateKey").value) byId("templateKey").value = toKey(nameInput.value);
            if (!byId("templateDescription").value) byId("templateDescription").value = flow?.description || "";
            if (isWorkCenter() && byId("templateCategory").value === "General") byId("templateCategory").value = "Work Center";
        });
        nameInput.addEventListener("input", () => {
            if (isWorkCenter() && !workflowKeyTouched) byId("workflowKey").value = toKey(nameInput.value);
            markDirty();
        });
        byId("workflowKey").addEventListener("input", () => { workflowKeyTouched = true; markDirty(); });
        byId("workflowDescription").addEventListener("input", markDirty);
        byId("workflowEnabled").addEventListener("change", markDirty);
        byId("undoButton").addEventListener("click", undo);
        byId("redoButton").addEventListener("click", redo);
        byId("presentationButton").addEventListener("click", enterPresentation);
        byId("exitPresentation").addEventListener("click", exitPresentation);
        byId("zoomIn").addEventListener("click", () => adapter.zoomIn());
        byId("zoomOut").addEventListener("click", () => adapter.zoomOut());
        byId("zoomReset").addEventListener("click", () => adapter.resetZoom());
        byId("fitView").addEventListener("click", () => adapter.fitToView());
        byId("gridToggle").addEventListener("click", event => {
            const enabled = !event.currentTarget.classList.contains("active");
            event.currentTarget.classList.toggle("active", enabled);
            adapter.setGrid(enabled);
        });
        byId("exportSvg").addEventListener("click", exportSvg);
        byId("exportPng").addEventListener("click", exportPng);

        ["nodeTitle", "nodeDescription", "nodeNotes"].forEach(id => {
            byId(id).addEventListener("input", updateSelectedNode);
        });
        byId("nodeTitle").addEventListener("blur", finalizeSelectedNodeTitle);
        byId("nodeType").addEventListener("change", changeSelectedNodeType);
        ["nodeTone", "nodeLayer", "nodePortLayout", "nodeSectionId", "nodePresentationStyle"].forEach(id => byId(id).addEventListener("change", updateSelectedNode));
        ["taskStepKey", "taskOwner", "taskExpectedHours", "taskReminderAfterHours", "taskReminderRepeatHours", "taskEscalationAfterHours", "taskEscalationOwner"].forEach(id => byId(id).addEventListener("input", updateSelectedNode));
        ["taskOwnerType", "taskRequired", "taskEnabled"].forEach(id => byId(id).addEventListener("change", updateSelectedNode));
        byId("addNodeComment").addEventListener("click", addNodeComment);
        byId("deleteNode").addEventListener("click", () => adapter.deleteSelected());
        byId("connectionLabel").addEventListener("input", () => {
            if (!selectedConnection) return;
            selectedConnection.label = byId("connectionLabel").value;
            adapter.updateConnectionLabel(selectedConnection, selectedConnection.label);
        });
        byId("connectionTone").addEventListener("change", () => {
            if (!selectedConnection) return;
            const tone = byId("connectionTone").value;
            selectedConnection.metadata = { ...(selectedConnection.metadata || {}), tone };
            adapter.updateConnectionTone(selectedConnection, tone);
        });
        byId("deleteConnection").addEventListener("click", () => adapter.deleteSelected());

        byId("backToFlows").addEventListener("click", event => {
            event.preventDefault();
            requestLeave(event.currentTarget.href);
        });
        byId("saveAndLeave").addEventListener("click", async () => {
            const target = byId("backToFlows").href;
            if (await save(true)) window.location.assign(target);
        });
        byId("discardAndLeave").addEventListener("click", () => discardAndLeave(byId("backToFlows").href));
        document.addEventListener("click", event => {
            if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
            const link = event.target.closest?.("a[href]");
            if (!link || link.target === "_blank" || link.hasAttribute("download")) return;
            const target = new URL(link.href, window.location.href);
            if (target.origin !== window.location.origin || target.href === window.location.href || (!dirty && !isDraft)) return;
            event.preventDefault();
            requestLeave(target.href);
        });

        byId("toggleToolbox").addEventListener("click", () => byId("toolbox").classList.toggle("open"));
        byId("closeToolbox").addEventListener("click", () => byId("toolbox").classList.remove("open"));
        byId("toggleProperties").addEventListener("click", () => byId("propertiesPanel").classList.toggle("open"));
        byId("closeProperties").addEventListener("click", () => byId("propertiesPanel").classList.remove("open"));
        byId("validationButton").addEventListener("click", () => byId("validationPopover").classList.toggle("d-none"));
        byId("closeValidation").addEventListener("click", () => byId("validationPopover").classList.add("d-none"));

        document.addEventListener("keydown", handleKeyboard);
        document.addEventListener("fullscreenchange", () => {
            if (!presentationMode) return;
            if (document.fullscreenElement === root) window.requestAnimationFrame(() => adapter.fitToView());
            else exitPresentation(false);
        });
        document.addEventListener("keyup", event => {
            if (event.code === "Space") adapter.setSpacePanning(false);
        });
        window.addEventListener("blur", () => adapter.setSpacePanning(false));
        window.addEventListener("resize", () => {
            if (presentationMode) window.requestAnimationFrame(() => adapter.fitToView());
        });
        window.addEventListener("beforeunload", event => {
            if (!dirty && !isDraft) return;
            event.preventDefault();
            event.returnValue = "";
        });
    }

    function activePalette() {
        const diagram = diagramTypes[flow?.diagramType] || diagramTypes.StandardFlowchart;
        return palettes[diagram.palette] || palettes.StandardFlowchart;
    }

    function isWorkCenter() {
        return flow?.diagramType === "WorkCenterWorkflow";
    }

    function toKey(value) {
        return String(value || "")
            .trim()
            .toUpperCase()
            .replace(/[^A-Z0-9]+/g, "_")
            .replace(/^_+|_+$/g, "");
    }

    function initializeWorkflowProperties() {
        const configuredKey = flow.metadata?.workflowKey || "";
        byId("workflowKey").value = configuredKey || toKey(flow.name);
        byId("workflowDescription").value = flow.description || "";
        byId("workflowEnabled").checked = String(flow.metadata?.enabled ?? "true").toLowerCase() !== "false";
        workflowKeyTouched = Boolean(configuredKey);
    }

    function buildPalette() {
        const palette = activePalette();
        byId("paletteTitle").textContent = palette.title;
        byId("diagramTypeLabel").textContent = (diagramTypes[flow?.diagramType] || diagramTypes.StandardFlowchart).title;
        const toolList = byId("nodeToolList");
        const typeSelect = byId("nodeType");
        toolList.replaceChildren();
        typeSelect.replaceChildren();

        for (const type of palette.nodes) {
            const config = nodeTypes[type];
            const tool = document.createElement("button");
            tool.className = "fd-tool";
            tool.type = "button";
            tool.draggable = true;
            tool.dataset.nodeType = type;
            tool.innerHTML = `<span class="fd-tool-icon fd-type-${type.toLowerCase()} fd-symbol-${config.shape}"></span><span><strong></strong><small></small></span>`;
            tool.querySelector("strong").textContent = config.title;
            tool.querySelector("small").textContent = config.help;
            toolList.appendChild(tool);

            const option = document.createElement("option");
            option.value = type;
            option.textContent = config.title;
            byId("nodeType").appendChild(option);
        }
    }

    function addAtCenter(type) {
        const canvas = byId("drawflow").getBoundingClientRect();
        const point = adapter.screenToCanvas(canvas.left + canvas.width / 2, canvas.top + canvas.height / 2);
        const position = nodePosition(type, point);
        adapter.addNode(type, position.x, position.y);
        byId("toolbox").classList.remove("open");
    }

    function nodePosition(type, point) {
        const config = nodeTypes[type] || nodeTypes.Process;
        return {
            x: point.x - (config.defaultWidth || 184) / 2,
            y: point.y - (config.defaultHeight || 76) / 2
        };
    }

    function handleCanvasChange() {
        updateCanvasHint();
        applyValidation(localValidation());
        markDirty();
        if (applyingHistory) return;
        window.clearTimeout(historyTimer);
        historyTimer = window.setTimeout(pushHistory, 100);
    }

    function markDirty() {
        dirty = true;
        setSaveState("unsaved", "Unsaved changes");
    }

    async function save(manual) {
        if (!flow || !canEdit) return false;
        finalizeSelectedNodeTitle();
        if (!dirty && !isDraft) {
            if (manual) showToast("Diagram is already saved");
            return true;
        }
        if (saving) return false;
        saving = true;
        setSaveState("saving", "Saving…");
        saveButton.disabled = true;
        saveButtonText.textContent = "Saving…";

        const graph = adapter.getGraph();
        flow = { ...flow, name: nameInput.value.trim() || "Untitled flow", nodes: graph.nodes, connections: graph.connections };
        flow.description = byId("workflowDescription").value.trim();
        if (isWorkCenter()) {
            flow.metadata = {
                ...(flow.metadata || {}),
                workflowKey: byId("workflowKey").value.trim() || toKey(flow.name),
                enabled: String(byId("workflowEnabled").checked)
            };
        }

        try {
            const response = await fetch(`/api/flows/${flowId}`, {
                method: "PUT",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(flow)
            });
            const result = await response.json();
            applyValidation(result.issues || []);
            if (!response.ok) throw new Error(result.message || "Save failed");
            flow = result.flow;
            isDraft = false;
            dirty = false;
            setSaveState("saved", `Saved ${new Date(flow.updatedAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`);
            if (manual) showToast("Diagram saved");
            return true;
        } catch (error) {
            console.error(error);
            dirty = true;
            setSaveState("error", "Save failed — try again");
            showToast(error.message || "Save failed");
            return false;
        } finally {
            saving = false;
            saveButton.disabled = false;
            saveButtonText.textContent = "Save";
        }
    }

    async function publish() {
        if (!publishButton || saving) return;
        if (!(await save(false))) return;

        publishButton.disabled = true;
        publishButtonText.textContent = "Publishingâ€¦";
        try {
            const response = await fetch(`/api/flows/${flowId}/publish`, { method: "POST" });
            const result = await response.json();
            if (!response.ok) {
                const issues = (result.errors || [result.message || "Publish failed"]).map((message, index) => ({
                    severity: "Error",
                    code: `publish-${index}`,
                    message
                }));
                applyValidation([...localValidation(), ...issues]);
                byId("validationPopover").classList.remove("d-none");
                throw new Error(issues[0]?.message || "Publish failed");
            }
            flow.isShared = true;
            updateShareButton();
            showToast(result.message || "Workflow published to Work Center");
            publishButtonText.textContent = "Published";
            window.setTimeout(() => { publishButtonText.textContent = "Publish to Work Center"; }, 1800);
        } catch (error) {
            console.error(error);
            showToast(error.message || "Publish failed");
            publishButtonText.textContent = "Publish to Work Center";
        } finally {
            publishButton.disabled = false;
        }
    }

    async function toggleSharing() {
        if (!shareButton || saving || !canEdit) return;
        if (!(await save(false))) return;

        const nextShared = !Boolean(flow.isShared);
        shareButton.disabled = true;
        shareButtonText.textContent = nextShared ? "Sharing..." : "Updating...";
        try {
            const response = await fetch(`/api/flows/${flowId}/share`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ isShared: nextShared })
            });
            const result = await response.json();
            if (!response.ok) throw new Error(result.message || "Could not update sharing");
            flow = result;
            updateShareButton();
            showToast(flow.isShared ? "Diagram shared with all users" : "Diagram is private");
        } catch (error) {
            console.error(error);
            showToast(error.message || "Could not update sharing");
            updateShareButton();
        } finally {
            shareButton.disabled = false;
        }
    }

    function updateShareButton() {
        if (!shareButton || !shareButtonText) return;
        const isShared = Boolean(flow?.isShared);
        root.dataset.isShared = String(isShared);
        shareButton.setAttribute("aria-pressed", String(isShared));
        shareButtonText.textContent = isShared ? "Unshare" : "Share";
        shareButton.title = isShared ? "Make this diagram private" : "Allow all users to view this diagram";
    }

    async function saveAsTemplate(event) {
        event.preventDefault();
        if (!(await save(false))) return;

        const button = byId("confirmSaveTemplate");
        button.disabled = true;
        button.textContent = "Saving...";
        try {
            const response = await fetch(`/api/flows/${flowId}/templates`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    templateKey: byId("templateKey").value,
                    name: byId("templateName").value,
                    category: byId("templateCategory").value,
                    description: byId("templateDescription").value
                })
            });
            const result = await response.json();
            if (!response.ok) throw new Error(result.message || "Template could not be saved");
            bootstrap.Modal.getInstance(byId("saveTemplateModal"))?.hide();
            showToast(`Template saved as ${result.templateKey} v${result.version}`);
        } catch (error) {
            console.error(error);
            showToast(error.message || "Template could not be saved");
        } finally {
            button.disabled = false;
            button.textContent = "Save template";
        }
    }

    function requestLeave(target) {
        if (!dirty && !isDraft) {
            window.location.assign(target);
            return;
        }
        if (isDraft && !dirty) {
            discardAndLeave(target);
            return;
        }
        bootstrap.Modal.getOrCreateInstance(byId("leaveDiagramModal"), { backdrop: "static" }).show();
    }

    async function discardAndLeave(target) {
        if (isDraft) {
            try {
                const response = await fetch(`/api/flows/${flowId}`, { method: "DELETE" });
                if (!response.ok && response.status !== 404) throw new Error("Could not discard the draft");
            } catch (error) {
                console.error(error);
                showToast(error.message || "Could not discard the draft");
                return;
            }
        }
        dirty = false;
        isDraft = false;
        window.location.assign(target);
    }

    function setSaveState(kind, message) {
        saveState.className = `fd-save-state is-${kind}`;
        saveState.lastElementChild.textContent = message;
    }

    function graphSnapshot() {
        return JSON.stringify(adapter.getGraph());
    }

    function pushHistory() {
        const snapshot = graphSnapshot();
        if (history[historyIndex] === snapshot) return;
        history = history.slice(0, historyIndex + 1);
        history.push(snapshot);
        if (history.length > 60) history.shift();
        historyIndex = history.length - 1;
        updateHistoryButtons();
    }

    function undo() {
        if (historyIndex <= 0) return;
        applyHistory(--historyIndex);
    }

    function redo() {
        if (historyIndex >= history.length - 1) return;
        applyHistory(++historyIndex);
    }

    function applyHistory(index) {
        applyingHistory = true;
        adapter.setGraph(JSON.parse(history[index]));
        applyingHistory = false;
        markDirty();
        updateCanvasHint();
        applyValidation(localValidation());
        updateHistoryButtons();
    }

    function updateHistoryButtons() {
        byId("undoButton").disabled = historyIndex <= 0;
        byId("redoButton").disabled = historyIndex >= history.length - 1;
    }

    function showNodeProperties(node) {
        if (!node) return;
        selectedNode = node;
        selectedConnection = null;
        emptyProperties.classList.add("d-none");
        workflowForm.classList.add("d-none");
        connectionForm.classList.add("d-none");
        nodeForm.classList.remove("d-none");
        byId("nodeTitle").value = node.title || "";
        byId("nodeDescription").value = node.description || "";
        byId("nodeType").value = node.type;
        byId("nodeTone").value = node.customProperties?.tone || "";
        byId("nodeLayer").value = node.customProperties?.layer || "";
        byId("nodePortLayout").value = node.customProperties?.portLayout || "horizontal";
        populateSectionOptions(node);
        byId("nodePresentationStyle").value = node.customProperties?.presentationStyle || "";
        byId("nodeNotes").value = node.customProperties?.notes || "";
        const isTask = isWorkCenter() && node.type === "Activity";
        byId("workCenterTaskProperties").classList.toggle("d-none", !isTask);
        byId("taskStepKey").value = node.customProperties?.stepKey || toKey(node.title);
        byId("taskOwner").value = node.customProperties?.owner || "";
        byId("taskOwnerType").value = node.customProperties?.ownerType || "User";
        byId("taskExpectedHours").value = node.customProperties?.expectedDurationHours || "24";
        byId("taskReminderAfterHours").value = node.customProperties?.reminderAfterHours || "";
        byId("taskReminderRepeatHours").value = node.customProperties?.reminderRepeatHours || "";
        byId("taskEscalationAfterHours").value = node.customProperties?.escalationAfterHours || "";
        byId("taskEscalationOwner").value = node.customProperties?.escalationOwner || "";
        byId("taskRequired").checked = String(node.customProperties?.required ?? "true").toLowerCase() !== "false";
        byId("taskEnabled").checked = String(node.customProperties?.enabled ?? "true").toLowerCase() !== "false";
        byId("newNodeComment").value = "";
        renderNodeComments(node.comments || []);
    }

    function populateSectionOptions(node) {
        const select = byId("nodeSectionId");
        select.replaceChildren(new Option("No section", ""));
        for (const section of adapter.getGraph().nodes.filter(item => item.type === "Section" && item.id !== node.id)) {
            select.add(new Option(section.title || "Untitled section", section.id));
        }
        select.value = node.customProperties?.sectionId || "";
        byId("nodeSectionField").classList.toggle("d-none", node.type === "Section");
    }

    function showConnectionProperties(connection) {
        selectedConnection = connection;
        selectedNode = null;
        emptyProperties.classList.add("d-none");
        workflowForm.classList.add("d-none");
        nodeForm.classList.add("d-none");
        connectionForm.classList.remove("d-none");
        byId("connectionLabel").value = connection.label || "";
        byId("connectionTone").value = connection.metadata?.tone || "";
        const graph = adapter.getGraph();
        const source = graph.nodes.find(node => node.id === connection.sourceNodeId);
        const target = graph.nodes.find(node => node.id === connection.targetNodeId);
        byId("connectionSummary").textContent = `${source?.title || "Source"} → ${target?.title || "Target"}`;
    }

    function clearProperties() {
        selectedNode = null;
        selectedConnection = null;
        nodeForm.classList.add("d-none");
        connectionForm.classList.add("d-none");
        workflowForm.classList.remove("d-none");
        emptyProperties.classList.add("d-none");
    }

    function updateSelectedNode() {
        if (!selectedNode) return;
        const customProperties = {
            ...(selectedNode.customProperties || {}),
            tone: byId("nodeTone").value,
            layer: byId("nodeLayer").value,
            portLayout: byId("nodePortLayout").value,
            sectionId: byId("nodeSectionId").value,
            presentationStyle: byId("nodePresentationStyle").value,
            notes: byId("nodeNotes").value
        };
        if (isWorkCenter() && selectedNode.type === "Activity") {
            Object.assign(customProperties, {
                stepKey: byId("taskStepKey").value,
                owner: byId("taskOwner").value,
                ownerType: byId("taskOwnerType").value,
                expectedDurationHours: byId("taskExpectedHours").value,
                reminderAfterHours: byId("taskReminderAfterHours").value,
                reminderRepeatHours: byId("taskReminderRepeatHours").value,
                escalationAfterHours: byId("taskEscalationAfterHours").value,
                escalationOwner: byId("taskEscalationOwner").value,
                required: String(byId("taskRequired").checked),
                enabled: String(byId("taskEnabled").checked)
            });
        }
        const changes = {
            title: byId("nodeTitle").value,
            description: byId("nodeDescription").value,
            customProperties
        };
        selectedNode = { ...selectedNode, ...changes };
        adapter.updateNode(selectedNode.id, changes);
    }

    function finalizeSelectedNodeTitle() {
        if (!selectedNode) return;
        const input = byId("nodeTitle");
        if (input.value.trim()) return;

        const config = nodeTypes[selectedNode.type] || nodeTypes.Process;
        input.value = config.defaultTitle || config.title || "Untitled";
        updateSelectedNode();
    }

    function addNodeComment() {
        if (!selectedNode) return;
        const input = byId("newNodeComment");
        const body = input.value.trim();
        if (!body) return;
        const comment = { id: crypto.randomUUID(), body, author: "You", createdAt: new Date().toISOString() };
        const comments = [...(selectedNode.comments || []), comment];
        selectedNode = { ...selectedNode, comments };
        adapter.updateNode(selectedNode.id, { comments });
        input.value = "";
        renderNodeComments(comments);
        showToast("Comment added — save to keep it");
    }

    function renderNodeComments(comments) {
        const list = byId("nodeComments");
        byId("nodeCommentCount").textContent = comments.length;
        list.replaceChildren();
        if (!comments.length) {
            const empty = document.createElement("p");
            empty.className = "fd-comments-empty";
            empty.textContent = "No comments yet.";
            list.appendChild(empty);
            return;
        }
        for (const comment of [...comments].sort((left, right) => new Date(left.createdAt) - new Date(right.createdAt))) {
            const item = document.createElement("article");
            item.className = "fd-comment";
            const header = document.createElement("div");
            header.className = "fd-comment-meta";
            const author = document.createElement("strong");
            author.textContent = comment.author || "Unknown user";
            const time = document.createElement("time");
            time.dateTime = comment.createdAt;
            time.textContent = new Date(comment.createdAt).toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
            header.append(author, time);
            const body = document.createElement("p");
            body.textContent = comment.body;
            item.append(header, body);
            list.appendChild(item);
        }
    }

    function changeSelectedNodeType() {
        if (!selectedNode) return;
        const graph = adapter.getGraph();
        const node = graph.nodes.find(item => item.id === selectedNode.id);
        if (!node) return;
        node.type = byId("nodeType").value;
        if (node.type === "Section") delete node.customProperties?.sectionId;
        adapter.setGraph(graph);
        selectedNode = node;
        window.requestAnimationFrame(() => adapter.selectNode(node.id));
        handleCanvasChange();
    }

    function handleKeyboard(event) {
        if (presentationMode) {
            if (event.key === "Escape") {
                if (document.fullscreenElement === root) return;
                event.preventDefault();
                exitPresentation(false);
            } else if (event.code === "Space" && !event.repeat) {
                event.preventDefault();
                adapter.setSpacePanning(true);
            }
            return;
        }
        if (!canEdit) {
            if (event.code === "Space" && !event.repeat) {
                event.preventDefault();
                adapter.setSpacePanning(true);
            }
            return;
        }
        const editing = ["INPUT", "TEXTAREA", "SELECT"].includes(document.activeElement?.tagName);
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "s") {
            event.preventDefault(); save(true); return;
        }
        if (editing) return;
        if (event.code === "Space" && !event.repeat) {
            event.preventDefault();
            adapter.setSpacePanning(true);
            return;
        }
        const key = event.key.toLowerCase();
        if ((event.ctrlKey || event.metaKey) && key === "z") { event.preventDefault(); event.shiftKey ? redo() : undo(); }
        else if ((event.ctrlKey || event.metaKey) && key === "y") { event.preventDefault(); redo(); }
        else if ((event.ctrlKey || event.metaKey) && key === "c" && selectedNode) { event.preventDefault(); clipboardNode = structuredClone(selectedNode); showToast("Node copied"); }
        else if ((event.ctrlKey || event.metaKey) && key === "v" && clipboardNode) { event.preventDefault(); pasteNode(); }
        else if ((event.ctrlKey || event.metaKey) && key === "d" && selectedNode) { event.preventDefault(); clipboardNode = structuredClone(selectedNode); pasteNode(); }
        else if (event.key === "Delete" || event.key === "Backspace") { if (adapter.deleteSelected()) event.preventDefault(); }
    }

    async function enterPresentation() {
        if (presentationMode) return;
        presentationMode = true;
        root.classList.add("is-presenting");
        byId("presentationButton").setAttribute("aria-pressed", "true");
        byId("toolbox").classList.remove("open");
        byId("propertiesPanel").classList.remove("open");
        byId("validationPopover").classList.add("d-none");
        adapter.setReadOnly(true);
        window.requestAnimationFrame(() => {
            adapter.fitToView();
            byId("exitPresentation").focus({ preventScroll: true });
        });

        if (document.fullscreenEnabled && !document.fullscreenElement) {
            try {
                await root.requestFullscreen();
            } catch {
                // The full-canvas presentation remains available when browser fullscreen is blocked.
            }
        }
    }

    function exitPresentation(exitFullscreen = true) {
        if (!presentationMode) return;
        if (exitFullscreen && document.fullscreenElement === root) {
            document.exitFullscreen()
                .then(() => { if (presentationMode) finishPresentationExit(); })
                .catch(finishPresentationExit);
            return;
        }
        finishPresentationExit();
    }

    function finishPresentationExit() {
        if (!presentationMode) return;
        presentationMode = false;
        root.classList.remove("is-presenting");
        byId("presentationButton").setAttribute("aria-pressed", "false");
        adapter.setSpacePanning(false);
        adapter.setReadOnly(false);
        window.requestAnimationFrame(() => {
            adapter.fitToView();
            byId("presentationButton").focus({ preventScroll: true });
        });
    }

    function pasteNode() {
        const copy = structuredClone(clipboardNode);
        copy.id = `node-${crypto.randomUUID()}`;
        copy.title = `${copy.title} copy`;
        copy.x += 30;
        copy.y += 30;
        clipboardNode = copy;
        adapter.addNode(copy.type, copy.x, copy.y, copy);
        showToast("Node duplicated");
    }

    function localValidation() {
        const graph = adapter.getGraph();
        const issues = [];
        const starts = graph.nodes.filter(node => node.type === "Start").length;
        if (isWorkCenter()) {
            if (starts !== 1) issues.push({ severity: "Error", code: "start-count", message: `Exactly one Start node is required; this workflow has ${starts}.` });
            if (!graph.nodes.some(node => node.type === "End")) issues.push({ severity: "Error", code: "missing-end", message: "At least one End node is required." });
            if (!graph.nodes.some(node => node.type === "Activity")) issues.push({ severity: "Error", code: "missing-task", message: "Add at least one Task." });
            graph.nodes.filter(node => node.type === "Activity" && !node.customProperties?.owner?.trim()).forEach(node => issues.push({ severity: "Warning", code: "missing-owner", message: `'${node.title}' needs an assignee before publishing.`, elementId: node.id }));
        } else {
            if (flow.diagramType !== "BusinessWorkflow" && starts !== 1) issues.push({ severity: "Warning", code: "start-count", message: `Exactly one Start node is recommended; this flow has ${starts}.` });
            if (flow.diagramType !== "BusinessWorkflow" && !graph.nodes.some(node => node.type === "End")) issues.push({ severity: "Warning", code: "missing-end", message: "At least one End node is recommended." });
        }
        const connected = new Set(graph.connections.flatMap(connection => [connection.sourceNodeId, connection.targetNodeId]));
        graph.nodes.filter(node => !["Section", "Annotation"].includes(node.type) && !connected.has(node.id)).forEach(node => issues.push({ severity: "Warning", code: "orphan-node", message: `'${node.title}' is not connected.`, elementId: node.id }));
        graph.nodes.filter(node => ["Decision", "Gateway", "ParallelGateway"].includes(node.type)).forEach(node => {
            const incoming = graph.connections.filter(connection => connection.targetNodeId === node.id).length;
            const outgoing = graph.connections.filter(connection => connection.sourceNodeId === node.id).length;
            const validParallelJoin = node.type === "ParallelGateway" && incoming >= 2;
            if (outgoing < 2 && !validParallelJoin) issues.push({ severity: "Warning", code: "incomplete-branch", message: `'${node.title}' should have at least two incoming or outgoing paths.`, elementId: node.id });
        });
        return issues;
    }

    function applyValidation(issues) {
        const summary = byId("validationSummary");
        const button = byId("validationButton");
        const icon = byId("validationIcon");
        const list = byId("validationIssues");
        const errors = issues.filter(issue => validationSeverity(issue) === "error").length;
        const warnings = issues.length - errors;
        button.classList.toggle("has-error", errors > 0);
        button.classList.toggle("has-warning", errors === 0 && warnings > 0);
        icon.textContent = errors ? "×" : warnings ? "!" : "✓";
        summary.textContent = errors ? `${errors} error${errors === 1 ? "" : "s"}` : warnings ? `${warnings} warning${warnings === 1 ? "" : "s"}` : "Diagram looks good";
        list.replaceChildren();
        if (!issues.length) {
            const message = document.createElement("p");
            message.className = "text-secondary mb-0";
            message.textContent = "No structural issues found.";
            list.appendChild(message);
            return;
        }
        for (const issue of issues) {
            const item = document.createElement("div");
            const severity = validationSeverity(issue);
            item.className = `fd-validation-item ${severity}`;
            item.innerHTML = `<span>${severity === "error" ? "×" : "!"}</span><span></span>`;
            item.lastElementChild.textContent = issue.message;
            list.appendChild(item);
        }
    }

    function validationSeverity(issue) {
        if (typeof issue?.severity === "string") return issue.severity.toLowerCase();
        return issue?.severity === 1 ? "error" : "warning";
    }

    function updateCanvasHint() {
        byId("canvasHint").classList.toggle("d-none", adapter.getGraph().nodes.length > 0);
    }

    function updateZoom(zoom) {
        byId("zoomReset").textContent = `${Math.round(Number(zoom) * 100)}%`;
    }

    function exportSvg() {
        const svg = buildExportSvg();
        downloadBlob(new Blob([svg], { type: "image/svg+xml;charset=utf-8" }), `${safeFileName()}.svg`);
        showToast("SVG exported");
    }

    function exportPng() {
        const svg = buildExportSvg();
        const blob = new Blob([svg], { type: "image/svg+xml;charset=utf-8" });
        const url = URL.createObjectURL(blob);
        const image = new Image();
        image.onload = () => {
            const canvas = document.createElement("canvas");
            canvas.width = Math.min(4096, image.width * 2);
            canvas.height = Math.min(4096, image.height * 2);
            const context = canvas.getContext("2d");
            context.scale(canvas.width / image.width, canvas.height / image.height);
            context.drawImage(image, 0, 0);
            URL.revokeObjectURL(url);
            canvas.toBlob(png => { if (png) downloadBlob(png, `${safeFileName()}.png`); }, "image/png");
            showToast("PNG exported");
        };
        image.src = url;
    }

    function buildExportSvg() {
        const graph = adapter.getGraph();
        const padding = 70;
        const minX = Math.min(0, ...graph.nodes.map(node => node.x));
        const minY = Math.min(0, ...graph.nodes.map(node => node.y));
        const maxX = Math.max(800, ...graph.nodes.map(node => node.x + (node.width || 184)));
        const maxY = Math.max(500, ...graph.nodes.map(node => node.y + (node.height || 76)));
        const width = maxX - minX + padding * 2;
        const height = maxY - minY + padding * 2;
        const offsetX = padding - minX;
        const offsetY = padding - minY;
        const nodes = new Map(graph.nodes.map(node => [node.id, node]));
        const colors = {
            Start: ["#07745f", "#dff4ed"], End: ["#ba3127", "#fbe7e4"],
            Process: ["#2563a9", "#e6f0fb"], Activity: ["#147549", "#e3f4ea"],
            Decision: ["#934900", "#fff0d4"], Gateway: ["#6840ad", "#f0e9fb"], ParallelGateway: ["#4338a8", "#ecebff"],
            InputOutput: ["#08728a", "#dff5f8"], ManualInput: ["#91440f", "#faeadf"],
            Document: ["#a13b68", "#fae6ef"],
            DataStore: ["#6740b5", "#eee9fb"], Subprocess: ["#3553a5", "#e7edfb"], Preparation: ["#4b5f76", "#e8eef3"],
            Connector: ["#53645f", "#e8eeec"], Event: ["#884487", "#f6e8f6"],
            Section: ["#1f5f8b", "#e5f1fa"], Annotation: ["#7c3a96", "#f7e9fb"]
        };
        const presentationTones = {
            red: ["#c93642", "#fdebed"], green: ["#168451", "#e5f6ec"],
            orange: ["#d85612", "#fff0e5"], purple: ["#7141ad", "#f2ebfb"],
            blue: ["#1769aa", "#e7f2fc"]
        };
        const connectorTones = { default: "#82958f", blue: "#1769aa", green: "#168451", red: "#c93642", orange: "#d85612", purple: "#7141ad" };
        const markers = Object.entries(connectorTones).map(([tone, color]) => `<marker id="fd-export-arrow-${tone}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="${color}"/></marker>`).join("");
        const parts = [`<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}"><defs>${markers}</defs><rect width="100%" height="100%" fill="#f8faf9"/><g font-family="Segoe UI,Arial,sans-serif">`];
        for (const section of graph.nodes.filter(node => node.type === "Section")) {
            const x = section.x + offsetX;
            const y = section.y + offsetY;
            const w = section.width || 420;
            const h = section.height || 300;
            const [color, fill] = presentationTones[String(section.customProperties?.tone || "").toLowerCase()] || colors.Section;
            parts.push(exportPresentationNode(section, x, y, w, h, color, fill));
        }
        for (const connection of graph.connections) {
            const source = nodes.get(connection.sourceNodeId);
            const target = nodes.get(connection.targetNodeId);
            if (!source || !target) continue;
            const start = exportPortPoint(source, "output", offsetX, offsetY);
            const end = exportPortPoint(target, "input", offsetX, offsetY);
            const bothVertical = source.customProperties?.portLayout === "vertical" && target.customProperties?.portLayout === "vertical";
            const curve = Math.max(45, Math.abs(bothVertical ? end.y - start.y : end.x - start.x) * .45);
            const path = bothVertical
                ? `M ${start.x} ${start.y} C ${start.x} ${start.y + curve}, ${end.x} ${end.y - curve}, ${end.x} ${end.y}`
                : `M ${start.x} ${start.y} C ${start.x + curve} ${start.y}, ${end.x - curve} ${end.y}, ${end.x} ${end.y}`;
            const tone = String(connection.metadata?.tone || "").toLowerCase();
            const resolvedTone = Object.hasOwn(connectorTones, tone) ? tone : "default";
            const connectorColor = connectorTones[resolvedTone];
            parts.push(`<path d="${path}" fill="none" stroke="${connectorColor}" stroke-width="3" marker-end="url(#fd-export-arrow-${resolvedTone})"/>`);
            if (connection.label) parts.push(`<text x="${(start.x + end.x) / 2}" y="${(start.y + end.y) / 2 - 7}" text-anchor="middle" font-size="12" font-weight="700" fill="${connectorColor}">${escapeXml(connection.label)}</text>`);
        }
        for (const node of graph.nodes) {
            if (node.type === "Section") continue;
            const x = node.x + offsetX;
            const y = node.y + offsetY;
            const w = node.width || 184;
            const h = node.height || 76;
            const [color, fill] = presentationTones[String(node.customProperties?.tone || "").toLowerCase()] || colors[node.type] || ["#596863", "#e9eeec"];
            if (node.type === "Annotation") {
                parts.push(exportPresentationNode(node, x, y, w, h, color, fill));
                continue;
            }
            const centerAligned = ["Decision", "Gateway", "ParallelGateway", "Connector", "Event"].includes(node.type);
            const presentationStyle = node.customProperties?.presentationStyle || "";
            if (["step", "card", "banner", "plain"].includes(presentationStyle)) {
                const titleColor = presentationStyle === "banner" ? "#fff" : color;
                const descriptionColor = presentationStyle === "banner" ? "#edf7ff" : "#52645f";
                const shape = exportNodeShape(node.type, x, y, w, h, color, fill, presentationStyle);
                const title = exportMultilineText(node.title, x + w / 2, y + 25, Math.max(15, Math.floor(w / 8)), 15, `text-anchor="middle" font-size="13" font-weight="700" fill="${titleColor}"`, 3);
                const description = node.description ? exportMultilineText(node.description, x + w / 2, y + 47, Math.max(18, Math.floor(w / 7)), 13, `text-anchor="middle" font-size="10" fill="${descriptionColor}"`, Math.max(1, Math.floor((h - 48) / 13))) : "";
                parts.push(`<g>${shape}${title}${description}</g>`);
                continue;
            }
            const textX = centerAligned ? x + w / 2 : x + 18;
            const anchor = centerAligned ? "middle" : "start";
            const description = node.description ? `<text x="${textX}" y="${y + h / 2 + 34}" text-anchor="${anchor}" font-size="10" fill="#52645f">${escapeXml(shorten(node.description, centerAligned ? 22 : 38))}</text>` : "";
            parts.push(`<g>${exportNodeShape(node.type, x, y, w, h, color, fill)}<text x="${textX}" y="${y + h / 2 - 5}" text-anchor="${anchor}" font-size="9" font-weight="700" fill="${color}" letter-spacing="1">${escapeXml(nodeTypes[node.type]?.title?.toUpperCase() || node.type.toUpperCase())}</text><text x="${textX}" y="${y + h / 2 + 17}" text-anchor="${anchor}" font-size="13" font-weight="700" fill="#172522">${escapeXml(shorten(node.title, centerAligned ? 18 : 28))}</text>${description}</g>`);
        }
        parts.push("</g></svg>");
        return parts.join("");
    }

    function exportPresentationNode(node, x, y, width, height, color, fill) {
        const presentationStyle = node.customProperties?.presentationStyle || "";
        if (node.type === "Section") {
            if (presentationStyle === "band") {
                const title = exportMultilineText(node.title, x + width / 2, y + 24, Math.max(18, Math.floor(width / 8)), 14, `text-anchor="middle" font-size="14" font-weight="700" fill="#fff"`, 2);
                const description = node.description ? exportMultilineText(node.description, x + width / 2, y + 45, Math.max(20, Math.floor(width / 7)), 12, `text-anchor="middle" font-size="10" font-weight="600" fill="#f5f7ff"`, 1) : "";
                return `<g><rect x="${x}" y="${y}" width="${width}" height="${height}" rx="12" fill="${fill}" stroke="${color}" stroke-width="2"/><path d="M ${x + 12} ${y} H ${x + width - 12} Q ${x + width} ${y} ${x + width} ${y + 12} V ${y + 58} H ${x} V ${y + 12} Q ${x} ${y} ${x + 12} ${y}" fill="${color}"/>${title}${description}</g>`;
            }
            const description = node.description ? `<text x="${x + 16}" y="${y + 62}" font-size="11" fill="#385c70">${escapeXml(shorten(node.description, 100))}</text>` : "";
            return `<g>${exportNodeShape(node.type, x, y, width, height, color, fill)}<rect x="${x + 14}" y="${y + 14}" width="${Math.min(width - 28, Math.max(110, node.title.length * 8 + 24))}" height="28" rx="5" fill="${color}"/><text x="${x + 26}" y="${y + 33}" font-size="13" font-weight="700" fill="#fff">${escapeXml(shorten(node.title, 48))}</text>${description}</g>`;
        }

        const centered = ["banner", "plain"].includes(presentationStyle);
        const textX = centered ? x + width / 2 : x + 16;
        const titleColor = presentationStyle === "banner" ? "#fff" : color;
        const descriptionColor = presentationStyle === "banner" ? "#edf7ff" : "#4e365a";
        const shape = exportNodeShape(node.type, x, y, width, height, color, fill, presentationStyle);
        const title = exportMultilineText(node.title, textX, y + 29, Math.max(18, Math.floor(width / 8)), 17, `${centered ? 'text-anchor="middle" ' : ""}font-size="16" font-weight="700" fill="${titleColor}"`, 3);
        const description = node.description ? exportMultilineText(node.description, textX, y + 57, Math.max(20, Math.floor(width / 7)), 14, `${centered ? 'text-anchor="middle" ' : ""}font-size="11" fill="${descriptionColor}"`, Math.max(1, Math.floor((height - 58) / 14))) : "";
        return `<g>${shape}${title}${description}</g>`;
    }

    function exportNodeShape(type, x, y, width, height, color, fill, presentationStyle = "") {
        if (presentationStyle === "plain") return "";
        if (presentationStyle === "banner") return `<rect x="${x}" y="${y}" width="${width}" height="${height}" rx="9" fill="${color}" stroke="${color}" stroke-width="2"/>`;
        if (presentationStyle === "step") return `<rect x="${x}" y="${y}" width="${width}" height="${height}" rx="8" fill="#fff" stroke="${color}" stroke-width="1.5"/>`;
        if (presentationStyle === "card") return `<rect x="${x}" y="${y}" width="${width}" height="${height}" rx="12" fill="${fill}" stroke="${color}" stroke-width="2"/>`;
        const common = `fill="${fill}" stroke="${color}" stroke-width="2"`;
        if (type === "Section") return `<rect x="${x}" y="${y}" width="${width}" height="${height}" rx="12" fill="${fill}" fill-opacity=".78" stroke="${color}" stroke-width="2" stroke-dasharray="8 5"/>`;
        if (type === "Annotation") return `<rect x="${x}" y="${y}" width="${width}" height="${height}" rx="9" ${common} stroke-dasharray="5 4"/>`;
        if (["Decision", "Gateway", "ParallelGateway"].includes(type)) {
            return `<polygon points="${x + width / 2},${y} ${x + width},${y + height / 2} ${x + width / 2},${y + height} ${x},${y + height / 2}" ${common}/>`;
        }
        if (type === "InputOutput") return `<polygon points="${x + 18},${y} ${x + width},${y} ${x + width - 18},${y + height} ${x},${y + height}" ${common}/>`;
        if (type === "ManualInput") return `<polygon points="${x + 14},${y + 12} ${x + width},${y} ${x + width - 14},${y + height} ${x},${y + height}" ${common}/>`;
        if (type === "Preparation") return `<polygon points="${x + 20},${y} ${x + width - 20},${y} ${x + width},${y + height / 2} ${x + width - 20},${y + height} ${x + 20},${y + height} ${x},${y + height / 2}" ${common}/>`;
        if (["Connector", "Event"].includes(type)) return `<ellipse cx="${x + width / 2}" cy="${y + height / 2}" rx="${width / 2}" ry="${height / 2}" ${common}/>`;
        if (type === "DataStore") return `<rect x="${x}" y="${y}" width="${width}" height="${height}" rx="${width / 2}" ry="${Math.min(15, height / 5)}" ${common}/><ellipse cx="${x + width / 2}" cy="${y + Math.min(15, height / 5)}" rx="${width / 2}" ry="${Math.min(15, height / 5)}" fill="none" stroke="${color}" stroke-width="2"/>`;
        if (type === "Document") return `<path d="M ${x} ${y} H ${x + width} V ${y + height - 12} Q ${x + width * .75} ${y + height - 24}, ${x + width * .5} ${y + height - 12} Q ${x + width * .25} ${y + height}, ${x} ${y + height - 12} Z" ${common}/>`;
        const radius = type === "Start" || type === "End" ? height / 2 : type === "Activity" ? 16 : 8;
        const extra = type === "Subprocess" ? `<path d="M ${x + 9} ${y} V ${y + height} M ${x + width - 9} ${y} V ${y + height}" stroke="${color}" stroke-width="1"/>` : "";
        return `<rect x="${x}" y="${y}" width="${width}" height="${height}" rx="${radius}" ${common}/>${extra}`;
    }

    function exportPortPoint(node, kind, offsetX, offsetY) {
        const width = node.width || 184;
        const height = node.height || 76;
        if (node.customProperties?.portLayout === "vertical") {
            return { x: node.x + width / 2 + offsetX, y: node.y + (kind === "output" ? height : 0) + offsetY };
        }
        return { x: node.x + (kind === "output" ? width : 0) + offsetX, y: node.y + height / 2 + offsetY };
    }

    function exportMultilineText(value, x, y, maxCharacters, lineHeight, attributes, maxLines) {
        const sourceLines = String(value || "").split(/\r?\n/);
        const lines = [];
        for (const sourceLine of sourceLines) {
            if (!sourceLine) { lines.push(""); continue; }
            let remaining = sourceLine;
            while (remaining.length > maxCharacters && lines.length < maxLines) {
                let split = remaining.lastIndexOf(" ", maxCharacters);
                if (split < Math.floor(maxCharacters * .55)) split = maxCharacters;
                lines.push(remaining.slice(0, split));
                remaining = remaining.slice(split).trimStart();
            }
            if (lines.length < maxLines) lines.push(remaining);
            if (lines.length >= maxLines) break;
        }
        if (lines.length === maxLines && sourceLines.join(" ").length > lines.join(" ").length) {
            lines[maxLines - 1] = shorten(lines[maxLines - 1], Math.max(2, maxCharacters));
        }
        return `<text x="${x}" y="${y}" ${attributes}>${lines.map((line, index) => `<tspan x="${x}" dy="${index === 0 ? 0 : lineHeight}">${escapeXml(line)}</tspan>`).join("")}</text>`;
    }

    function escapeXml(value) {
        return String(value ?? "").replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;").replaceAll("'", "&apos;");
    }

    function shorten(value, length) { return value.length <= length ? value : `${value.slice(0, length - 1)}…`; }
    function safeFileName() { return (nameInput.value || "flow-diagram").replace(/[\\/:*?"<>|]+/g, "-").trim() || "flow-diagram"; }
    function downloadBlob(blob, fileName) {
        const link = document.createElement("a");
        link.href = URL.createObjectURL(blob);
        link.download = fileName;
        link.click();
        window.setTimeout(() => URL.revokeObjectURL(link.href), 1000);
    }

    function showToast(message) {
        const toast = byId("toast");
        toast.textContent = message;
        toast.classList.add("show");
        window.clearTimeout(toastTimer);
        toastTimer = window.setTimeout(() => toast.classList.remove("show"), 2200);
    }
})();
