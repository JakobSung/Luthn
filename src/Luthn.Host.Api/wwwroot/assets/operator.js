const state = {
  token: sessionStorage.getItem("luthn.serviceToken") || "",
  decisionToken: sessionStorage.getItem("luthn.decisionToken") || "",
  operatorIdentity: sessionStorage.getItem("luthn.operatorIdentity") || ""
};

const $ = (selector) => document.querySelector(selector);

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

const authHeaders = (useDecisionToken = false) => {
  const headers = {};
  const token = useDecisionToken ? state.decisionToken : state.token;
  if (!token) {
    return state.operatorIdentity
      ? { "X-Luthn-Operator": state.operatorIdentity }
      : headers;
  }

  headers.Authorization = `Bearer ${token}`;
  if (state.operatorIdentity) {
    headers["X-Luthn-Operator"] = state.operatorIdentity;
  }

  return headers;
};

const requestJson = async (url, options = {}) => {
  const { useDecisionToken = false, ...requestOptions } = options;
  const response = await fetch(url, {
    ...requestOptions,
    headers: {
      ...authHeaders(useDecisionToken),
      ...(requestOptions.body ? { "Content-Type": "application/json" } : {}),
      ...(requestOptions.headers || {})
    }
  });

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
  cell.colSpan = 6;
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
    $("#connectionsStatus").textContent = "Unavailable";
    setAction("connections failed", "Status unavailable");
  } finally {
    refreshButton.disabled = false;
  }
};

const refreshSyncStatus = async () => {
  const refreshButton = $("#refreshSyncStatus");
  refreshButton.disabled = true;
  try {
    const result = await requestJson("/api/external-publication/status");
    $("#syncStatus").textContent = `${result.connectionState} / ${result.outboxState}`;
    writeResult($("#publicationOutput"), result);
  } catch (error) {
    $("#syncStatus").textContent = "Unavailable";
    writeResult($("#publicationOutput"), error.message);
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
  Unconfigured: { model: "", endpoint: "", authHeaderName: "Authorization" },
  Mock: { model: "", endpoint: "", authHeaderName: "Authorization" },
  OpenAi: {
    model: "gpt-4.1-mini",
    endpoint: "https://api.openai.com/v1/chat/completions",
    authHeaderName: "Authorization"
  },
  Anthropic: {
    model: "claude-sonnet-4-5",
    endpoint: "https://api.anthropic.com/v1/messages",
    authHeaderName: "x-api-key"
  },
  GoogleAi: {
    model: "gemini-2.5-flash",
    endpoint: "https://generativelanguage.googleapis.com/v1beta/models",
    authHeaderName: "x-goog-api-key"
  },
  OpenRouter: {
    model: "openai/gpt-4.1-mini",
    endpoint: "https://openrouter.ai/api/v1/chat/completions",
    authHeaderName: "Authorization"
  },
  ExternalHttp: { model: "", endpoint: "", authHeaderName: "Authorization" }
};

const renderProviderSettings = (settings) => {
  const form = $("#providerForm");
  const mockOption = form.provider.querySelector('option[value="Mock"]');
  if (mockOption) {
    mockOption.disabled = !settings.mockAllowed;
  }
  form.provider.value = settings.provider;
  form.model.value = settings.model || "";
  form.endpoint.value = settings.endpoint || "";
  form.authHeaderName.value = settings.authHeaderName || "Authorization";
  form.apiKey.value = "";
  form.clearApiKey.checked = false;
  $("#providerStatus").textContent = settings.statusDetail;
  writeResult($("#providerOutput"), settings);
};

const refreshProviderSettings = async () => {
  try {
    const settings = await requestJson("/api/operator/classification-provider");
    renderProviderSettings(settings);
  } catch (error) {
    writeResult($("#providerOutput"), error.message);
    $("#providerStatus").textContent = "Provider settings unavailable";
  }
};

const applyProviderDefaults = () => {
  const form = $("#providerForm");
  const defaults = providerDefaults[form.provider.value] || providerDefaults.Unconfigured;
  if (!form.model.value.trim()) {
    form.model.value = defaults.model;
  }
  if (!form.endpoint.value.trim()) {
    form.endpoint.value = defaults.endpoint;
  }
  form.authHeaderName.value = defaults.authHeaderName;
};

const saveProviderSettings = async (event) => {
  event.preventDefault();
  const form = new FormData(event.currentTarget);
  const body = {
    provider: form.get("provider")?.toString(),
    model: form.get("model")?.toString().trim(),
    endpoint: form.get("endpoint")?.toString().trim(),
    authHeaderName: form.get("authHeaderName")?.toString().trim(),
    apiKey: form.get("apiKey")?.toString(),
    clearApiKey: form.get("clearApiKey") === "on"
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

const refreshAudit = async (event) => {
  event?.preventDefault();
  const form = new FormData($("#auditForm"));
  const params = new URLSearchParams();
  const subjectId = form.get("subjectId")?.toString().trim();
  const limit = form.get("limit")?.toString().trim() || "25";
  if (subjectId) {
    params.set("subjectId", subjectId);
  }
  params.set("limit", limit);

  try {
    const result = await requestJson(`/api/audit-events?${params}`);
    renderAuditRows(result.events || []);
    setAction("audit refreshed", `${result.events?.length || 0} events`);
  } catch (error) {
    renderAuditRows([]);
    setAction("audit failed", error.message);
  }
};

const refreshAccessRequests = async (event) => {
  event?.preventDefault();
  const form = new FormData($("#accessForm"));
  const params = new URLSearchParams();
  const status = form.get("status")?.toString().trim();
  const limit = form.get("limit")?.toString().trim() || "25";
  if (status) {
    params.set("status", status);
  }
  params.set("limit", limit);

  try {
    const result = await requestJson(`/api/access-requests?${params}`, {
      useDecisionToken: true
    });
    renderAccessRows(result.requests || []);
    setAction("access refreshed", `${result.requests?.length || 0} requests`);
  } catch (error) {
    renderAccessRows([]);
    setAction("access failed", error.message);
  }
};

const decideAccessRequest = async (id, decision) => {
  const form = new FormData($("#accessForm"));
  const reason = form.get("reason")?.toString().trim() || "Reviewed in operator console.";
  const redactedSummary = form.get("redactedSummary")?.toString().trim();
  const body = decision === "approve"
    ? {
        reason,
        ...(redactedSummary ? { redactedSummary } : {})
      }
    : { reason };

  try {
    const result = await requestJson(`/api/access-requests/${id}/${decision}`, {
      method: "POST",
      body: JSON.stringify(body),
      useDecisionToken: true
    });
    setAction(`access ${decision}`, result.id);
    await refreshAccessRequests();
    await refreshAudit();
  } catch (error) {
    setAction(`access ${decision} failed`, error.message);
  }
};

const renderAccessRows = (requests) => {
  const rows = $("#accessRows");
  if (requests.length === 0) {
    rows.innerHTML = '<tr><td colspan="7">No access requests available.</td></tr>';
    return;
  }

  rows.replaceChildren(...requests.map((request) => {
    const tr = document.createElement("tr");
    [
      new Date(request.createdAt).toLocaleString(),
      request.id,
      request.sensitiveReferenceId,
      request.status,
      request.outputPolicy || (request.redactedOutputAvailable ? "available" : "unavailable"),
      request.requestedBy
    ].forEach((value) => {
      const td = document.createElement("td");
      td.textContent = value || "";
      tr.appendChild(td);
    });

    const actionCell = document.createElement("td");
    if (request.status === "Pending") {
      const actions = document.createElement("div");
      actions.className = "row-actions";
      const approve = document.createElement("button");
      approve.type = "button";
      approve.textContent = "Approve";
      approve.addEventListener("click", () => decideAccessRequest(request.id, "approve"));
      const deny = document.createElement("button");
      deny.type = "button";
      deny.className = "secondary";
      deny.textContent = "Deny";
      deny.addEventListener("click", () => decideAccessRequest(request.id, "deny"));
      actions.append(approve, deny);
      actionCell.appendChild(actions);
    } else {
      actionCell.textContent = request.decidedBy || "decided";
    }
    tr.appendChild(actionCell);

    return tr;
  }));
};

const renderAuditRows = (events) => {
  const rows = $("#auditRows");
  if (events.length === 0) {
    rows.innerHTML = '<tr><td colspan="6">No audit events available.</td></tr>';
    return;
  }

  rows.replaceChildren(...events.map((event) => {
    const tr = document.createElement("tr");
    [
      new Date(event.occurredAt).toLocaleString(),
      event.actor,
      event.action,
      event.subjectId,
      event.payloadClass,
      event.redactionState
    ].forEach((value) => {
      const td = document.createElement("td");
      td.textContent = value || "";
      tr.appendChild(td);
    });
    return tr;
  }));
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

$("#serviceToken").value = state.token;
$("#decisionToken").value = state.decisionToken;
$("#operatorIdentity").value = state.operatorIdentity;
$("#saveToken").addEventListener("click", () => {
  state.token = $("#serviceToken").value.trim();
  state.decisionToken = $("#decisionToken").value.trim();
  state.operatorIdentity = $("#operatorIdentity").value.trim();
  if (state.token) {
    sessionStorage.setItem("luthn.serviceToken", state.token);
  } else {
    sessionStorage.removeItem("luthn.serviceToken");
  }
  if (state.decisionToken) {
    sessionStorage.setItem("luthn.decisionToken", state.decisionToken);
  } else {
    sessionStorage.removeItem("luthn.decisionToken");
  }
  if (state.operatorIdentity) {
    sessionStorage.setItem("luthn.operatorIdentity", state.operatorIdentity);
  } else {
    sessionStorage.removeItem("luthn.operatorIdentity");
  }
  setAction("token saved", state.token ? "Bearer header enabled" : "No token set");
  refreshAgentConnections();
  refreshSyncStatus();
});
$("#clearToken").addEventListener("click", () => {
  state.token = "";
  state.decisionToken = "";
  state.operatorIdentity = "";
  $("#serviceToken").value = "";
  $("#decisionToken").value = "";
  $("#operatorIdentity").value = "";
  sessionStorage.removeItem("luthn.serviceToken");
  sessionStorage.removeItem("luthn.decisionToken");
  sessionStorage.removeItem("luthn.operatorIdentity");
  setAction("token cleared", "Bearer header disabled");
  refreshAgentConnections();
  refreshSyncStatus();
});
$("#previewForm").addEventListener("submit", previewContent);
$("#intakeForm").addEventListener("submit", submitSource);
$("#providerForm").addEventListener("submit", saveProviderSettings);
$("#providerForm").provider.addEventListener("change", applyProviderDefaults);
$("#testProvider").addEventListener("click", testProviderSettings);
$("#accessForm").addEventListener("submit", refreshAccessRequests);
$("#auditForm").addEventListener("submit", refreshAudit);
$("#previewExample").addEventListener("click", fillPreviewExample);
$("#intakeExample").addEventListener("click", fillIntakeExample);
$("#refreshConnections").addEventListener("click", refreshAgentConnections);
$("#refreshSyncStatus").addEventListener("click", refreshSyncStatus);
$("#readPublication").addEventListener("click", readPublication);
$("#approvePublication").addEventListener("click", () => changePublication("approve"));
$("#revokePublication").addEventListener("click", () => changePublication("revoke"));

refreshStatus();
refreshAgentConnections();
refreshSyncStatus();
refreshProviderSettings();
refreshAccessRequests();
refreshAudit();
