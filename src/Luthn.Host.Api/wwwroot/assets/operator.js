const state = {
  consoleSession: null,
  csrfProof: "",
  localConnectPending: false,
  localAccessError: "",
  sessionReason: "",
  selectedAccessRequestId: "",
  selectedAccessDetail: null,
  accessDetailRequestSequence: 0,
  accessDecisionPending: false,
  auditEvents: [],
  auditNextCursor: "",
  auditBaseQuery: "",
  consoleProfile: null,
  enrollment: null,
  cloudLogin: null,
  lifecycle: null
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
    session?.nextAction === "arm-local-session";
  button.hidden = !localMode;
  button.disabled = state.localConnectPending || !connectable;

  if (state.localAccessError && !state.localConnectPending) {
    detail.textContent = state.localAccessError;
  } else if (state.localConnectPending || session?.nextAction === "create-local-session") {
    detail.textContent = t("auth.localConnectingDetail");
  } else if (session?.state === "Active" && localMode) {
    detail.textContent = t("auth.localActiveDetail");
  } else if (connectable) {
    detail.textContent = t("auth.localReadyDetail");
  } else if (session?.state === "LoginRequired") {
    detail.textContent = t("auth.localCloudRequired");
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
    : state.sessionReason === "cloud-account-expired"
      ? "auth.cloudExpired"
    : state.consoleSession?.state === "Active"
    ? "auth.active"
    : state.consoleSession?.state === "Restricted"
      ? "auth.restricted"
      : state.consoleSession?.state === "LoginRequired"
        ? "auth.loginRequired"
        : state.consoleSession?.mode === "LocalAuto" && state.consoleSession?.nextAction === "arm-local-session"
          ? "auth.localReady"
        : "auth.starting";
  const status = t(statusKey);
  ["#authStatus", "#authPanelStatus"].forEach((selector) => {
    const target = $(selector);
    if (target) {
      target.textContent = status;
      target.classList.toggle("configured", statusKey === "auth.active");
      target.classList.toggle("partial", statusKey === "auth.restricted" || statusKey === "auth.loginRequired");
    }
  });
  renderLocalAccess();
};

const renderSessionGuidance = () => {
  const guidance = state.sessionReason === "cloud-account-expired"
    ? t("auth.cloudExpiredDetail")
    : "Console login is required to view agent connections.";
  renderConnectionMessage(guidance);
  $("#connectionsStatus").textContent = "Login required";
  $("#syncStatus").textContent = "Login required";
  writeResult($("#publicationOutput"), guidance);
  $("#providerStatus").textContent = "Console login required";
  writeResult($("#providerOutput"), guidance);
  const providerForm = $("#providerForm");
  if (providerForm) {
    providerForm.clearApiKey.checked = false;
  }
  renderAccessRows([]);
  clearAccessDetail(guidance);
  state.auditEvents = [];
  state.auditNextCursor = "";
  state.auditBaseQuery = "";
  renderAuditRows([]);
  $("#auditStatus").textContent = guidance;
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

const hasConsoleSession = () => ["Active", "Restricted"].includes(state.consoleSession?.state);

const refreshConsoleSession = async () => {
  let session = await requestJson("/api/operator/session", { cache: "no-store" });
  if (session?.state === "Anonymous" && session?.nextAction === "create-local-session") {
    session = await requestJson("/api/operator/session/local", { method: "POST" });
  }

  state.consoleSession = session;
  state.sessionReason = session?.reason || "";
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
    state.consoleSession = await requestJson("/api/operator/session/local/connect", { method: "POST" });
    state.sessionReason = "";
    state.localAccessError = "";
    renderAuthStatus();
    await refreshCloudLogin();
    await refreshLifecycle();
    await refreshEnrollment();
    setAction("local access connected", "Local console authority is active");
    refreshAgentConnections();
    refreshSyncStatus();
    refreshProviderSettings();
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

const renderEnrollment = () => {
  const enrollment = state.enrollment;
  if (!enrollment) {
    $("#enrollmentStatus").textContent = "Unavailable";
    $("#enrollmentFingerprint").textContent = "Unavailable";
    $("#startEnrollment").disabled = true;
    $("#verifyEnrollment").disabled = true;
    return;
  }

  $("#enrollmentStatus").textContent = enrollment.state || enrollment.adapter;
  $("#enrollmentFingerprint").textContent = boundedText(enrollment.installationFingerprint, 64, "Unavailable");
  $("#startEnrollment").disabled = enrollment.adapter === "Disabled" || Boolean(enrollment.state);
  $("#verifyEnrollment").disabled = enrollment.state !== "Pending";
  $("#enrollmentDetail").textContent = enrollment.state === "Approved"
    ? "Enrollment is active. End this Local session and continue with Cloud login."
    : enrollment.state === "Pending"
      ? "The Local session stays active until the bound grant is durably verified."
      : enrollment.adapter === "Disabled"
        ? "Cloud enrollment is disabled. Zero outbound is preserved."
        : "Review the five steps, then start a provider-neutral enrollment challenge.";
};

const refreshEnrollment = async () => {
  if (!hasConsoleSession()) {
    state.enrollment = null;
    renderEnrollment();
    return;
  }

  try {
    state.enrollment = await requestJson("/api/operator/enrollment", { cache: "no-store" });
  } catch {
    state.enrollment = null;
  }
  renderEnrollment();
};

const changeEnrollment = async (action) => {
  try {
    state.enrollment = await requestJson(`/api/operator/enrollment/${action}`, { method: "POST" });
    renderEnrollment();
    if (state.enrollment?.state === "Approved") {
      await refreshConsoleSession();
      await refreshCloudLogin();
      renderSessionGuidance();
    }
  } catch (error) {
    $("#enrollmentDetail").textContent = error.message;
    setAction("enrollment failed", error.message);
  }
};

const renderCloudLogin = () => {
  const login = state.cloudLogin;
  const button = $("#cloudLogin");
  if (!login) {
    button.disabled = true;
    $("#cloudLoginDetail").textContent = "Cloud login status is unavailable.";
    return;
  }

  button.disabled = !login.available || login.sessionState === "Active";
  $("#cloudLoginDetail").textContent = state.sessionReason === "cloud-account-expired"
    ? t("auth.cloudExpiredDetail")
    : login.sessionState === "Active"
    ? "Cloud login is active. Authority and capabilities came from the server-verified provider result."
    : login.available
      ? "Cloud login is required. Organization and workspace identity are never accepted from this page."
      : "Cloud login is not configured in this OSS build. No outbound request was made.";
};

const refreshCloudLogin = async () => {
  try {
    state.cloudLogin = await requestJson("/api/operator/cloud-login", { cache: "no-store" });
  } catch {
    state.cloudLogin = null;
  }
  renderCloudLogin();
};

const loginToCloud = async () => {
  try {
    state.consoleSession = await requestJson("/api/operator/cloud-login", { method: "POST" });
    state.sessionReason = "";
    renderAuthStatus();
    await refreshCloudLogin();
    await refreshLifecycle();
    await refreshEnrollment();
    setAction("cloud login", "Server-derived console authority is active");
    refreshAgentConnections();
    refreshSyncStatus();
    refreshProviderSettings();
    refreshAccessRequests();
    refreshAudit();
  } catch (error) {
    $("#cloudLoginDetail").textContent = error.message;
    setAction("cloud login failed", error.message);
  }
};

const renderLifecycle = () => {
  const lifecycle = state.lifecycle;
  if (!lifecycle) {
    $("#lifecycleStatus").textContent = "Unavailable";
    $("#lifecycleActions").textContent = "None";
    return;
  }

  const actions = Array.isArray(lifecycle.allowedActions) ? lifecycle.allowedActions : [];
  $("#lifecycleStatus").textContent = lifecycle.organizationState;
  $("#lifecycleActions").textContent = actions.join(", ") || "None";
  $("#lifecycleDetail").textContent = lifecycle.organizationState === "RestrictedOffboarding"
    ? "New Cloud connection authority and console mutations are stopped. Export, reconnect, detach, or explicit reclaim remain available."
    : actions.includes("switch-account")
      ? "This account has no active access. Switch account or contact the Organization administrator; Local access was not restored."
      : lifecycle.organizationState === "Detached"
        ? "Cloud authority is revoked. This SingleOwner installation may now create a Local session."
        : "Cloud access is evaluated server-side. Loss never falls back to Local automatically.";
  $("#reconnectCloud").disabled = !actions.includes("reconnect");
  $("#reclaimCloudOwner").disabled = !actions.includes("local-reclaim");
  $("#reclaimOffline").disabled = !actions.includes("local-reclaim") || lifecycle.recoveryVerifier === "Disabled";
  $("#fakeLifecycleActions").hidden = state.cloudLogin?.provider !== "Fake" || state.cloudLogin?.sessionState !== "Active";
};

const refreshLifecycle = async () => {
  try {
    state.lifecycle = await requestJson("/api/operator/lifecycle", { cache: "no-store" });
  } catch {
    state.lifecycle = null;
  }
  renderLifecycle();
};

const changeLifecycle = async (path, body) => {
  try {
    state.lifecycle = await requestJson(`/api/operator/lifecycle/${path}`, {
      method: "POST",
      ...(body ? { body: JSON.stringify(body) } : {})
    });
    await refreshConsoleSession();
    await refreshCloudLogin();
    renderLifecycle();
    if (state.lifecycle?.organizationState === "Detached") {
      await refreshConsoleSession();
    }
    setAction("lifecycle updated", state.lifecycle?.nextAction || path);
  } catch (error) {
    $("#lifecycleDetail").textContent = error.message;
    setAction("lifecycle failed", error.message);
  }
};

const renderConsoleProfile = () => {
  const profile = state.consoleProfile;
  if (!profile) {
    $("#consoleMode").textContent = t("mode.checking");
    $("#consoleModeDetail").textContent = t("mode.unavailable");
    return;
  }

  const isHub = profile.consoleMode === "Hub";
  $("#consoleMode").textContent = t(isHub ? "mode.hub" : "mode.local");
  $("#consoleModeDetail").textContent = t(isHub ? "mode.hubDetail" : "mode.localDetail");
  $("#consoleTransport").textContent = t("mode.transportDisabled");
  $("#consoleAuthority").textContent = t("mode.authorityOss");
};

const refreshConsoleProfile = async () => {
  try {
    const result = await requestJson("/api/operator/console-profile");
    if (
      !["Local", "Hub"].includes(result?.consoleMode) ||
      result?.cloudTransport !== "disabled" ||
      result?.sensitiveAuthority !== "oss-console" ||
      result?.tenancySource !== "authenticated-request" ||
      result?.serverDerived !== true
    ) {
      throw new Error("Console profile boundary is invalid.");
    }
    state.consoleProfile = {
      consoleMode: result.consoleMode,
      cloudTransport: result.cloudTransport,
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
  form.clearApiKey.checked = false;
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
  const params = buildAuditParams();

  try {
    const result = await requestJson(`/api/audit-events?${params}`);
    state.auditEvents = Array.isArray(result?.events) ? result.events : [];
    state.auditNextCursor = typeof result?.nextCursor === "string" ? result.nextCursor : "";
    state.auditBaseQuery = params.toString();
    renderAuditRows(state.auditEvents);
    $("#nextAuditPage").disabled = !state.auditNextCursor;
    $("#auditStatus").textContent = `${state.auditEvents.length} metadata events loaded.`;
    setAction("audit refreshed", `${state.auditEvents.length} events`);
  } catch (error) {
    state.auditEvents = [];
    state.auditNextCursor = "";
    state.auditBaseQuery = "";
    renderAuditRows([]);
    $("#nextAuditPage").disabled = true;
    $("#auditStatus").textContent = hasConsoleSession()
      ? "Audit metadata is unavailable for these filters."
      : "Console login is required to load audit metadata.";
    setAction(hasConsoleSession() ? "audit failed" : "audit waiting", hasConsoleSession() ? error.message : "Sign in through Console access");
  }
};

const loadNextAuditPage = async () => {
  if (!state.auditNextCursor || !state.auditBaseQuery) {
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
  const button = $("#exportAudit");
  button.disabled = true;
  const params = buildAuditParams();
  params.delete("limit");
  try {
    const response = await fetch(`/api/audit-events/export?${params}`, {
      headers: authHeaders()
    });
    if (!response.ok) {
      throw new Error(`${response.status} ${response.statusText}`);
    }
    const url = URL.createObjectURL(await response.blob());
    const link = document.createElement("a");
    link.href = url;
    link.download = "luthn-audit-metadata.json";
    link.click();
    URL.revokeObjectURL(url);
    setAction("audit exported", "metadata-only JSON");
  } catch (error) {
    setAction("audit export failed", error.message);
  } finally {
    button.disabled = false;
  }
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
    const requests = Array.isArray(result.requests) ? result.requests : [];
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

const decideAccessRequest = async (id, decision) => {
  if (state.accessDecisionPending || !state.selectedAccessDetail || state.selectedAccessRequestId !== id || state.selectedAccessDetail.status !== "Pending") {
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
  const body = decision === "approve"
    ? {
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
  createdAt: "—",
  expiresAt: "—",
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
  const canDecide = !state.accessDecisionPending && state.selectedAccessDetail?.status === "Pending" && Boolean(reason);
  $("#approveAccess").disabled = !canDecide;
  $("#denyAccess").disabled = !canDecide;
  $("#viewAccessAudit").disabled = !state.selectedAccessRequestId;
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
  createdAt: formatTimestamp(detail?.createdAt),
  expiresAt: formatTimestamp(detail?.expiresAt),
  requestReason: boundedText(detail?.requestReason, 1000, "Not provided"),
  decisionReason: boundedText(detail?.decisionReason, 1000, "Not decided"),
  decidedAt: detail?.decidedAt ? formatTimestamp(detail.decidedAt) : "Not decided",
  outputPolicy: boundedText(detail?.outputPolicy, 128),
  referenceLabel: boundedText(detail?.reference?.referenceLabel, 256),
  referenceSource: [
    boundedText(detail?.reference?.sourceSystem, 128, ""),
    boundedText(detail?.reference?.sourceType, 128, "")
  ].filter(Boolean).join(" / ") || "Unknown",
  redactedSummary: boundedText(detail?.reference?.redactedSummary, 4000, "Not available")
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
    $("#accessDetailStatus").textContent = safeDetail.status === "Pending"
      ? "Review metadata and enter a decision reason."
      : `Decision complete: ${safeDetail.status}`;
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
      new Date(request.createdAt).toLocaleString(),
      request.id,
      request.sensitiveReferenceId,
      request.status,
      request.outputPolicy || (request.redactedOutputAvailable ? "available" : "unavailable")
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
    cell.colSpan = 11;
    cell.textContent = "No audit events available.";
    row.appendChild(cell);
    rows.replaceChildren(row);
    return;
  }

  rows.replaceChildren(...events.map((event) => {
    const tr = document.createElement("tr");
    [
      new Date(event.occurredAt).toLocaleString(),
      event.category,
      event.actor,
      event.action,
      event.subjectId,
      event.subjectType,
      event.outcome,
      event.correlationId,
      event.payloadClass,
      event.redactionState,
      `${boundedText(event.retentionClass, 64, "Unknown")} / ${formatTimestamp(event.retainedUntil)}`
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
    state.sessionReason = "";
    renderAuthStatus();
    renderSessionGuidance();
    setAction("session ended", "Reload to create a new eligible Local session or sign in to Cloud");
  }
});
$("#connectLocal").addEventListener("click", connectLocalAccess);
$("#startEnrollment").addEventListener("click", () => changeEnrollment("start"));
$("#verifyEnrollment").addEventListener("click", () => changeEnrollment("verify"));
$("#cloudLogin").addEventListener("click", loginToCloud);
$("#reconnectCloud").addEventListener("click", () => changeLifecycle("reconnect"));
$("#reclaimCloudOwner").addEventListener("click", () => changeLifecycle("reclaim", { method: "CloudOwnerReauthentication" }));
$("#reclaimOffline").addEventListener("click", () => changeLifecycle("reclaim", { method: "OfflineRecovery" }));
$("#simulateMembershipRemoval").addEventListener("click", () => changeLifecycle("fake-membership-removed"));
$("#simulateRestriction").addEventListener("click", () => changeLifecycle("fake-organization-restricted"));
$("#previewForm").addEventListener("submit", previewContent);
$("#intakeForm").addEventListener("submit", submitSource);
$("#providerForm").addEventListener("submit", saveProviderSettings);
$("#providerForm").provider.addEventListener("change", applyProviderDefaults);
$("#testProvider").addEventListener("click", testProviderSettings);
$("#accessForm").addEventListener("submit", refreshAccessRequests);
$("#accessDecisionForm").reason.addEventListener("input", updateAccessDecisionState);
$("#approveAccess").addEventListener("click", () => decideAccessRequest(state.selectedAccessRequestId, "approve"));
$("#denyAccess").addEventListener("click", () => decideAccessRequest(state.selectedAccessRequestId, "deny"));
$("#viewAccessAudit").addEventListener("click", viewSelectedAccessAudit);
$("#auditForm").addEventListener("submit", refreshAudit);
$("#nextAuditPage").addEventListener("click", loadNextAuditPage);
$("#exportAudit").addEventListener("click", exportAuditMetadata);
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
$("#refreshSyncStatus").addEventListener("click", refreshSyncStatus);
$("#readPublication").addEventListener("click", readPublication);
$("#approvePublication").addEventListener("click", () => changePublication("approve"));
$("#revokePublication").addEventListener("click", () => changePublication("revoke"));

const initializeConsole = async () => {
  refreshConsoleProfile();
  refreshStatus();
  try {
    await refreshConsoleSession();
    await refreshCloudLogin();
    await refreshLifecycle();
  } catch (error) {
    state.consoleSession = null;
    setAction("session unavailable", error.message);
  }

  if (hasConsoleSession()) {
    refreshEnrollment();
    refreshAgentConnections();
    refreshSyncStatus();
    refreshProviderSettings();
    refreshAccessRequests();
    refreshAudit();
  } else {
    renderSessionGuidance();
  }
};

initializeConsole();
