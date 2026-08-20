# 여러 에이전트 연결

[English](agent-connections.md)

Luthn은 한 설치에 Codex와 Claude Code를 동시에 연결할 수 있습니다. 두 에이전트는
같은 Luthn API와 정책을 통과한 안전한 공유 기억을 사용하지만, 호스트 설정과
연결 수명주기는 서로 독립적입니다.

이 문서는 구현된 개인/셀프호스트 연결, 선택 활성화 OSS Hub 기준선, 별도 라이선스의
Luthn Cloud가 사용하는 Apache-2.0 AgentDevice client 경계를 설명합니다. Hub는
서버가 정한 identity와 metadata-only 감사를 사용해 암호화 durable ingress를 받을 수
있지만 기본값은 비활성입니다. Cloud 연결은 별도의 명시적 작업이며 로컬 MCP를
비활성화하지 않습니다. MCP는 회상과 도구를 제공하고, 자동 수명주기 수집은 독립적인
Agent-native hook, plugin 또는 관리형 설정이 담당합니다. 자세한 내용은
[중앙 팀 Hub data plane](cloud-hub-data-plane.ko.md)에 있습니다.

## 로컬 Luthn을 유지하면서 Cloud 추가하기

관리자가 Workspace ID와 Cloud 주소를 제공하면 두 번째 MCP 서버를 연결합니다.

```bash
luthn cloud connect codex \
  --workspace 00000000-0000-0000-0000-000000000000 \
  --cloud-url https://cloud.example

luthn cloud status codex
```

이 명령은 서로 다른 장치 키 쌍 세 개를 만들고, private key와 credential을
AES-256-GCM으로 암호화한 로컬 상태 파일에 저장합니다. 무작위 복호화 키는 운영 데이터
볼륨과 분리된 호스트의 소유자 전용 설정 파일에 두고, 코드 기반 장치 승인 페이지를
엽니다. 승인된 remote MCP는
`luthn-cloud`라는 별도 이름으로 등록됩니다. 브라우저 주소와 사용자 코드는 따로
표시하므로 코드가 브라우저 기록, referrer 또는 proxy URL log에 들어가지 않습니다.
기존 로컬 `luthn` 등록은 보존되며 Cloud 장애 중에도 계속 동작합니다.

Codex는 MCP 등록 뒤 OAuth를 완료합니다. Claude Code도 같은 remote MCP endpoint를
사용하고 연결 시 지원되는 OAuth 흐름을 진행합니다. Cloud 동의 화면은 정확한
Organization, Workspace, 장치와 Agent 연결을 묶으며, PC에 조직 공용 token을
설치하지 않습니다.

상태 파일과 분리된 복호화 키는 같은 보호 수준의 호스트 backup 정책으로 함께
보관해야 합니다. 키를 잃으면 AgentDevice 상태는 의도적으로 복구할 수 없으므로
Cloud 콘솔에서 장치를 철회하고 다시 등록해야 합니다.

로컬 해제는 Luthn이 소유한 설정에만 적용됩니다.

```bash
luthn cloud disconnect codex
```

이 명령은 로컬의 `luthn-cloud` 등록만 제거합니다. 서버 접근 권한 종료는 Cloud 고객
콘솔에서 해당 AgentConnection 또는 장치를 철회해야 합니다. 사용자가
`luthn cloud connect`를 명시적으로 실행하기 전까지 Cloud 모드는 꺼져 있습니다.

## 동시에 연결하기

두 명령은 순서와 관계없이 실행할 수 있습니다.

```bash
luthn connect codex
luthn connect claude

luthn connection status codex
luthn connection status claude
```

Codex를 먼저 연결한 상태에서 Claude Code를 연결해도 Codex hook, MCP 등록과 회상
지침은 변경되지 않습니다. 이미 필요한 Luthn API scope가 구성되어 있다면 두 번째
연결 때문에 API를 다시 시작하지도 않습니다.

## 공유되는 것과 분리되는 것

| 범위 | 동작 |
|---|---|
| Luthn 서버와 안전한 기억 | 두 에이전트가 공유 |
| 분류·가림·정책·service-token scope | 같은 서버 정책 적용 |
| 저장 출처 | `codex`와 `claude-code`로 구분 |
| Stop hook | 에이전트별 설정 파일에서 독립 관리 |
| MCP 등록 | 각 에이전트 CLI 안에서 독립 관리 |
| 자동 회상 지침 | Codex `AGENTS.md`, Claude Code `CLAUDE.md`에서 독립 관리 |
| ownership state | 에이전트별 파일로 독립 기록 |

따라서 Codex와 Claude Code를 동시에 실행하면서 한쪽에서 저장된 안전한 프로젝트
맥락을 다른 쪽에서 조회할 수 있습니다. 이는 두 CLI가 서로 직접 대화하거나 한
터미널에서 멀티 에이전트 작업을 자동 조정한다는 의미는 아닙니다.

## MCP 이름 충돌

각 에이전트 내부에서는 MCP 이름 `luthn`을 하나만 사용할 수 있습니다. 연결하려는
에이전트에 사용자가 직접 만든 동명의 MCP가 있고 Luthn ownership 기록이 없다면,
Luthn은 해당 등록을 덮어쓰지 않고 그 에이전트의 연결만 중단합니다.

```bash
claude mcp get luthn
```

기존 등록이 불필요하다고 사용자가 확인한 경우에만 직접 제거하고 다시 연결합니다.

```bash
claude mcp remove luthn
luthn connect claude
```

Claude Code 내부의 이름 충돌은 기존 Codex 연결에 영향을 주지 않습니다. Luthn이
소유한 등록도 연결 후 내용이 변경되면 자동으로 덮어쓰거나 삭제하지 않고 충돌로
보고합니다.

## 상태 확인과 개별 해제

상태와 해제는 에이전트별로 수행합니다.

```bash
luthn connection status codex
luthn connection status claude

luthn disconnect codex
luthn disconnect claude
```

`disconnect claude`는 Luthn이 소유한 Claude Code hook, 회상 블록과 MCP 등록만
제거하며 Codex 연결은 유지합니다. `disconnect codex`도 같은 방식으로 Claude Code
연결을 보존합니다. 정리 중 실패하면 ownership state를 남겨 다음 해제에서 안전하게
재시도할 수 있게 합니다.

`luthn uninstall`은 runtime을 제거하기 전에 기록된 Codex와 Claude Code 연결을
모두 정리합니다. 어느 한쪽의 안전한 정리가 완료되지 않으면 uninstall을 중단하고
설정 소유권을 보존합니다.

## 운영체제 지원

동시 연결과 독립 수명주기는 Windows, macOS와 Linux에서 같은 명령으로 제공됩니다.
Windows는 PowerShell connector를, macOS와 Linux는 shell/Python connector를
사용합니다. 개별 hook과 자동 회상의 자세한 데이터 경계는
[에이전트 연결과 기억](agent-quickstart.ko.md)을 참고하세요.
