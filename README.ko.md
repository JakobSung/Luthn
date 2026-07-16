<p align="center">
  <img src="docs/assets/luthn-brand.png" alt="Luthn - Safe context for AI agents." width="920">
</p>

<p align="center">
  <strong>명확한 데이터 경계를 갖춘, AI 에이전트용 직접 호스팅 공유 기억.</strong>
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="docs/installation.ko.md">설치</a> ·
  <a href="docs/agent-quickstart.ko.md">Codex 연결과 기억</a> ·
  <a href="docs/data-boundaries.ko.md">데이터 경계</a> ·
  <a href="docs/local-development.ko.md">개발</a>
</p>

# Luthn

Luthn은 비공개 원본 데이터를 모델의 기본 맥락으로 넘기지 않으면서 여러 AI
에이전트가 프로젝트 기억을 안전하게 공유하고 다시 사용하도록 돕습니다.

- Docker와 PostgreSQL을 사용해 직접 관리하는 인프라에서 실행합니다.
- 수집한 정보를 분류하고 가린 뒤 에이전트에 안전한 요약과 맥락만 제공합니다.
- 어떤 정보가 저장·공유·조회됐는지 확인할 수 있습니다.

## 기억이 이어지는 방식

macOS, Linux와 Windows에서는 사용자가 신뢰한 Codex hook이 한 턴의 최종 답변을
길이가 제한된 capsule로 보낼 수 있습니다. Luthn은 이를 가리고 분류한 뒤 허용된
내용만 에이전트용 맥락으로 만듭니다. MCP는 안전한 조회와 명시적인 공유 기억
쓰기를 담당합니다.

가벼운 자동 회상은 Codex 연결 시 기본으로 활성화되며 새 작업이나 중요한 주제가
시작될 때 작은 context pack을 한 번 가져옵니다. 같은 작업에서는 가져온 맥락을
재사용하므로 매 턴마다 조회하지 않습니다.

```text
완료된 턴 -> 제한된 capsule -> 분류 후 안전한 맥락 저장
새 작업   -> 자동 회상 또는 MCP -> 관련 맥락 재사용
```

설정, 최초 한 번의 hook Trust 단계, 전송 범위와 회상 제한은
[Codex 연결과 기억](docs/agent-quickstart.ko.md)을 참고하세요.

## 권장 설치

Codex나 다른 코딩 에이전트에 아래 프롬프트를 전달하세요.

```text
Install and configure Luthn locally by following the instructions here:
https://raw.githubusercontent.com/JakobSung/Luthn/refs/heads/main/docs/installation.md
```

필수 조건, 직접 설치, Windows 설정, 수명주기 명령, 에이전트 연결 방법은
[상세 설치 문서](docs/installation.ko.md)를 참고하세요.

## 데이터 경계

고객 원문, 비공개 메시지, 자격 증명, 가리지 않은 운영 자료는 비공개 경계 안에
남습니다. 에이전트에는 검토된 요약, 가린 참조, 허용된 프로젝트 맥락처럼 정책을
통과한 안전한 투영만 제공합니다. 외부 공개는 별도의 명시적 승인 경로입니다.

분류 예시, 외부 provider 전송 범위, 에이전트 가시성, 외부 공개 규칙은
[데이터 경계](docs/data-boundaries.ko.md)를 참고하세요.

## 문서

- [설치, 복구와 수명주기](docs/installation.ko.md)
- [Codex 연결, hook, MCP와 자동 회상](docs/agent-quickstart.ko.md)
- [데이터 경계](docs/data-boundaries.ko.md)
- [운영과 복구](docs/operations.ko.md)
- [API](docs/api.ko.md)
- [구조](docs/architecture.ko.md)
- [로컬 개발](docs/local-development.ko.md)
- [프로젝트 맥락](docs/project-context.ko.md)
- [프로젝트 구조](docs/project-structure.ko.md)
- [원본 참조](docs/source-references.ko.md)
- [라이선스](docs/licensing.ko.md)

## 라이선스와 기여

직접 호스팅 runtime은 AGPL-3.0-only이고 SDK, HTTP connector, 공개 plugin template은
Apache-2.0입니다. 패키지 경계는 [라이선스](docs/licensing.ko.md), 변경 제안 전 정책은
[CONTRIBUTING.md](CONTRIBUTING.md)를 참고하세요.
