<p align="center">
  <img src="docs/assets/luthn-brand.png" alt="Luthn - Safe context for AI agents." width="920">
</p>

<p align="center">
  <strong>명확한 데이터 경계를 갖춘, AI 에이전트용 직접 호스팅 공유 기억.</strong>
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="docs/installation.md">설치</a> ·
  <a href="docs/agent-quickstart.md">Codex 연결과 기억</a> ·
  <a href="docs/data-boundaries.md">데이터 경계</a> ·
  <a href="docs/local-development.md">개발</a>
</p>

# Luthn

Luthn은 비공개 원본 데이터를 모델의 기본 맥락으로 넘기지 않으면서 여러 AI
에이전트가 프로젝트 기억을 안전하게 공유하고 다시 사용하도록 돕습니다.

- Docker와 PostgreSQL을 사용해 직접 관리하는 인프라에서 실행합니다.
- 수집한 정보를 분류하고 가린 뒤 에이전트에 안전한 요약과 맥락만 제공합니다.
- 어떤 정보가 저장·공유·조회됐는지 확인할 수 있습니다.

## 기억이 이어지는 방식

macOS와 Linux에서는 사용자가 신뢰한 Codex hook이 한 턴의 최종 답변을 길이가
제한된 capsule로 보낼 수 있습니다. Luthn은 이를 가리고 분류한 뒤 허용된 내용만
에이전트용 맥락으로 만듭니다. MCP는 안전한 조회와 명시적인 공유 기억 쓰기를
담당합니다.

선택 기능인 가벼운 자동 회상은 새 작업이나 중요한 주제가 시작될 때 작은 context
pack을 한 번 가져옵니다. 같은 작업에서는 가져온 맥락을 재사용하므로 매 턴마다
조회하지 않습니다.

```text
완료된 턴 -> 제한된 capsule -> 분류 후 안전한 맥락 저장
새 작업   -> 자동 회상 또는 MCP -> 관련 맥락 재사용
```

Windows는 현재 MCP channel만 연결하며 자동 hook과 자동 회상 지침은 설치하지
않습니다. 설정, Trust 단계, 전송 범위, 회상 제한, OS별 차이는
[Codex 연결과 기억](docs/agent-quickstart.md)을 참고하세요.

## 권장 설치: 에이전트에게 맡기기

아래 프롬프트를 Codex나 다른 코딩 에이전트에 전달하세요. 설치 문서는 단순한 명령
목록이 아니라 에이전트가 상태를 점검하고 복구하면서 완료하도록 작성되어 있습니다.

```text
다음 문서에 따라 Luthn을 설치하고 설정하세요.
https://raw.githubusercontent.com/JakobSung/Luthn/refs/heads/main/docs/installation.md

현재 host가 macOS, Linux, Windows 중 무엇인지 판별하고 해당 Docker 직접 호스팅
절차만 사용하세요. 기존 prerequisite를 점검하고 Docker volume, Luthn 설정, Codex
설정, hook, 관련 없는 MCP 등록을 보존하세요. 복구 가능한 PowerShell, PATH, Docker
daemon/context, Codex CLI 탐색 문제를 해결한 뒤 설치를 끝까지 진행하세요.

Codex를 연결하고 health와 readiness를 검증하며 MCP 도구 목록에 get_context_pack이
있는지 확인한 뒤 운영 콘솔 URL을 보여주세요. macOS나 Linux에서는 선택 기능인
--auto-recall의 동작을 설명하되 내가 요청하지 않으면 활성화하지 마세요. 서비스
token이나 자격 증명이 포함된 파일은 출력하거나 복사하지 마세요. 사용자만 처리할
수 있는 license, 권한, 재시작, trust 단계에서만 멈추고 필요한 동작을 정확히
알려주세요.
```

수동 설치 명령, 요구 사항, Windows 복구, 수명주기와 제거 방법은
[설치 문서](docs/installation.md)를 참고하세요.

## 설치 확인

```bash
luthn status
luthn mcp --list-tools
```

health와 readiness가 `ready`여야 하며 MCP 도구 목록에 `get_context_pack`이 있어야
합니다. 기본 운영 콘솔 주소는 <http://127.0.0.1:8080/>입니다.

## 데이터 경계

고객 원문, 비공개 메시지, 자격 증명, 가리지 않은 운영 자료는 비공개 경계 안에
남습니다. 에이전트에는 검토된 요약, 가린 참조, 허용된 프로젝트 맥락처럼 정책을
통과한 안전한 투영만 제공합니다. 외부 공개는 별도의 명시적 승인 경로입니다.

분류 예시, 외부 provider 전송 범위, 에이전트 가시성, 외부 공개 규칙은
[데이터 경계](docs/data-boundaries.md)를 참고하세요.

## 문서

- [설치, 복구와 수명주기](docs/installation.md)
- [Codex 연결, hook, MCP와 자동 회상](docs/agent-quickstart.md)
- [데이터 경계](docs/data-boundaries.md)
- [운영과 복구](docs/operations.md)
- [API](docs/api.md)
- [아키텍처](docs/architecture.md)
- [로컬 개발](docs/local-development.md)
- [라이선스](docs/licensing.md)

## 라이선스와 기여

직접 호스팅 runtime은 AGPL-3.0-only이고 SDK, HTTP connector, 공개 plugin template은
Apache-2.0입니다. 패키지 경계는 [라이선스](docs/licensing.md), 변경 제안 전 정책은
[CONTRIBUTING.md](CONTRIBUTING.md)를 참고하세요.
