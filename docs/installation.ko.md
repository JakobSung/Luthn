# Luthn 설치

[English](installation.md)

사용자를 위한 원본 코드 없는 직접 호스팅 설치 절차입니다. macOS, Linux, Windows 모두 같은 Linux Compose runtime을 사용하며 Git, Luthn 원본 저장소, .NET SDK는 필요하지 않습니다.

## 에이전트에 전달할 요청

```text
Install and configure Luthn locally by following the instructions here:
https://raw.githubusercontent.com/JakobSung/Luthn/refs/heads/main/docs/installation.md
```

에이전트는 운영체제를 확인해 해당 절차만 사용하고, 기존 Docker volume·Luthn/Codex 설정·hook·관계없는 MCP 등록을 보존해야 합니다. token이나 자격 증명 파일을 출력하면 안 됩니다. 새 설치의 완료 조건은 `luthn status`의 health가 `ready`이고 readiness가 분류 provider 설정 필요 상태를 정확히 보고하는 것, 운영자 화면 URL 확인, `luthn mcp --list-tools`의 `get_context_pack` 확인, Codex MCP 등록 확인입니다. 운영자가 실제 분류 provider를 설정한 뒤에는 readiness도 `ready`여야 합니다. 사용자만 할 수 있는 provider 선택·재시작·hook Trust가 남으면 정확히 안내해야 합니다.

## 요구 사항

### macOS와 Linux

- Docker와 Docker Compose
- curl
- 실행 중인 Docker daemon
- Codex 연결 시 Python 3

```bash
docker info
docker compose version
```

오래된 socket을 가리키면 `docker context ls`와 `docker context use <context-name>`으로 동작하는 context를 선택합니다.

### Windows

- Windows 11
- PowerShell 7.4 이상(`pwsh`)
- Linux container mode로 실행 중인 Docker Desktop과 Docker Compose
- Codex 연결 시 실행 가능한 Codex CLI

```powershell
pwsh -NoProfile -Command '$PSVersionTable.PSVersion'
docker info --format '{{.OSType}}'
docker compose version
```

첫 Docker 명령 결과는 `linux`여야 합니다. Windows container는 지원하지 않습니다.

## macOS·Linux 설치

```bash
curl -fsSL https://raw.githubusercontent.com/JakobSung/Luthn/main/scripts/install.sh | bash -s -- --channel stable --connect-codex
```

설치 과정은 `~/.local/bin/luthn` CLI와 원본 없는 Compose 묶음을 설치하고,
`ghcr.io/jakobsung/luthn:stable`을 변경 불가 digest로 확정해 로컬 서비스 token을
만들며, PostgreSQL 시작·migration·공개 안전 예제 자료 입력·health 확인을
수행합니다. 새 설치의 분류 상태는 명시적인 `unconfigured`이며 운영자가 실제
provider를 선택하기 전까지 `/readyz`는 `not_ready`입니다. `--connect-codex`는
Codex hook, MCP, 기본 자동 회상도 설정합니다.

| 용도 | 기본 경로 |
|---|---|
| CLI | `~/.local/bin/luthn` |
| Compose runtime | `~/.local/share/luthn/compose.yaml` |
| 비공개 설정 | `~/.config/luthn/luthn.env` |
| 서비스 token | `~/.config/luthn/service-token` |
| 갱신 상태·backup | `~/.local/state/luthn/` |
| connector runtime | `~/.local/share/luthn/runtime/` |
| connector 상태 | `~/.local/state/luthn/connectors/` |
| PostgreSQL volume | `luthn-postgres` |
| 운영자 설정·Data Protection key volume | `luthn-operator` |

## Windows 설치

PowerShell이 없거나 오래되었으면 다음으로 설치·갱신합니다.

```powershell
winget install --id Microsoft.PowerShell --source winget
winget upgrade --id Microsoft.PowerShell --source winget
```

Docker Desktop을 실행하고 `docker context show`, `docker info --format '{{.OSType}}'`, `docker compose version`을 확인한 뒤 PowerShell에서 설치합니다.

```powershell
$installer = Join-Path ([IO.Path]::GetTempPath()) "luthn-install.ps1"
try {
    irm https://raw.githubusercontent.com/JakobSung/Luthn/main/scripts/install.ps1 -OutFile $installer
    pwsh -NoProfile -File $installer -Channel stable -ConnectCodex
    if ($LASTEXITCODE -ne 0) { throw "Luthn installation failed with exit code $LASTEXITCODE" }
} finally {
    Remove-Item -LiteralPath $installer -ErrorAction SilentlyContinue
}
```

열려 있던 상위 terminal은 변경된 `PATH`를 받지 못합니다. 재설치하지 말고 새 terminal을 열거나 현재 session만 고칩니다.

```powershell
$env:Path = "$env:LOCALAPPDATA\Luthn\bin;$env:Path"
luthn status
& "$env:LOCALAPPDATA\Luthn\bin\luthn.ps1" status
```

Codex CLI 검색 순서는 `LUTHN_CODEX_COMMAND`, `CODEX_CLI_PATH`, `%LOCALAPPDATA%\OpenAI\Codex\bin`, `PATH`입니다. 후보는 `codex --version`이 성공해야 합니다.

```powershell
$env:LUTHN_CODEX_COMMAND = 'C:\path\to\codex.exe'
& "$env:LOCALAPPDATA\Luthn\bin\luthn.ps1" connect codex
```

`WindowsApps`에서 실행 파일을 복사하거나 ACL을 바꾸지 않습니다. 설치 후 Codex를 다시 시작하고 `/mcp`에서 `luthn`, `/hooks`에서 `Stop > luthn.agent-connector.v1`을 Trust한 다음 turn 하나를 완료해 `luthn connection status codex`를 확인합니다.

Windows connector는 현재 운영 경로로 구현되어 있습니다. PowerShell Stop hook은
제한된 capsule을 훅 프로세스 안에서 전송하며, 4초 API 요청 두 번과 프로세스
여유 시간을 포함하도록 훅 제한 시간을 10초로 설정합니다. 전송 실패는 Codex
turn을 실패시키지 않습니다. `luthn connect codex`를 다시 실행하면 이전 5초 관리
hook을 10초 정의로 교정하고 관계없는 hook과 Codex 지침은 보존합니다. 같은 명령은
전역 Codex `AGENTS.md`에 표시된 Luthn 블록을 설치해 자동 회상도 기본 활성화합니다.

| 증상 | 조치 |
|---|---|
| `PowerShell 7.4 or later is required` | PowerShell을 갱신하고 `pwsh`로 설치 실행 |
| `dockerDesktopLinuxEngine` pipe 없음 | Docker Desktop을 실행하고 `docker info` 대기 |
| Docker가 `windows` 보고 | Linux container로 전환 |
| `luthn` 인식 안 됨 | 현재 `PATH` 갱신 또는 직접 CLI 경로 사용 |
| Codex CLI를 찾지 못함 | CLI 복구 또는 검증된 `LUTHN_CODEX_COMMAND` 지정 |
| `automatic ingestion: waiting for Codex hook trust` | Codex 재시작 후 `/hooks`에서 `Stop > luthn.agent-connector.v1` Trust |
| hook이 없거나 이전 5초 제한을 사용하거나 자동 회상 지침이 변경됨 | `luthn connect codex` 재실행 후 Codex 재시작, 필요하면 Trust 갱신 |

## 상태와 운영자 화면

```bash
luthn status
luthn version --json
luthn update check --json
luthn doctor --json
```

Compose service, health, readiness, 화면 URL, image 참조/식별자/digest를 보고합니다. 운영자 화면은 <http://127.0.0.1:8080/>이며 API port는 기본적으로 loopback에만 연결됩니다.

### 분류 기본값

새 설치는 로컬 `mock` 분류기를 사용하므로 별도 provider 설정 없이 분류가 필요한 쓰기와 `/readyz`가 바로 동작합니다. mock은 결정론적 로컬 분류기이므로 provider 기반 분류가 필요하면 운영자 화면에서 원하는 provider로 교체하세요. `luthn install`과 `luthn update`는 이전 기본값 조합인 `unconfigured`/`false`만 `mock`/`true`로 바꾸며, 그 밖의 설정 값은 유지합니다.

### 자동 turn memory 보존기간

새 설치는 비공개 설정에 `Luthn__Memory__AutomaticTurnRetentionDays=30`을 추가합니다.
지원 범위는 1일부터 365일까지입니다. `luthn update`는 key가 없으면 30일 기본값을
추가하고 운영자가 설정한 기존 값은 보존합니다. 이 설정은 새로 수집되는 자동 turn
요약에만 적용하며, 명시적으로 선별한 memory와 기존 database 행은 바꾸지 않습니다.

물리 정리 기본값도 `Luthn__Memory__AutomaticTurnCleanupEnabled=true`,
`Luthn__Memory__AutomaticTurnCleanupIntervalMinutes=60`,
`Luthn__Memory__AutomaticTurnCleanupBatchSize=100`으로 추가합니다. interval은
1~1440분, batch는 1~1000개를 지원합니다. install과 update는 유효한 운영자
override를 보존하고 key가 없을 때만 기본값을 추가합니다. 정리 대상은 만료된
local-only 자동 turn capsule로 제한하며, 직접 만든 memory와 외부 공개·outbox
record는 제외합니다.

`version`은 update channel과 변경 불가 설치 image 참조·실행 digest, source revision, CLI/connector template
version, MCP schema version과 image에 존재하는 stable release version을
보고합니다. `update check`는 설정된 공식 registry channel의 원격
metadata만 확인하며 image pull, service 중지, 설정 변경, backup 생성을 하지
않습니다. 고정 상태값은 `current`, `update-available`, `pinned`,
`unavailable`, `error`입니다.

`doctor`는 Docker/Compose, 설치 runtime 파일, health/readiness와 migration,
runtime drift, update 가능 여부, Luthn 소유 Codex MCP·hook·auto-recall 상태를
함께 진단합니다. 필수 항목 실패 시 nonzero로 종료합니다. 기본 출력은 사람이
읽는 형식이고 `--json`은 비밀 값 없이 같은 계약을 에이전트와 자동화에
제공합니다.

## 수명주기 명령

macOS·Linux:

```bash
luthn version --json
luthn update check --json
luthn update
luthn doctor --json
luthn reset --yes
luthn uninstall
luthn uninstall --purge-data --yes
```

`update`는 volume과 설정을 보존하고 migration 전 backup을 만듭니다. `reset`은 `--yes`가 있을 때만 data volume을 다시 만들고, 완전 삭제는 `--purge-data --yes`가 모두 있을 때만 수행합니다. Windows는 현재 `version`, `status`, `update check`, `update`, `doctor`, Codex 연결/상태/해제, `uninstall`을 지원하며 `reset`과 purge uninstall은 아직 제공하지 않습니다.

`luthn update`는 mutable channel 또는 명시적 릴리즈를 변경 불가 digest로 확정하고 target image를
받은 뒤, 레거시 설치의 operator credential과 connector scope를 먼저
조정합니다. 이전 설정과 관리 Codex 파일을 snapshot한 상태에서 target
CLI·Compose·MCP schema를 검증한 다음 PostgreSQL 확인, 쓰기 경로 중지,
압축 backup, migration, API 재시작을 수행합니다. `/healthz`가 성공하고
`/readyz`가 성공하거나 제한된 분류 provider 설정 필요 상태임을 확인한 경우에만
완료합니다. migration, health 또는 분류 설정 외 readiness 실패는 완료로
처리하지 않습니다.

```bash
luthn update stable
luthn update v0.1.0
```

SemVer, digest 또는 `sha-<full-commit-sha>`처럼 변경 불가능한 참조를 선택한
경우 `update check`는 `pinned`를 반환합니다. 대상 없는 `luthn update`는 고정
설치를 이동하지 않고 중단하므로 `stable`, `main` 또는 다른 릴리즈를 명시해야
합니다.

성공한 update에서 MCP schema, connector template 또는 관리 Codex 지시문이
바뀐 경우에만 운영자용 restart-required message와 다음 agent-safe notice가
출력됩니다: `Agent notice: restart the current Codex host before invoking Luthn tools again.`
일반 runtime-only update에는 이 알림이 나오지 않습니다.

실패 시 자동 database 복원·내림을 하지 않습니다. 자세한 절차는 [운영](operations.ko.md)을 참고하세요.

## 에이전트 연결

```bash
luthn connect codex
luthn connect codex --no-auto-recall
luthn connection status codex
luthn disconnect codex
luthn mcp --list-tools
```

연결은 Luthn 소유 Stop hook과 Docker Compose stdio MCP를 등록하고 전역 Codex
`AGENTS.md`에 표시된 자동 회상 블록을 설치합니다. Windows는 분리 프로세스 종료로
인한 누락을 막기 위해 Stop hook 프로세스 안에서 최대 10초 동안 동기 전송하며
실패 허용 동작을 유지합니다. macOS와 Linux는 기존 Python helper의 비동기 전송을
사용합니다. 기본 자동 회상은 새 작업이나 중요한 주제 변경 때 최대 3개 항목·약
600 token·200ms 실패 허용 제한·10분 cache로 `get_context_pack`을 한 번 호출하고,
같은 작업에서는 이미 반환된 맥락을 재사용합니다. 메모리가 실제 반환된 경우에만
commentary에 `Luthn 메모리 N개 참고`를 한 번 표시합니다. 반복 실행과 연결 해제는
관계없는 Codex 설정을 보존합니다.

최초 연결 또는 관리 hook 변경 뒤 Codex를 다시 시작하고 `/hooks`에서
`Stop > luthn.agent-connector.v1`을 Trust합니다. Luthn은 Codex의 hook 신뢰 결정을
우회하지 않습니다. 다음 명령으로 자동 수집, MCP, 자동 회상 상태를 함께 확인합니다.

```bash
luthn connection status codex
luthn doctor --json
```

예상 MCP tool은 `get_context_pack`, `search_safe_context`, `get_wiki_proposal`, `classify_preview`, `create_shared_memory`, `query_shared_memory`, `get_shared_memory_item`, `create_sensitive_access_request`, `get_sensitive_access_request`, `get_sensitive_access_result`입니다. 기본 connector token에는 `access.request`가 포함되어 새 설치와 update 뒤 요청·상태·결과 도구가 바로 동작합니다. 승인·거절은 MCP 밖의 신뢰된 운영자 경로에 남습니다.

## 비밀 값

`luthn.env`, `service-token`, provider API key, database backup, 자격 증명이 든 운영자 설정을 출력하거나 커밋하지 않습니다. Windows 대응 경로는 `%LOCALAPPDATA%\Luthn\config\luthn.env`와 `%LOCALAPPDATA%\Luthn\config\service-token`입니다. 원본 build와 개발 도구는 [로컬 개발](local-development.ko.md)을 참고하세요.
