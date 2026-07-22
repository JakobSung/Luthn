<p align="center">
  <img src="docs/assets/luthn-brand.png" alt="Luthn - Safe context for AI agents." width="920">
</p>

<p align="center">
  <strong>명확한 데이터 경계를 갖춘, AI 에이전트용 직접 호스팅 공유 기억.</strong>
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="docs/installation.ko.md">설치</a> ·
  <a href="docs/agent-quickstart.ko.md">에이전트 연결과 기억</a> ·
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

macOS, Linux와 Windows에서는 Codex 또는 Claude Code의 Stop hook이 한 턴의 최종 답변을
길이가 제한된 capsule로 보냅니다. Luthn은 이를 가리고 분류한 뒤 허용된
내용만 에이전트용 맥락으로 만듭니다. MCP는 안전한 조회와 명시적인 공유 기억
쓰기를 담당합니다.

가벼운 자동 회상은 에이전트 연결 시 기본으로 활성화되며 새 작업이나 중요한 주제가
시작될 때 작은 context pack을 한 번 가져옵니다. 같은 작업에서는 가져온 맥락을
재사용하므로 매 턴마다 조회하지 않습니다. 선택적인 비민감 프로젝트·작업·주제
키로 회상 범위와 순위를 조정하며, 최근 안전 투영에는 제한된 최신성 점수를 줍니다.

```text
완료된 턴 -> 제한된 capsule -> 분류 후 안전한 맥락 저장
새 작업   -> 자동 회상 또는 MCP -> 관련 맥락 재사용
```

설정, 최초 한 번의 hook Trust 단계, 전송 범위와 회상 제한은
[에이전트 연결과 기억](docs/agent-quickstart.ko.md)을 참고하세요.

Codex 통합은 이미 구현되어 있습니다. 첫 번째 명령으로 구성하고 두 번째 명령으로
상태를 확인합니다.

```bash
luthn connect codex
luthn connection status codex

luthn connect claude
luthn connection status claude
```

Codex와 Claude Code는 한 Luthn 설치에 동시에 연결할 수 있습니다. 두 에이전트는
안전한 공유 기억을 함께 사용하지만 hook, MCP 등록, 회상 지침과 연결 상태는 각각
독립적으로 관리됩니다. 공존 방식과 이름 충돌·해제 동작은
[여러 에이전트 연결](docs/agent-connections.ko.md)을 참고하세요.

각 연결 명령은 해당 에이전트에 Luthn 소유 Stop hook과 Docker 기반 `luthn` MCP를
등록하고, Codex `AGENTS.md` 또는 Claude Code `CLAUDE.md`에 표시된 자동 회상 지침을
추가합니다. Windows PowerShell
Stop hook은 Codex가 분리된 업로더를 종료하지 못하도록 훅 프로세스 안에서 제한된
업로드를 완료하며 제한 시간은 10초이고 실패 허용 동작을 유지합니다. macOS와
Linux는 기존 비동기 helper를 사용합니다. 자동 회상은 기본으로 활성화되며 최대
3개 항목, 약 600 token, 200ms 실패 허용 제한, 10분 cache를 사용합니다. 최초 연결
또는 관리 hook 갱신 뒤 Codex를 다시 시작하고 `/hooks`에서
`Stop > luthn.agent-connector.v1`을 한 번 Trust합니다.

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
- [에이전트 연결, hook, MCP와 자동 회상](docs/agent-quickstart.ko.md)
- [Codex와 Claude Code 동시 연결](docs/agent-connections.ko.md)
- [데이터 경계](docs/data-boundaries.ko.md)
- [운영과 복구](docs/operations.ko.md)
- [컨테이너 릴리즈](docs/releases.ko.md)
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
