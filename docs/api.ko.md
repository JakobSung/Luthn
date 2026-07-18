# API

[English](api.md)

## 이름 규칙

Core로 거른 맥락 선택에는 `coreTags`, 공개 원본 식별자에는 `sourceId`를 사용합니다. 예약된 예전 tag 별칭은 공개 API에 포함하지 않습니다. 기존 source intake 응답의 `sourceEventId`는 하위 호환 별칭이지만 새 API·SDK·connector·MCP 계약은 `sourceId`를 표준으로 사용합니다.

## 에이전트 Turn 요약 수집

```http
POST /api/agent/turn-summaries
```

대화 turn 뒤 제한된 요약을 제출하는 endpoint이며 원본 대화 기록용이 아닙니다. Luthn이 공유 기억으로 만들기 전에 분류합니다.

```json
{
  "sessionId": "session-1",
  "turnId": "turn-12",
  "sourceAgent": "codex",
  "projectPath": "/path/to/project",
  "summary": "Published release note for external contributors.",
  "coreTags": ["release", "codex"],
  "contentDigest": "sha256:...",
  "idempotencyKey": "session-1-turn-12",
  "title": "Codex release note"
}
```

응답은 `summaryId`, `sourceEventId`, `classificationResultId`, `memoryItemId`, `auditEventId`, `allowsAgentContext`, `duplicate`, `classification`, `storageDecision`을 반환합니다. 공개 안전 요약은 `SharedAcrossAgents` 기억이 될 수 있고, 민감 요약은 기본 에이전트 API에서 반환하지 않습니다. `idempotencyKey`가 재시도 중복 쓰기를 막습니다.

## 에이전트 연결 관측

```http
GET  /api/agent-connections
POST /api/agent-connections/{agentId}/observations
```

connector가 channel별 메타데이터 상태를 보고합니다. agent/channel의 최신 row를 교체하는 상태 표면이며 사건 기록이 아닙니다.

```json
{
  "agentName": "Codex",
  "integrationKind": "host-hook-mcp",
  "connectorVersion": "1",
  "channels": [{
    "channel": "automatic-ingestion",
    "configured": true,
    "verificationState": "Verified",
    "activityState": "Succeeded",
    "failureCode": null
  }]
}
```

server가 관측 시각을 붙입니다. `failureCode`는 제한된 기계용 code이며 실패한 관측에서만 허용합니다. token, prompt, 응답, 대화 기록, 원본 오류, 로컬 경로는 계약에 없습니다. 연결 상태는 `Unknown`, `Configured`, `Verified`, `Active`, `Degraded`, `Disconnected`이며 조회에는 `agent.connection.read`, 보고에는 `agent.connection.write`가 필요합니다.

## 외부 공개 통제

```http
GET  /api/external-publication/status
GET  /api/external-publication/memory-items/{id}
POST /api/external-publication/memory-items/{id}/approve
POST /api/external-publication/memory-items/{id}/revoke
```

공개·에이전트 표시 가능·미만료 안전 기억만 승인할 수 있습니다. 승인은 version 지정 안전 투영을 로컬 durable outbox에 쓸 뿐 cloud에 연결하지 않습니다. 취소는 본문 없는 tombstone을 queue에 넣습니다. 같은 승인·취소 반복은 새 revision 없이 기존 상태를 반환합니다. 초기 envelope는 독립적으로 분류된 `safeSummary`만 내보내며 `title`, `coreTags`는 비워 둡니다. 읽기는 `external-publication.read`, 변경은 `external-publication.write`가 필요합니다.

## 상태 확인

API host의 `/`에서 운영자 화면을 제공합니다.

```http
GET /healthz
GET /readyz
```

`/healthz`는 PostgreSQL을 조회하지 않는 생존 확인이며 `{ "status": "ok" }`를 반환합니다. `/readyz`는 database 의존성을 확인해 준비되면 `{ "status": "ready", "dependency": "database" }`, 아니면 `not_ready`를 반환합니다.

## 분류 미리 보기

```http
POST /api/classification/preview
```

```json
{
  "sourceId": "source-1",
  "content": "Public implementation note.",
  "sourceType": "note"
}
```

분류 메타데이터와 저장 결정을 반환하며 Vault 원문은 노출하지 않습니다.

## 운영자 분류 Provider

```http
GET  /api/operator/classification-provider
PUT  /api/operator/classification-provider
POST /api/operator/classification-provider/test
```

현재 분류 provider 설정을 조회·저장·시험하는 운영자 전용 endpoint입니다. 세 endpoint 모두 서비스 token에 `config.write` scope가 있어야 합니다. 지원 값은 `Mock`, `ExternalHttp`, `OpenAi`, `Anthropic`, `GoogleAi`, `OpenRouter`입니다.

```json
{
  "provider": "ExternalHttp",
  "model": "",
  "endpoint": "https://provider.example/classify",
  "authHeaderName": "Authorization",
  "apiKey": "operator-supplied-secret",
  "clearApiKey": false
}
```

응답은 `provider`, `model`, `endpoint`, `authHeaderName`, `payloadClass`, `redactionState`, `hasApiKey`만 반환하고 API key는 절대 돌려주지 않습니다. 시험 endpoint는 선택적 `content`, `sourceType`을 받아 현재 provider와 정책 engine을 실행하고 안전한 설정 보기, 분류, 저장 결정을 반환합니다. 저장과 시험은 메타데이터 전용 감사 사건을 기록합니다.

## 운영 관측 지표 내보내기

```http
GET /api/operator/metrics
GET /api/operator/metrics/export
```

두 로컬 운영자 endpoint는 `metrics.read` 서비스 token scope가 필요합니다.
동일한 bounded JSON 스냅샷을 반환하며, `/export`는 다운로드 응답입니다. 스냅샷에는
저카디널리티 분류 provider 요청 시간·결과, 민감 접근 요청·결정 처리량, 안전 검색 후보
압력의 집계만 포함됩니다. query 텍스트, memory/source 식별자, actor 식별값, prompt,
원문, 경로, token은 포함하지 않고 외부 공개 작업도 만들지 않습니다.

## 원본 수집

```http
POST /api/sources
```

```json
{
  "sourceSystem": "local",
  "sourceType": "note",
  "content": "Public onboarding checklist.",
  "title": "Contributor onboarding",
  "safeSummary": "Public onboarding checklist for local contributors.",
  "coreTags": ["onboarding", "public"]
}
```

`sha256:` digest를 계산해 원문 대신 저장하고, 설정된 분류기·정책을 실행하며 provider 호출·수집 결정에 대한 메타데이터 감사 사건을 기록합니다. 정책이 허용하면 `title`, `safeSummary`, `coreTags`로 위키 후보를 만들고, 민감 자료라면 민감 참조만 만들며 에이전트 표시 위키 후보는 만들지 않습니다. 민감 자료의 요청자 제공 `safeSummary`를 승인 출력으로 저장하지 않으며, 승인 결정자가 검토한 가림 출력만 붙일 수 있습니다.

### Plugin 수집 계약

email, messenger, document, local file, agent chat plugin은 다음 메타데이터를 정규화한 뒤 source intake를 호출합니다.

- `sourceIdentity`: plugin id, source system/kind, 외부 source id, 선택적 표시 이름
- `consent`: 동의 종류, actor, 시각
- `contentDigest`: payload의 `sha256:` digest
- `payloadClass`: `RawSource`, `RedactedSummary`, `MetadataOnly`, `BinaryDigestOnly`
- `retry`: 시도 수, 최대 시도 수, 다음 시각, 선택적 오류 분류
- `ordering`: partition key, 증가 sequence, enqueue 시각, 순서 처리 여부
- `deadLetter`: 이유, 시각, 오류 분류, 진단 code
- `receivedAt`, `coreTags`, 선택적 media type·payload 크기

이 envelope만으로 내용이 에이전트에 보이지 않으며 분류 정책을 대신하지 않습니다. 원문은 수집 입력일 뿐 공개 source record에 저장하지 않습니다. 응답은 `sourceId`/`sourceEventId`, 분류·위키·민감 참조·감사 식별자, `classification`, `storageDecision`을 반환합니다.

## 에이전트 Context Pack

```http
POST /api/agent/context-packs
```

```json
{ "query": "release runbook", "coreTags": ["runbook"], "maxItems": 20 }
```

`query`는 선택 사항이며 설정된 안전 검색 backend로 순위를 정합니다. 공개·에이전트 허용 위키 후보와 공유 기억만 반환합니다. MCP `get_context_pack`은 `maxTokens`, `timeoutMs`, `cacheKey`, `cacheTtlSeconds`, `failOpen`도 받습니다. 이는 MCP process 안에서 이미 안전한 응답의 크기·시간·cache를 제한할 뿐 조회 범위를 넓히지 않습니다.

## 에이전트 안전 검색

```http
POST /api/agent/search
```

```json
{
  "query": "release runbook",
  "coreTags": ["runbook"],
  "maxItems": 20
}
```

공개·에이전트 허용 위키 후보와 공유 기억의 `title`, `safeSummary`, `coreTags`만 검색합니다. 기본은 결정적 process 내부 순위이며 첫 계획 vector provider는 `pgvector`입니다. 원본 Vault/source는 검색·반환하지 않습니다. 외부 기억 adapter도 `public-agent-allowed-safe-projections`, `metadata-only`, `safe-projection-only` 경계를 따릅니다.

## 안전 기억 항목

```http
POST /api/memory/items
GET  /api/memory/items/{id}
POST /api/memory/query
```

```json
{
  "title": "Release runbook memory",
  "safeSummary": "Public-safe deployment memory.",
  "sensitivity": "Public",
  "coreTags": ["runbook", "release"],
  "visibility": "SharedAcrossAgents",
  "retentionKind": "Durable",
  "expiresAt": null,
  "sourceSessionId": null
}
```

메타데이터 전용 공유 기억을 저장하며 원문은 받지 않습니다. 읽기·조회는 공개·미만료·에이전트 허용 투영만 반환합니다. 쓰기는 저장 전에 분류하며 민감하다고 판단되면 비공개 기억 경계 뒤에 둡니다.

## 위키 안전 후보

```http
GET /api/wiki/proposals/{id}
```

안전 요약과 가려진 원본 참조에서 만든 Markdown만 반환합니다.

## 민감 접근 요청

```http
GET  /api/access-requests?status=Pending&limit=25
POST /api/access-requests
GET  /api/access-requests/{id}
GET  /api/access-requests/{id}/result
POST /api/access-requests/{id}/approve
POST /api/access-requests/{id}/deny
```

기존 민감 참조에 대한 메타데이터 전용 요청을 만들고 결정합니다. 원본 Vault/source payload는 반환하지 않습니다. 목록·결정에는 `access.decide`, 생성·조회에는 `access.request` scope가 필요합니다.

승인 시 선택적 `redactedSummary`를 받을 수 있으며 4000자 제한, 재분류, 공개 에이전트 안전 조건을 모두 만족해야 저장합니다. 거부된 승인 요약은 메타데이터 감사 사건만 만듭니다. `/result`는 명시적 출력 정책 계약이며 `pending-approval`, `denied-no-output`, `approved-redacted-output-available`, `approved-redacted-output-unavailable` 중 하나를 사용하고 원문은 반환하지 않습니다. 결과 조회는 `sensitive_access.result_read` 감사 사건을 만듭니다.

## 감사 사건

```http
GET /api/audit-events?subjectId=access-...&limit=50
```

```json
{
  "events": [{
    "id": "audit-...",
    "actor": "agent-service",
    "action": "sensitive_access.requested",
    "subjectId": "access-...",
    "payloadVersion": 1,
    "payloadClass": "metadata-only",
    "redactionState": "sensitive-boundary-only"
  }]
}
```

현재 `payloadVersion`은 `1`입니다. 미래의 알 수 없는 version도 메타데이터로 보존해야 하며 원본을 포함한다고 가정하면 안 됩니다.

## 운영 인증 경계

운영·직접 호스팅 환경은 외부 설정의 token SHA-256 digest와 scope로 보호 API에 bearer token을 요구할 수 있습니다. token 값, 실제 digest, 로컬 환경 파일을 커밋하지 않습니다. `X-Luthn-Operator`는 권한을 주지 않는 감사 actor 메타데이터입니다.

```bash
dotnet run --project src/Luthn.Tools -- token-digest --stdin
```

지원 scope: `agent.read`, `agent.write.summary`, `agent.connection.read`, `agent.connection.write`, `classification.preview`, `config.write`, `source.write`, `memory.write`, `memory.read`, `external-publication.read`, `external-publication.write`, `access.request`, `access.decide`, `audit.read`, `metrics.read`, `*`.

## Vault 경계

원본 Vault 조회는 기본적으로 제공하지 않습니다. 미래의 제한 접근도 제한된 가림 출력을 반환하기 전에 승인과 감사 기록을 거쳐야 합니다.
