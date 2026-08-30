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
      "orientation.description": "This console is for operators. Start with access setup, then choose one workflow. Approved protected memory is delivered only to the bound requester; credentials are never delivered.",
      "orientation.stepAccessTitle": "Set up access",
      "orientation.stepAccessBody": "Use Console access to connect one bounded Personal Local session.",
      "orientation.stepAccessAction": "Open access settings",
      "orientation.stepReviewTitle": "Review a decision",
      "orientation.stepReviewBody": "Select a pending sensitive-access request. Only allowlisted metadata and a redacted summary are shown.",
      "orientation.stepReviewAction": "Open approvals",
      "orientation.stepAuditTitle": "Investigate the trail",
      "orientation.stepAuditBody": "Use a preset to trace access, failures, configuration, publication, or retention metadata.",
      "orientation.stepAuditAction": "Open audit center",
      "auth.summaryLabel": "Console access",
      "auth.starting": "Starting secure session",
      "auth.localReady": "Local access ready",
      "auth.localConnecting": "Connecting local access",
      "auth.active": "Secure session active",
      "auth.openSettings": "Set up access",
      "auth.connectLocal": "Connect local access",
      "auth.localEyebrow": "Personal Local access",
      "auth.localTitle": "Connect local access",
      "auth.eyebrow": "Access setup",
      "auth.title": "How this console authenticates",
      "auth.description": "The Host keeps the credential server-side. The browser receives only a bounded HttpOnly session cookie.",
      "auth.serviceTitle": "Personal Local = one-time authorized session",
      "auth.serviceBody": "No key entry is needed. The server permits it only on the hardened personal installation.",
      "auth.identityTitle": "Agent credentials stay separate",
      "auth.identityBody": "Existing service credentials continue to authenticate agents and API clients, never a human console session.",
      "auth.scopeNote": "LocalAuto is available only for a SingleOwner installation bound to loopback. The installed Host Helper authorizes one explicit browser request without a terminal command.",
      "auth.localReadyDetail": "Local access is ready to connect from this console.",
      "auth.localArmRequired": "Select Connect local access. The installed Host Helper will authorize this browser without a terminal command.",
      "auth.localConnectingDetail": "The console is creating one bounded LocalAuto session.",
      "auth.localActiveDetail": "Local access is active.",
      "auth.localUnavailable": "Local access is unavailable for this installation.",
      "auth.modeLabel": "Session mode",
      "auth.expiryLabel": "Idle expiry",
      "auth.logout": "End this session",
      "auth.securityNote": "No console credential is placed in the page, URL, browser storage, logs, or exports.",
      "mode.label": "Deployment mode",
      "mode.checking": "Checking",
      "mode.local": "Local",
      "mode.multiUser": "Multi-user",
      "mode.localDetail": "Personal self-host mode. Sensitive approval remains in this OSS console.",
      "mode.multiUserDetail": "Multi-user self-host mode. Member identity is derived from authenticated requests.",
      "mode.transport": "Outbound transport",
      "mode.transportDisabled": "Disabled — zero outbound",
      "mode.authority": "Sensitive authority",
      "mode.authorityOss": "OSS console",
      "mode.unavailable": "Console profile unavailable",
      "publication.eyebrow": "Separate publication boundary",
      "publication.title": "External publication",
      "access.eyebrow": "Sensitive access authority",
      "access.title": "Access requests",
      "access.review": "Request review",
      "access.policyEyebrow": "Common bounded policy",
      "access.policyTitle": "Sensitive access settings",
      "access.policyLoading": "Loading policy…",
      "access.requestTimeout": "Approval wait (minutes)",
      "access.grantDuration": "Result availability (minutes)",
      "access.maxReads": "Maximum successful reads",
      "access.savePolicy": "Save policy",
      "access.policyNote": "Changes apply to new requests and later approvals. Existing grants are never extended.",
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
      "orientation.description": "이 콘솔은 운영자용입니다. 접근을 설정한 다음 작업을 선택하세요. 승인된 보호 정보는 요청자에게만 전달되며 자격증명은 절대 전달되지 않습니다.",
      "orientation.stepAccessTitle": "접근 설정",
      "orientation.stepAccessBody": "콘솔 접근 화면에서 개인 로컬 세션 하나를 제한된 시간 동안 연결합니다.",
      "orientation.stepAccessAction": "접근 설정 열기",
      "orientation.stepReviewTitle": "결정 검토",
      "orientation.stepReviewBody": "대기 중인 민감 접근 요청을 선택하세요. 허용 목록 metadata와 redacted summary만 표시됩니다.",
      "orientation.stepReviewAction": "승인 화면 열기",
      "orientation.stepAuditTitle": "감사 기록 조사",
      "orientation.stepAuditBody": "preset으로 접근·실패·설정·공개·보존 metadata를 추적하세요.",
      "orientation.stepAuditAction": "감사 센터 열기",
      "auth.summaryLabel": "콘솔 접근",
      "auth.starting": "보안 세션 시작 중",
      "auth.localReady": "로컬 access 연결 준비됨",
      "auth.localConnecting": "로컬 access 연결 중",
      "auth.active": "보안 세션 활성",
      "auth.openSettings": "접근 설정",
      "auth.connectLocal": "로컬 access 연결",
      "auth.localEyebrow": "개인 Local access",
      "auth.localTitle": "로컬 access 연결",
      "auth.eyebrow": "접근 설정",
      "auth.title": "콘솔 인증 방식",
      "auth.description": "Host가 자격 증명을 서버 안에 보관하며 브라우저에는 제한된 HttpOnly 세션 쿠키만 전달합니다.",
      "auth.serviceTitle": "개인 로컬 = 일회성 승인 세션",
      "auth.serviceBody": "키 입력이 필요하지 않습니다. 강화된 개인 설치에서만 서버가 허용합니다.",
      "auth.identityTitle": "Agent 자격 증명은 별도 유지",
      "auth.identityBody": "기존 service 자격 증명은 Agent와 API client 인증에만 사용되며 사람의 콘솔 세션이 되지 않습니다.",
      "auth.scopeNote": "LocalAuto는 루프백에 바인딩된 SingleOwner 설치에서만 사용할 수 있습니다. 설치된 Host Helper가 터미널 명령 없이 명시적으로 요청한 브라우저 하나만 승인합니다.",
      "auth.localReadyDetail": "이 콘솔에서 로컬 access를 연결할 준비가 되었습니다.",
      "auth.localArmRequired": "로컬 access 연결을 선택하세요. 설치된 Host Helper가 터미널 명령 없이 이 브라우저를 승인합니다.",
      "auth.localConnectingDetail": "제한된 LocalAuto 세션 하나를 생성하고 있습니다.",
      "auth.localActiveDetail": "로컬 access가 활성화되었습니다.",
      "auth.localUnavailable": "이 설치에서는 로컬 access를 사용할 수 없습니다.",
      "auth.modeLabel": "세션 모드",
      "auth.expiryLabel": "유휴 만료",
      "auth.logout": "이 세션 종료",
      "auth.securityNote": "콘솔 자격 증명은 페이지·URL·브라우저 저장소·로그·내보내기에 포함되지 않습니다.",
      "mode.label": "배포 모드",
      "mode.checking": "확인 중",
      "mode.local": "로컬",
      "mode.multiUser": "다중 사용자",
      "mode.localDetail": "개인 self-host 모드입니다. 민감 접근 승인은 이 OSS 콘솔에서 유지됩니다.",
      "mode.multiUserDetail": "다중 사용자 self-host 모드입니다. 구성원 identity는 인증된 요청에서 결정됩니다.",
      "mode.transport": "외부 전송",
      "mode.transportDisabled": "비활성 — 외부 전송 없음",
      "mode.authority": "민감 접근 권한 정본",
      "mode.authorityOss": "OSS 콘솔",
      "mode.unavailable": "콘솔 profile을 확인할 수 없습니다",
      "publication.eyebrow": "분리된 외부 공개 경계",
      "publication.title": "외부 공개 승인",
      "access.eyebrow": "민감 접근 권한 정본",
      "access.title": "민감 접근 요청",
      "access.review": "요청 검토",
      "access.policyEyebrow": "공통 제한 정책",
      "access.policyTitle": "민감 접근 설정",
      "access.policyLoading": "정책을 불러오는 중…",
      "access.requestTimeout": "승인 대기시간(분)",
      "access.grantDuration": "결과 노출시간(분)",
      "access.maxReads": "최대 성공 조회 횟수",
      "access.savePolicy": "정책 저장",
      "access.policyNote": "변경값은 새 요청과 이후 승인부터 적용되며 기존 grant를 연장하지 않습니다.",
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
