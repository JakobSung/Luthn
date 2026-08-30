const state = {
  consoleSession: null,
  csrfProof: "",
  localConnectPending: false,
  localAccessError: "",
  selectedAccessRequestId: "",
  selectedAccessDetail: null,
  accessDetailRequestSequence: 0,
  accessDecisionPending: false,
  accessPolicyPending: false,
  auditEvents: [],
  selectedAuditEvent: null,
  auditNextCursor: "",
  auditBaseQuery: "",
  consoleProfile: null,
  mcpProfiles: null,
  managedExtensionOffer: null,
  managedExtensionActionId: "",
  managedExtensionPollTimer: null,
  managedExtensionApproved: false,
  remoteProfileOffer: null,
  remoteProfileActionId: "",
  remoteProfilePollTimer: null,
  localProfileActionId: "",
  localProfilePollTimer: null
};

const $ = (selector) => document.querySelector(selector);
const i18n = window.LuthnOperatorI18n;
const t = (key) => i18n?.translate(key) || key;

const renderLocalAccess = () => {
  const button = $("#connectLocal");
  const detail = $("#localAccessDetail");
  if (!button || !detail) {
    return;
  }

  const session = state.consoleSession;
  const localMode = session?.mode === "LocalAuto";
  const connectable = localMode &&
    session?.state === "Anonymous" &&
    ["arm-local-session", "await-host-helper", "create-local-session"].includes(session?.nextAction);
  button.hidden = !localMode;
  button.disabled = state.localConnectPending || !connectable;

  if (state.localAccessError && !state.localConnectPending) {
    detail.textContent = state.localAccessError;
  } else if (state.localConnectPending || ["await-host-helper", "create-local-session"].includes(session?.nextAction)) {
    detail.textContent = t("auth.localConnectingDetail");
  } else if (session?.state === "Active" && localMode) {
    detail.textContent = t("auth.localActiveDetail");
  } else if (session?.nextAction === "arm-local-session") {
    detail.textContent = t("auth.localArmRequired");
  } else if (connectable) {
    detail.textContent = t("auth.localReadyDetail");
  } else {
    detail.textContent = t("auth.localUnavailable");
  }
};

const setConsoleView = (view) => {
  document.querySelectorAll("[data-console-view]").forEach((section) => {
    section.hidden = section.dataset.consoleView !== view;
  });

  document.querySelectorAll("[data-console-nav]").forEach((control) => {
    const active = control.dataset.consoleNav === view;
    if (control.classList.contains("nav-tab")) {
      control.classList.toggle("active", active);
      if (active) {
        control.setAttribute("aria-current", "page");
      } else {
        control.removeAttribute("aria-current");
      }
    }
  });

  const activePanel = document.querySelector('[data-console-view="' + view + '"]');
  activePanel?.scrollIntoView({ behavior: "smooth", block: "start" });
};

const renderAuthStatus = () => {
  const statusKey = state.localConnectPending || state.consoleSession?.nextAction === "create-local-session"
    ? "auth.localConnecting"
    : state.consoleSession?.state === "Active"
    ? "auth.active"
    : state.consoleSession?.mode === "LocalAuto" && state.consoleSession?.nextAction === "arm-local-session"
      ? "auth.localReady"
      : "auth.starting";
  const status = t(statusKey);
  ["#authStatus", "#authPanelStatus"].forEach((selector) => {
    const target = $(selector);
    if (target) {
      target.textContent = status;
      target.classList.toggle("configured", statusKey === "auth.active");
      target.classList.remove("partial");
    }
  });
  renderLocalAccess();
};

const renderSessionGuidance = () => {
  const guidance = "Local console access is required to view agent connections.";
  renderConnectionMessage(guidance);
  $("#connectionsStatus").textContent = "Access required";
  renderMcpProfileMessage("Local console access is required to view MCP profiles.");
  $("#mcpProfilesStatus").textContent = "Access required";
  $("#syncStatus").textContent = "Access required";
  writeResult($("#publicationOutput"), guidance);
  $("#providerStatus").textContent = "Console access required";
  writeResult($("#providerOutput"), guidance);
  const providerForm = $("#providerForm");
  if (providerForm) {
    providerForm.endpoint.value = "";
  }
  renderAccessRows([]);
  clearAccessDetail(guidance);
  state.auditEvents = [];
  state.selectedAuditEvent = null;
  state.auditNextCursor = "";
  state.auditBaseQuery = "";
  renderAuditRows([]);
  renderAuditDetail(null);
  setAuditControlsEnabled(false);
  $("#auditStatus").textContent = "Console access is required to review audit metadata.";
};

const writeResult = (target, value) => {
  target.textContent = typeof value === "string"
    ? value
    : JSON.stringify(value, null, 2);
};

const setAction = (label, detail) => {
  $("#lastAction").textContent = label;
  $("#lastActionDetail").textContent = detail;
};

const renderReadinessChecks = (checks) => {
  const target = $("#readinessChecks");
  if (!Array.isArray(checks) || checks.length === 0) {
    target.replaceChildren(Object.assign(document.createElement("span"), {
      className: "check-pill warning",
      textContent: "No readiness detail returned"
    }));
    return;
  }

  target.replaceChildren(...checks.map((check) => {
    const item = document.createElement("div");
    item.className = `check-pill ${check.status || "warning"}`;
    const name = document.createElement("strong");
    name.textContent = check.name || "check";
    const detail = document.createElement("span");
    detail.textContent = check.detail || check.status || "";
    item.append(name, detail);
    return item;
  }));
};

const requestJson = async (url, options = {}) => {
  const requestOptions = options;
  const method = (requestOptions.method || "GET").toUpperCase();
  const mutation = !["GET", "HEAD", "OPTIONS", "TRACE"].includes(method);
  const response = await fetch(url, {
    ...requestOptions,
    credentials: "same-origin",
    headers: {
      ...(requestOptions.body ? { "Content-Type": "application/json" } : {}),
      ...(mutation && state.csrfProof ? { "X-Luthn-CSRF": state.csrfProof } : {}),
      ...(requestOptions.headers || {})
    }
  });

  const nextCsrfProof = response.headers.get("X-Luthn-CSRF");
  if (nextCsrfProof) {
    state.csrfProof = nextCsrfProof;
  }

  const text = await response.text();
  const body = text ? JSON.parse(text) : null;
  if (!response.ok) {
    const message = body?.detail || body?.title || response.statusText;
    const error = new Error(`${response.status} ${message}`);
    error.body = body;
    throw error;
  }

  return body;
};

const hasConsoleSession = () => state.consoleSession?.state === "Active";

const setAuditControlsEnabled = (enabled) => {
  const refreshButton = $("#auditForm button[type=submit]");
  if (refreshButton) {
    refreshButton.disabled = !enabled;
  }
  $("#nextAuditPage").disabled = !enabled || !state.auditNextCursor;
  $("#exportAudit").disabled = !enabled;
};

const refreshConsoleSession = async () => {
  let session = await requestJson("/api/operator/session", { cache: "no-store" });
  if (session?.state === "Anonymous" && session?.nextAction === "create-local-session") {
    session = await requestJson("/api/operator/session/local", { method: "POST" });
  }

  state.consoleSession = session;
  if (session?.state === "Active") {
    state.localAccessError = "";
  }
  renderAuthStatus();
  const mode = $("#sessionMode");
  const expiry = $("#sessionExpiry");
  if (mode) {
    mode.textContent = session?.mode || "Unavailable";
  }
  if (expiry) {
    expiry.textContent = session?.idleExpiresAt
      ? formatTimestamp(session.idleExpiresAt)
      : "Login required";
  }
  return session;
};

const connectLocalAccess = async () => {
  state.localConnectPending = true;
  state.localAccessError = "";
  renderAuthStatus();
  try {
    let session = state.consoleSession;
    if (session?.nextAction === "arm-local-session") {
      await requestJson("/api/operator/session/local/request", { method: "POST" });
      for (let attempt = 0; attempt < 20; attempt += 1) {
        await new Promise((resolve) => window.setTimeout(resolve, 250));
        session = await refreshConsoleSession();
        if (session?.nextAction === "create-local-session" || session?.state === "Active") break;
      }
    }
    if (session?.state !== "Active" &&
        session?.nextAction === "create-local-session") {
      session = await requestJson("/api/operator/session/local/connect", { method: "POST" });
    }
    if (session?.state !== "Active") {
      throw new Error(t("auth.localArmRequired"));
    }
    state.consoleSession = session;
    state.localAccessError = "";
    renderAuthStatus();
    setAction("local access connected", "Local console authority is active");
    refreshAgentConnections();
    refreshMcpProfiles();
    refreshSyncStatus();
    refreshProviderSettings();
    refreshAccessPolicy();
    refreshAccessRequests();
    refreshAudit();
  } catch (error) {
    state.localAccessError = error.message;
    setAction("local access failed", error.message);
  } finally {
    state.localConnectPending = false;
    renderAuthStatus();
  }
};

const renderConsoleProfile = () => {
  const profile = state.consoleProfile;
  if (!profile) {
    $("#consoleMode").textContent = t("mode.checking");
    $("#consoleModeDetail").textContent = t("mode.unavailable");
    return;
  }

  const isMultiUser = profile.consoleMode === "MultiUser";
  $("#consoleMode").textContent = t(isMultiUser ? "mode.multiUser" : "mode.local");
  $("#consoleModeDetail").textContent = t(isMultiUser ? "mode.multiUserDetail" : "mode.localDetail");
  $("#consoleTransport").textContent = t("mode.transportDisabled");
  $("#consoleAuthority").textContent = t("mode.authorityOss");
};

const refreshConsoleProfile = async () => {
  try {
    const result = await requestJson("/api/operator/console-profile");
    if (
      !["Local", "MultiUser"].includes(result?.consoleMode) ||
      result?.outboundTransport !== "disabled" ||
      result?.sensitiveAuthority !== "oss-console" ||
      result?.tenancySource !== "authenticated-request" ||
      result?.serverDerived !== true
    ) {
      throw new Error("Console profile boundary is invalid.");
    }
    state.consoleProfile = {
      consoleMode: result.consoleMode,
      outboundTransport: result.outboundTransport,
      sensitiveAuthority: result.sensitiveAuthority
    };
  } catch {
    state.consoleProfile = null;
  }
  renderConsoleProfile();
};

const refreshStatus = async () => {
  try {
    const health = await requestJson("/healthz");
    $("#healthStatus").textContent = health.status;
    $("#healthDetail").textContent = "live";
  } catch (error) {
    $("#healthStatus").textContent = "down";
    $("#healthDetail").textContent = error.message;
  }

  try {
    const ready = await requestJson("/readyz");
    $("#readyStatus").textContent = ready.status;
    $("#readyDetail").textContent = ready.dependency;
    renderReadinessChecks(ready.checks);
  } catch (error) {
    $("#readyStatus").textContent = "not ready";
    $("#readyDetail").textContent = error.message;
    renderReadinessChecks(error.body?.checks || []);
  }
};

const knownConnectionStates = new Set([
  "active",
  "configured",
  "degraded",
  "disconnected",
  "unknown",
  "verified"
]);

const boundedText = (value, maxLength, fallback = "Unknown") => {
  const text = typeof value === "string" || typeof value === "number"
    ? String(value).trim()
    : "";

  if (!text) {
    return fallback;
  }

  return text.length <= maxLength
    ? text
    : `${text.slice(0, Math.max(0, maxLength - 3))}...`;
};

const formatTimestamp = (value) => {
  if (!value) {
    return "Never";
  }

  const timestamp = new Date(value);
  return Number.isNaN(timestamp.getTime())
    ? "Unknown"
    : timestamp.toLocaleString();
};

const createStatusBadge = (value) => {
  const label = boundedText(value, 32);
  const normalized = label.toLowerCase();
  const badge = document.createElement("span");
  badge.className = `status-badge status-${knownConnectionStates.has(normalized) ? normalized : "unknown"}`;
  badge.textContent = label;
  return badge;
};

const createConnectionCell = (label) => {
  const cell = document.createElement("td");
  cell.dataset.label = label;
  return cell;
};

const createChannelDetail = (label, value) => {
  const item = document.createElement("div");
  const term = document.createElement("dt");
  const detail = document.createElement("dd");
  term.textContent = label;
  detail.textContent = value;
  item.append(term, detail);
  return item;
};

const createChannelSummary = (channel) => {
  const summary = document.createElement("div");
  summary.className = "channel-summary";

  const heading = document.createElement("div");
  heading.className = "channel-heading";
  const name = document.createElement("strong");
  name.textContent = boundedText(channel?.channel, 64, "Unnamed channel");
  heading.append(name, createStatusBadge(channel?.state));

  let configured = "Unknown";
  if (channel?.configured === true) {
    configured = "Yes";
  } else if (channel?.configured === false) {
    configured = "No";
  }
  const details = document.createElement("dl");
  details.className = "channel-details";
  details.append(
    createChannelDetail("Configured", configured),
    createChannelDetail("Verification", boundedText(channel?.verificationState, 32)),
    createChannelDetail("Activity", boundedText(channel?.activityState, 32)),
    createChannelDetail("Last success", formatTimestamp(channel?.lastSuccessfulActivityAt))
  );

  summary.append(heading, details);

  const failureCode = boundedText(channel?.failureCode, 64, "");
  if (failureCode) {
    const failure = document.createElement("div");
    failure.className = "channel-failure";
    const label = document.createElement("span");
    label.textContent = "Failure";
    const code = document.createElement("code");
    code.textContent = failureCode;
    failure.append(label, code);
    summary.appendChild(failure);
  }

  return summary;
};

const createConnectionRow = (connection) => {
  const row = document.createElement("tr");

  const ownerCell = createConnectionCell("Owner");
  const owner = document.createElement("code");
  owner.textContent = boundedText(connection?.ownerUserId, 128, "Unknown owner");
  ownerCell.appendChild(owner);

  const agentCell = createConnectionCell("Agent");
  const identity = document.createElement("div");
  identity.className = "agent-identity";
  const agentName = document.createElement("strong");
  agentName.textContent = boundedText(connection?.agentName, 128, "Unnamed agent");
  const agentId = document.createElement("span");
  agentId.textContent = boundedText(connection?.agentId, 64, "Unknown id");
  identity.append(agentName, agentId);
  agentCell.appendChild(identity);

  const integrationCell = createConnectionCell("Integration");
  integrationCell.textContent = boundedText(connection?.integrationKind, 64);

  const stateCell = createConnectionCell("Overall state");
  stateCell.appendChild(createStatusBadge(connection?.state));

  const channelsCell = createConnectionCell("Channels");
  const channelList = document.createElement("div");
  channelList.className = "channel-list";
  const channels = Array.isArray(connection?.channels) ? connection.channels : [];
  if (channels.length === 0) {
    const empty = document.createElement("span");
    empty.className = "connection-muted";
    empty.textContent = "No channel observations";
    channelList.appendChild(empty);
  } else {
    channelList.append(...channels.map(createChannelSummary));
  }
  channelsCell.appendChild(channelList);

  const lastSuccessCell = createConnectionCell("Last success");
  lastSuccessCell.textContent = formatTimestamp(connection?.lastSuccessfulActivityAt);

  const versionCell = createConnectionCell("Version");
  const version = document.createElement("code");
  version.className = "connector-version";
  version.textContent = boundedText(connection?.connectorVersion, 64);
  versionCell.appendChild(version);

  row.append(
    ownerCell,
    agentCell,
    integrationCell,
    stateCell,
    channelsCell,
    lastSuccessCell,
    versionCell
  );
  return row;
};

const renderConnectionMessage = (message) => {
  const row = document.createElement("tr");
  row.className = "connection-message-row";
  const cell = document.createElement("td");
  cell.colSpan = 7;
  cell.textContent = message;
  row.appendChild(cell);
  $("#connectionRows").replaceChildren(row);
};

const renderAgentConnections = (connections) => {
  if (!Array.isArray(connections) || connections.length === 0) {
    renderConnectionMessage("No agent connections available.");
    return;
  }

  $("#connectionRows").replaceChildren(...connections.map(createConnectionRow));
};

const refreshAgentConnections = async () => {
  const refreshButton = $("#refreshConnections");
  refreshButton.disabled = true;
  $("#connectionsStatus").textContent = "Refreshing...";

  try {
    const result = await requestJson("/api/agent-connections");
    const connections = Array.isArray(result?.connections) ? result.connections : [];
    renderAgentConnections(connections);
    const label = `${connections.length} ${connections.length === 1 ? "connection" : "connections"}`;
    $("#connectionsStatus").textContent = label;
    setAction("connections refreshed", label);
  } catch {
    renderConnectionMessage("Agent connection status is unavailable.");
    if (!hasConsoleSession()) {
      renderConnectionMessage("Console login is required to view agent connections.");
      $("#connectionsStatus").textContent = "Login required";
      setAction("connections waiting", "Sign in through Console access");
    } else {
      $("#connectionsStatus").textContent = "Unavailable";
      setAction("connections failed", "Status unavailable");
    }
  } finally {
    refreshButton.disabled = false;
  }
};

const renderMcpProfileMessage = (message) => {
  const paragraph = document.createElement("p");
  paragraph.className = "empty-state";
  paragraph.textContent = message;
  $("#mcpProfileCards").replaceChildren(paragraph);
};

const renderMcpProfiles = (snapshot) => {
  state.mcpProfiles = snapshot;
  const clients = Array.isArray(snapshot?.clients) ? snapshot.clients : [];
  if (clients.length === 0) {
    renderMcpProfileMessage(snapshot?.helperOnline
      ? "No supported Agent MCP profiles were reported."
      : "The local Host Helper is offline or has not reported yet.");
  } else {
    const cards = clients.map((client) => {
      const card = document.createElement("article");
      card.className = "mcp-profile-card";
      const heading = document.createElement("div");
      heading.className = "channel-heading";
      const name = document.createElement("strong");
      name.textContent = client.agentKind === "claude" ? "Claude Code" : "Codex";
      heading.append(name, createStatusBadge(client.mode));

      const entries = document.createElement("dl");
      entries.className = "mcp-entry-list";
      const values = Array.isArray(client.entries) ? client.entries : [];
      if (values.length === 0) {
        entries.append(createChannelDetail("MCP", "No registrations"));
      } else {
        values.forEach((entry) => {
          const enabled = entry.enabled ? "enabled" : "disabled";
          const authority = entry.endpointHost ? ` · ${entry.endpointHost}` : "";
          const auth = entry.authStatus ? ` · ${entry.authStatus}` : "";
          entries.append(createChannelDetail(
            boundedText(entry.name, 128),
            `${boundedText(entry.transport, 16)} · ${enabled}${auth}${authority}`
          ));
        });
      }
      card.append(heading, entries);
      const ownsRemoteProfile = values.some((entry) => entry.name === "luthn-remote");
      if (ownsRemoteProfile && ["remote", "conflict"].includes(client.mode)) {
        const restore = document.createElement("button");
        restore.type = "button";
        restore.className = "secondary mcp-profile-restore";
        restore.textContent = "Use local MCP";
        restore.disabled = Boolean(state.localProfileActionId);
        restore.addEventListener("click", () => restoreLocalMcpProfile(client.agentKind));
        card.append(restore);
      }
      return card;
    });
    $("#mcpProfileCards").replaceChildren(...cards);
  }

  const helperLabel = snapshot?.helperOnline ? "Helper online" : "Helper offline";
  const seen = snapshot?.lastSeenAt ? ` · ${formatTimestamp(snapshot.lastSeenAt)}` : "";
  $("#mcpProfilesStatus").textContent = `${helperLabel}${seen}`;

  const action = snapshot?.action;
  if (action && action.id === state.remoteProfileActionId) {
    renderRemoteProfileAction(action);
  } else if (action && action.id === state.localProfileActionId) {
    renderLocalProfileAction(action);
  }
};

const clearLocalProfilePoll = () => {
  if (state.localProfilePollTimer) {
    window.clearTimeout(state.localProfilePollTimer);
    state.localProfilePollTimer = null;
  }
};

const renderLocalProfileAction = (action) => {
  if (!["succeeded", "failed", "expired"].includes(action.state)) return;
  const succeeded = action.state === "succeeded";
  setAction(
    succeeded ? "Local MCP restored" : "Local MCP not restored",
    succeeded ? "Open a new Agent session to use local Luthn" : boundedText(action.failureCode, 64, "profile.change_failed")
  );
  state.localProfileActionId = "";
  clearLocalProfilePoll();
  window.setTimeout(refreshMcpProfiles, 250);
};

const pollLocalProfileAction = async () => {
  try {
    const snapshot = await requestJson("/api/operator/mcp-profiles", { cache: "no-store" });
    renderMcpProfiles(snapshot);
    if (snapshot?.action?.id === state.localProfileActionId &&
        ["pending", "claimed"].includes(snapshot.action.state)) {
      state.localProfilePollTimer = window.setTimeout(pollLocalProfileAction, 1000);
    }
  } catch {
    setAction("Local MCP status unavailable", "No MCP profile was changed by the browser");
    state.localProfileActionId = "";
    clearLocalProfilePoll();
  }
};

const restoreLocalMcpProfile = async (agentKind) => {
  if (!hasConsoleSession() || state.localProfileActionId || state.remoteProfileActionId) return;
  try {
    const action = await requestJson("/api/operator/mcp-profiles/actions", {
      method: "POST",
      body: JSON.stringify({
        agentKind,
        operation: "restore-local",
        displayName: "Local Luthn",
        remoteUrl: null,
        oauthClientId: null,
        oauthResource: null
      })
    });
    state.localProfileActionId = action.id;
    setAction("Restoring local MCP", "Waiting for the installed Host Helper");
    clearLocalProfilePoll();
    state.localProfilePollTimer = window.setTimeout(pollLocalProfileAction, 500);
    renderMcpProfiles(state.mcpProfiles);
  } catch (error) {
    setAction("Local MCP was not changed", error.message);
  }
};

const refreshMcpProfiles = async () => {
  const button = $("#refreshMcpProfiles");
  button.disabled = true;
  try {
    renderMcpProfiles(await requestJson("/api/operator/mcp-profiles", { cache: "no-store" }));
  } catch (error) {
    renderMcpProfileMessage("MCP profile status is unavailable.");
    $("#mcpProfilesStatus").textContent = error.message.includes("401") || error.message.includes("403")
      ? "Login required"
      : "Unavailable";
  } finally {
    button.disabled = false;
  }
};

const clearRemoteProfilePoll = () => {
  if (state.remoteProfilePollTimer) {
    window.clearTimeout(state.remoteProfilePollTimer);
    state.remoteProfilePollTimer = null;
  }
};

const clearManagedExtensionPoll = () => {
  if (state.managedExtensionPollTimer) {
    window.clearTimeout(state.managedExtensionPollTimer);
    state.managedExtensionPollTimer = null;
  }
};

const notifyManagedExtensionOpener = (status, verificationCode = null, failureCode = null) => {
  const offer = state.managedExtensionOffer;
  if (!offer || !window.opener) return;
  window.opener.postMessage({
    type: "luthn.managed-extension.result",
    nonce: offer.nonce,
    status,
    verificationCode,
    failureCode
  }, offer.sourceOrigin);
};

const pollManagedExtensionAction = async () => {
  try {
    const action = await requestJson(
      `/api/operator/managed-extensions/actions/${encodeURIComponent(state.managedExtensionActionId)}`,
      { cache: "no-store" }
    );
    $("#remoteProfileOfferStatus").textContent = boundedText(action.state, 32);
    if (["pending", "claimed", "cleanup-pending", "cleanup-claimed"].includes(action.state)) {
      state.managedExtensionPollTimer = window.setTimeout(pollManagedExtensionAction, 1000);
      return;
    }
    clearManagedExtensionPoll();
    if (action.state === "prepared" && action.verificationCode) {
      state.managedExtensionApproved = true;
      $("#remoteProfileOfferStatus").textContent = "Activating extension";
      setAction("Managed extension prepared", "Waiting for the authenticated service to finish activation");
      notifyManagedExtensionOpener("prepared", action.verificationCode);
    } else if (action.state === "succeeded") {
      state.managedExtensionActionId = "";
      $("#remoteProfileOfferStatus").textContent = "Extension activated";
      setAction("Managed extension activated", "Waiting for the remote MCP profile");
      notifyManagedExtensionOpener("activated");
    } else {
      state.managedExtensionActionId = "";
      const failure = boundedText(action.failureCode, 64, "extension.bootstrap_failed");
      $("#remoteProfileOfferStatus").textContent = "Not connected";
      setAction("Managed extension not connected", failure);
      notifyManagedExtensionOpener(action.state, null, failure);
      $("#approveRemoteProfile").disabled = false;
      $("#rejectRemoteProfile").disabled = false;
    }
  } catch {
    clearManagedExtensionPoll();
    $("#remoteProfileOfferStatus").textContent = "Status unavailable";
  }
};

const approveManagedExtensionOffer = async () => {
  const offer = state.managedExtensionOffer;
  if (!offer || !hasConsoleSession() || state.managedExtensionActionId) {
    $("#remoteProfileOfferStatus").textContent = "Local access required";
    return;
  }
  $("#approveRemoteProfile").disabled = true;
  $("#rejectRemoteProfile").disabled = true;
  $("#remoteProfileOfferStatus").textContent = "Preparing managed extension";
  try {
    const action = await requestJson("/api/operator/managed-extensions/actions", {
      method: "POST",
      body: JSON.stringify({
        agentKind: $("#remoteProfileAgent").value,
        manifest: offer.manifest,
        signature: offer.signature,
        provisioningToken: offer.provisioningToken
      })
    });
    state.managedExtensionActionId = action.id;
    clearManagedExtensionPoll();
    state.managedExtensionPollTimer = window.setTimeout(pollManagedExtensionAction, 500);
  } catch (error) {
    $("#remoteProfileOfferStatus").textContent = error.message;
    $("#approveRemoteProfile").disabled = false;
    $("#rejectRemoteProfile").disabled = false;
  }
};

const notifyRemoteProfileOpener = (status, failureCode = null) => {
  const offer = state.remoteProfileOffer;
  if (!offer || !window.opener) return;
  window.opener.postMessage({
    type: "luthn.remote-profile.result",
    nonce: offer.nonce,
    status,
    failureCode
  }, offer.sourceOrigin);
};

const renderRemoteProfileAction = (action) => {
  $("#remoteProfileOfferStatus").textContent = boundedText(action.state, 32);
  const terminal = ["succeeded", "failed", "expired"].includes(action.state);
  $("#approveRemoteProfile").disabled = !terminal;
  $("#rejectRemoteProfile").disabled = !terminal;
  if (action.state === "succeeded") {
    setAction("Remote MCP connected", "Open a new Agent session to use the remote profile");
    notifyRemoteProfileOpener("succeeded");
  } else if (action.state === "failed" || action.state === "expired") {
    const failure = boundedText(action.failureCode, 64, "profile.change_failed");
    setAction("Remote MCP not connected", failure);
    notifyRemoteProfileOpener(action.state, failure);
  }
  if (terminal) {
    clearRemoteProfilePoll();
    state.remoteProfileActionId = "";
    refreshMcpProfiles();
  }
};

const pollRemoteProfileAction = async () => {
  try {
    const snapshot = await requestJson("/api/operator/mcp-profiles", { cache: "no-store" });
    renderMcpProfiles(snapshot);
    if (snapshot?.action?.id === state.remoteProfileActionId &&
        ["pending", "claimed"].includes(snapshot.action.state)) {
      state.remoteProfilePollTimer = window.setTimeout(pollRemoteProfileAction, 1000);
    }
  } catch {
    $("#remoteProfileOfferStatus").textContent = "Status unavailable";
    clearRemoteProfilePoll();
  }
};

const approveRemoteProfileOffer = async () => {
  if (state.managedExtensionOffer && !state.managedExtensionApproved && !state.remoteProfileOffer) {
    await approveManagedExtensionOffer();
    return;
  }
  const offer = state.remoteProfileOffer;
  if (!offer || !hasConsoleSession()) {
    $("#remoteProfileOfferStatus").textContent = "Local access required";
    return;
  }

  $("#approveRemoteProfile").disabled = true;
  $("#rejectRemoteProfile").disabled = true;
  $("#remoteProfileOfferStatus").textContent = "Waiting for Host Helper";
  try {
    const action = await requestJson("/api/operator/mcp-profiles/actions", {
      method: "POST",
      body: JSON.stringify({
        agentKind: $("#remoteProfileAgent").value,
        operation: "activate-remote",
        displayName: offer.displayName,
        remoteUrl: offer.remoteUrl,
        oauthClientId: offer.oauthClientId || null,
        oauthResource: offer.oauthResource || null
      })
    });
    state.remoteProfileActionId = action.id;
    renderRemoteProfileAction(action);
    clearRemoteProfilePoll();
    state.remoteProfilePollTimer = window.setTimeout(pollRemoteProfileAction, 500);
  } catch (error) {
    $("#remoteProfileOfferStatus").textContent = error.message;
    $("#approveRemoteProfile").disabled = false;
    $("#rejectRemoteProfile").disabled = false;
  }
};

const rejectRemoteProfileOffer = () => {
  if (state.managedExtensionOffer && !state.remoteProfileOffer) {
    notifyManagedExtensionOpener("cancelled");
  } else {
    notifyRemoteProfileOpener("cancelled");
  }
  state.managedExtensionOffer = null;
  state.managedExtensionApproved = false;
  state.managedExtensionActionId = "";
  clearManagedExtensionPoll();
  state.remoteProfileOffer = null;
  state.remoteProfileActionId = "";
  clearRemoteProfilePoll();
  $("#remoteProfileOffer").hidden = true;
};

const receiveRemoteProfileOffer = (event) => {
  const value = event.data;
  const fragment = new URLSearchParams(window.location.hash.replace(/^#/, ""));
  const nonce = fragment.get("remote-profile") || fragment.get("managed-extension");
  if (!nonce || !/^[A-Za-z0-9_-]{22,128}$/.test(nonce) ||
      event.source !== window.opener ||
      value?.type !== "luthn.remote-profile.offer" || value?.nonce !== nonce) return;

  try {
    const remote = new URL(value.remoteUrl);
    if (remote.protocol !== "https:" || remote.origin !== event.origin ||
        remote.username || remote.password || remote.search || remote.hash ||
        typeof value.displayName !== "string" || !value.displayName.trim() ||
        value.displayName.length > 128) return;

    state.remoteProfileOffer = {
      nonce,
      sourceOrigin: event.origin,
      displayName: value.displayName.trim(),
      remoteUrl: remote.href,
      oauthClientId: typeof value.oauthClientId === "string" ? value.oauthClientId : null,
      oauthResource: typeof value.oauthResource === "string" ? value.oauthResource : null
    };
    $("#remoteProfileService").textContent = state.remoteProfileOffer.displayName;
    $("#remoteProfileHost").textContent = remote.host;
    $("#remoteProfileOfferStatus").textContent = "Review required";
    $("#remoteProfileOffer").hidden = false;
    $("#remoteProfileOffer").scrollIntoView({ behavior: "smooth", block: "start" });
    if (state.managedExtensionApproved && state.managedExtensionOffer?.sourceOrigin === event.origin) {
      approveRemoteProfileOffer();
    }
  } catch {
    // Invalid cross-origin offers fail closed without revealing local state.
  }
};

const receiveManagedExtensionOffer = (event) => {
  const value = event.data;
  const fragment = new URLSearchParams(window.location.hash.replace(/^#/, ""));
  const nonce = fragment.get("managed-extension");
  if (!nonce || !/^[A-Za-z0-9_-]{22,128}$/.test(nonce) ||
      event.source !== window.opener || value?.type !== "luthn.managed-extension.offer" ||
      value?.nonce !== nonce || typeof value?.signature !== "string" ||
      typeof value?.provisioningToken !== "string") return;
  try {
    const manifest = value.manifest;
    const packageUrl = new URL(manifest?.packageUri);
    const serviceOrigin = new URL(manifest?.serviceOrigin);
    if (manifest?.schemaVersion !== 1 ||
        typeof manifest?.extensionId !== "string" || !/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(manifest.extensionId) ||
        typeof manifest?.publisher !== "string" || !/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(manifest.publisher) ||
        packageUrl.protocol !== "https:" ||
        serviceOrigin.protocol !== "https:" || packageUrl.origin !== event.origin ||
        packageUrl.search || packageUrl.hash ||
        serviceOrigin.pathname !== "/" || serviceOrigin.search || serviceOrigin.hash) return;
    state.managedExtensionOffer = {
      nonce,
      sourceOrigin: event.origin,
      manifest,
      signature: value.signature,
      provisioningToken: value.provisioningToken
    };
    state.managedExtensionApproved = false;
    $("#remoteProfileOfferTitle").textContent = "Connect this PC to the managed service";
    $("#remoteProfileService").textContent = boundedText(manifest.displayName, 128, "Managed extension");
    $("#remoteProfileHost").textContent = packageUrl.host;
    $("#remoteProfileOfferStatus").textContent = "Review required";
    $("#approveRemoteProfile").textContent = "Connect service";
    $("#remoteProfileOffer").hidden = false;
    $("#remoteProfileOffer").scrollIntoView({ behavior: "smooth", block: "start" });
  } catch {
    // Invalid cross-origin managed-extension offers fail closed without revealing local state.
  }
};

const receiveManagedExtensionFinalize = async (event) => {
  const value = event.data;
  const offer = state.managedExtensionOffer;
  if (!offer || !state.managedExtensionActionId || event.source !== window.opener ||
      event.origin !== offer.sourceOrigin || value?.type !== "luthn.managed-extension.finalize" ||
      value?.nonce !== offer.nonce || !["activated", "failed"].includes(value?.outcome)) return;
  try {
    await requestJson(
      `/api/operator/managed-extensions/actions/${encodeURIComponent(state.managedExtensionActionId)}/finalize`,
      { method: "POST", body: JSON.stringify({ outcome: value.outcome }) }
    );
    clearManagedExtensionPoll();
    state.managedExtensionPollTimer = window.setTimeout(pollManagedExtensionAction, 250);
  } catch (error) {
    $("#remoteProfileOfferStatus").textContent = "Finalization unavailable";
    setAction("Extension activation could not be finalized", error.message);
  }
};

const announceRemoteProfileReady = () => {
  const fragment = new URLSearchParams(window.location.hash.replace(/^#/, ""));
  const nonce = fragment.get("remote-profile");
  if (!window.opener || !nonce || !/^[A-Za-z0-9_-]{22,128}$/.test(nonce)) return;
  window.opener.postMessage({ type: "luthn.remote-profile.ready", nonce }, "*");
};

const announceManagedExtensionReady = () => {
  const fragment = new URLSearchParams(window.location.hash.replace(/^#/, ""));
  const nonce = fragment.get("managed-extension");
  if (!window.opener || !nonce || !/^[A-Za-z0-9_-]{22,128}$/.test(nonce)) return;
  window.opener.postMessage({ type: "luthn.managed-extension.ready", nonce }, "*");
};

const refreshSyncStatus = async () => {
  const refreshButton = $("#refreshSyncStatus");
  refreshButton.disabled = true;
  try {
    const result = await requestJson("/api/external-publication/status");
    $("#syncStatus").textContent = `${result.connectionState} / ${result.outboxState}`;
    writeResult($("#publicationOutput"), result);
  } catch (error) {
    if (!hasConsoleSession()) {
      $("#syncStatus").textContent = "Login required";
      writeResult($("#publicationOutput"), "Console login is required to view publication state.");
    } else {
      $("#syncStatus").textContent = "Unavailable";
      writeResult($("#publicationOutput"), error.message);
    }
  } finally {
    refreshButton.disabled = false;
  }
};

const publicationMemoryId = () =>
  new FormData($("#publicationForm")).get("memoryItemId")?.toString().trim() || "";

const readPublication = async () => {
  const memoryItemId = publicationMemoryId();
  if (!memoryItemId) {
    writeResult($("#publicationOutput"), "Memory item id is required.");
    return;
  }

  try {
    const result = await requestJson(`/api/external-publication/memory-items/${encodeURIComponent(memoryItemId)}`);
    $("#publicationState").value = result.publicationState;
    writeResult($("#publicationOutput"), result);
    setAction("publication read", result.publicationState);
  } catch (error) {
    $("#publicationState").value = "Unavailable";
    writeResult($("#publicationOutput"), error.message);
    setAction("publication read failed", error.message);
  }
};

const changePublication = async (action) => {
  const memoryItemId = publicationMemoryId();
  if (!memoryItemId) {
    writeResult($("#publicationOutput"), "Memory item id is required.");
    return;
  }

  try {
    const result = await requestJson(
      `/api/external-publication/memory-items/${encodeURIComponent(memoryItemId)}/${action}`,
      { method: "POST" }
    );
    $("#publicationState").value = result.publicationState;
    writeResult($("#publicationOutput"), result);
    setAction(`publication ${action}`, result.publicationState);
    await refreshSyncStatus();
    await refreshAudit();
  } catch (error) {
    writeResult($("#publicationOutput"), error.message);
    setAction(`publication ${action} failed`, error.message);
  }
};

const providerDefaults = {
  Unconfigured: { endpoint: "" },
  LocalDeterministic: { endpoint: "" },
  LocalHttp: { endpoint: "http://127.0.0.1:11434/classify" }
};

const renderProviderSettings = (settings) => {
  const form = $("#providerForm");
  form.provider.value = settings.provider;
  form.endpoint.value = settings.endpoint || "";
  form.endpoint.disabled = settings.provider !== "LocalHttp";
  $("#providerStatus").textContent = settings.statusDetail;
  writeResult($("#providerOutput"), settings);
};

const refreshProviderSettings = async () => {
  try {
    const settings = await requestJson("/api/operator/classification-provider");
    renderProviderSettings(settings);
  } catch (error) {
    writeResult($("#providerOutput"), hasConsoleSession()
      ? error.message
      : "Console login is required to view provider settings.");
    $("#providerStatus").textContent = hasConsoleSession()
      ? "Provider settings unavailable"
      : "Console login required";
  }
};

const applyProviderDefaults = () => {
  const form = $("#providerForm");
  const defaults = providerDefaults[form.provider.value] || providerDefaults.Unconfigured;
  form.endpoint.disabled = form.provider.value !== "LocalHttp";
  form.endpoint.value = defaults.endpoint;
};

const saveProviderSettings = async (event) => {
  event.preventDefault();
  const form = new FormData(event.currentTarget);
  const body = {
    provider: form.get("provider")?.toString(),
    endpoint: form.get("endpoint")?.toString().trim()
  };

  try {
    const settings = await requestJson("/api/operator/classification-provider", {
      method: "PUT",
      body: JSON.stringify(body)
    });
    renderProviderSettings(settings);
    setAction("provider saved", settings.provider);
  } catch (error) {
    writeResult($("#providerOutput"), error.message);
    setAction("provider save failed", error.message);
  }
};

const testProviderSettings = async () => {
  try {
    const result = await requestJson("/api/operator/classification-provider/test", {
      method: "POST",
      body: JSON.stringify({
        sourceType: "note",
        content: "Public implementation note for provider connectivity testing."
      })
    });
    writeResult($("#providerOutput"), result);
    setAction("provider tested", result.classification?.sensitivity || "classified");
  } catch (error) {
    writeResult($("#providerOutput"), error.message);
    setAction("provider test failed", error.message);
  }
};

const buildAuditParams = () => {
  const form = new FormData($("#auditForm"));
  const params = new URLSearchParams();
  ["scope", "category", "subjectId", "action", "actionPrefix", "outcome", "subjectType", "actorKind", "correlationId"]
    .forEach((field) => {
      const value = form.get(field)?.toString().trim();
      if (value) {
        params.set(field, value);
      }
    });
  ["from", "to"].forEach((field) => {
    const value = form.get(field)?.toString().trim();
    if (value) {
      const timestamp = new Date(value);
      if (!Number.isNaN(timestamp.getTime())) {
        params.set(field, timestamp.toISOString());
      }
    }
  });
  const limit = form.get("limit")?.toString().trim() || "25";
  params.set("limit", limit);
  return params;
};

const refreshAudit = async (event) => {
  event?.preventDefault();
  if (!hasConsoleSession()) {
    renderSessionGuidance();
    return;
  }
  setAuditControlsEnabled(true);
  const params = buildAuditParams();

  try {
    const result = await requestJson(`/api/audit-events?${params}`);
    state.auditEvents = Array.isArray(result?.events) ? result.events : [];
    state.selectedAuditEvent = null;
    state.auditNextCursor = typeof result?.nextCursor === "string" ? result.nextCursor : "";
    state.auditBaseQuery = params.toString();
    renderAuditRows(state.auditEvents);
    renderAuditDetail(null);
    $("#nextAuditPage").disabled = !state.auditNextCursor;
    $("#auditStatus").textContent = `${state.auditEvents.length} metadata events loaded.`;
    setAction("audit refreshed", `${state.auditEvents.length} events`);
  } catch (error) {
    state.auditEvents = [];
    state.selectedAuditEvent = null;
    state.auditNextCursor = "";
    state.auditBaseQuery = "";
    renderAuditRows([]);
    renderAuditDetail(null);
    $("#nextAuditPage").disabled = true;
    $("#auditStatus").textContent = hasConsoleSession()
      ? "Audit metadata is unavailable for these filters."
      : "Console login is required to load audit metadata.";
    setAction(hasConsoleSession() ? "audit failed" : "audit waiting", hasConsoleSession() ? error.message : "Sign in through Console access");
  }
};

const loadNextAuditPage = async () => {
  if (!hasConsoleSession() || !state.auditNextCursor || !state.auditBaseQuery) {
    if (!hasConsoleSession()) {
      renderSessionGuidance();
    }
    return;
  }

  const button = $("#nextAuditPage");
  button.disabled = true;
  const params = new URLSearchParams(state.auditBaseQuery);
  params.set("cursor", state.auditNextCursor);
  try {
    const result = await requestJson(`/api/audit-events?${params}`);
    const page = Array.isArray(result?.events) ? result.events : [];
    state.auditEvents = [...state.auditEvents, ...page];
    state.auditNextCursor = typeof result?.nextCursor === "string" ? result.nextCursor : "";
    renderAuditRows(state.auditEvents);
    $("#auditStatus").textContent = `${state.auditEvents.length} metadata events loaded.`;
    setAction("audit page loaded", `${page.length} events`);
  } catch (error) {
    $("#auditStatus").textContent = "The next audit page could not be loaded.";
    setAction("audit page failed", error.message);
  } finally {
    button.disabled = !state.auditNextCursor;
  }
};

const exportAuditMetadata = async () => {
  if (!hasConsoleSession()) {
    renderSessionGuidance();
    return;
  }
  const button = $("#exportAudit");
  button.disabled = true;
  const params = buildAuditParams();
  params.delete("limit");
  try {
    const response = await fetch(`/api/audit-events/export?${params}`, {
      credentials: "same-origin"
    });
    if (!response.ok) {
      const responseText = await response.text();
      let problem = null;
      try {
        problem = responseText ? JSON.parse(responseText) : null;
      } catch {
        problem = null;
      }
      throw new Error(`${response.status} ${problem?.detail || problem?.title || response.statusText}`);
    }
    const url = URL.createObjectURL(await response.blob());
    const link = document.createElement("a");
    link.href = url;
    link.download = readAuditExportFilename(response.headers.get("content-disposition"));
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 0);
    setAction("audit exported", `${link.download} / metadata-only JSON`);
  } catch (error) {
    setAction("audit export failed", error.message);
  } finally {
    button.disabled = false;
  }
};

const readAuditExportFilename = (contentDisposition) => {
  const fallback = "luthn-audit-metadata.json";
  const encoded = contentDisposition?.match(/filename\*=UTF-8''([^;]+)/i)?.[1];
  if (encoded) {
    try {
      return decodeURIComponent(encoded);
    } catch {
      return fallback;
    }
  }
  return contentDisposition?.match(/filename="?([^";]+)"?/i)?.[1] || fallback;
};

const applyAuditPreset = (preset) => {
  const form = $("#auditForm");
  form.reset();
  form.limit.value = "25";
  if (preset === "sensitive") {
    form.scope.value = "workspace";
    form.category.value = "Access";
    form.actionPrefix.value = "sensitive_access.";
    form.subjectType.value = "sensitive_access_request";
  } else if (preset === "failures") {
    form.scope.value = "workspace";
    form.category.value = "Security";
    form.outcome.value = "failed";
  } else if (preset === "hub") {
    form.scope.value = "workspace";
    form.category.value = "Ingestion";
    form.actionPrefix.value = "hub.ingress.";
    form.subjectType.value = "hub_ingress_item";
  } else if (preset === "configuration") {
    form.scope.value = "installation";
    form.category.value = "Configuration";
    form.actionPrefix.value = "operator.classification_provider.";
    form.subjectType.value = "classification_provider";
  }
  refreshAudit();
};

const viewSelectedAccessAudit = () => {
  if (!state.selectedAccessRequestId) {
    return;
  }

  const form = $("#auditForm");
  form.reset();
  form.scope.value = "workspace";
  form.category.value = "Access";
  form.subjectId.value = state.selectedAccessRequestId;
  form.actionPrefix.value = "sensitive_access.";
  form.limit.value = "25";
  refreshAudit();
  $("#auditForm").scrollIntoView({ behavior: "smooth", block: "center" });
};

const refreshAccessRequests = async (event) => {
  event?.preventDefault();
  const previousSelectedId = state.selectedAccessRequestId;
  clearAccessDetail("Refreshing access requests...");
  const form = new FormData($("#accessForm"));
  const params = new URLSearchParams();
  const status = form.get("status")?.toString().trim();
  const limit = form.get("limit")?.toString().trim() || "25";
  if (status) {
    params.set("status", status);
  }
  params.set("limit", limit);

  try {
    const result = await requestJson(`/api/access-requests?${params}`);
    const liveRequests = Array.isArray(result.requests) ? result.requests : [];
    const tombstones = Array.isArray(result.tombstones) ? result.tombstones : [];
    const requests = [...liveRequests, ...tombstones];
    renderAccessRows(requests);
    if (previousSelectedId && requests.some((request) => request.id === previousSelectedId)) {
      await loadAccessRequestDetail(previousSelectedId);
    } else if (previousSelectedId) {
      $("#accessDetailStatus").textContent = "The selected request is no longer in the current list.";
    }
    setAction("access refreshed", `${requests.length} requests`);
  } catch (error) {
    renderAccessRows([]);
    clearAccessDetail(hasConsoleSession()
      ? "Access requests could not be loaded."
      : "Console login is required to review sensitive access requests.");
    setAction(hasConsoleSession() ? "access failed" : "access waiting", hasConsoleSession() ? error.message : "Sign in through Console access");
  }
};

const renderAccessPolicy = (policy) => {
  const form = $("#accessPolicyForm");
  if (!form) {
    return;
  }
  form.requestTimeoutMinutes.value = (policy?.requestTimeoutSeconds || 600) / 60;
  form.grantDurationMinutes.value = (policy?.grantDurationSeconds || 600) / 60;
  form.maximumSuccessfulReads.value = policy?.maximumSuccessfulReads || 1;
  $("#accessPolicyStatus").textContent = policy?.revision
    ? `Revision ${policy.revision} · ${formatTimestamp(policy.createdAt)}`
    : "Policy unavailable";
  $("#saveAccessPolicy").disabled = state.accessPolicyPending;
};

const refreshAccessPolicy = async () => {
  try {
    renderAccessPolicy(await requestJson("/api/access-requests/policy", { cache: "no-store" }));
  } catch (error) {
    $("#accessPolicyStatus").textContent = hasConsoleSession()
      ? `Policy unavailable: ${error.message}`
      : "Console login is required to configure sensitive access.";
  }
};

const saveAccessPolicy = async (event) => {
  event?.preventDefault();
  const form = $("#accessPolicyForm");
  if (!form.reportValidity() || state.accessPolicyPending) {
    return;
  }

  state.accessPolicyPending = true;
  $("#saveAccessPolicy").disabled = true;
  $("#accessPolicyStatus").textContent = "Saving policy…";
  try {
    const policy = await requestJson("/api/access-requests/policy", {
      method: "PUT",
      body: JSON.stringify({
        requestTimeoutSeconds: Math.round(Number(form.requestTimeoutMinutes.value) * 60),
        grantDurationSeconds: Math.round(Number(form.grantDurationMinutes.value) * 60),
        maximumSuccessfulReads: Number(form.maximumSuccessfulReads.value)
      })
    });
    renderAccessPolicy(policy);
    setAction("access policy updated", `revision ${policy.revision}`);
  } catch (error) {
    $("#accessPolicyStatus").textContent = `Policy update failed: ${error.message}`;
    setAction("access policy failed", error.message);
  } finally {
    state.accessPolicyPending = false;
    $("#saveAccessPolicy").disabled = false;
  }
};

const decideAccessRequest = async (id, decision) => {
  if (state.accessDecisionPending ||
      !state.selectedAccessDetail ||
      state.selectedAccessRequestId !== id ||
      state.selectedAccessDetail.status !== "Pending" ||
      state.selectedAccessDetail.statusCode !== "request-pending") {
    return;
  }

  const formElement = $("#accessDecisionForm");
  const form = new FormData(formElement);
  const reason = form.get("reason")?.toString().trim() || "";
  if (!reason) {
    formElement.reportValidity();
    updateAccessDecisionState();
    return;
  }
  const redactedSummary = form.get("redactedSummary")?.toString().trim();
  const protectedMode = state.selectedAccessDetail.accessMode === "ProtectedMemory";
  if (decision === "approve" && protectedMode && !formElement.reportValidity()) {
    return;
  }
  const body = decision === "approve"
    ? protectedMode
      ? {
          reason,
          grantDurationSeconds: Math.round(Number(form.get("protectedGrantDurationMinutes")) * 60),
          maximumSuccessfulReads: Number(form.get("protectedMaximumSuccessfulReads"))
        }
      : {
          reason,
          ...(redactedSummary ? { redactedSummary } : {})
        }
    : { reason };

  state.accessDecisionPending = true;
  updateAccessDecisionState();
  try {
    const result = await requestJson(`/api/access-requests/${encodeURIComponent(id)}/${decision}`, {
      method: "POST",
      body: JSON.stringify(body)
    });
    setAction(`access ${decision}`, result.id);
    await refreshAccessRequests();
    await loadAccessRequestDetail(id);
    await refreshAudit();
  } catch (error) {
    setAction(`access ${decision} failed`, error.message);
  } finally {
    state.accessDecisionPending = false;
    updateAccessDecisionState();
  }
};

const accessDetailDefaults = {
  id: "Not selected",
  status: "—",
  accessMode: "—",
  statusCode: "—",
  createdAt: "—",
  expiresAt: "—",
  grantExpiresAt: "—",
  readUsage: "—",
  requestReason: "—",
  decisionReason: "—",
  decidedAt: "—",
  outputPolicy: "—",
  referenceLabel: "—",
  referenceSource: "—",
  redactedSummary: "—"
};

const setAccessDetailFields = (values = accessDetailDefaults) => {
  Object.entries(accessDetailDefaults).forEach(([field, fallback]) => {
    const target = document.querySelector(`[data-access-field="${field}"]`);
    target.textContent = values[field] || fallback;
  });
};

const updateAccessDecisionState = () => {
  const reason = $("#accessDecisionForm").reason.value.trim();
  const isTombstone = Boolean(state.selectedAccessDetail?.isTombstone);
  const canDecide = !state.accessDecisionPending &&
    !isTombstone &&
    state.selectedAccessDetail?.status === "Pending" &&
    state.selectedAccessDetail?.statusCode === "request-pending" &&
    Boolean(reason);
  $("#approveAccess").disabled = !canDecide;
  $("#denyAccess").disabled = !canDecide;
  $("#viewAccessAudit").disabled = !state.selectedAccessRequestId;
  document.querySelectorAll("#accessDecisionForm label, #approveAccess, #denyAccess")
    .forEach((element) => { element.hidden = isTombstone; });
  const protectedMode = !isTombstone && state.selectedAccessDetail?.accessMode === "ProtectedMemory";
  document.querySelectorAll("[data-access-protected-control]")
    .forEach((element) => { element.hidden = !protectedMode; });
  document.querySelectorAll("[data-access-protected-control] input")
    .forEach((element) => { element.disabled = !protectedMode; });
  document.querySelectorAll("[data-access-redacted-control]")
    .forEach((element) => { element.hidden = isTombstone || protectedMode; });
  document.querySelectorAll("[data-tombstone-hidden]")
    .forEach((element) => { element.hidden = isTombstone; });
};

const clearAccessDetail = (message = "Select a request to review.") => {
  state.accessDetailRequestSequence += 1;
  state.selectedAccessRequestId = "";
  state.selectedAccessDetail = null;
  setAccessDetailFields();
  $("#accessDetailStatus").textContent = message;
  $("#accessDecisionForm").reset();
  updateAccessDecisionState();
  document.querySelectorAll("#accessRows tr").forEach((row) => row.removeAttribute("aria-selected"));
};

const sanitizeAccessDetail = (detail) => ({
  id: boundedText(detail?.id, 128, "Unknown request"),
  status: boundedText(detail?.status, 32),
  accessMode: boundedText(detail?.accessMode, 64, "RedactedSummary"),
  statusCode: boundedText(detail?.statusCode, 64),
  createdAt: formatTimestamp(detail?.createdAt),
  expiresAt: formatTimestamp(detail?.requestExpiresAt || detail?.expiresAt),
  grantExpiresAt: detail?.grantExpiresAt ? formatTimestamp(detail.grantExpiresAt) : "Not granted",
  readUsage: detail?.maxReads == null
    ? "Not granted"
    : `${detail.usedReads || 0} used · ${detail.remainingReads || 0} remaining · ${detail.maxReads} max`,
  requestReason: boundedText(detail?.requestReason, 1000, "Not provided"),
  decisionReason: boundedText(detail?.decisionReason, 1000, "Not decided"),
  decidedAt: detail?.decidedAt ? formatTimestamp(detail.decidedAt) : "Not decided",
  outputPolicy: boundedText(detail?.outputPolicy, 128),
  referenceLabel: boundedText(detail?.reference?.referenceLabel, 256),
  referenceSource: [
    boundedText(detail?.reference?.sourceSystem, 128, ""),
    boundedText(detail?.reference?.sourceType, 128, "")
  ].filter(Boolean).join(" / ") || "Unknown",
  redactedSummary: boundedText(detail?.reference?.redactedSummary, 4000, "Not available"),
  isTombstone: detail?.status === "Expired" &&
    detail?.outputPolicy === "expired-no-output" &&
    !("sensitiveReferenceId" in (detail || {}))
});

const loadAccessRequestDetail = async (id) => {
  const sequence = state.accessDetailRequestSequence + 1;
  clearAccessDetail("Loading request metadata...");
  state.accessDetailRequestSequence = sequence;

  try {
    const detail = await requestJson(`/api/access-requests/${encodeURIComponent(id)}/operator-detail`, {
      cache: "no-store"
    });
    if (state.accessDetailRequestSequence !== sequence) {
      return;
    }

    const safeDetail = sanitizeAccessDetail(detail);
    state.selectedAccessRequestId = id;
    state.selectedAccessDetail = safeDetail;
    setAccessDetailFields(safeDetail);
    $("#accessDetailStatus").textContent = safeDetail.isTombstone
      ? "Expired · content removed"
      : safeDetail.statusCode === "request-pending"
      ? safeDetail.accessMode === "ProtectedMemory"
        ? "Review metadata, duration, and read count. Protected content stays hidden from the console."
        : "Review metadata and enter a decision reason."
      : `Lifecycle: ${safeDetail.statusCode || safeDetail.status}`;
    document.querySelectorAll("#accessRows tr").forEach((row) => {
      row.setAttribute("aria-selected", row.dataset.requestId === id ? "true" : "false");
    });
    updateAccessDecisionState();
    setAction("access detail loaded", safeDetail.id);
  } catch (error) {
    if (state.accessDetailRequestSequence === sequence) {
      clearAccessDetail("Request metadata is unavailable.");
      setAction("access detail failed", error.message);
    }
  }
};

const renderAccessRows = (requests) => {
  const rows = $("#accessRows");
  if (requests.length === 0) {
    const row = document.createElement("tr");
    const cell = document.createElement("td");
    cell.colSpan = 6;
    cell.textContent = "No access requests available.";
    row.appendChild(cell);
    rows.replaceChildren(row);
    return;
  }

  rows.replaceChildren(...requests.map((request) => {
    const tr = document.createElement("tr");
    tr.dataset.requestId = request.id;
    [
      request.createdAt ? new Date(request.createdAt).toLocaleString() : "Content removed",
      request.id,
      request.sensitiveReferenceId || "Content removed",
      request.statusCode || request.status,
      request.maxReads == null
        ? request.outputPolicy || (request.redactedOutputAvailable ? "available" : "unavailable")
        : `${request.remainingReads || 0}/${request.maxReads} reads remaining`
    ].forEach((value) => {
      const td = document.createElement("td");
      td.textContent = value || "";
      tr.appendChild(td);
    });

    const actionCell = document.createElement("td");
    const review = document.createElement("button");
    review.type = "button";
    review.className = "secondary";
    review.textContent = request.status === "Pending" ? "Review" : "View";
    review.addEventListener("click", () => loadAccessRequestDetail(request.id));
    actionCell.appendChild(review);
    tr.appendChild(actionCell);

    return tr;
  }));
};

const renderAuditRows = (events) => {
  const rows = $("#auditRows");
  if (events.length === 0) {
    const row = document.createElement("tr");
    const cell = document.createElement("td");
    cell.colSpan = 6;
    cell.textContent = "No audit events available.";
    row.appendChild(cell);
    rows.replaceChildren(row);
    return;
  }

  rows.replaceChildren(...events.map((event) => {
    const tr = document.createElement("tr");
    const selected = state.selectedAuditEvent?.id === event.id;
    tr.className = selected ? "audit-row selected" : "audit-row";
    tr.tabIndex = 0;
    tr.setAttribute("role", "button");
    tr.setAttribute("aria-label", `Inspect ${event.action || "audit event"}`);
    tr.setAttribute("aria-pressed", String(selected));
    [
      new Date(event.occurredAt).toLocaleString(),
      event.action,
      event.outcome,
      event.category,
      event.subjectId,
      event.correlationId,
    ].forEach((value) => {
      const td = document.createElement("td");
      td.textContent = value || "";
      tr.appendChild(td);
    });
    const inspect = () => {
      state.selectedAuditEvent = event;
      renderAuditRows(state.auditEvents);
      renderAuditDetail(event);
    };
    tr.addEventListener("click", inspect);
    tr.addEventListener("keydown", (keyEvent) => {
      if (["Enter", " "].includes(keyEvent.key)) {
        keyEvent.preventDefault();
        inspect();
      }
    });
    return tr;
  }));
};

const renderAuditDetail = (event) => {
  const fields = $("#auditDetailFields");
  const status = $("#auditDetailStatus");
  const subjectButton = $("#viewAuditSubject");
  const correlationButton = $("#viewAuditCorrelation");
  if (!event) {
    fields.replaceChildren(Object.assign(document.createElement("div"), {
      className: "detail-summary"
    }));
    const detail = fields.firstElementChild;
    const label = document.createElement("dt");
    label.textContent = "Detail";
    const value = document.createElement("dd");
    value.textContent = "Select an audit event to inspect metadata and continue the investigation.";
    detail.append(label, value);
    status.textContent = "Select a timeline event.";
    subjectButton.disabled = true;
    correlationButton.disabled = true;
    return;
  }

  const details = [
    ["Occurred", formatTimestamp(event.occurredAt)],
    ["Action", event.action],
    ["Outcome", event.outcome],
    ["Category", event.category],
    ["Actor", event.actor],
    ["Actor user", event.actorUserId || "Not recorded"],
    ["Actor kind", event.actorKind],
    ["Subject", event.subjectId],
    ["Subject type", event.subjectType],
    ["Correlation", event.correlationId || "Not recorded"],
    ["Payload boundary", event.payloadClass],
    ["Redaction", event.redactionState],
    ["Retention", `${boundedText(event.retentionClass, 64, "Unknown")} / ${formatTimestamp(event.retainedUntil)}`]
  ];
  fields.replaceChildren(...details.map(([labelText, valueText]) => {
    const item = document.createElement("div");
    const label = document.createElement("dt");
    label.textContent = labelText;
    const value = document.createElement("dd");
    value.textContent = valueText || "Not recorded";
    item.append(label, value);
    return item;
  }));
  status.textContent = "Metadata-only detail.";
  subjectButton.disabled = !event.subjectId;
  correlationButton.disabled = !event.correlationId;
};

const viewSelectedAuditSubject = () => {
  const event = state.selectedAuditEvent;
  if (!event?.subjectId) {
    return;
  }
  const form = $("#auditForm");
  form.reset();
  form.scope.value = event.scopeKind === "Installation" ? "installation" : "workspace";
  form.subjectId.value = event.subjectId;
  form.limit.value = "25";
  refreshAudit();
};

const viewSelectedAuditCorrelation = () => {
  const event = state.selectedAuditEvent;
  if (!event?.correlationId) {
    return;
  }
  const form = $("#auditForm");
  form.reset();
  form.scope.value = event.scopeKind === "Installation" ? "installation" : "workspace";
  form.correlationId.value = event.correlationId;
  form.limit.value = "25";
  refreshAudit();
};

const previewContent = async (event) => {
  event.preventDefault();
  const form = new FormData(event.currentTarget);
  const body = {
    sourceId: form.get("sourceId")?.toString().trim(),
    content: form.get("content")?.toString(),
    sourceType: form.get("sourceType")?.toString().trim()
  };

  try {
    const result = await requestJson("/api/classification/preview", {
      method: "POST",
      body: JSON.stringify(body)
    });
    writeResult($("#previewOutput"), result);
    setAction("preview complete", result.storageDecision?.kind || "classified");
  } catch (error) {
    writeResult($("#previewOutput"), error.message);
    setAction("preview failed", error.message);
  }
};

const submitSource = async (event) => {
  event.preventDefault();
  const form = new FormData(event.currentTarget);
  const body = {
    sourceSystem: form.get("sourceSystem")?.toString().trim(),
    sourceType: form.get("sourceType")?.toString().trim(),
    content: form.get("content")?.toString(),
    title: form.get("title")?.toString().trim(),
    safeSummary: form.get("safeSummary")?.toString().trim(),
    coreTags: form.get("coreTags")?.toString()
      .split(",")
      .map((tag) => tag.trim())
      .filter(Boolean)
  };

  try {
    const result = await requestJson("/api/sources", {
      method: "POST",
      body: JSON.stringify(body)
    });
    writeResult($("#intakeOutput"), result);
    setAction("source submitted", result.sourceId ?? result.sourceEventId);
    await refreshAudit();
  } catch (error) {
    writeResult($("#intakeOutput"), error.message);
    setAction("source failed", error.message);
  }
};

const fillPreviewExample = () => {
  $("#previewForm").sourceId.value = "operator-preview-sensitive";
  $("#previewForm").sourceType.value = "note";
  $("#previewForm").content.value = "Customer contract includes payment terms.";
};

const fillIntakeExample = () => {
  $("#intakeForm").sourceSystem.value = "operator";
  $("#intakeForm").sourceType.value = "runbook";
  $("#intakeForm").title.value = "Safe release checklist";
  $("#intakeForm").safeSummary.value = "Public-safe release checklist for operator validation.";
  $("#intakeForm").coreTags.value = "release, runbook";
  $("#intakeForm").content.value = "Implementation decision and release runbook note.";
};

renderAuthStatus();
setConsoleView("overview");
document.querySelectorAll("[data-console-nav]").forEach((control) => {
  control.addEventListener("click", () => setConsoleView(control.dataset.consoleNav));
});
$("#logoutSession").addEventListener("click", async () => {
  try {
    await requestJson("/api/operator/session/logout", { method: "POST" });
  } finally {
    state.consoleSession = null;
    state.csrfProof = "";
    state.localAccessError = "";
    renderAuthStatus();
    renderSessionGuidance();
    setAction("session ended", "Reload to create a new eligible Local session");
  }
});
$("#connectLocal").addEventListener("click", connectLocalAccess);
$("#previewForm").addEventListener("submit", previewContent);
$("#intakeForm").addEventListener("submit", submitSource);
$("#providerForm").addEventListener("submit", saveProviderSettings);
$("#providerForm").provider.addEventListener("change", applyProviderDefaults);
$("#testProvider").addEventListener("click", testProviderSettings);
$("#accessForm").addEventListener("submit", refreshAccessRequests);
$("#accessPolicyForm").addEventListener("submit", saveAccessPolicy);
$("#accessDecisionForm").reason.addEventListener("input", updateAccessDecisionState);
$("#approveAccess").addEventListener("click", () => decideAccessRequest(state.selectedAccessRequestId, "approve"));
$("#denyAccess").addEventListener("click", () => decideAccessRequest(state.selectedAccessRequestId, "deny"));
$("#viewAccessAudit").addEventListener("click", viewSelectedAccessAudit);
$("#auditForm").addEventListener("submit", refreshAudit);
$("#nextAuditPage").addEventListener("click", loadNextAuditPage);
$("#exportAudit").addEventListener("click", exportAuditMetadata);
$("#viewAuditSubject").addEventListener("click", viewSelectedAuditSubject);
$("#viewAuditCorrelation").addEventListener("click", viewSelectedAuditCorrelation);
$("#consoleLanguage").addEventListener("change", (event) => {
  i18n?.apply(event.currentTarget.value);
  renderConsoleProfile();
});
document.querySelectorAll("[data-audit-preset]").forEach((button) => {
  button.addEventListener("click", () => applyAuditPreset(button.dataset.auditPreset));
});
$("#previewExample").addEventListener("click", fillPreviewExample);
$("#intakeExample").addEventListener("click", fillIntakeExample);
$("#refreshConnections").addEventListener("click", refreshAgentConnections);
$("#refreshMcpProfiles").addEventListener("click", refreshMcpProfiles);
$("#approveRemoteProfile").addEventListener("click", approveRemoteProfileOffer);
$("#rejectRemoteProfile").addEventListener("click", rejectRemoteProfileOffer);
window.addEventListener("message", receiveRemoteProfileOffer);
window.addEventListener("message", receiveManagedExtensionOffer);
window.addEventListener("message", receiveManagedExtensionFinalize);
$("#refreshSyncStatus").addEventListener("click", refreshSyncStatus);
$("#readPublication").addEventListener("click", readPublication);
$("#approvePublication").addEventListener("click", () => changePublication("approve"));
$("#revokePublication").addEventListener("click", () => changePublication("revoke"));

const initializeConsole = async () => {
  refreshConsoleProfile();
  refreshStatus();
  try {
    await refreshConsoleSession();
  } catch (error) {
    state.consoleSession = null;
    setAction("session unavailable", error.message);
  }

  if (hasConsoleSession()) {
    refreshAgentConnections();
    refreshMcpProfiles();
    refreshSyncStatus();
    refreshProviderSettings();
    refreshAccessPolicy();
    refreshAccessRequests();
    refreshAudit();
  } else {
    renderSessionGuidance();
  }
};

announceRemoteProfileReady();
announceManagedExtensionReady();
initializeConsole();
