(() => {
  const supportedLanguages = new Set(["en", "ko"]);
  const resources = {
    en: {
      "console.eyebrow": "OSS control plane",
      "console.title": "Luthn Operator Console",
      "console.language": "Language",
      "nav.eyebrow": "Operator workspace",
      "nav.prompt": "Choose a task",
      "nav.overview": "Overview",
      "nav.access": "Access approvals",
      "nav.publication": "Publication",
      "nav.intake": "Classify & intake",
      "nav.audit": "Audit center",
      "nav.settings": "Console access",
      "orientation.eyebrow": "Start here",
      "orientation.title": "Use the console by task",
      "orientation.description": "This console is for operators. Start with access setup, then choose one workflow. Agents never receive approval or raw protected content.",
      "orientation.stepAccessTitle": "Set up access",
      "orientation.stepAccessBody": "Add the API token for reading the local runtime. Add a separate decision token only when you approve or deny.",
      "orientation.stepAccessAction": "Open access settings",
      "orientation.stepReviewTitle": "Review a decision",
      "orientation.stepReviewBody": "Select a pending sensitive-access request. Only allowlisted metadata and a redacted summary are shown.",
      "orientation.stepReviewAction": "Open approvals",
      "orientation.stepAuditTitle": "Investigate the trail",
      "orientation.stepAuditBody": "Use a preset to trace access, failures, configuration, publication, or retention metadata.",
      "orientation.stepAuditAction": "Open audit center",
      "auth.summaryLabel": "Console access",
      "auth.notConfigured": "Not configured",
      "auth.configured": "Session credentials set",
      "auth.partial": "Service token set; decision token missing",
      "auth.decisionOnly": "Decision token set; service token missing",
      "auth.openSettings": "Set up access",
      "auth.eyebrow": "Access setup",
      "auth.title": "How this console authenticates",
      "auth.description": "Tokens are local session credentials. They are kept in this browser session only and are sent as bearer headers to the local Host API.",
      "auth.serviceTitle": "Service token = routine API access",
      "auth.serviceBody": "Use the token configured for this Luthn installation. Its server scopes control each menu. In Testing mode it can be blank; it never grants sensitive approval by itself.",
      "auth.decisionTitle": "Decision token = approve or deny",
      "auth.decisionBody": "Use a trusted operator token with the decision scope for access requests. Keep it separate when read-only operators should not decide.",
      "auth.identityTitle": "Operator label = audit context only",
      "auth.identityBody": "This optional label appears in audit metadata. It never creates permission or changes the authenticated identity.",
      "auth.serviceLabel": "Service token",
      "auth.serviceHint": "Used for non-decision API calls; the required scope depends on the selected menu.",
      "auth.servicePlaceholder": "Optional in Testing mode",
      "auth.decisionLabel": "Decision token",
      "auth.decisionHint": "Required for sensitive-access list, detail, approve, and deny.",
      "auth.decisionPlaceholder": "Operator token with access.decide",
      "auth.identityLabel": "Operator label",
      "auth.identityHint": "Optional audit label; not an authorization control.",
      "auth.identityPlaceholder": "for example local-operator",
      "auth.save": "Save for this session",
      "auth.clear": "Clear session credentials",
      "auth.scopeNote": "Common service scopes: agent.connection.read, classification.preview, source.write, external-publication.read/write, audit.read. Configure only what this operator needs.",
      "auth.securityNote": "Never paste a credential into a ticket, screenshot, prompt, or audit export. The console does not display raw Vault/source content.",
      "mode.label": "Deployment mode",
      "mode.checking": "Checking",
      "mode.local": "Local",
      "mode.hub": "Hub",
      "mode.localDetail": "Personal self-host mode. Sensitive approval remains in this OSS console.",
      "mode.hubDetail": "Central OSS Hub mode. Member identity is derived from authenticated requests.",
      "mode.transport": "Cloud transport",
      "mode.transportDisabled": "Disabled — zero outbound",
      "mode.authority": "Sensitive authority",
      "mode.authorityOss": "OSS console",
      "mode.unavailable": "Console profile unavailable",
      "publication.eyebrow": "Separate publication boundary",
      "publication.title": "External publication",
      "access.eyebrow": "Sensitive access authority",
      "access.title": "Access requests",
      "access.review": "Request review",
      "audit.eyebrow": "Metadata-only trail",
      "audit.title": "Audit center",
      "audit.category": "Category",
      "audit.next": "Next page",
      "audit.export": "Export metadata",
      "audit.occurred": "Occurred",
      "audit.actor": "Actor",
      "audit.action": "Action",
      "audit.subject": "Subject",
      "audit.type": "Type",
      "audit.outcome": "Outcome",
      "audit.correlation": "Correlation",
      "audit.payload": "Payload",
      "audit.redaction": "Redaction",
      "audit.retention": "Retention"
    },
    ko: {
      "console.eyebrow": "OSS 제어 영역",
      "console.title": "Luthn 운영 콘솔",
      "console.language": "언어",
      "nav.eyebrow": "운영자 작업 공간",
      "nav.prompt": "작업을 선택하세요",
      "nav.overview": "개요",
      "nav.access": "민감 접근 승인",
      "nav.publication": "외부 공개",
      "nav.intake": "분류·수집",
      "nav.audit": "감사 센터",
      "nav.settings": "콘솔 접근",
      "orientation.eyebrow": "시작하기",
      "orientation.title": "작업별로 콘솔을 사용하세요",
      "orientation.description": "이 콘솔은 운영자용입니다. 접근을 설정한 다음 작업을 선택하세요. Agent에는 승인 권한이나 보호된 원문이 전달되지 않습니다.",
      "orientation.stepAccessTitle": "접근 설정",
      "orientation.stepAccessBody": "로컬 runtime을 읽을 API token을 입력하세요. 승인·반려할 때만 별도의 decision token을 추가하세요.",
      "orientation.stepAccessAction": "접근 설정 열기",
      "orientation.stepReviewTitle": "결정 검토",
      "orientation.stepReviewBody": "대기 중인 민감 접근 요청을 선택하세요. 허용 목록 metadata와 redacted summary만 표시됩니다.",
      "orientation.stepReviewAction": "승인 화면 열기",
      "orientation.stepAuditTitle": "감사 기록 조사",
      "orientation.stepAuditBody": "preset으로 접근·실패·설정·공개·보존 metadata를 추적하세요.",
      "orientation.stepAuditAction": "감사 센터 열기",
      "auth.summaryLabel": "콘솔 접근",
      "auth.notConfigured": "설정되지 않음",
      "auth.configured": "이 세션의 자격 증명 설정됨",
      "auth.partial": "Service token 설정됨; decision token 없음",
      "auth.decisionOnly": "Decision token 설정됨; service token 없음",
      "auth.openSettings": "접근 설정",
      "auth.eyebrow": "접근 설정",
      "auth.title": "콘솔 인증 방식",
      "auth.description": "Token은 이 브라우저 세션에서만 사용하는 자격 증명입니다. 로컬 Host API에 bearer header로 전송됩니다.",
      "auth.serviceTitle": "Service token = 일반 API 접근",
      "auth.serviceBody": "이 Luthn 설치에 설정된 token을 사용하세요. 서버 scope가 각 메뉴의 사용 범위를 정합니다. Testing mode에서는 비워 둘 수 있으며 민감 접근 승인 권한은 만들지 않습니다.",
      "auth.decisionTitle": "Decision token = 승인·반려",
      "auth.decisionBody": "민감 접근 결정 scope가 있는 신뢰된 운영자 token을 사용하세요. 읽기 전용 운영자가 결정을 내리지 못하게 별도로 유지할 수 있습니다.",
      "auth.identityTitle": "Operator label = 감사 맥락만",
      "auth.identityBody": "선택한 label은 감사 metadata에만 남습니다. 권한을 만들거나 인증 identity를 바꾸지 않습니다.",
      "auth.serviceLabel": "Service token",
      "auth.serviceHint": "결정 이외의 API 호출에 사용하며 필요한 scope는 선택한 메뉴에 따라 다릅니다.",
      "auth.servicePlaceholder": "Testing mode에서는 선택 사항",
      "auth.decisionLabel": "Decision token",
      "auth.decisionHint": "민감 접근 목록·상세·승인·반려에 필요합니다.",
      "auth.decisionPlaceholder": "access.decide가 있는 운영자 token",
      "auth.identityLabel": "Operator label",
      "auth.identityHint": "선택적인 감사 label이며 권한 제어가 아닙니다.",
      "auth.identityPlaceholder": "예: local-operator",
      "auth.save": "이 세션에 저장",
      "auth.clear": "세션 자격 증명 삭제",
      "auth.scopeNote": "일반적인 service scope: agent.connection.read, classification.preview, source.write, external-publication.read/write, audit.read. 운영자에게 필요한 것만 설정하세요.",
      "auth.securityNote": "자격 증명을 ticket·screenshot·prompt·감사 export에 붙여 넣지 마세요. 콘솔은 Vault/source 원문을 표시하지 않습니다.",
      "mode.label": "배포 모드",
      "mode.checking": "확인 중",
      "mode.local": "로컬",
      "mode.hub": "허브",
      "mode.localDetail": "개인 self-host 모드입니다. 민감 접근 승인은 이 OSS 콘솔에서 유지됩니다.",
      "mode.hubDetail": "중앙 OSS Hub 모드입니다. 구성원 identity는 인증된 요청에서 결정됩니다.",
      "mode.transport": "Cloud 전송",
      "mode.transportDisabled": "비활성 — 외부 전송 없음",
      "mode.authority": "민감 접근 권한 정본",
      "mode.authorityOss": "OSS 콘솔",
      "mode.unavailable": "콘솔 profile을 확인할 수 없습니다",
      "publication.eyebrow": "분리된 외부 공개 경계",
      "publication.title": "외부 공개 승인",
      "access.eyebrow": "민감 접근 권한 정본",
      "access.title": "민감 접근 요청",
      "access.review": "요청 검토",
      "audit.eyebrow": "메타데이터 전용 기록",
      "audit.title": "감사 센터",
      "audit.category": "분류",
      "audit.next": "다음 페이지",
      "audit.export": "메타데이터 내보내기",
      "audit.occurred": "발생 시각",
      "audit.actor": "행위자",
      "audit.action": "행위",
      "audit.subject": "대상",
      "audit.type": "유형",
      "audit.outcome": "결과",
      "audit.correlation": "연결 ID",
      "audit.payload": "Payload",
      "audit.redaction": "가림 상태",
      "audit.retention": "보존"
    }
  };

  const normalizeLanguage = (value) => supportedLanguages.has(value) ? value : "en";
  const storedLanguage = () => {
    try {
      return normalizeLanguage(localStorage.getItem("luthn.consoleLanguage"));
    } catch {
      return "en";
    }
  };

  let language = storedLanguage();
  const translate = (key) => resources[language]?.[key] || resources.en[key] || key;
  const apply = (requestedLanguage) => {
    language = normalizeLanguage(requestedLanguage);
    document.documentElement.lang = language;
    document.querySelectorAll("[data-i18n]").forEach((node) => {
      node.textContent = translate(node.dataset.i18n);
    });
    document.querySelectorAll("[data-i18n-placeholder]").forEach((node) => {
      node.setAttribute("placeholder", translate(node.dataset.i18nPlaceholder));
    });
    const selector = document.querySelector("#consoleLanguage");
    if (selector) {
      selector.value = language;
    }
    try {
      localStorage.setItem("luthn.consoleLanguage", language);
    } catch {
      // A blocked preference store must not block console operation.
    }
    document.dispatchEvent(new CustomEvent("luthn:language-changed", { detail: { language } }));
  };

  window.LuthnOperatorI18n = {
    apply,
    language: () => language,
    translate
  };
  apply(language);
})();
