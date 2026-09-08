(function () {
    "use strict";

    const nodeTypes = {
        Start: { title: "Start", help: "Entry point", icon: "S", inputs: 0, outputs: 1, shape: "terminator" },
        End: { title: "End", help: "Exit point", icon: "E", inputs: 1, outputs: 0, shape: "terminator" },
        Process: { title: "Process", help: "Action or operation", icon: "P", inputs: 1, outputs: 1, shape: "process" },
        Decision: { title: "Decision", help: "Conditional branch", icon: "?", inputs: 1, outputs: 2, shape: "decision" },
        InputOutput: { title: "Input / output", help: "Read or produce data", icon: "I/O", inputs: 1, outputs: 1, shape: "input-output" },
        Document: { title: "Document", help: "Document or report", icon: "D", inputs: 1, outputs: 1, shape: "document" },
        DataStore: { title: "Data store", help: "Stored information", icon: "DB", inputs: 1, outputs: 1, shape: "data-store" },
        Subprocess: { title: "Subprocess", help: "Defined process or function", icon: "SP", inputs: 1, outputs: 1, shape: "subprocess" },
        Connector: { title: "Connector", help: "Continue elsewhere", icon: "C", inputs: 1, outputs: 1, shape: "connector" },
        ManualInput: { title: "Manual input", help: "User-provided input", icon: "IN", inputs: 1, outputs: 1, shape: "manual-input" },
        Preparation: { title: "Preparation", help: "Initialize or prepare", icon: "PRE", inputs: 1, outputs: 1, shape: "preparation" },
        Activity: { title: "Activity", help: "Business work step", icon: "A", inputs: 1, outputs: 1, shape: "activity" },
        Event: { title: "Intermediate event", help: "Something that occurs", icon: "EV", inputs: 1, outputs: 1, shape: "event" },
        Gateway: { title: "Exclusive gateway", help: "Choose one path", icon: "X", inputs: 1, outputs: 2, shape: "gateway" },
        ParallelGateway: { title: "Parallel gateway", help: "Split or join paths", icon: "+", inputs: 2, outputs: 2, shape: "gateway" },
        Note: { title: "Note", help: "Supporting annotation", icon: "N", inputs: 1, outputs: 1, shape: "note" }
    };

    const diagramTypes = {
        StandardFlowchart: { title: "Standard flowchart", palette: "StandardFlowchart" },
        BusinessWorkflow: { title: "Business workflow", palette: "BusinessWorkflow" },
        CodeFlow: { title: "Code flow", palette: "CodeFlow" }
    };

    const palettes = {
        StandardFlowchart: {
            title: "Flowchart symbols",
            primary: "Process",
            nodes: ["Start", "Process", "Decision", "InputOutput", "Document", "DataStore", "Subprocess", "Connector", "ManualInput", "Preparation", "End", "Note"]
        },
        BusinessWorkflow: {
            title: "Workflow symbols",
            primary: "Activity",
            nodes: ["Start", "Activity", "Gateway", "ParallelGateway", "Event", "Document", "DataStore", "End", "Note"]
        },
        CodeFlow: {
            title: "Code-flow symbols",
            primary: "Process",
            nodes: ["Start", "Process", "Decision", "InputOutput", "Subprocess", "DataStore", "Connector", "Preparation", "End", "Note"]
        }
    };

    function text(value) {
        return String(value ?? "")
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#039;");
    }

    function newId(prefix) {
        return `${prefix}-${crypto.randomUUID()}`;
    }

    class DrawflowAdapter {
        constructor(element, callbacks = {}) {
            if (!window.Drawflow) {
                throw new Error("Drawflow could not be loaded.");
            }

            this.element = element;
            this.callbacks = callbacks;
            this.externalToInternal = new Map();
            this.internalToExternal = new Map();
            this.connectionState = new Map();
            this.selected = null;
            this.suppressChanges = false;
            this.snapToGrid = true;

            this.editor = new Drawflow(element);
            this.editor.reroute = true;
            this.editor.reroute_fix_curvature = true;
            this.editor.zoom_min = 0.3;
            this.editor.zoom_max = 1.8;
            this.editor.zoom_value = 0.1;
            this.editor.start();
            this.bindEvents();
        }

        bindEvents() {
            ["nodeCreated", "nodeRemoved", "connectionRemoved"].forEach(eventName => {
                this.editor.on(eventName, () => this.changed());
            });

            this.editor.on("nodeMoved", internalId => {
                if (this.snapToGrid) this.snapNode(internalId);
                this.changed();
            });

            this.editor.on("connectionCreated", detail => {
                if (String(detail.output_id) === String(detail.input_id)) {
                    this.editor.removeSingleConnection(detail.output_id, detail.input_id, detail.output_class, detail.input_class);
                    return;
                }

                const key = this.connectionKey(detail.output_id, detail.input_id, detail.output_class, detail.input_class);
                if (!this.connectionState.has(key)) {
                    this.connectionState.set(key, { id: newId("connection"), label: "", metadata: {} });
                }
                this.refreshConnectionLabels();
                this.changed();
            });

            this.editor.on("nodeSelected", internalId => {
                const id = this.internalToExternal.get(String(internalId));
                this.selected = id ? { kind: "node", id } : null;
                if (id) this.callbacks.onSelectNode?.(this.getNode(id));
            });

            this.editor.on("nodeUnselected", () => {
                if (this.selected?.kind === "node") {
                    this.selected = null;
                    this.callbacks.onClearSelection?.();
                }
            });

            this.editor.on("connectionSelected", detail => {
                const key = this.connectionKey(detail.output_id, detail.input_id, detail.output_class, detail.input_class);
                this.selected = { kind: "connection", key };
                this.callbacks.onSelectConnection?.(this.connectionFromDetail(detail));
            });

            this.editor.on("connectionUnselected", () => {
                if (this.selected?.kind === "connection") {
                    this.selected = null;
                    this.callbacks.onClearSelection?.();
                }
            });

            this.editor.on("zoom", zoom => this.callbacks.onZoom?.(zoom));

            this.element.addEventListener("pointerup", () => {
                window.setTimeout(() => {
                    this.updateAllConnections();
                    this.captureNodeSizes();
                }, 0);
            });
        }

        changed() {
            if (this.suppressChanges) return;
            this.refreshConnectionLabels();
            this.callbacks.onChange?.();
        }

        setGraph(graph) {
            this.suppressChanges = true;
            this.selected = null;
            this.editor.clear();
            this.externalToInternal.clear();
            this.internalToExternal.clear();
            this.connectionState.clear();

            for (const node of graph.nodes ?? []) {
                this.addNode(node.type, node.x, node.y, node, false);
            }

            for (const connection of graph.connections ?? []) {
                const sourceId = this.externalToInternal.get(connection.sourceNodeId);
                const targetId = this.externalToInternal.get(connection.targetNodeId);
                if (!sourceId || !targetId) continue;

                const sourcePort = this.validPort(sourceId, connection.sourcePort, "output");
                const targetPort = this.validPort(targetId, connection.targetPort, "input");
                if (!sourcePort || !targetPort) continue;

                this.editor.addConnection(sourceId, targetId, sourcePort, targetPort);
                this.connectionState.set(
                    this.connectionKey(sourceId, targetId, sourcePort, targetPort),
                    { id: connection.id || newId("connection"), label: connection.label || "", metadata: connection.metadata || {} }
                );
            }

            this.suppressChanges = false;
            this.callbacks.onClearSelection?.();
            window.setTimeout(() => {
                this.applySavedSizes(graph.nodes ?? []);
                this.updateAllConnections();
                this.refreshConnectionLabels();
            }, 0);
        }

        addNode(type, x, y, supplied = {}, emitChange = true) {
            const config = nodeTypes[type] || nodeTypes.Note;
            const externalId = supplied.id || newId("node");
            const data = {
                externalId,
                type: nodeTypes[type] ? type : "Note",
                title: supplied.title || config.title,
                description: supplied.description || "",
                metadata: supplied.metadata || {},
                customProperties: supplied.customProperties || {},
                comments: supplied.comments || [],
                width: supplied.width ?? null,
                height: supplied.height ?? null
            };
            const previousSuppress = this.suppressChanges;
            if (!emitChange) this.suppressChanges = true;
            const internalId = this.editor.addNode(
                externalId,
                config.inputs,
                config.outputs,
                Math.max(10, Number(x) || 100),
                Math.max(10, Number(y) || 100),
                `fd-node-${data.type.toLowerCase()} fd-shape-${config.shape}`,
                data,
                this.nodeHtml(data),
                false
            );
            this.externalToInternal.set(externalId, String(internalId));
            this.internalToExternal.set(String(internalId), externalId);
            this.suppressChanges = previousSuppress;
            if (emitChange) this.changed();
            return externalId;
        }

        nodeHtml(data) {
            const config = nodeTypes[data.type] || nodeTypes.Note;
            const commentCount = data.comments?.length || 0;
            const comments = commentCount ? `<span class="fd-node-comments" title="${commentCount} comment${commentCount === 1 ? "" : "s"}">${commentCount}</span>` : "";
            return `<div class="fd-node-content"><span class="fd-tool-icon fd-type-${data.type.toLowerCase()}">${config.icon}</span><span class="fd-node-copy"><span class="fd-node-type">${text(config.title)}</span><span class="fd-node-title">${text(data.title)}</span>${comments}</span></div>`;
        }

        getGraph() {
            const exported = this.editor.export()?.drawflow?.Home?.data || {};
            const nodes = Object.values(exported).map(node => {
                const data = node.data || {};
                const dom = document.getElementById(`node-${node.id}`);
                return {
                    id: data.externalId || this.internalToExternal.get(String(node.id)) || `node-${node.id}`,
                    type: data.type || "Note",
                    title: data.title || "Untitled",
                    description: data.description || "",
                    x: node.pos_x,
                    y: node.pos_y,
                    width: dom ? dom.offsetWidth : data.width,
                    height: dom ? dom.offsetHeight : data.height,
                    metadata: data.metadata || {},
                    customProperties: data.customProperties || {},
                    comments: data.comments || []
                };
            });

            const connections = [];
            for (const source of Object.values(exported)) {
                for (const [sourcePort, output] of Object.entries(source.outputs || {})) {
                    for (const connection of output.connections || []) {
                        const targetId = String(connection.node);
                        const targetPort = connection.output;
                        const key = this.connectionKey(source.id, targetId, sourcePort, targetPort);
                        const state = this.connectionState.get(key) || {};
                        connections.push({
                            id: state.id || newId("connection"),
                            sourceNodeId: this.internalToExternal.get(String(source.id)),
                            targetNodeId: this.internalToExternal.get(targetId),
                            sourcePort,
                            targetPort,
                            label: state.label || "",
                            metadata: state.metadata || {}
                        });
                    }
                }
            }
            return { nodes, connections };
        }

        getNode(externalId) {
            return this.getGraph().nodes.find(node => node.id === externalId) || null;
        }

        updateNode(externalId, changes, emitChange = true) {
            const internalId = this.externalToInternal.get(externalId);
            if (!internalId) return;
            const node = this.editor.drawflow.drawflow.Home.data[internalId];
            node.data = { ...node.data, ...changes };
            const content = document.querySelector(`#node-${internalId} .drawflow_content_node`);
            if (content) content.innerHTML = this.nodeHtml(node.data);
            if (emitChange) this.changed();
            this.callbacks.onSelectNode?.(this.getNode(externalId));
        }

        updateConnectionLabel(connection, label) {
            const source = this.externalToInternal.get(connection.sourceNodeId);
            const target = this.externalToInternal.get(connection.targetNodeId);
            if (!source || !target) return;
            const key = this.connectionKey(source, target, connection.sourcePort, connection.targetPort);
            const state = this.connectionState.get(key) || { id: connection.id || newId("connection"), metadata: {} };
            state.label = label;
            this.connectionState.set(key, state);
            this.refreshConnectionLabels();
            this.changed();
        }

        deleteSelected() {
            if (!this.selected) return false;
            if (this.selected.kind === "node") {
                const internalId = this.externalToInternal.get(this.selected.id);
                if (!internalId) return false;
                this.editor.removeNodeId(`node-${internalId}`);
                this.externalToInternal.delete(this.selected.id);
                this.internalToExternal.delete(internalId);
            } else {
                const parts = this.parseConnectionKey(this.selected.key);
                this.editor.removeSingleConnection(parts.source, parts.target, parts.sourcePort, parts.targetPort);
                this.connectionState.delete(this.selected.key);
            }
            this.selected = null;
            this.callbacks.onClearSelection?.();
            this.changed();
            return true;
        }

        connectionFromDetail(detail) {
            const key = this.connectionKey(detail.output_id, detail.input_id, detail.output_class, detail.input_class);
            const state = this.connectionState.get(key) || { id: newId("connection"), label: "", metadata: {} };
            this.connectionState.set(key, state);
            return {
                ...state,
                sourceNodeId: this.internalToExternal.get(String(detail.output_id)),
                targetNodeId: this.internalToExternal.get(String(detail.input_id)),
                sourcePort: detail.output_class,
                targetPort: detail.input_class
            };
        }

        screenToCanvas(clientX, clientY) {
            const rect = this.editor.precanvas.getBoundingClientRect();
            return { x: (clientX - rect.left) / this.editor.zoom, y: (clientY - rect.top) / this.editor.zoom };
        }

        zoomIn() { this.editor.zoom_in(); }
        zoomOut() { this.editor.zoom_out(); }
        resetZoom() { this.editor.zoom_reset(); }
        getZoom() { return this.editor.zoom; }
        setGrid(enabled) { this.snapToGrid = enabled; this.element.classList.toggle("no-grid", !enabled); }

        fitToView() {
            const nodes = [...this.element.querySelectorAll(".drawflow-node")];
            if (!nodes.length) return;
            this.editor.zoom_reset();
            const canvasRect = this.element.getBoundingClientRect();
            const minX = Math.min(...nodes.map(node => Number.parseFloat(node.style.left) || 0));
            const minY = Math.min(...nodes.map(node => Number.parseFloat(node.style.top) || 0));
            const maxX = Math.max(...nodes.map(node => (Number.parseFloat(node.style.left) || 0) + node.offsetWidth));
            const maxY = Math.max(...nodes.map(node => (Number.parseFloat(node.style.top) || 0) + node.offsetHeight));
            const width = Math.max(1, maxX - minX);
            const height = Math.max(1, maxY - minY);
            const zoom = Math.min(1.2, Math.max(.3, Math.min((canvasRect.width - 100) / width, (canvasRect.height - 100) / height)));
            this.editor.zoom = zoom;
            this.editor.canvas_x = (canvasRect.width - width * zoom) / 2 - minX * zoom;
            this.editor.canvas_y = (canvasRect.height - height * zoom) / 2 - minY * zoom;
            this.editor.precanvas.style.transform = `translate(${this.editor.canvas_x}px, ${this.editor.canvas_y}px) scale(${zoom})`;
            this.editor.precanvas.style.transformOrigin = "0 0";
            this.callbacks.onZoom?.(zoom);
        }

        snapNode(internalId) {
            const id = String(internalId);
            const node = this.editor.drawflow.drawflow.Home.data[id];
            const element = document.getElementById(`node-${id}`);
            if (!node || !element) return;
            node.pos_x = Math.round(node.pos_x / 20) * 20;
            node.pos_y = Math.round(node.pos_y / 20) * 20;
            element.style.left = `${node.pos_x}px`;
            element.style.top = `${node.pos_y}px`;
            this.editor.updateConnectionNodes(`node-${id}`);
        }

        captureNodeSizes() {
            const exported = this.editor.drawflow.drawflow.Home.data;
            for (const [id, node] of Object.entries(exported)) {
                const dom = document.getElementById(`node-${id}`);
                if (!dom) continue;
                const width = dom.offsetWidth;
                const height = dom.offsetHeight;
                if (node.data.width !== width || node.data.height !== height) {
                    node.data.width = width;
                    node.data.height = height;
                    this.changed();
                }
            }
        }

        applySavedSizes(nodes) {
            for (const node of nodes) {
                const internal = this.externalToInternal.get(node.id);
                const dom = internal ? document.getElementById(`node-${internal}`) : null;
                if (dom && node.width) dom.style.width = `${Math.max(150, node.width)}px`;
                if (dom && node.height) dom.style.height = `${Math.max(76, node.height)}px`;
            }
        }

        updateAllConnections() {
            for (const internalId of this.internalToExternal.keys()) {
                this.editor.updateConnectionNodes(`node-${internalId}`);
            }
            this.refreshConnectionLabels();
        }

        refreshConnectionLabels() {
            window.requestAnimationFrame(() => {
                for (const [key, state] of this.connectionState) {
                    const parts = this.parseConnectionKey(key);
                    const svg = this.element.querySelector(`svg.connection.node_out_node-${parts.source}.node_in_node-${parts.target}.${parts.sourcePort}.${parts.targetPort}`);
                    if (!svg) continue;
                    svg.querySelectorAll(".fd-connection-label").forEach(label => label.remove());
                    if (!state.label) continue;
                    const path = svg.querySelector(".main-path");
                    if (!path) continue;
                    const pathId = `fd-path-${parts.source}-${parts.target}-${parts.sourcePort}-${parts.targetPort}`;
                    path.id = pathId;
                    const label = document.createElementNS("http://www.w3.org/2000/svg", "text");
                    label.setAttribute("class", "fd-connection-label");
                    const textPath = document.createElementNS("http://www.w3.org/2000/svg", "textPath");
                    textPath.setAttribute("href", `#${pathId}`);
                    textPath.setAttribute("startOffset", "50%");
                    textPath.setAttribute("text-anchor", "middle");
                    textPath.textContent = state.label;
                    label.appendChild(textPath);
                    svg.appendChild(label);
                }
            });
        }

        validPort(internalId, requested, kind) {
            const node = this.editor.drawflow.drawflow.Home.data[String(internalId)];
            const ports = Object.keys(kind === "output" ? node.outputs : node.inputs);
            return ports.includes(requested) ? requested : ports[0];
        }

        connectionKey(source, target, sourcePort, targetPort) {
            return [source, target, sourcePort, targetPort].map(String).join("|");
        }

        parseConnectionKey(key) {
            const [source, target, sourcePort, targetPort] = key.split("|");
            return { source, target, sourcePort, targetPort };
        }
    }

    window.FlowDesignerAdapters = { DrawflowAdapter, nodeTypes, diagramTypes, palettes };
})();
