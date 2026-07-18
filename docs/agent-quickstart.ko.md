# Codex 연결과 기억

[English](agent-quickstart.md)

이 문서는 설치된 직접 호스팅 Luthn과 Codex를 연결하고, 완료된 작업을 다시 쓸 수 있는 기억으로 만들며, 이후 작업에서 불러오는 방법을 설명합니다. 먼저 [설치 안내](installation.ko.md)를 따라 Luthn을 설치하세요. 원본 저장소나 로컬 .NET SDK는 필요하지 않습니다.

## 지속되는 기억 흐름

```text
1. Codex가 turn을 완료
2. 신뢰된 Stop hook이 제한된 최종 응답 capsule 제출
3. Luthn이 가림·분류 후 허용된 안전 투영 저장
4. 이후 작업이 자동 회상 또는 MCP로 관련 맥락 조회
5. Codex가 작업 중 해당 맥락 재사용
```

hook은 turn 완료 뒤 수집을 일정하게 수행하고, MCP는 조회와 쓰기를 명시적이고 정책 통제된 상태로 유지합니다. 기본 자동 회상은 매 turn마다 조회하지 않고 새 작업이나 중요한 주제 변경 때만 작고 제한된 조회를 수행합니다. 모든 에이전트 결과에는 서비스 token scope, 분류, 정책, 에이전트 안전 투영 경계가 적용됩니다. 자세한 내용은 [데이터 경계](data-boundaries.ko.md)를 참고하세요.

## 지원 운영체제

| 운영체제 | MCP 안전 조회/쓰기 | 자동 turn hook | 자동 회상 |
|---|---|---|---|
| macOS·Linux | 지원 | 사용자 Trust 후 지원 | 기본 사용 |
| Windows | 지원 | 사용자 Trust 후 지원 | 기본 사용 |

macOS·Linux는 shell/Python connector를, Windows는 같은 기본값·소유권·데이터 경계 계약을 가진 PowerShell connector를 사용합니다.

## Codex 연결

```bash
luthn connect codex
```

이 명령은 관계없는 Codex hook, 지침, MCP 등록을 보존합니다. bearer token은 Luthn 비공개 설정에 남고 Codex 설정으로 복사되지 않습니다.

### 모든 운영체제에서 Hook 신뢰 완료

1. Codex를 다시 시작합니다.
2. `/hooks`를 엽니다.
3. `Stop > luthn.agent-connector.v1`을 열어 **Trust**를 선택합니다.
4. Codex turn 하나를 완료합니다.
5. `automatic-ingestion`이 `Active`인지 확인합니다.

```bash
luthn connection status codex
```

운영자 화면은 연결 관측 상태만 보여 주며 에이전트 설정을 설치·변경·신뢰·삭제하지 않습니다. Windows에서 Codex CLI를 찾지 못하면 [설치 안내의 복구 절차](installation.ko.md)를 따르고 `WindowsApps` 실행 파일을 복사하거나 ACL을 바꾸지 마세요.

## 가벼운 자동 회상

`luthn connect codex`와 `--connect-codex` 설치 흐름은 자동 회상을 기본으로 켭니다. 이전 호환 형식도 사용할 수 있습니다.

```bash
luthn connect codex --auto-recall
luthn connect codex --no-auto-recall
```

Luthn 관리 블록만 Codex 지침에 추가하며 기존 사용자 지침은 보존합니다. 관리 블록은 다음을 요구합니다.

- 새 작업이나 중요한 주제 변경 때 `get_context_pack`을 한 번 호출
- 최대 3개 항목, 예상 600 token으로 제한
- 200ms 안에 끝내고 시간 초과·실패 시 기억 없이 계속 진행
- 같은 작업에서는 반환된 맥락 재사용
- 작업이 이어지면 10분 뒤 새로 고침
- 매 turn 자동 조회 금지

더 깊은 회상이 필요하면 `search_safe_context` 또는 `query_shared_memory`를 명시적으로 사용합니다.

## Hook이 수집하는 내용

Stop hook은 최종 assistant 응답에서 만든 제한된 capsule과 중복 방지를 위한 hash 식별자만 보냅니다. 전체 대화 기록, 사용자 prompt, 작업 폴더, 대화 기록 경로, 자격 증명 파일, Luthn 서비스 token은 읽거나 올리지 않습니다. 전송은 비동기이며 작업 완료를 막지 않습니다. 자주 쓰이는 자격 증명 모양을 로컬에서 가리고 모든 capsule을 분류한 뒤에만 공유 맥락으로 만들 수 있습니다.

## MCP가 제공하는 기능

```text
get_context_pack
search_safe_context
get_wiki_proposal
classify_preview
create_shared_memory
query_shared_memory
get_shared_memory_item
create_sensitive_access_request
get_sensitive_access_request
get_sensitive_access_result
```

원본 Vault 일괄 추출, 제한 없는 원본 조회, 비공개 기록 내보내기는 기본 에이전트 기능이 아닙니다. connector는 위 세 가지 메타데이터 전용 요청 도구에 필요한 `access.request` scope를 기본 설정하지만 MCP는 승인·거절을 노출하지 않습니다. 비공개 세부 정보 결정에는 별도의 신뢰된 운영자 경로가 필요합니다.

## 확인과 연결 해제

```bash
luthn status
luthn mcp --list-tools
luthn connection status codex
luthn disconnect codex
```

연결 해제는 Luthn 소유 hook, 선택적 자동 회상 블록, 일치하는 MCP 등록, 비밀이 아닌 소유 상태만 제거합니다. 관계없는 설정은 보존합니다.

## 사용자 정의 에이전트 Adapter

```bash
printf '%s\n' '{"sessionId":"session-1","turnId":"turn-1","sourceAgent":"custom","summary":"Published a safe project decision.","coreTags":["decision"],"idempotencyKey":"session-1-turn-1"}' \
  | luthn adapter
```

Claude Code 지원은 같은 수명주기 계약 아래 계획되어 있고, Hermes는 공식 MemoryProvider interface를 쓰는 별도 연동으로 계획되어 있습니다. 설치 과정은 공개해도 안전한 예제 맥락만 넣습니다. 실제 에이전트 쓰기는 분류와 정책이 안전 투영을 허용하기 전까지 신뢰하지 않는 후보로 취급합니다.
