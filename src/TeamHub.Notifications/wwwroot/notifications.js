(() => {
    const inboxes = Array.from(document.querySelectorAll("[data-notification-inbox]"));
    if (!inboxes.length) return;

    function formatUtc(value) {
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return "";
        return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, "0")}-${String(date.getUTCDate()).padStart(2, "0")} ${String(date.getUTCHours()).padStart(2, "0")}:${String(date.getUTCMinutes()).padStart(2, "0")} UTC`;
    }

    function createNotification(item) {
        const link = document.createElement("a");
        link.className = "teamhub-notification-item";
        link.href = typeof item.openUrl === "string" ? item.openUrl : "/Notifications";

        const type = document.createElement("span");
        type.className = "teamhub-notification-type";
        type.textContent = item.type || "Notification";
        const subject = document.createElement("strong");
        subject.textContent = item.subject || "Workflow notification";
        link.append(type, subject);

        if (item.bodyPreview) {
            const preview = document.createElement("span");
            preview.textContent = item.bodyPreview;
            link.append(preview);
        }

        const time = document.createElement("time");
        time.dateTime = item.createdAtUtc || "";
        time.textContent = formatUtc(item.createdAtUtc);
        link.append(time);
        return link;
    }

    function updateInbox(root, payload) {
        const unreadCount = Number.isFinite(Number(payload.unreadCount))
            ? Math.max(0, Number(payload.unreadCount))
            : 0;
        const notifications = Array.isArray(payload.notifications) ? payload.notifications : [];
        const bell = root.querySelector("[data-notification-bell]");
        const headingCount = root.querySelector("[data-notification-heading-count]");
        const list = root.querySelector("[data-notification-list]");
        const empty = root.querySelector("[data-notification-empty]");

        if (bell) {
            bell.setAttribute("aria-label", `Notifications, ${unreadCount} unread`);
            let badge = bell.querySelector("[data-notification-count]");
            if (unreadCount > 0) {
                if (!badge) {
                    badge = document.createElement("span");
                    badge.className = "teamhub-notification-count";
                    badge.setAttribute("data-notification-count", "");
                    bell.append(badge);
                }
                badge.textContent = unreadCount > 99 ? "99+" : String(unreadCount);
            } else {
                badge?.remove();
            }
        }

        if (headingCount) headingCount.textContent = `${unreadCount} unread`;
        if (list) {
            list.replaceChildren(...notifications.map(createNotification));
            list.hidden = notifications.length === 0;
        }
        if (empty) empty.hidden = notifications.length > 0;
    }

    async function refreshInbox(root) {
        if (root.dataset.notificationLoading === "true") return;
        const url = root.dataset.inboxUrl;
        if (!url) return;
        root.dataset.notificationLoading = "true";
        try {
            const response = await fetch(url, {
                cache: "no-store",
                headers: { "Accept": "application/json" }
            });
            if (!response.ok) return;
            updateInbox(root, await response.json());
        } catch {
            // Keep the server-rendered inbox when a refresh cannot be completed.
        } finally {
            delete root.dataset.notificationLoading;
        }
    }

    function refreshAll() {
        for (const inbox of inboxes) refreshInbox(inbox);
    }

    window.addEventListener("pageshow", refreshAll);
    document.addEventListener("shown.bs.dropdown", event => {
        const inbox = event.target.closest?.("[data-notification-inbox]");
        if (inbox) refreshInbox(inbox);
    });
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "visible") refreshAll();
    });
})();
