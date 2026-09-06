(function () {
    "use strict";

    const root = document.getElementById("flowDesigner");
    if (!root) return;

    const byId = id => document.getElementById(id);
    const flowId = root.dataset.flowId;
    const nameInput = byId("flowName");
    const saveState = byId("saveState");
    const saveButton = byId("saveButton");
    const saveButtonText = byId("saveButtonText");
    const nodeForm = byId("nodeProperties");
    const connectionForm = byId("connectionProperties");
    const emptyProperties = byId("emptyProperties");
    const nodeTypes = window.FlowDesignerAdapters.nodeTypes;
    let flow = null;
    let selectedNode = null;
    let selectedConnection = null;
    let dirty = false;
    let saving = false;
    let pendingSave = false;
    let autosaveTimer = null;
    let historyTimer = null;
    let toastTimer = null;
    let clipboardNode = null;
    let history = [];
    let historyIndex = -1;
    let applyingHistory = false;

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
        nameInput.value = flow.name;
        adapter.setGraph(flow);
        history = [graphSnapshot()];
        historyIndex = 0;
        updateHistoryButtons();
        updateCanvasHint();
        applyValidation(localValidation());
        setSaveState("saved", "Saved");
        bindUi();
    }

    function bindUi() {
        for (const [type, config] of Object.entries(nodeTypes)) {
            const option = document.createElement("option");
            option.value = type;
            option.textContent = config.title;
            byId("nodeType").appendChild(option);
        }

        document.querySelectorAll("[data-node-type]").forEach(tool => {
            tool.addEventListener("dragstart", event => {
                event.dataTransfer.setData("application/x-flow-node", tool.dataset.nodeType);
                event.dataTransfer.effectAllowed = "copy";
            });
            tool.addEventListener("click", () => addAtCenter(tool.dataset.nodeType));
        });

        const canvas = byId("drawflow");
        canvas.addEventListener("dragover", event => { event.preventDefault(); event.dataTransfer.dropEffect = "copy"; });
        canvas.addEventListener("drop", event => {
            event.preventDefault();
            const type = event.dataTransfer.getData("application/x-flow-node");
            if (!nodeTypes[type]) return;
            const point = adapter.screenToCanvas(event.clientX, event.clientY);
            adapter.addNode(type, point.x - 90, point.y - 35);
        });
        canvas.addEventListener("dblclick", event => {
            if (event.target !== canvas && !event.target.classList.contains("drawflow")) return;
            const point = adapter.screenToCanvas(event.clientX, event.clientY);
            adapter.addNode("Task", point.x - 90, point.y - 35);
        });

        saveButton.addEventListener("click", () => save(true));
        nameInput.addEventListener("input", markDirty);
        byId("undoButton").addEventListener("click", undo);
        byId("redoButton").addEventListener("click", redo);
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

        ["nodeTitle", "nodeDescription", "nodeOwner", "nodeDuration", "nodeNotes"].forEach(id => {
            byId(id).addEventListener("input", updateSelectedNode);
        });
        byId("nodeType").addEventListener("change", changeSelectedNodeType);
        byId("deleteNode").addEventListener("click", () => adapter.deleteSelected());
        byId("connectionLabel").addEventListener("input", () => {
            if (!selectedConnection) return;
            selectedConnection.label = byId("connectionLabel").value;
            adapter.updateConnectionLabel(selectedConnection, selectedConnection.label);
        });
        byId("deleteConnection").addEventListener("click", () => adapter.deleteSelected());

        byId("toggleToolbox").addEventListener("click", () => byId("toolbox").classList.toggle("open"));
        byId("closeToolbox").addEventListener("click", () => byId("toolbox").classList.remove("open"));
        byId("toggleProperties").addEventListener("click", () => byId("propertiesPanel").classList.toggle("open"));
        byId("closeProperties").addEventListener("click", () => byId("propertiesPanel").classList.remove("open"));
        byId("validationButton").addEventListener("click", () => byId("validationPopover").classList.toggle("d-none"));
        byId("closeValidation").addEventListener("click", () => byId("validationPopover").classList.add("d-none"));

        document.addEventListener("keydown", handleKeyboard);
        window.addEventListener("beforeunload", event => {
            if (!dirty) return;
            event.preventDefault();
            event.returnValue = "";
        });
    }

    function addAtCenter(type) {
        const canvas = byId("drawflow").getBoundingClientRect();
        const point = adapter.screenToCanvas(canvas.left + canvas.width / 2, canvas.top + canvas.height / 2);
        adapter.addNode(type, point.x - 90, point.y - 35);
        byId("toolbox").classList.remove("open");
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
        window.clearTimeout(autosaveTimer);
        autosaveTimer = window.setTimeout(() => save(false), 1800);
    }

    async function save(manual) {
        if (!flow || (!dirty && !manual)) return;
        if (saving) { pendingSave = true; return; }
        saving = true;
        pendingSave = false;
        window.clearTimeout(autosaveTimer);
        setSaveState("saving", "Saving…");
        saveButton.disabled = true;
        saveButtonText.textContent = "Saving…";

        const graph = adapter.getGraph();
        flow = { ...flow, name: nameInput.value.trim() || "Untitled flow", nodes: graph.nodes, connections: graph.connections };

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
            dirty = false;
            setSaveState("saved", `Saved ${new Date(flow.updatedAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`);
            if (manual) showToast("Diagram saved");
        } catch (error) {
            console.error(error);
            dirty = true;
            setSaveState("error", "Save failed — try again");
            showToast(error.message || "Save failed");
        } finally {
            saving = false;
            saveButton.disabled = false;
            saveButtonText.textContent = "Save";
            if (pendingSave) save(false);
        }
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
        connectionForm.classList.add("d-none");
        nodeForm.classList.remove("d-none");
        byId("nodeTitle").value = node.title || "";
        byId("nodeDescription").value = node.description || "";
        byId("nodeType").value = node.type;
        byId("nodeOwner").value = node.metadata?.owner || "";
        byId("nodeDuration").value = node.metadata?.duration || "";
        byId("nodeNotes").value = node.customProperties?.notes || "";
    }

    function showConnectionProperties(connection) {
        selectedConnection = connection;
        selectedNode = null;
        emptyProperties.classList.add("d-none");
        nodeForm.classList.add("d-none");
        connectionForm.classList.remove("d-none");
        byId("connectionLabel").value = connection.label || "";
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
        emptyProperties.classList.remove("d-none");
    }

    function updateSelectedNode() {
        if (!selectedNode) return;
        const changes = {
            title: byId("nodeTitle").value || nodeTypes[selectedNode.type].title,
            description: byId("nodeDescription").value,
            metadata: { ...(selectedNode.metadata || {}), owner: byId("nodeOwner").value, duration: byId("nodeDuration").value },
            customProperties: { ...(selectedNode.customProperties || {}), notes: byId("nodeNotes").value }
        };
        selectedNode = { ...selectedNode, ...changes };
        adapter.updateNode(selectedNode.id, changes);
    }

    function changeSelectedNodeType() {
        if (!selectedNode) return;
        const graph = adapter.getGraph();
        const node = graph.nodes.find(item => item.id === selectedNode.id);
        if (!node) return;
        node.type = byId("nodeType").value;
        adapter.setGraph(graph);
        selectedNode = node;
        showNodeProperties(node);
        handleCanvasChange();
    }

    function handleKeyboard(event) {
        const editing = ["INPUT", "TEXTAREA", "SELECT"].includes(document.activeElement?.tagName);
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "s") {
            event.preventDefault(); save(true); return;
        }
        if (editing) return;
        const key = event.key.toLowerCase();
        if ((event.ctrlKey || event.metaKey) && key === "z") { event.preventDefault(); event.shiftKey ? redo() : undo(); }
        else if ((event.ctrlKey || event.metaKey) && key === "y") { event.preventDefault(); redo(); }
        else if ((event.ctrlKey || event.metaKey) && key === "c" && selectedNode) { event.preventDefault(); clipboardNode = structuredClone(selectedNode); showToast("Node copied"); }
        else if ((event.ctrlKey || event.metaKey) && key === "v" && clipboardNode) { event.preventDefault(); pasteNode(); }
        else if ((event.ctrlKey || event.metaKey) && key === "d" && selectedNode) { event.preventDefault(); clipboardNode = structuredClone(selectedNode); pasteNode(); }
        else if (event.key === "Delete" || event.key === "Backspace") { if (adapter.deleteSelected()) event.preventDefault(); }
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
        if (starts !== 1) issues.push({ severity: "Warning", code: "start-count", message: `Exactly one Start node is recommended; this flow has ${starts}.` });
        if (!graph.nodes.some(node => node.type === "End")) issues.push({ severity: "Warning", code: "missing-end", message: "At least one End node is recommended." });
        const connected = new Set(graph.connections.flatMap(connection => [connection.sourceNodeId, connection.targetNodeId]));
        graph.nodes.filter(node => !connected.has(node.id)).forEach(node => issues.push({ severity: "Warning", code: "orphan-node", message: `'${node.title}' is not connected.`, elementId: node.id }));
        return issues;
    }

    function applyValidation(issues) {
        const summary = byId("validationSummary");
        const button = byId("validationButton");
        const icon = byId("validationIcon");
        const list = byId("validationIssues");
        const errors = issues.filter(issue => issue.severity?.toLowerCase() === "error").length;
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
            item.className = `fd-validation-item ${issue.severity?.toLowerCase() || "warning"}`;
            item.innerHTML = `<span>${issue.severity?.toLowerCase() === "error" ? "×" : "!"}</span><span></span>`;
            item.lastElementChild.textContent = issue.message;
            list.appendChild(item);
        }
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
        const colors = { Start: "#087c68", Task: "#2563a9", Decision: "#b45309", Approval: "#6d4bd1", Parallel: "#0e7490", Wait: "#8a5c10", Notification: "#a33c67", AutomatedAction: "#4f46a5", End: "#ba3127", Custom: "#596863" };
        const parts = [`<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}"><rect width="100%" height="100%" fill="#f8faf9"/><g font-family="Segoe UI,Arial,sans-serif">`];
        for (const connection of graph.connections) {
            const source = nodes.get(connection.sourceNodeId);
            const target = nodes.get(connection.targetNodeId);
            if (!source || !target) continue;
            const x1 = source.x + (source.width || 184) + offsetX;
            const y1 = source.y + (source.height || 76) / 2 + offsetY;
            const x2 = target.x + offsetX;
            const y2 = target.y + (target.height || 76) / 2 + offsetY;
            const curve = Math.max(45, Math.abs(x2 - x1) * .45);
            parts.push(`<path d="M ${x1} ${y1} C ${x1 + curve} ${y1}, ${x2 - curve} ${y2}, ${x2} ${y2}" fill="none" stroke="#82958f" stroke-width="3"/>`);
            if (connection.label) parts.push(`<text x="${(x1 + x2) / 2}" y="${(y1 + y2) / 2 - 7}" text-anchor="middle" font-size="12" font-weight="700" fill="#445651">${escapeXml(connection.label)}</text>`);
        }
        for (const node of graph.nodes) {
            const x = node.x + offsetX;
            const y = node.y + offsetY;
            const w = node.width || 184;
            const h = node.height || 76;
            const color = colors[node.type] || colors.Custom;
            parts.push(`<g><rect x="${x}" y="${y}" width="${w}" height="${h}" rx="9" fill="#fff" stroke="#cedbd7"/><rect x="${x}" y="${y}" width="5" height="${h}" rx="3" fill="${color}"/><text x="${x + 18}" y="${y + 25}" font-size="10" font-weight="700" fill="${color}" letter-spacing="1">${escapeXml(nodeTypes[node.type]?.title?.toUpperCase() || node.type.toUpperCase())}</text><text x="${x + 18}" y="${y + 49}" font-size="14" font-weight="700" fill="#172522">${escapeXml(shorten(node.title, 28))}</text></g>`);
        }
        parts.push("</g></svg>");
        return parts.join("");
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
