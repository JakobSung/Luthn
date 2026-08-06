(() => {
  const supportedLanguages = new Set(["en", "ko"]);
  const resources = {
    en: {
      "console.eyebrow": "OSS control plane",
      "console.title": "Luthn Operator Console",
      "console.language": "Language",
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
