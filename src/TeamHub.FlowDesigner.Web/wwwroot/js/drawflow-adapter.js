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
        Activity: { title: "Task", help: "Assignable work step", icon: "A", inputs: 1, outputs: 1, shape: "activity" },
        Event: { title: "Intermediate event", help: "Something that occurs", icon: "EV", inputs: 1, outputs: 1, shape: "event" },
        Gateway: { title: "Exclusive gateway", help: "Choose one path", icon: "X", inputs: 1, outputs: 2, shape: "gateway" },
        ParallelGateway: { title: "Parallel gateway", help: "Split or join paths", icon: "+", inputs: 2, outputs: 2, shape: "gateway" },
        Section: { title: "Section group", defaultTitle: "Section", help: "Move related steps together", icon: "▣", inputs: 1, outputs: 1, shape: "section", defaultWidth: 420, defaultHeight: 300 },
        Annotation: { title: "Annotation / heading", defaultTitle: "Heading", help: "Freeform title or supporting text", icon: "T", inputs: 0, outputs: 0, shape: "annotation", defaultWidth: 280, defaultHeight: 110 }
    };

    const diagramTypes = {
        StandardFlowchart: { title: "Standard flowchart", palette: "StandardFlowchart" },
        BusinessWorkflow: { title: "Business workflow", palette: "BusinessWorkflow" },
        CodeFlow: { title: "Code flow", palette: "CodeFlow" },
        WorkCenterWorkflow: { title: "Work Center workflow", palette: "WorkCenterWorkflow" }
    };

    const palettes = {
        StandardFlowchart: {
            title: "Flowchart symbols",
            primary: "Process",
            nodes: ["Section", "Annotation", "Start", "Process", "Decision", "InputOutput", "Document", "DataStore", "Subprocess", "Connector", "ManualInput", "Preparation", "End"]
        },
        BusinessWorkflow: {
            title: "Workflow symbols",
            primary: "Activity",
            nodes: ["Section", "Annotation", "Start", "Activity", "Gateway", "ParallelGateway", "Event", "Document", "DataStore", "End"]
        },
        CodeFlow: {
            title: "Code-flow symbols",
            primary: "Process",
            nodes: ["Section", "Annotation", "Start", "Process", "Decision", "InputOutput", "Subprocess", "DataStore", "Connector", "Preparation", "End"]
        },
        WorkCenterWorkflow: {
            title: "Work Center blocks",
            primary: "Activity",
            nodes: ["Section", "Annotation", "Start", "Activity", "ParallelGateway", "End"]
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

    const validPortSides = new Set(["left", "right", "top", "bottom"]);

    function normalizePortLayout(layout) {
        const legacyLayouts = {
            horizontal: "left-right",
            vertical: "top-bottom",
            "horizontal-reverse": "right-left",
            "vertical-reverse": "bottom-top"
        };
        const normalized = legacyLayouts[String(layout || "").toLowerCase()] || String(layout || "").toLowerCase();
        const [inputSide, outputSide, extra] = normalized.split("-");
        return !extra && validPortSides.has(inputSide) && validPortSides.has(outputSide) && inputSide !== outputSide
            ? `${inputSide}-${outputSide}`
            : "left-right";
    }

    function portSides(data) {
        const [input, output] = normalizePortLayout(data?.customProperties?.portLayout).split("-");
        return { input, output };
    }

    function sideDirection(side) {
        return {
            left: { x: -1, y: 0 },
            right: { x: 1, y: 0 },
            top: { x: 0, y: -1 },
            bottom: { x: 0, y: 1 }
        }[side] || { x: 1, y: 0 };
    }

    function connectionPath(startX, startY, endX, endY, sourceSide, targetSide) {
        const distance = Math.hypot(endX - startX, endY - startY);
        const controlDistance = Math.max(55, Math.min(180, distance * .38));
        const sourceDirection = sideDirection(sourceSide);
        const targetDirection = sideDirection(targetSide);
        const sourceControl = { x: startX + sourceDirection.x * controlDistance, y: startY + sourceDirection.y * controlDistance };
        const targetControl = { x: endX + targetDirection.x * controlDistance, y: endY + targetDirection.y * controlDistance };

        return `M ${startX} ${startY} C ${sourceControl.x} ${sourceControl.y}, ${targetControl.x} ${targetControl.y}, ${endX} ${endY}`;
    }

    function diamondPortPoint(side, progress) {
        const position = Math.max(0, Math.min(1, Number(progress) || 0));
        const edgeOffset = Math.abs(position - .5);
        return {
            top: { x: position, y: edgeOffset },
            bottom: { x: position, y: 1 - edgeOffset },
            left: { x: edgeOffset, y: position },
            right: { x: 1 - edgeOffset, y: position }
        }[side] || { x: 1, y: position };
    }

    function nodeAppearanceClasses(data) {
        const tone = String(data?.customProperties?.tone || "").toLowerCase();
        const layer = String(data?.customProperties?.layer || "").toLowerCase();
        const sides = portSides(data);
        const presentationStyle = String(data?.customProperties?.presentationStyle || "").toLowerCase();
        const toneClass = ["blue", "red", "green", "orange", "purple"].includes(tone) ? ` fd-tone-${tone}` : "";
        const layerClass = ["background", "foreground"].includes(layer) ? ` fd-layer-${layer}` : "";
        const portClass = ` fd-input-${sides.input} fd-output-${sides.output}`;
        const styleClass = ["step", "card", "banner", "plain", "band"].includes(presentationStyle) ? ` fd-style-${presentationStyle}` : "";
        const childClass = data?.childFlowId ? " fd-has-child" : "";
        return `${toneClass}${layerClass}${portClass}${styleClass}${childClass}`;
    }

    const resizeDirections = ["n", "ne", "e", "se", "s", "sw", "w", "nw"];

    function resizeHandles(label) {
        return resizeDirections
            .map(direction => `<span class="fd-resize-handle fd-resize-${direction}" data-resize-direction="${direction}" title="${text(label)}" aria-hidden="true"></span>`)
            .join("");
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
            this.nodePositions = new Map();
            this.connectionState = new Map();
            this.selected = null;
            this.selectedGroupId = null;
            this.suppressChanges = false;
            this.snapToGrid = true;
            this.spacePanning = false;
            this.readOnly = false;
            this.defaultPortLayout = "horizontal";

            this.editor = new Drawflow(element);
            this.editor.reroute = true;
            this.editor.reroute_fix_curvature = true;
            this.editor.zoom_min = 0.3;
            this.editor.zoom_max = 1.8;
            this.editor.zoom_value = 0.1;
            this.editor.start();
            this.installArrowMarker();
            this.bindEvents();
        }

        bindEvents() {
            ["nodeCreated", "nodeRemoved"].forEach(eventName => {
                this.editor.on(eventName, () => this.changed());
            });

            this.editor.on("connectionRemoved", detail => {
                this.connectionState.delete(this.connectionKey(detail.output_id, detail.input_id, detail.output_class, detail.input_class));
                this.changed();
            });

            this.editor.on("nodeMoved", internalId => {
                this.handleNodeMoved(internalId);
                this.changed();
                const externalId = this.internalToExternal.get(String(internalId));
                if (externalId && this.selected?.kind === "node" && this.selected.id === externalId) {
                    this.callbacks.onSelectNode?.(this.getNode(externalId));
                }
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
                this.selectedGroupId = this.nodeData(internalId)?.type === "Section" ? id : null;
                this.refreshGroupAppearance();
                if (id) this.callbacks.onSelectNode?.(this.getNode(id));
            });

            this.editor.on("nodeUnselected", () => {
                if (this.selected?.kind === "node") {
                    this.selected = null;
                    this.selectedGroupId = null;
                    this.refreshGroupAppearance();
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

            this.element.addEventListener("pointerdown", event => this.beginResize(event), true);
            this.element.addEventListener("pointerdown", event => this.beginPan(event), true);
            this.element.addEventListener("mousedown", event => {
                const panning = (this.spacePanning && event.button === 0) || event.button === 1 || event.button === 2;
                if (!panning) return;
                event.preventDefault();
                event.stopImmediatePropagation();
            }, true);
            this.element.addEventListener("contextmenu", event => {
                event.preventDefault();
                event.stopImmediatePropagation();
            }, true);
            this.element.addEventListener("wheel", event => {
                if (!this.readOnly || event.ctrlKey) return;
                event.preventDefault();
                event.stopImmediatePropagation();
                if (event.deltaY < 0) this.editor.zoom_in();
                else if (event.deltaY > 0) this.editor.zoom_out();
            }, { capture: true, passive: false });
        }

        installArrowMarker() {
            if (document.getElementById("fd-connection-arrow")) return;
            const namespace = "http://www.w3.org/2000/svg";
            const svg = document.createElementNS(namespace, "svg");
            svg.classList.add("fd-connection-definitions");
            const definitions = document.createElementNS(namespace, "defs");
            const tones = { "": "#82958f", blue: "#1769aa", green: "#168451", red: "#c93642", orange: "#d85612", purple: "#7141ad" };
            for (const [tone, color] of Object.entries(tones)) {
                const marker = document.createElementNS(namespace, "marker");
                marker.id = tone ? `fd-connection-arrow-${tone}` : "fd-connection-arrow";
                marker.setAttribute("viewBox", "0 0 10 10");
                marker.setAttribute("refX", "9");
                marker.setAttribute("refY", "5");
                marker.setAttribute("markerWidth", "7");
                marker.setAttribute("markerHeight", "7");
                marker.setAttribute("orient", "auto-start-reverse");
                const arrow = document.createElementNS(namespace, "path");
                arrow.setAttribute("d", "M 0 0 L 10 5 L 0 10 z");
                arrow.setAttribute("fill", color);
                marker.appendChild(arrow);
                definitions.appendChild(marker);
            }
            svg.appendChild(definitions);
            this.element.appendChild(svg);
        }

        beginPan(event) {
            const enabled = (this.spacePanning && event.button === 0) || event.button === 1 || event.button === 2;
            if (!enabled || event.target.closest?.(".fd-resize-handle")) return;

            event.preventDefault();
            event.stopImmediatePropagation();
            const startX = event.clientX;
            const startY = event.clientY;
            const canvasX = this.editor.canvas_x;
            const canvasY = this.editor.canvas_y;
            this.element.classList.add("is-panning");

            const move = moveEvent => {
                moveEvent.preventDefault();
                this.editor.canvas_x = canvasX + moveEvent.clientX - startX;
                this.editor.canvas_y = canvasY + moveEvent.clientY - startY;
                this.editor.precanvas.style.transform = `translate(${this.editor.canvas_x}px, ${this.editor.canvas_y}px) scale(${this.editor.zoom})`;
                this.editor.precanvas.style.transformOrigin = "0 0";
            };
            const finish = () => {
                window.removeEventListener("pointermove", move);
                window.removeEventListener("pointerup", finish);
                window.removeEventListener("pointercancel", finish);
                this.element.classList.remove("is-panning");
            };

            window.addEventListener("pointermove", move, { passive: false });
            window.addEventListener("pointerup", finish);
            window.addEventListener("pointercancel", finish);
        }

        beginResize(event) {
            if (this.readOnly) return;
            const handle = event.target.closest?.(".fd-resize-handle");
            const nodeElement = handle?.closest(".drawflow-node");
            if (!handle || !nodeElement || event.button !== 0) return;

            event.preventDefault();
            event.stopImmediatePropagation();
            const internalId = nodeElement.id.slice(5);
            const startX = event.clientX;
            const startY = event.clientY;
            const startWidth = nodeElement.offsetWidth;
            const startHeight = nodeElement.offsetHeight;
            const startLeft = nodeElement.offsetLeft;
            const startTop = nodeElement.offsetTop;
            const direction = handle.dataset.resizeDirection || "se";
            const isSection = nodeElement.classList.contains("fd-shape-section");
            const isConnector = nodeElement.classList.contains("fd-shape-connector");
            const isAnnotation = nodeElement.classList.contains("fd-shape-annotation");
            const minimumWidth = isSection ? 260 : isConnector || isAnnotation ? 180 : Math.min(150, startWidth);
            const minimumHeight = isSection ? 180 : isConnector ? 120 : 80;
            nodeElement.classList.add("is-resizing");

            const move = moveEvent => {
                moveEvent.preventDefault();
                const zoom = this.editor.zoom || 1;
                const deltaX = (moveEvent.clientX - startX) / zoom;
                const deltaY = (moveEvent.clientY - startY) / zoom;
                const resizeWest = direction.includes("w");
                const resizeNorth = direction.includes("n");
                const resizeHorizontal = resizeWest || direction.includes("e");
                const resizeVertical = resizeNorth || direction.includes("s");
                const width = resizeHorizontal ? Math.max(minimumWidth, startWidth + (resizeWest ? -deltaX : deltaX)) : startWidth;
                const height = resizeVertical ? Math.max(minimumHeight, startHeight + (resizeNorth ? -deltaY : deltaY)) : startHeight;

                nodeElement.style.width = `${width}px`;
                nodeElement.style.height = `${height}px`;
                if (resizeWest) {
                    nodeElement.style.left = `${startLeft + startWidth - width}px`;
                    this.editor.drawflow.drawflow.Home.data[internalId].pos_x = startLeft + startWidth - width;
                }
                if (resizeNorth) {
                    nodeElement.style.top = `${startTop + startHeight - height}px`;
                    this.editor.drawflow.drawflow.Home.data[internalId].pos_y = startTop + startHeight - height;
                }
                this.editor.updateConnectionNodes(`node-${internalId}`);
            };
            const finish = () => {
                window.removeEventListener("pointermove", move);
                window.removeEventListener("pointerup", finish);
                window.removeEventListener("pointercancel", finish);
                nodeElement.classList.remove("is-resizing");
                this.ensureNodeFitsContent(internalId);
                this.captureNodeSize(internalId);
                const node = this.editor.drawflow.drawflow.Home.data[internalId];
                this.nodePositions.set(internalId, { x: node.pos_x, y: node.pos_y });
                if (isSection && this.reconcileSectionMembership(false)) this.changed();
                else if (!isSection && this.updateNodeSectionMembership(internalId)) this.changed();
                this.refreshGroupAppearance();
                this.editor.updateConnectionNodes(`node-${internalId}`);
            };

            window.addEventListener("pointermove", move, { passive: false });
            window.addEventListener("pointerup", finish);
            window.addEventListener("pointercancel", finish);
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
            this.nodePositions.clear();
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
                this.reconcileSectionMembership(false, true);
                this.updateAllConnections();
                this.refreshConnectionLabels();
                this.refreshGroupAppearance();
            }, 0);
        }

        addNode(type, x, y, supplied = {}, emitChange = true) {
            const resolvedType = nodeTypes[type] ? type : "Process";
            const config = nodeTypes[resolvedType];
            const externalId = supplied.id || newId("node");
            const data = {
                externalId,
                type: resolvedType,
                title: supplied.title || config.defaultTitle || config.title,
                description: supplied.description || "",
                childFlowId: supplied.childFlowId || null,
                metadata: supplied.metadata || {},
                customProperties: supplied.customProperties || {},
                comments: supplied.comments || [],
                width: supplied.width ?? null,
                height: supplied.height ?? null
            };
            if (!data.customProperties.portLayout && emitChange) {
                data.customProperties = { ...data.customProperties, portLayout: this.defaultPortLayout };
            }
            const inputCount = this.portCount(config.inputs, data.customProperties.inputPins);
            const outputCount = this.portCount(config.outputs, data.customProperties.outputPins);
            const previousSuppress = this.suppressChanges;
            if (!emitChange) this.suppressChanges = true;
            const internalId = this.editor.addNode(
                externalId,
                inputCount,
                outputCount,
                Math.max(10, Number(x) || 100),
                Math.max(10, Number(y) || 100),
                `fd-node-${data.type.toLowerCase()} fd-shape-${config.shape}${nodeAppearanceClasses(data)}`,
                data,
                this.nodeHtml(data),
                false
            );
            this.externalToInternal.set(externalId, String(internalId));
            this.internalToExternal.set(String(internalId), externalId);
            this.nodePositions.set(String(internalId), { x: Math.max(10, Number(x) || 100), y: Math.max(10, Number(y) || 100) });
            this.positionShapePorts(String(internalId));
            this.suppressChanges = previousSuppress;
            if (emitChange) {
                this.updateNodeSectionMembership(String(internalId));
                this.refreshGroupAppearance();
                this.changed();
            }
            return externalId;
        }

        nodeHtml(data) {
            const config = nodeTypes[data.type] || nodeTypes.Process;
            if (config.shape === "section") {
                const description = data.description ? `<span class="fd-section-description">${text(data.description)}</span>` : "";
                return `<div class="fd-section-content"><span class="fd-section-title">${text(data.title)}</span>${description}${resizeHandles("Drag an edge or corner to resize the section")}</div>`;
            }
            if (config.shape === "annotation") {
                const description = data.description ? `<span class="fd-annotation-description">${text(data.description)}</span>` : "";
                return `<div class="fd-annotation-content"><span class="fd-annotation-title">${text(data.title)}</span>${description}${resizeHandles("Drag an edge or corner to resize the annotation")}</div>`;
            }
            const commentCount = data.comments?.length || 0;
            const comments = commentCount ? `<span class="fd-node-comments" title="${commentCount} comment${commentCount === 1 ? "" : "s"}">${commentCount}</span>` : "";
            const description = data.description ? `<span class="fd-node-description">${text(data.description)}</span>` : "";
            const drilldown = data.childFlowId ? '<span class="fd-node-drilldown" title="Open detailed diagram" aria-hidden="true">&#8599;</span>' : "";
            const resize = data.type === "Connector" ? resizeHandles("Drag an edge or corner to resize the connector") : "";
            return `<div class="fd-node-content"><span class="fd-tool-icon fd-type-${data.type.toLowerCase()}">${config.icon}</span><span class="fd-node-copy"><span class="fd-node-type">${text(config.title)}</span><span class="fd-node-title">${text(data.title)}</span>${description}${comments}</span>${drilldown}</div>${resize}`;
        }

        getGraph() {
            const exported = this.editor.export()?.drawflow?.Home?.data || {};
            const nodes = Object.values(exported).map(node => {
                const data = node.data || {};
                const dom = document.getElementById(`node-${node.id}`);
                return {
                    id: data.externalId || this.internalToExternal.get(String(node.id)) || `node-${node.id}`,
                    type: nodeTypes[data.type] ? data.type : "Process",
                    title: data.title ?? "Untitled",
                    description: data.description || "",
                    x: node.pos_x,
                    y: node.pos_y,
                    width: dom ? dom.offsetWidth : data.width,
                    height: dom ? dom.offsetHeight : data.height,
                    childFlowId: data.childFlowId || null,
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

        getNodeFromElement(element) {
            const nodeElement = element?.closest?.(".drawflow-node");
            if (!nodeElement?.id?.startsWith("node-")) return null;
            const externalId = this.internalToExternal.get(nodeElement.id.slice(5));
            return externalId ? this.getNode(externalId) : null;
        }

        nodeData(internalId) {
            return this.editor.drawflow.drawflow.Home.data[String(internalId)]?.data || null;
        }

        updateNode(externalId, changes, emitChange = true) {
            const internalId = this.externalToInternal.get(externalId);
            if (!internalId) return;
            const node = this.editor.drawflow.drawflow.Home.data[internalId];
            const nextData = { ...node.data, ...changes };
            this.syncNodePorts(internalId, nextData);
            node.data = nextData;
            this.refreshNodeAppearance(internalId, node.data);
            this.positionShapePorts(internalId);
            const content = document.querySelector(`#node-${internalId} .drawflow_content_node`);
            if (content) content.innerHTML = this.nodeHtml(node.data);
            window.requestAnimationFrame(() => {
                this.ensureNodeFitsContent(internalId);
                this.editor.updateConnectionNodes(`node-${internalId}`);
                this.refreshConnectionLabels();
            });
            this.refreshGroupAppearance();
            if (emitChange) this.changed();
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

        updateConnectionTone(connection, tone) {
            const source = this.externalToInternal.get(connection.sourceNodeId);
            const target = this.externalToInternal.get(connection.targetNodeId);
            if (!source || !target) return;
            const key = this.connectionKey(source, target, connection.sourcePort, connection.targetPort);
            const state = this.connectionState.get(key) || { id: connection.id || newId("connection"), label: connection.label || "", metadata: {} };
            state.metadata = { ...(state.metadata || {}), tone };
            this.connectionState.set(key, state);
            this.refreshConnectionLabels();
            this.changed();
        }

        selectNode(externalId) {
            const internalId = this.externalToInternal.get(externalId);
            const element = internalId ? document.getElementById(`node-${internalId}`) : null;
            if (!element) return false;
            this.editor.ele_selected?.classList.remove("selected");
            element.classList.add("selected");
            this.editor.ele_selected = element;
            this.editor.node_selected = element.id;
            this.selected = { kind: "node", id: externalId };
            this.selectedGroupId = this.nodeData(internalId)?.type === "Section" ? externalId : null;
            this.refreshGroupAppearance();
            this.callbacks.onSelectNode?.(this.getNode(externalId));
            return true;
        }

        centerOnNode(externalId) {
            const internalId = this.externalToInternal.get(externalId);
            const node = internalId ? this.editor.drawflow.drawflow.Home.data[internalId] : null;
            const element = internalId ? document.getElementById(`node-${internalId}`) : null;
            if (!node || !element) return false;

            const canvasRect = this.element.getBoundingClientRect();
            const zoom = this.editor.zoom || 1;
            this.editor.canvas_x = canvasRect.width / 2 - (node.pos_x + element.offsetWidth / 2) * zoom;
            this.editor.canvas_y = canvasRect.height / 2 - (node.pos_y + element.offsetHeight / 2) * zoom;
            this.editor.precanvas.style.transform = `translate(${this.editor.canvas_x}px, ${this.editor.canvas_y}px) scale(${zoom})`;
            this.editor.precanvas.style.transformOrigin = "0 0";
            return true;
        }

        deleteSelected() {
            if (this.readOnly || !this.selected) return false;
            if (this.selected.kind === "node") {
                const internalId = this.externalToInternal.get(this.selected.id);
                if (!internalId) return false;
                if (this.nodeData(internalId)?.type === "Section") this.clearSectionMembership(this.selected.id);
                this.editor.removeNodeId(`node-${internalId}`);
                this.externalToInternal.delete(this.selected.id);
                this.internalToExternal.delete(internalId);
                this.nodePositions.delete(internalId);
            } else {
                const parts = this.parseConnectionKey(this.selected.key);
                this.editor.removeSingleConnection(parts.source, parts.target, parts.sourcePort, parts.targetPort);
                this.connectionState.delete(this.selected.key);
            }
            this.selected = null;
            this.selectedGroupId = null;
            this.refreshGroupAppearance();
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
        setZoom(value) {
            if (value === null || value === undefined || value === "") return false;

            const zoom = Number(value);
            if (!Number.isFinite(zoom)) return false;

            this.editor.zoom = Math.min(
                this.editor.zoom_max,
                Math.max(this.editor.zoom_min, zoom));
            this.editor.zoom_refresh();
            return true;
        }
        getZoom() { return this.editor.zoom; }
        setGrid(enabled) { this.snapToGrid = enabled; this.element.classList.toggle("no-grid", !enabled); }
        setSpacePanning(enabled) { this.spacePanning = enabled; this.element.classList.toggle("is-space-pan", enabled); }
        setDefaultPortLayout(layout) { this.defaultPortLayout = normalizePortLayout(layout); }

        setReadOnly(enabled) {
            this.readOnly = Boolean(enabled);
            this.editor.editor_mode = this.readOnly ? "view" : "edit";
            this.element.classList.toggle("is-read-only", this.readOnly);
            if (!this.readOnly) return;

            this.element.querySelectorAll(".selected").forEach(element => element.classList.remove("selected"));
            this.editor.ele_selected = null;
            this.editor.node_selected = null;
            this.editor.connection_selected = null;
            this.selected = null;
            this.selectedGroupId = null;
            this.refreshGroupAppearance();
            this.callbacks.onClearSelection?.();
        }

        handleNodeMoved(internalId) {
            const id = String(internalId);
            const node = this.editor.drawflow.drawflow.Home.data[id];
            if (!node) return;
            const previous = this.nodePositions.get(id) || { x: node.pos_x, y: node.pos_y };
            if (this.snapToGrid) this.snapNode(id);
            const current = { x: node.pos_x, y: node.pos_y };
            if (node.data?.type === "Section") {
                this.moveSectionMembers(node.data.externalId, current.x - previous.x, current.y - previous.y);
            } else {
                this.updateNodeSectionMembership(id);
            }
            this.nodePositions.set(id, current);
            this.refreshGroupAppearance();
        }

        moveSectionMembers(sectionId, deltaX, deltaY) {
            if (!sectionId || (!deltaX && !deltaY)) return;
            for (const [internalId, node] of Object.entries(this.editor.drawflow.drawflow.Home.data)) {
                if (node.data?.type === "Section" || node.data?.customProperties?.sectionId !== sectionId) continue;
                node.pos_x += deltaX;
                node.pos_y += deltaY;
                const element = document.getElementById(`node-${internalId}`);
                if (element) {
                    element.style.left = `${node.pos_x}px`;
                    element.style.top = `${node.pos_y}px`;
                }
                this.nodePositions.set(String(internalId), { x: node.pos_x, y: node.pos_y });
                this.editor.updateConnectionNodes(`node-${internalId}`);
            }
        }

        reconcileSectionMembership(emitChange = false, onlyMissing = false) {
            let changed = false;
            for (const [internalId, node] of Object.entries(this.editor.drawflow.drawflow.Home.data)) {
                if (node.data?.type === "Section") continue;
                const existing = node.data?.customProperties?.sectionId;
                if (onlyMissing && existing && this.externalToInternal.has(existing)) continue;
                changed = this.updateNodeSectionMembership(internalId) || changed;
            }
            this.refreshGroupAppearance();
            if (changed && emitChange) this.changed();
            return changed;
        }

        updateNodeSectionMembership(internalId) {
            const id = String(internalId);
            const node = this.editor.drawflow.drawflow.Home.data[id];
            if (!node || node.data?.type === "Section") return false;
            const element = document.getElementById(`node-${id}`);
            const centerX = node.pos_x + (element?.offsetWidth || node.data?.width || 184) / 2;
            const centerY = node.pos_y + (element?.offsetHeight || node.data?.height || 76) / 2;
            const containingSections = Object.entries(this.editor.drawflow.drawflow.Home.data)
                .filter(([, candidate]) => candidate.data?.type === "Section")
                .map(([sectionInternalId, section]) => {
                    const sectionElement = document.getElementById(`node-${sectionInternalId}`);
                    const width = sectionElement?.offsetWidth || section.data?.width || 420;
                    const height = sectionElement?.offsetHeight || section.data?.height || 300;
                    return { id: section.data.externalId, x: section.pos_x, y: section.pos_y, width, height, area: width * height };
                })
                .filter(section => centerX >= section.x && centerX <= section.x + section.width && centerY >= section.y && centerY <= section.y + section.height)
                .sort((left, right) => left.area - right.area);
            const sectionId = containingSections[0]?.id || "";
            const currentSectionId = node.data.customProperties?.sectionId || "";
            if (sectionId === currentSectionId) return false;
            node.data.customProperties = { ...(node.data.customProperties || {}) };
            if (sectionId) node.data.customProperties.sectionId = sectionId;
            else delete node.data.customProperties.sectionId;
            return true;
        }

        clearSectionMembership(sectionId) {
            for (const node of Object.values(this.editor.drawflow.drawflow.Home.data)) {
                if (node.data?.customProperties?.sectionId !== sectionId) continue;
                node.data.customProperties = { ...(node.data.customProperties || {}) };
                delete node.data.customProperties.sectionId;
            }
        }

        refreshGroupAppearance() {
            const validSections = new Set(Object.values(this.editor.drawflow.drawflow.Home.data)
                .filter(node => node.data?.type === "Section")
                .map(node => node.data.externalId));
            for (const [internalId, node] of Object.entries(this.editor.drawflow.drawflow.Home.data)) {
                const element = document.getElementById(`node-${internalId}`);
                if (!element) continue;
                element.classList.remove("fd-group-member", "fd-group-highlight", "fd-group-selected");
                if (node.data?.type === "Section") {
                    element.classList.toggle("fd-group-selected", node.data.externalId === this.selectedGroupId);
                    continue;
                }
                const sectionId = node.data?.customProperties?.sectionId;
                if (!sectionId || !validSections.has(sectionId)) continue;
                element.classList.add("fd-group-member");
                if (sectionId === this.selectedGroupId) element.classList.add("fd-group-highlight");
            }
        }

        refreshNodeAppearance(internalId, data) {
            const element = document.getElementById(`node-${internalId}`);
            if (!element) return;
            for (const className of [...element.classList]) {
                if (className.startsWith("fd-tone-") || className.startsWith("fd-layer-") || className.startsWith("fd-ports-") || className.startsWith("fd-input-") || className.startsWith("fd-output-") || className.startsWith("fd-style-") || className === "fd-has-child") element.classList.remove(className);
            }
            nodeAppearanceClasses(data).trim().split(/\s+/).filter(Boolean).forEach(className => element.classList.add(className));
        }

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
            for (const id of Object.keys(exported)) this.captureNodeSize(id);
        }

        captureNodeSize(internalId) {
            const node = this.editor.drawflow.drawflow.Home.data[String(internalId)];
            const dom = document.getElementById(`node-${internalId}`);
            if (!node || !dom) return;
            const width = dom.offsetWidth;
            const height = dom.offsetHeight;
            if (node.data.width !== width || node.data.height !== height) {
                node.data.width = width;
                node.data.height = height;
                this.changed();
            }
        }

        ensureNodeFitsContent(internalId) {
            const id = String(internalId);
            const node = this.editor.drawflow.drawflow.Home.data[id];
            const dom = document.getElementById(`node-${id}`);
            if (!node || !dom || node.data.type === "Section") return false;

            const content = dom.querySelector(":scope > .drawflow_content_node");
            if (!content) return false;

            const borderHeight = Math.max(0, dom.offsetHeight - dom.clientHeight);
            const requiredHeight = Math.ceil(content.scrollHeight + borderHeight);
            if (requiredHeight <= dom.offsetHeight) return false;

            dom.style.height = `${requiredHeight}px`;
            node.data.height = dom.offsetHeight;
            return true;
        }

        applySavedSizes(nodes) {
            for (const node of nodes) {
                const internal = this.externalToInternal.get(node.id);
                const dom = internal ? document.getElementById(`node-${internal}`) : null;
                if (!dom) continue;
                const resizablePresentationNode = node.type === "Section" || node.type === "Annotation";
                const hasResizeHandle = Boolean(dom.querySelector(".fd-resize-handle"));
                const browserResizableNode = getComputedStyle(dom).resize !== "none";
                const isResizable = resizablePresentationNode || hasResizeHandle || browserResizableNode;
                const minimumWidth = node.type === "Section" ? 260 : node.type === "Connector" ? 180 : 150;
                const minimumHeight = node.type === "Section" ? 180 : node.type === "Connector" ? 120 : 76;
                const legacyNarrowConnector = node.type === "Connector" && node.width && node.width < minimumWidth;
                if (node.width && isResizable) dom.style.width = `${Math.max(minimumWidth, node.width)}px`;
                if (node.height && isResizable && !legacyNarrowConnector) dom.style.height = `${Math.max(minimumHeight, node.height)}px`;
                this.ensureNodeFitsContent(internal);
            }
        }

        positionShapePorts(internalId) {
            const node = this.editor.drawflow.drawflow.Home.data[String(internalId)];
            const element = document.getElementById(`node-${internalId}`);
            const shape = nodeTypes[node?.data?.type]?.shape;
            if (!node || !element || !["decision", "gateway"].includes(shape)) return;

            const sides = portSides(node.data);
            this.positionDiamondPortKind(element.querySelector(":scope > .inputs"), sides.input);
            this.positionDiamondPortKind(element.querySelector(":scope > .outputs"), sides.output);
        }

        positionDiamondPortKind(container, side) {
            const ports = [...(container?.children || [])];
            ports.forEach((port, index) => {
                const point = diamondPortPoint(side, (index + 1) / (ports.length + 1));
                port.style.position = "absolute";
                port.style.left = `${point.x * 100}%`;
                port.style.top = `${point.y * 100}%`;
                port.style.transform = "translate(-50%, -50%)";
            });
        }

        updateAllConnections() {
            for (const internalId of this.internalToExternal.keys()) {
                this.positionShapePorts(internalId);
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
                    this.refreshConnectionGeometry(svg, parts);
                    const path = svg.querySelector(".main-path");
                    if (!path) continue;
                    for (const className of [...path.classList]) {
                        if (className.startsWith("fd-connection-tone-")) path.classList.remove(className);
                    }
                    const tone = String(state.metadata?.tone || "").toLowerCase();
                    if (["blue", "green", "red", "orange", "purple"].includes(tone)) path.classList.add(`fd-connection-tone-${tone}`);
                    if (!state.label) continue;
                    let point;
                    try {
                        point = path.getPointAtLength(path.getTotalLength() / 2);
                    } catch {
                        continue;
                    }
                    const label = document.createElementNS("http://www.w3.org/2000/svg", "text");
                    label.setAttribute("class", "fd-connection-label");
                    label.setAttribute("x", point.x);
                    label.setAttribute("y", point.y - 7);
                    label.setAttribute("text-anchor", "middle");
                    label.setAttribute("dominant-baseline", "central");
                    label.setAttribute("data-tone", ["blue", "green", "red", "orange", "purple"].includes(tone) ? tone : "default");
                    label.textContent = state.label;
                    svg.appendChild(label);
                }
            });
        }

        refreshConnectionGeometry(svg, parts) {
            const paths = [...svg.querySelectorAll(".main-path")];
            if (paths.length !== 1) return;

            const sourceSide = portSides(this.nodeData(parts.source)).output;
            const targetSide = portSides(this.nodeData(parts.target)).input;

            const path = paths[0];
            const coordinates = (path.getAttribute("d") || "")
                .match(/-?\d*\.?\d+(?:e[-+]?\d+)?/gi)
                ?.map(Number);
            if (!coordinates || coordinates.length < 4 || coordinates.some(value => !Number.isFinite(value))) return;

            const startX = coordinates[0];
            const startY = coordinates[1];
            const endX = coordinates.at(-2);
            const endY = coordinates.at(-1);
            path.setAttribute("d", connectionPath(startX, startY, endX, endY, sourceSide, targetSide));
        }

        portCount(defaultCount, configuredCount) {
            if (defaultCount === 0) return 0;
            const count = Number(configuredCount);
            return Number.isInteger(count) && count >= 1 && count <= 6 ? count : defaultCount;
        }

        getPortCount(externalId, kind) {
            const internalId = this.externalToInternal.get(externalId);
            const node = internalId ? this.editor.drawflow.drawflow.Home.data[internalId] : null;
            return node ? Object.keys(kind === "output" ? node.outputs : node.inputs).length : 0;
        }

        syncNodePorts(internalId, data) {
            const config = nodeTypes[data.type] || nodeTypes.Process;
            this.syncPortKind(internalId, "input", this.portCount(config.inputs, data.customProperties?.inputPins));
            this.syncPortKind(internalId, "output", this.portCount(config.outputs, data.customProperties?.outputPins));
        }

        syncPortKind(internalId, kind, desiredCount) {
            const node = this.editor.drawflow.drawflow.Home.data[internalId];
            const collection = kind === "output" ? node.outputs : node.inputs;
            let currentCount = Object.keys(collection).length;
            while (currentCount < desiredCount) {
                if (kind === "output") this.editor.addNodeOutput(internalId);
                else this.editor.addNodeInput(internalId);
                currentCount++;
            }
            while (currentCount > desiredCount) {
                const portName = `${kind}_${currentCount}`;
                if (kind === "output") this.editor.removeNodeOutput(internalId, portName);
                else this.editor.removeNodeInput(internalId, portName);
                currentCount--;
            }
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

    window.FlowDesignerAdapters = { DrawflowAdapter, nodeTypes, diagramTypes, palettes, connectionPath, diamondPortPoint, normalizePortLayout, portSides };
})();
