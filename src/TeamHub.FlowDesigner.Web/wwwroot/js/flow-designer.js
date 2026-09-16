(function () {
    "use strict";

    const root = document.getElementById("flowDesigner");
    if (!root) return;

    const byId = id => document.getElementById(id);
    const flowId = root.dataset.flowId;
    const loadUrl = root.dataset.loadUrl || `/api/flows/${flowId}`;
    const saveUrl = root.dataset.saveUrl || `/api/flows/${flowId}`;
    const linkTargetsUrl = root.dataset.linkTargetsUrl || `/api/flows/${flowId}/link-targets`;
    const reviewRequestId = root.dataset.reviewRequestId || "";
    const zoomStorageKey = "teamhub.flowDesigner.zoom.v1";
    const canEdit = root.dataset.canEdit === "true";
    const isPublishedView = root.dataset.publishedView === "true";
    const currentUser = root.dataset.currentUser || "You";
    const nameInput = byId("flowName");
    const saveState = byId("saveState");
    const saveButton = byId("saveButton");
    const saveButtonText = byId("saveButtonText");
    const requestPublicationButton = byId("requestPublicationButton");
    const requestPublicationButtonText = byId("requestPublicationButtonText");
    const saveTemplateForm = byId("saveTemplateForm");
    const nodeForm = byId("nodeProperties");
    const connectionForm = byId("connectionProperties");
    const workflowForm = byId("workflowProperties");
    const emptyProperties = byId("emptyProperties");
    const nodeReadOnlyDetails = byId("nodeReadOnlyDetails");
    const connectionReadOnlyDetails = byId("connectionReadOnlyDetails");
    const nodeTypes = window.FlowDesignerAdapters.nodeTypes;
    const diagramTypes = window.FlowDesignerAdapters.diagramTypes;
    const palettes = window.FlowDesignerAdapters.palettes;
    const normalizePortLayout = window.FlowDesignerAdapters.normalizePortLayout;
    const portSides = window.FlowDesignerAdapters.portSides;
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
    let linkTargets = [];
    let creatingChildForNodeId = null;
    let commentOverviewEntries = [];
    let commentsIndexLoading = false;
    let commentsIndexRefreshPending = false;
    let replyingToCommentId = null;

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
        const response = await fetch(loadUrl);
        if (!response.ok) throw new Error(`Load failed (${response.status})`);
        flow = await response.json();
        if (canEdit) await loadLinkTargets();
        isDraft = canEdit && Number(flow.version) <= 0;
        nameInput.value = flow.name;
        root.dataset.diagramType = flow.diagramType;
        root.classList.add(`fd-mode-${flow.diagramType.toLowerCase()}`);
        adapter.setGraph(flow);
        const storedZoom = readStoredZoom();
        if (!adapter.setZoom(storedZoom)) {
            updateZoom(adapter.getZoom());
        }
        adapter.setReadOnly(!canEdit);
        nameInput.readOnly = !canEdit;
        initializeWorkflowProperties();
        history = [graphSnapshot()];
        historyIndex = 0;
        updateHistoryButtons();
        updateCanvasHint();
        applyValidation(localValidation());
        setSaveState(canEdit ? (isDraft ? "unsaved" : "saved") : "saved", canEdit ? (isDraft ? "Not saved yet" : "Saved") : "View only");
        bindUi();
        clearProperties();
        if (root.dataset.startPresentation === "true") await enterPresentation(false);
        refreshCommentsOverview();
        window.requestAnimationFrame(focusCommentFromUrl);
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
        if (canEdit) canvas.addEventListener("pointerdown", finalizeSelectedNodeTitle, true);
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
            const linkedNode = adapter.getNodeFromElement(event.target);
            if (linkedNode?.childFlowId) {
                event.preventDefault();
                openChildFlowById(linkedNode.childFlowId);
                return;
            }
            if (!canEdit || presentationMode) return;
            if (event.target !== canvas && !event.target.classList.contains("drawflow")) return;
            const point = adapter.screenToCanvas(event.clientX, event.clientY);
            const position = nodePosition(activePalette().primary, point);
            adapter.addNode(activePalette().primary, position.x, position.y);
        });
        canvas.addEventListener("click", event => {
            if (canEdit && !presentationMode) return;
            const linkedNode = adapter.getNodeFromElement(event.target);
            if (!presentationMode) {
                if (linkedNode) adapter.selectNode(linkedNode.id);
                return;
            }
            if (!linkedNode) {
                closePresentationDetails();
                return;
            }
            adapter.selectNode(linkedNode.id);
            showPresentationNodeDetails(linkedNode);
        });

        saveButton.addEventListener("click", () => save(true));
        requestPublicationButton?.addEventListener("click", requestPublication);
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
        byId("presentationButton").addEventListener("click", () => enterPresentation());
        byId("commentsButton").addEventListener("click", () => toggleCommentsPanel());
        byId("presentationCommentsButton").addEventListener("click", () => toggleCommentsPanel());
        byId("closeCommentsPanel").addEventListener("click", () => closeCommentsPanel());
        byId("commentsScope").addEventListener("change", renderCommentsOverview);
        byId("commentsSearch").addEventListener("input", renderCommentsOverview);
        byId("exitPresentation").addEventListener("click", () => exitPresentation());
        byId("presentationBack").addEventListener("click", event => {
            event.preventDefault();
            requestLeave(event.currentTarget.href);
        });
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
        ["nodeInputPins", "nodeOutputPins"].forEach(id => byId(id).addEventListener("change", updateConnectorPins));
        byId("nodeChildFlowId").addEventListener("change", updateSelectedChildFlow);
        byId("openChildFlow").addEventListener("click", () => {
            if (selectedNode?.childFlowId) openChildFlowById(selectedNode.childFlowId);
        });
        byId("openReadOnlyChildFlow").addEventListener("click", () => {
            if (selectedNode?.childFlowId) openChildFlowById(selectedNode.childFlowId);
        });
        byId("closePresentationDetails").addEventListener("click", closePresentationDetails);
        byId("openPresentationChildFlow").addEventListener("click", () => {
            if (selectedNode?.childFlowId) openChildFlowById(selectedNode.childFlowId);
        });
        byId("unlinkChildFlow").addEventListener("click", unlinkSelectedChildFlow);
        byId("createChildFlowModal").addEventListener("show.bs.modal", () => {
            creatingChildForNodeId = selectedNode?.id || null;
            byId("childFlowName").value = selectedNode?.title?.trim() || "Detailed diagram";
        });
        byId("createChildFlowForm").addEventListener("submit", createChildFlow);
        ["taskStepKey", "taskOwner", "taskExpectedHours", "taskReminderAfterHours", "taskReminderRepeatHours", "taskEscalationAfterHours", "taskEscalationOwner"].forEach(id => byId(id).addEventListener("input", updateSelectedNode));
        ["taskOwnerType", "taskRequired", "taskEnabled"].forEach(id => byId(id).addEventListener("change", updateSelectedNode));
        byId("addNodeComment").addEventListener("click", addNodeComment);
        byId("cancelNodeCommentReply").addEventListener("click", cancelNodeCommentReply);
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
            if (!canEdit || target.origin !== window.location.origin || target.href === window.location.href || (!dirty && !isDraft)) return;
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
            if (!canEdit || (!dirty && !isDraft)) return;
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

    async function loadLinkTargets() {
        try {
            const response = await fetch(linkTargetsUrl);
            if (!response.ok) throw new Error(`Link target load failed (${response.status})`);
            linkTargets = await response.json();
        } catch (error) {
            console.error(error);
            linkTargets = [];
            showToast("Existing diagrams could not be loaded.");
        }
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
        if (!canEdit || presentationMode) return;
        updateCanvasHint();
        applyValidation(localValidation());
        markDirty();
        if (applyingHistory) return;
        window.clearTimeout(historyTimer);
        historyTimer = window.setTimeout(pushHistory, 100);
    }

    function markDirty() {
        if (!canEdit || presentationMode) return;
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
            const response = await fetch(saveUrl, {
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

    async function requestPublication() {
        if (!requestPublicationButton || saving || !canEdit) return;
        if (!(await save(false))) return;

        requestPublicationButton.disabled = true;
        requestPublicationButtonText.textContent = "Submitting...";
        try {
            const response = await fetch(`/api/flows/${flowId}/publication-requests`, { method: "POST" });
            const result = await response.json().catch(() => ({}));
            if (!response.ok) throw new Error(result.message || "The publication request could not be submitted.");
            requestPublicationButtonText.textContent = "Pending admin review";
            showToast(`Publication request sent for the complete "${result.flowName || flow.name}" hierarchy`);
        } catch (error) {
            console.error(error);
            requestPublicationButton.disabled = false;
            requestPublicationButtonText.textContent = "Request publication";
            showToast(error.message || "The publication request could not be submitted.");
        }
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
        if (!canEdit) {
            window.location.assign(target);
            return;
        }
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
        if (selectedNode?.id !== node.id) cancelNodeCommentReply();
        selectedNode = node;
        selectedConnection = null;
        emptyProperties.classList.add("d-none");
        workflowForm.classList.add("d-none");
        connectionForm.classList.add("d-none");
        connectionReadOnlyDetails.classList.add("d-none");
        if (!canEdit) {
            nodeForm.classList.add("d-none");
            nodeReadOnlyDetails.classList.remove("d-none");
            const config = nodeTypes[node.type] || nodeTypes.Process;
            byId("nodeReadOnlyType").textContent = config.title || node.type || "Selected symbol";
            byId("nodeReadOnlyTitle").textContent = node.title || "Untitled symbol";
            byId("nodeReadOnlyDescription").textContent = node.description?.trim() || "No description provided.";
            byId("nodeReadOnlyNotes").textContent = node.customProperties?.notes?.trim() || "No notes provided.";
            byId("openReadOnlyChildFlow").classList.toggle("d-none", !node.childFlowId);
            renderNodeComments(node.comments || [], {
                listId: "nodeReadOnlyComments",
                countId: "nodeReadOnlyCommentCount",
                allowActions: false
            });
            if (window.matchMedia("(max-width: 1040px)").matches) byId("propertiesPanel").classList.add("open");
            return;
        }
        nodeReadOnlyDetails.classList.add("d-none");
        nodeForm.classList.remove("d-none");
        byId("nodeTitle").value = node.title || "";
        byId("nodeDescription").value = node.description || "";
        byId("nodeType").value = node.type;
        byId("nodeTone").value = node.customProperties?.tone || "";
        byId("nodeLayer").value = node.customProperties?.layer || "";
        byId("nodePortLayout").value = normalizePortLayout(node.customProperties?.portLayout);
        configureConnectorPinControls(node);
        populateSectionOptions(node);
        byId("nodePresentationStyle").value = node.customProperties?.presentationStyle || "";
        byId("nodeNotes").value = node.customProperties?.notes || "";
        const supportsChildFlow = node.type !== "Section" && node.type !== "Annotation";
        byId("nodeChildFlowSection").classList.toggle("d-none", !supportsChildFlow);
        if (supportsChildFlow) populateChildFlowOptions(node);
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
        byId("newNodeCommentPublic").checked = isPublishedView;
        renderNodeComments(node.comments || []);
    }

    function configureConnectorPinControls(node) {
        const config = nodeTypes[node.type] || nodeTypes.Process;
        configureConnectorPinSelect(byId("nodeInputPins"), config.inputs, node.customProperties?.inputPins);
        configureConnectorPinSelect(byId("nodeOutputPins"), config.outputs, node.customProperties?.outputPins);
    }

    function configureConnectorPinSelect(select, defaultCount, configuredCount) {
        const supported = defaultCount > 0;
        select.disabled = !supported;
        select.options[0].textContent = supported ? `Automatic (${defaultCount})` : "Not available";
        const count = Number(configuredCount);
        select.value = supported && Number.isInteger(count) && count >= 1 && count <= 6
            ? String(count)
            : "";
    }

    function updateConnectorPins(event) {
        if (!selectedNode) return;
        const kind = event.currentTarget.id === "nodeInputPins" ? "input" : "output";
        const config = nodeTypes[selectedNode.type] || nodeTypes.Process;
        const defaultCount = kind === "input" ? config.inputs : config.outputs;
        const desiredCount = adapter.portCount(defaultCount, event.currentTarget.value);
        const currentCount = adapter.getPortCount(selectedNode.id, kind);

        if (desiredCount < currentCount) {
            const prefix = `${kind}_`;
            const affectedConnections = adapter.getGraph().connections.filter(connection => {
                const ownsPort = kind === "input"
                    ? connection.targetNodeId === selectedNode.id
                    : connection.sourceNodeId === selectedNode.id;
                const port = kind === "input" ? connection.targetPort : connection.sourcePort;
                return ownsPort && port?.startsWith(prefix) && Number(port.slice(prefix.length)) > desiredCount;
            });
            if (affectedConnections.length > 0
                && !window.confirm(`Reducing ${kind} pins will remove ${affectedConnections.length} connected path${affectedConnections.length === 1 ? "" : "s"}. Continue?`)) {
                configureConnectorPinControls(selectedNode);
                return;
            }
        }

        updateSelectedNode();
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

    function populateChildFlowOptions(node) {
        const select = byId("nodeChildFlowId");
        select.replaceChildren(new Option("No detailed diagram", ""));
        for (const target of linkTargets) {
            select.add(new Option(`${target.name} (${diagramTypes[target.diagramType]?.title || target.diagramType})`, target.id));
        }
        if (node.childFlowId && !linkTargets.some(target => target.id === node.childFlowId)) {
            select.add(new Option("Linked diagram is unavailable", node.childFlowId));
        }
        select.value = node.childFlowId || "";
        const hasChild = Boolean(node.childFlowId);
        byId("openChildFlow").disabled = !hasChild;
        byId("unlinkChildFlow").disabled = !hasChild;
    }

    function showConnectionProperties(connection) {
        selectedConnection = connection;
        selectedNode = null;
        emptyProperties.classList.add("d-none");
        workflowForm.classList.add("d-none");
        nodeForm.classList.add("d-none");
        nodeReadOnlyDetails.classList.add("d-none");
        const graph = adapter.getGraph();
        const source = graph.nodes.find(node => node.id === connection.sourceNodeId);
        const target = graph.nodes.find(node => node.id === connection.targetNodeId);
        const summary = `${source?.title || "Source"} → ${target?.title || "Target"}`;
        if (!canEdit) {
            connectionForm.classList.add("d-none");
            connectionReadOnlyDetails.classList.remove("d-none");
            byId("connectionReadOnlyTitle").textContent = connection.label || "Unlabeled connector";
            byId("connectionReadOnlySummary").textContent = summary;
            if (window.matchMedia("(max-width: 1040px)").matches) byId("propertiesPanel").classList.add("open");
            return;
        }
        connectionReadOnlyDetails.classList.add("d-none");
        connectionForm.classList.remove("d-none");
        byId("connectionLabel").value = connection.label || "";
        byId("connectionTone").value = connection.metadata?.tone || "";
        byId("connectionSummary").textContent = summary;
    }

    function clearProperties() {
        cancelNodeCommentReply();
        selectedNode = null;
        selectedConnection = null;
        nodeForm.classList.add("d-none");
        connectionForm.classList.add("d-none");
        nodeReadOnlyDetails.classList.add("d-none");
        connectionReadOnlyDetails.classList.add("d-none");
        workflowForm.classList.toggle("d-none", !canEdit);
        emptyProperties.classList.toggle("d-none", canEdit);
    }
    function updateSelectedNode() {
        if (!canEdit || presentationMode || !selectedNode) return;
        const customProperties = {
            ...(selectedNode.customProperties || {}),
            tone: byId("nodeTone").value,
            layer: byId("nodeLayer").value,
            portLayout: byId("nodePortLayout").value,
            sectionId: byId("nodeSectionId").value,
            presentationStyle: byId("nodePresentationStyle").value,
            notes: byId("nodeNotes").value
        };
        delete customProperties.inputPins;
        delete customProperties.outputPins;
        if (!byId("nodeInputPins").disabled && byId("nodeInputPins").value) customProperties.inputPins = byId("nodeInputPins").value;
        if (!byId("nodeOutputPins").disabled && byId("nodeOutputPins").value) customProperties.outputPins = byId("nodeOutputPins").value;

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

    function updateSelectedChildFlow() {
        if (!selectedNode) return;
        const childFlowId = byId("nodeChildFlowId").value || null;
        selectedNode = { ...selectedNode, childFlowId };
        adapter.updateNode(selectedNode.id, { childFlowId });
        populateChildFlowOptions(selectedNode);
    }

    function unlinkSelectedChildFlow() {
        if (!selectedNode?.childFlowId) return;
        selectedNode = { ...selectedNode, childFlowId: null };
        adapter.updateNode(selectedNode.id, { childFlowId: null });
        populateChildFlowOptions(selectedNode);
        showToast("Detailed diagram link removed");
    }

    async function openChildFlowById(childFlowId) {
        if (!childFlowId) return;
        if (canEdit && !(await save(false))) return;

        const ancestors = (root.dataset.trail || "").split(",").filter(Boolean);
        if (!ancestors.includes(flowId)) ancestors.push(flowId);
        const nextTrail = ancestors.slice(-20).join(",");
        const parameters = new URLSearchParams({ trail: nextTrail });
        if (root.dataset.publishedView === "true") parameters.set("published", "true");
        else if (reviewRequestId) parameters.set("requestId", reviewRequestId);
        if (presentationMode) parameters.set("present", "true");
        window.location.assign(`/flows/${childFlowId}/edit?${parameters}`);
    }

    async function createChildFlow(event) {
        event.preventDefault();
        const parentNode = creatingChildForNodeId ? adapter.getNode(creatingChildForNodeId) : null;
        if (!parentNode) {
            showToast("Select a node before creating a detailed diagram.");
            return;
        }

        const button = byId("confirmCreateChildFlow");
        button.disabled = true;
        button.textContent = "Creating...";
        try {
            const response = await fetch(`/api/flows/${flowId}/children`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ name: byId("childFlowName").value.trim() })
            });
            const child = await response.json().catch(() => ({}));
            if (!response.ok) throw new Error(child.message || "The detailed diagram could not be created.");

            linkTargets.push({ id: child.id, name: child.name, diagramType: child.diagramType });
            adapter.updateNode(parentNode.id, { childFlowId: child.id });
            if (selectedNode?.id === parentNode.id) {
                selectedNode = { ...selectedNode, childFlowId: child.id };
                populateChildFlowOptions(selectedNode);
            }
            if (!(await save(false))) return;

            bootstrap.Modal.getInstance(byId("createChildFlowModal"))?.hide();
            await openChildFlowById(child.id);
        } catch (error) {
            console.error(error);
            showToast(error.message || "The detailed diagram could not be created.");
        } finally {
            button.disabled = false;
            button.textContent = "Create and open";
        }
    }

    function finalizeSelectedNodeTitle() {
        if (!canEdit || presentationMode || !selectedNode) return;
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
        const parent = (selectedNode.comments || []).find(comment => comment.id === replyingToCommentId);
        const comment = {
            id: crypto.randomUUID(),
            parentCommentId: parent?.parentCommentId || parent?.id || null,
            body,
            author: currentUser,
            createdAt: new Date().toISOString(),
            isPublic: parent ? Boolean(parent.isPublic) : byId("newNodeCommentPublic").checked
        };
        const comments = [...(selectedNode.comments || []), comment];
        updateSelectedNodeComments(comments);
        input.value = "";
        cancelNodeCommentReply();
        byId("newNodeCommentPublic").checked = isPublishedView;
        showToast("Comment added - save to keep it");
    }

    function updateSelectedNodeComments(comments) {
        if (!selectedNode) return;
        selectedNode = { ...selectedNode, comments };
        adapter.updateNode(selectedNode.id, { comments });
        renderNodeComments(comments);
        refreshCommentsOverview();
    }

    function beginNodeCommentReply(comment) {
        if (!canEdit || presentationMode || !selectedNode) return;
        const rootComment = (selectedNode.comments || []).find(item => item.id === (comment.parentCommentId || comment.id)) || comment;
        replyingToCommentId = rootComment.id;
        byId("nodeCommentReplyText").textContent = `Replying to ${rootComment.author || "Unknown user"}`;
        byId("nodeCommentReplyContext").classList.remove("d-none");
        byId("newNodeCommentPublic").checked = Boolean(rootComment.isPublic);
        const input = byId("newNodeComment");
        input.placeholder = "Write a reply";
        input.focus();
    }

    function cancelNodeCommentReply() {
        replyingToCommentId = null;
        byId("nodeCommentReplyContext")?.classList.add("d-none");
        const input = byId("newNodeComment");
        if (input) input.placeholder = "Add a comment about this symbol";
    }

    function ownsNodeComment(comment) {
        return String(comment?.author || "").localeCompare(currentUser, undefined, { sensitivity: "accent" }) === 0;
    }

    function editNodeComment(comment) {
        if (!selectedNode || !ownsNodeComment(comment)) return;
        const item = byId("nodeComments").querySelector(`[data-comment-id="${CSS.escape(comment.id)}"]`);
        if (!item) return;

        const body = item.querySelector(".fd-comment-body");
        const actions = item.querySelector(".fd-comment-actions");
        const editor = document.createElement("textarea");
        editor.className = "form-control form-control-sm fd-comment-editor";
        editor.rows = 3;
        editor.maxLength = 2000;
        editor.value = comment.body || "";

        const visibility = document.createElement("div");
        visibility.className = "form-check mt-2";
        const publicInput = document.createElement("input");
        publicInput.className = "form-check-input";
        publicInput.type = "checkbox";
        publicInput.id = `comment-public-${comment.id}`;
        publicInput.checked = Boolean(comment.isPublic);
        const publicLabel = document.createElement("label");
        publicLabel.className = "form-check-label fd-comment-visibility-label";
        publicLabel.htmlFor = publicInput.id;
        publicLabel.textContent = "Show in published diagram";
        visibility.append(publicInput, publicLabel);

        const save = document.createElement("button");
        save.className = "btn btn-sm btn-primary";
        save.type = "button";
        save.textContent = "Save";
        save.addEventListener("click", () => {
            const nextBody = editor.value.trim();
            if (!nextBody) {
                editor.setCustomValidity("A comment is required.");
                editor.reportValidity();
                return;
            }
            const comments = (selectedNode.comments || []).map(existing =>
                existing.id === comment.id
                    ? { ...existing, body: nextBody, isPublic: publicInput.checked }
                    : existing);
            updateSelectedNodeComments(comments);
            showToast("Comment updated - save to keep it");
        });

        const cancel = document.createElement("button");
        cancel.className = "btn btn-sm btn-light";
        cancel.type = "button";
        cancel.textContent = "Cancel";
        cancel.addEventListener("click", () => renderNodeComments(selectedNode?.comments || []));

        body.replaceWith(editor);
        editor.insertAdjacentElement("afterend", visibility);
        actions.replaceChildren(save, cancel);
        editor.focus();
        editor.setSelectionRange(editor.value.length, editor.value.length);
    }

    function deleteNodeComment(comment) {
        if (!selectedNode || !ownsNodeComment(comment) || !window.confirm("Delete this comment?")) return;
        const comments = (selectedNode.comments || []).filter(existing => existing.id !== comment.id);
        updateSelectedNodeComments(comments);
        showToast("Comment deleted - save to keep it");
    }

    function renderNodeComments(comments, options = {}) {
        const list = byId(options.listId || "nodeComments");
        byId(options.countId || "nodeCommentCount").textContent = comments.length;
        const allowActions = options.allowActions ?? canEdit;
        list.replaceChildren();
        if (!comments.length) {
            const empty = document.createElement("p");
            empty.className = "fd-comments-empty";
            empty.textContent = "No comments yet.";
            list.appendChild(empty);
            return;
        }
        const commentIds = new Set(comments.map(comment => comment.id));
        for (const comment of threadedComments(comments)) {
            const item = document.createElement("article");
            item.className = `fd-comment${comment.parentCommentId && commentIds.has(comment.parentCommentId) ? " is-reply" : ""}`;
            item.dataset.commentId = comment.id;
            const header = document.createElement("div");
            header.className = "fd-comment-meta";
            const identity = document.createElement("span");
            identity.className = "fd-comment-identity";
            const author = document.createElement("strong");
            author.textContent = comment.author || "Unknown user";
            const visibility = document.createElement("span");
            visibility.className = `fd-comment-visibility ${comment.isPublic ? "is-public" : ""}`;
            visibility.textContent = comment.isPublic ? "Public" : "Private";
            identity.append(author, visibility);
            const time = document.createElement("time");
            time.dateTime = comment.createdAt;
            time.textContent = new Date(comment.createdAt).toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
            header.append(identity, time);
            const body = document.createElement("p");
            body.className = "fd-comment-body";
            appendLinkedCommentText(body, comment.body || "");
            const actions = document.createElement("div");
            actions.className = "fd-comment-actions";
            const reply = document.createElement("button");
            reply.className = "btn btn-sm btn-link";
            reply.type = "button";
            reply.textContent = "Reply";
            reply.setAttribute("aria-label", `Reply to ${comment.author || "Unknown user"}`);
            reply.addEventListener("click", () => beginNodeCommentReply(comment));
            const edit = document.createElement("button");
            edit.className = "btn btn-sm btn-link";
            edit.type = "button";
            edit.textContent = "Edit";
            edit.setAttribute("aria-label", `Edit comment by ${comment.author || "Unknown user"}`);
            edit.addEventListener("click", () => editNodeComment(comment));
            const remove = document.createElement("button");
            remove.className = "btn btn-sm btn-link text-danger";
            remove.type = "button";
            remove.textContent = "Delete";
            remove.setAttribute("aria-label", `Delete comment by ${comment.author || "Unknown user"}`);
            remove.addEventListener("click", () => deleteNodeComment(comment));
            const ownsComment = allowActions && ownsNodeComment(comment);
            if (allowActions) actions.append(reply);
            if (ownsComment) actions.append(edit, remove);
            item.append(header, body);
            if (actions.childElementCount) item.append(actions);
            list.appendChild(item);
        }
    }

    function threadedComments(comments) {
        const sorted = [...comments].sort((left, right) => new Date(left.createdAt) - new Date(right.createdAt));
        const ids = new Set(sorted.map(comment => comment.id));
        const roots = sorted.filter(comment => !comment.parentCommentId || !ids.has(comment.parentCommentId));
        const replies = new Map();
        for (const comment of sorted) {
            if (!comment.parentCommentId || !ids.has(comment.parentCommentId)) continue;
            const items = replies.get(comment.parentCommentId) || [];
            items.push(comment);
            replies.set(comment.parentCommentId, items);
        }
        return roots.flatMap(comment => [comment, ...(replies.get(comment.id) || [])]);
    }
    function appendLinkedCommentText(container, value) {
        const urlPattern = /https?:\/\/[^\s]+/g;
        let offset = 0;
        for (const match of value.matchAll(urlPattern)) {
            if (match.index > offset) container.append(document.createTextNode(value.slice(offset, match.index)));
            const link = document.createElement("a");
            link.href = match[0];
            link.target = "_blank";
            link.rel = "noopener noreferrer";
            link.textContent = match[0];
            container.append(link);
            offset = match.index + match[0].length;
        }
        if (offset < value.length) container.append(document.createTextNode(value.slice(offset)));
    }

    async function toggleCommentsPanel(forceOpen) {
        const panel = byId("commentsPanel");
        const open = forceOpen ?? !panel.classList.contains("open");
        if (!open) {
            closeCommentsPanel();
            return;
        }

        closePresentationDetails();
        panel.classList.add("open");
        panel.setAttribute("aria-hidden", "false");
        byId("commentsButton").setAttribute("aria-expanded", "true");
        byId("presentationCommentsButton").setAttribute("aria-expanded", "true");
        await refreshCommentsOverview();
        byId("commentsSearch").focus({ preventScroll: true });
    }

    function closeCommentsPanel() {
        const panel = byId("commentsPanel");
        panel.classList.remove("open");
        panel.setAttribute("aria-hidden", "true");
        byId("commentsButton").setAttribute("aria-expanded", "false");
        byId("presentationCommentsButton").setAttribute("aria-expanded", "false");
    }

    async function refreshCommentsOverview() {
        if (!flow) return;
        if (commentsIndexLoading) {
            commentsIndexRefreshPending = true;
            return;
        }
        commentsIndexLoading = true;
        const status = byId("commentsOverviewStatus");
        status.textContent = "Loading comments...";
        let incomplete = false;
        const entries = [];
        const visited = new Set();
        const ancestors = (root.dataset.trail || "").split(",").filter(Boolean);
        const hierarchyRootId = ancestors[0] || flowId;

        async function visit(id, ancestorIds) {
            const key = String(id).toLowerCase();
            if (visited.has(key)) return;
            visited.add(key);

            let definition;
            try {
                definition = await loadCommentFlow(id);
            } catch (error) {
                console.error(error);
                incomplete = true;
                return;
            }

            for (const node of definition.nodes || []) {
                for (const comment of node.comments || []) {
                    if ((presentationMode || isPublishedView) && !comment.isPublic) continue;
                    entries.push({
                        flowId: String(definition.id),
                        flowName: definition.name || "Untitled diagram",
                        nodeId: node.id,
                        nodeTitle: node.title || "Untitled symbol",
                        comment,
                        ancestorIds: [...ancestorIds]
                    });
                }
            }

            const childIds = [...new Set((definition.nodes || [])
                .map(node => node.childFlowId)
                .filter(Boolean)
                .map(String))];
            for (const childId of childIds) {
                await visit(childId, [...ancestorIds, String(definition.id)]);
            }
        }

        try {
            await visit(hierarchyRootId, []);
            if (!visited.has(String(flowId).toLowerCase())) await visit(flowId, ancestors);
            commentOverviewEntries = entries.sort((left, right) =>
                new Date(right.comment.createdAt) - new Date(left.comment.createdAt));
            updateCommentBadges();
            renderCommentsOverview();
            status.textContent = incomplete
                ? `${commentOverviewEntries.length} visible comments. Some linked diagrams could not be loaded.`
                : `${commentOverviewEntries.length} visible comment${commentOverviewEntries.length === 1 ? "" : "s"} across the diagram.`;
        } finally {
            commentsIndexLoading = false;
            if (commentsIndexRefreshPending) {
                commentsIndexRefreshPending = false;
                refreshCommentsOverview();
            }
        }
    }

    async function loadCommentFlow(id) {
        if (String(id).toLowerCase() === String(flowId).toLowerCase()) {
            const graph = adapter.getGraph();
            return { ...flow, id: flowId, name: nameInput.value.trim() || flow.name, nodes: graph.nodes, connections: graph.connections };
        }

        let url;
        if (isPublishedView) url = `/api/flows/published/${id}`;
        else if (reviewRequestId) url = `/api/flows/publication-requests/${reviewRequestId}/snapshot?flowId=${encodeURIComponent(id)}`;
        else url = `/api/flows/${id}`;
        const response = await fetch(url);
        if (!response.ok) throw new Error(`Comment diagram load failed (${response.status})`);
        return response.json();
    }

    function updateCommentBadges() {
        for (const id of ["commentsOverviewCount", "presentationCommentsOverviewCount"]) {
            const badge = byId(id);
            badge.textContent = commentOverviewEntries.length;
            badge.classList.toggle("d-none", commentOverviewEntries.length === 0);
        }
    }

    function renderCommentsOverview() {
        const container = byId("commentsOverview");
        const scope = byId("commentsScope").value;
        const search = byId("commentsSearch").value.trim().toLocaleLowerCase();
        const visible = commentOverviewEntries.filter(entry => {
            if (scope === "page" && String(entry.flowId).toLowerCase() !== String(flowId).toLowerCase()) return false;
            if (scope === "mine" && !ownsNodeComment(entry.comment)) return false;
            if (!search) return true;
            return [entry.flowName, entry.nodeTitle, entry.comment.author, entry.comment.body]
                .some(value => String(value || "").toLocaleLowerCase().includes(search));
        });

        container.replaceChildren();
        if (!visible.length) {
            const empty = document.createElement("div");
            empty.className = "fd-comments-overview-empty";
            empty.textContent = commentOverviewEntries.length
                ? "No comments match the selected filter."
                : "No comments are available in this diagram.";
            container.appendChild(empty);
            return;
        }

        for (const entry of visible) {
            const item = document.createElement("article");
            item.className = "fd-comments-overview-item";
            const target = document.createElement("button");
            target.className = "fd-comments-overview-target";
            target.type = "button";
            target.addEventListener("click", () => openCommentTarget(entry, false));

            const location = document.createElement("div");
            location.className = "fd-comments-overview-location";
            const page = document.createElement("span");
            page.textContent = entry.flowName;
            const separator = document.createElement("span");
            separator.textContent = "/";
            separator.setAttribute("aria-hidden", "true");
            const node = document.createElement("span");
            node.textContent = entry.nodeTitle;
            location.append(page, separator, node);

            const meta = document.createElement("div");
            meta.className = "fd-comments-overview-meta";
            const author = document.createElement("span");
            author.textContent = entry.comment.author || "Unknown user";
            const time = document.createElement("time");
            time.dateTime = entry.comment.createdAt;
            time.textContent = new Date(entry.comment.createdAt).toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
            meta.append(author, time);

            const body = document.createElement("p");
            body.className = "fd-comments-overview-body";
            body.textContent = entry.comment.body || "";
            target.append(location, meta, body);
            item.appendChild(target);

            if (canEdit && !presentationMode) {
                const actions = document.createElement("div");
                actions.className = "fd-comments-overview-actions";
                const reply = document.createElement("button");
                reply.className = "btn btn-sm btn-link";
                reply.type = "button";
                reply.textContent = "Open and reply";
                reply.addEventListener("click", () => openCommentTarget(entry, true));
                actions.appendChild(reply);
                item.appendChild(actions);
            }
            container.appendChild(item);
        }
    }

    async function openCommentTarget(entry, reply) {
        if (String(entry.flowId).toLowerCase() === String(flowId).toLowerCase()) {
            focusNodeComment(entry.nodeId, entry.comment.id, reply ? entry.comment.id : null);
            return;
        }
        if (canEdit && !(await save(false))) return;

        const parameters = new URLSearchParams();
        if (entry.ancestorIds.length) parameters.set("trail", entry.ancestorIds.slice(-20).join(","));
        if (isPublishedView) parameters.set("published", "true");
        else if (reviewRequestId) parameters.set("requestId", reviewRequestId);
        if (presentationMode) parameters.set("present", "true");
        parameters.set("focusNode", entry.nodeId);
        parameters.set("focusComment", entry.comment.id);
        if (reply) parameters.set("replyTo", entry.comment.id);
        window.location.assign(`/flows/${entry.flowId}/edit?${parameters}`);
    }

    function focusCommentFromUrl() {
        const url = new URL(window.location.href);
        const nodeId = url.searchParams.get("focusNode");
        if (!nodeId) return;
        focusNodeComment(
            nodeId,
            url.searchParams.get("focusComment"),
            url.searchParams.get("replyTo"));
        url.searchParams.delete("focusNode");
        url.searchParams.delete("focusComment");
        url.searchParams.delete("replyTo");
        window.history.replaceState(window.history.state, "", url);
    }

    function focusNodeComment(nodeId, commentId, replyTo) {
        const node = adapter.getNode(nodeId);
        if (!node) {
            showToast("The node linked to this comment is no longer available.");
            return;
        }

        closeCommentsPanel();
        adapter.centerOnNode(nodeId);
        adapter.selectNode(nodeId);
        if (presentationMode) showPresentationNodeDetails(node);

        window.requestAnimationFrame(() => {
            const listId = presentationMode
                ? "presentationComments"
                : canEdit ? "nodeComments" : "nodeReadOnlyComments";
            const comment = commentId
                ? byId(listId)?.querySelector(`[data-comment-id="${CSS.escape(commentId)}"]`)
                : null;
            if (comment) {
                comment.classList.add("is-focused");
                comment.scrollIntoView({ block: "center", behavior: "smooth" });
                window.setTimeout(() => comment.classList.remove("is-focused"), 1900);
            }
            if (replyTo && canEdit && !presentationMode) {
                const replyComment = (selectedNode?.comments || []).find(item => item.id === replyTo);
                if (replyComment) beginNodeCommentReply(replyComment);
            }
        });
    }

    function showPresentationNodeDetails(node) {
        selectedNode = node;
        const config = nodeTypes[node.type] || nodeTypes.Process;
        byId("presentationDetailsType").textContent = config.title || node.type || "Selected symbol";
        byId("presentationDetailsTitle").textContent = node.title || "Untitled symbol";
        byId("presentationDetailsDescription").textContent = node.description?.trim() || "No description provided.";
        byId("presentationDetailsNotes").textContent = node.customProperties?.notes?.trim() || "No notes provided.";
        byId("openPresentationChildFlow").classList.toggle("d-none", !node.childFlowId);
        renderNodeComments((node.comments || []).filter(comment => comment.isPublic), {
            listId: "presentationComments",
            countId: "presentationCommentCount",
            allowActions: false
        });
        byId("presentationDetails").classList.add("open");
        byId("presentationDetails").setAttribute("aria-hidden", "false");
    }

    function closePresentationDetails() {
        byId("presentationDetails").classList.remove("open");
        byId("presentationDetails").setAttribute("aria-hidden", "true");
    }

    function changeSelectedNodeType() {
        if (!selectedNode) return;
        const graph = adapter.getGraph();
        const node = graph.nodes.find(item => item.id === selectedNode.id);
        if (!node) return;
        node.type = byId("nodeType").value;
        const config = nodeTypes[node.type] || nodeTypes.Process;
        if (config.inputs === 0) delete node.customProperties?.inputPins;
        if (config.outputs === 0) delete node.customProperties?.outputPins;
        if (node.type === "Section") delete node.customProperties?.sectionId;
        if (node.type === "Section" || node.type === "Annotation") node.childFlowId = null;
        adapter.setGraph(graph);
        selectedNode = node;
        window.requestAnimationFrame(() => adapter.selectNode(node.id));
        handleCanvasChange();
    }

    function handleKeyboard(event) {
        if (event.key === "Escape" && byId("commentsPanel").classList.contains("open")) {
            event.preventDefault();
            closeCommentsPanel();
            return;
        }
        if (presentationMode) {
            if (event.key === "Escape") {
                if (byId("presentationDetails").classList.contains("open")) {
                    event.preventDefault();
                    closePresentationDetails();
                    return;
                }
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

    async function enterPresentation(requestFullscreen = true) {
        if (presentationMode) return;
        presentationMode = true;
        setPresentationUrl(true);
        root.classList.add("is-presenting");
        closeCommentsPanel();
        byId("presentationButton").setAttribute("aria-pressed", "true");
        byId("toolbox").classList.remove("open");
        byId("propertiesPanel").classList.remove("open");
        closePresentationDetails();
        byId("validationPopover").classList.add("d-none");
        adapter.setReadOnly(true);
        refreshCommentsOverview();
        window.requestAnimationFrame(() => {
            adapter.fitToView();
            byId("exitPresentation").focus({ preventScroll: true });
        });

        if (requestFullscreen && document.fullscreenEnabled && !document.fullscreenElement) {
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
        setPresentationUrl(false);
        root.classList.remove("is-presenting");
        closePresentationDetails();
        closeCommentsPanel();
        byId("presentationButton").setAttribute("aria-pressed", "false");
        adapter.setSpacePanning(false);
        adapter.setReadOnly(!canEdit);
        refreshCommentsOverview();
        window.requestAnimationFrame(() => {
            adapter.fitToView();
            byId("presentationButton").focus({ preventScroll: true });
        });
    }

    function setPresentationUrl(enabled) {
        const url = new URL(window.location.href);
        if (enabled) url.searchParams.set("present", "true");
        else url.searchParams.delete("present");
        window.history.replaceState(window.history.state, "", url);
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
        const numericZoom = Number(zoom);
        if (!Number.isFinite(numericZoom)) return;

        byId("zoomReset").textContent = `${Math.round(numericZoom * 100)}%`;
        try {
            window.localStorage.setItem(zoomStorageKey, String(numericZoom));
        } catch {
            // Storage can be unavailable in privacy-restricted browser contexts.
        }
    }

    function readStoredZoom() {
        try {
            const storedZoom = Number.parseFloat(window.localStorage.getItem(zoomStorageKey));
            return Number.isFinite(storedZoom) ? storedZoom : null;
        } catch {
            return null;
        }
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
            const start = exportPortPoint(source, "output", connection.sourcePort, offsetX, offsetY);
            const end = exportPortPoint(target, "input", connection.targetPort, offsetX, offsetY);
            const path = window.FlowDesignerAdapters.connectionPath(
                start.x,
                start.y,
                end.x,
                end.y,
                portSides(source).output,
                portSides(target).input);
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

    function exportPortPoint(node, kind, portName, offsetX, offsetY) {
        const width = node.width || 184;
        const height = node.height || 76;
        const config = nodeTypes[node.type] || nodeTypes.Process;
        const defaultCount = kind === "output" ? config.outputs : config.inputs;
        const configuredCount = Number(node.customProperties?.[kind === "output" ? "outputPins" : "inputPins"]);
        const count = defaultCount > 0 && Number.isInteger(configuredCount) && configuredCount >= 1 && configuredCount <= 6
            ? configuredCount
            : defaultCount;
        const match = String(portName || "").match(/_(\d+)$/);
        const index = Math.min(count, Math.max(1, Number(match?.[1]) || 1));
        const position = count > 0 ? index / (count + 1) : .5;
        const side = portSides(node)[kind];
        if (side === "top" || side === "bottom") {
            return { x: node.x + width * position + offsetX, y: node.y + (side === "bottom" ? height : 0) + offsetY };
        }
        return { x: node.x + (side === "right" ? width : 0) + offsetX, y: node.y + height * position + offsetY };
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
