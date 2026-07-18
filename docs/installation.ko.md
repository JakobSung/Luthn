# Luthn 설치

[English](installation.md)

사용자를 위한 원본 코드 없는 직접 호스팅 설치 절차입니다. macOS, Linux, Windows 모두 같은 Linux Compose runtime을 사용하며 Git, Luthn 원본 저장소, .NET SDK는 필요하지 않습니다.

## 에이전트에 전달할 요청

```text
Install and configure Luthn locally by following the instructions here:
https://raw.githubusercontent.com/JakobSung/Luthn/refs/heads/main/docs/installation.md
```

에이전트는 운영체제를 확인해 해당 절차만 사용하고, 기존 Docker volume·Luthn/Codex 설정·hook·관계없는 MCP 등록을 보존해야 합니다. token이나 자격 증명 파일을 출력하면 안 됩니다. 완료 조건은 `luthn status`의 health/readiness가 모두 `ready`, 운영자 화면 URL 확인, `luthn mcp --list-tools`의 `get_context_pack` 확인, Codex MCP 등록 확인입니다. 사용자만 할 수 있는 재시작·hook Trust가 남으면 정확히 안내해야 합니다.

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
curl -fsSL https://raw.githubusercontent.com/JakobSung/Luthn/main/scripts/install.sh | bash -s -- --connect-codex
```

설치 과정은 `~/.local/bin/luthn` CLI와 원본 없는 Compose 묶음을 설치하고, `ghcr.io/jakobsung/luthn:main`을 내려받아 로컬 서비스 token을 만들며, PostgreSQL 시작·migration·공개 안전 예제 자료 입력·health/readiness 확인을 수행합니다. `--connect-codex`는 Codex hook, MCP, 기본 자동 회상도 설정합니다.

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
| 운영자 설정 volume | `luthn-operator` |

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
    pwsh -NoProfile -File $installer -ConnectCodex
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

| 증상 | 조치 |
|---|---|
| `PowerShell 7.4 or later is required` | PowerShell을 갱신하고 `pwsh`로 설치 실행 |
| `dockerDesktopLinuxEngine` pipe 없음 | Docker Desktop을 실행하고 `docker info` 대기 |
| Docker가 `windows` 보고 | Linux container로 전환 |
| `luthn` 인식 안 됨 | 현재 `PATH` 갱신 또는 직접 CLI 경로 사용 |
| Codex CLI를 찾지 못함 | CLI 복구 또는 검증된 `LUTHN_CODEX_COMMAND` 지정 |

## 상태와 운영자 화면

```bash
luthn status
```

Compose service, health, readiness, 화면 URL, image 참조/식별자/digest를 보고합니다. 운영자 화면은 <http://127.0.0.1:8080/>이며 API port는 기본적으로 loopback에만 연결됩니다.

## 수명주기 명령

macOS·Linux:

```bash
luthn update
luthn reset --yes
luthn uninstall
luthn uninstall --purge-data --yes
```

`update`는 volume과 설정을 보존하고 migration 전 backup을 만듭니다. `reset`은 `--yes`가 있을 때만 data volume을 다시 만들고, 완전 삭제는 `--purge-data --yes`가 모두 있을 때만 수행합니다. Windows는 현재 `status`, `update`, Codex 연결/상태/해제, `uninstall`을 지원하며 `reset`과 purge uninstall은 아직 제공하지 않습니다.

`luthn update`는 대상 image·CLI·Compose를 받고, PostgreSQL을 확인하고, 쓰기 경로를 멈추고, 압축 backup과 이전 image id를 기록한 뒤, 대상 image로 migration과 API를 실행해 `/healthz`, `/readyz`가 모두 성공한 경우에만 완료합니다.

```bash
luthn update ghcr.io/jakobsung/luthn:sha-<full-commit-sha>
```

실패 시 자동 database 복원·내림을 하지 않습니다. 자세한 절차는 [운영](operations.ko.md)을 참고하세요.

## 에이전트 연결

```bash
luthn connect codex
luthn connect codex --no-auto-recall
luthn connection status codex
luthn disconnect codex
luthn mcp --list-tools
```

연결은 Luthn 소유 Stop hook과 Docker Compose stdio MCP를 등록합니다. 기본 자동 회상은 새 작업 때 최대 3개 항목·약 600 token·200ms 제한·10분 cache로 `get_context_pack`을 한 번 호출합니다. 반복 실행과 연결 해제는 관계없는 Codex 설정을 보존합니다.

예상 MCP tool은 `get_context_pack`, `search_safe_context`, `get_wiki_proposal`, `classify_preview`, `create_shared_memory`, `query_shared_memory`, `get_shared_memory_item`, `create_sensitive_access_request`, `get_sensitive_access_request`, `get_sensitive_access_result`입니다. 기본 connector token에는 `access.request`가 포함되어 새 설치와 update 뒤 요청·상태·결과 도구가 바로 동작합니다. 승인·거절은 MCP 밖의 신뢰된 운영자 경로에 남습니다.

## 비밀 값

`luthn.env`, `service-token`, provider API key, database backup, 자격 증명이 든 운영자 설정을 출력하거나 커밋하지 않습니다. Windows 대응 경로는 `%LOCALAPPDATA%\Luthn\config\luthn.env`와 `%LOCALAPPDATA%\Luthn\config\service-token`입니다. 원본 build와 개발 도구는 [로컬 개발](local-development.ko.md)을 참고하세요.
