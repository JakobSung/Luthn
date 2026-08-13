# API

[English](api.md)

## 이름 규칙

Core로 거른 맥락 선택에는 `coreTags`, 공개 원본 식별자에는 `sourceId`를 사용합니다. 예약된 예전 tag 별칭은 공개 API에 포함하지 않습니다. 기존 source intake 응답의 `sourceEventId`는 하위 호환 별칭이지만 새 API·SDK·connector·MCP 계약은 `sourceId`를 표준으로 사용합니다.

## 서버가 정하는 Workspace 경계

보호 데이터의 보안·공유 경계는 `WorkspaceId`입니다. 서버는 일치한 service-token
설정으로 workspace, 사용자, actor 종류와 actor ID를 정합니다. request body, SDK,
connector, MCP tool은 workspace나 owner를 덮어쓰는 필드를 노출하지 않습니다.
`OwnerUserId`와 `AuthenticatedUserId`는 작성자 귀속·하위 호환 정보이며 조회 경계가
아닙니다.

`SingleOwner`의 기본 workspace는 `default`입니다. 명시적 workspace가 없는 기존
`MultiUser` token은 호환 규칙에 따라 `personal:{userId}`를 사용합니다. 팀 공유는
서로 다른 사용자·agent token에 같은 `WorkspaceId`를 서버 설정으로 연결합니다.
memory, source, wiki, 민감 접근, 외부 공개, 안전 검색, context pack, turn-summary
idempotency는 모두 workspace 안에서만 동작하고 `SharedAcrossAgents`도 같은 workspace의
agent 사이 공유를 뜻합니다. operator 역시 product data에 대한 cross-workspace bypass가
없으며 관리할 workspace에 명시적으로 연결되어야 합니다. `/readyz`는 잘못되거나 빠진
identity binding을 service-token 상태와 분리해 보고합니다.

audit 사건은 이제 `ScopeKind`(`Workspace` 또는 `Installation`), 해당할 때 server가 정한
`WorkspaceId`, actor 귀속, 제한된 subject/outcome 메타데이터와 선택적 opaque
`CorrelationId`를 가집니다. 기본 audit 목록은 호출자의 workspace에 속한 workspace
사건만 반환하고, operator는 `scope=installation`으로 installation 사건을 읽을 수
있습니다. 기존 row는 migration에서 `default` workspace로 채우며 metadata-only로
유지합니다. provenance 조회는 호출자의 workspace로 제한됩니다.

## 에이전트 Turn 요약 수집

```http
POST /api/agent/turn-summaries
```

대화 turn 뒤 제한된 요약을 제출하는 endpoint이며 원본 대화 기록용이 아닙니다. Luthn은 공유 기억으로 만들기 전에 제출된 요약, 확정된 title, Core tag를 하나의 agent 표시 가능 투영으로 함께 분류합니다.

새로 수집한 turn 요약은 `Ephemeral`로 보존합니다. `expiresAt`은 server 수신
시각에 `Luthn:Memory:AutomaticTurnRetentionDays`를 더한 값이며, 기본값은 30일이고
1일부터 365일까지 설정할 수 있습니다. Docker 설정에서는
`Luthn__Memory__AutomaticTurnRetentionDays`를 사용합니다. 만료 시각부터 recall과
검색 후보에서 제외합니다.

기본 API runtime은 정리 조건을 충족한 만료 자동 turn capsule도 물리적으로 정리합니다.
기본값은 활성화, 60분 간격, batch당 최대 100개입니다. 불변 provenance로
`turn-summary` source event와 연결된 `Ephemeral` memory 중 `LocalOnly`이고 safe-sync
outbox 이력과 민감 record reference가 없는 항목만 대상입니다. reference가 연결된
turn 요약은 전용 민감 접근 lifecycle cleanup 전까지 만료 후 fail-closed 상태로
남습니다. 정리 가능한 비참조 capsule은 memory 행, 암호화 payload, provenance,
classification, source event를 한 transaction에서 삭제합니다. 기존 audit event는
남기고 metadata-only `turn_summary.retention.pruned` event 하나를 추가합니다.
`Luthn__Memory__AutomaticTurnCleanupEnabled`,
`Luthn__Memory__AutomaticTurnCleanupIntervalMinutes`(1~1440),
`Luthn__Memory__AutomaticTurnCleanupBatchSize`(1~1000)로 제어할 수 있습니다.
기존 Durable memory는 migration하거나 다시 쓰지 않습니다.

자동 수집 보존 정책은 명시적인 `POST /api/memory/items` 쓰기와 분리되어 있습니다.
사람이 선별한 memory는 요청한 `Durable`, `Session`, `Ephemeral` 보존 계약을 그대로
사용하며 자동 turn 설정의 영향을 받지 않습니다. 직접 만든 Ephemeral memory,
turn이 아닌 source, 외부 공개가 승인 또는 취소된 memory, outbox 이력이 있는
record도 자동 물리 정리에서 제외합니다.

```json
{
  "sessionId": "session-1",
  "turnId": "turn-12",
  "sourceAgent": "codex",
  "summary": "Published release note for external contributors.",
  "coreTags": ["release", "codex"],
  "contentDigest": "sha256:...",
  "idempotencyKey": "session-1-turn-12",
  "title": "Codex release note"
}
```

원본 프로젝트 경로와 자유 형식 `sourceMetadata`는 거부합니다. 대신 제한된 `projectKey`, `taskKey`, `topicTags`와 구조화된 `provenance` 필드를 사용합니다.

응답은 `summaryId`, `sourceEventId`, `classificationResultId`, `memoryItemId`, `sensitiveReferenceId`, `auditEventId`, `allowsAgentContext`, `duplicate`, `classification`, `storageDecision`을 반환합니다. 암호화 payload가 생긴 turn 요약은 부모 memory와 연결되고 같은 `expiresAt`을 공유하는 민감 reference 하나를 멱등 생성하며, 재시도는 같은 `sensitiveReferenceId`를 반환합니다. 공개 안전 요약은 `SharedAcrossAgents` 기억이 될 수 있고, 민감 요약은 기본 에이전트 API에서 반환하지 않습니다. `idempotencyKey`가 재시도 중복 쓰기를 막습니다.

결정적 필드 마스킹으로 탐지된 고신뢰 민감 값을 모두 제거하면서 의미 있는 업무
사건을 보존할 수 있으면, Luthn은 안전 투영을 다시 분류해 `SharedAcrossAgents`로
저장할 수 있습니다. 응답의 classification과 storage decision은 선택된 안전 투영을
설명하고, source event에는 원본이 민감정보를 포함했다는 상태가 유지됩니다. 원본
title·summary·metadata·session identifier는 owner 범위의 암호화 payload에만 남습니다.
마스킹이 불완전하거나 민감정보가 남거나 의미 있는 사건이 사라지면 전체 turn 요약을
기존 private inert 경계에 유지합니다.

## 에이전트 연결 관측

```http
GET  /api/agent-connections
POST /api/agent-connections/{agentId}/observations
```

connector가 channel별 메타데이터 상태를 보고합니다. workspace/agent/channel 조합의 최신
row를 교체하는 상태 표면이며 사건 기록이 아닙니다. workspace는 일치한 service token에서
server가 정하고 관측 body에서는 받지 않습니다. 호출자는 자기 workspace의 row만 읽고
갱신하며 operator도 이 경계를 우회하지 않습니다. 응답의 `workspaceId`가 같은 agent ID를
workspace별로 구분하고 `ownerUserId`는 관측 작성자를 나타냅니다.

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

현재 분류 provider 설정을 조회·저장·시험하는 운영자 전용 endpoint입니다. 세 endpoint 모두 서비스 token에 `config.write` scope가 있어야 합니다. 지원 값은 `LocalDeterministic`, `LocalHttp`이며 `Unconfigured`는 fail-closed system 상태입니다. `LocalHttp`는 `localhost`, loopback IP, `host.docker.internal`만 허용하고 model·credential·인증 설정을 받지 않습니다.

```json
{
  "provider": "LocalHttp",
  "endpoint": "http://host.docker.internal:11434/classify"
}
```

응답은 `provider`, 비워진 호환 필드 `model`·`authHeaderName`, `endpoint`, `payloadClass`,
`redactionState`, `providerBoundary`,
`localSensitiveDataGuardActive`, `localSensitiveDataGuardVersion`을 반환하고
credential이나 detector 일치값은 절대 돌려주지 않습니다. `LocalHttp`는
`same-device-local-http` 경계로 표시되고 redirect를 거부합니다. 시험 endpoint는 선택적
`content`, `sourceType`을 받아 현재 provider와 정책 engine을 실행하고 안전한 설정
보기, 분류, 저장 결정을 반환합니다. 저장과 시험은 메타데이터 전용 감사 사건을
기록합니다.

기존 상용 provider, `Mock`, `ExternalHttp`, 원격 `LocalHttp` 저장/runtime 설정은
secret을 사용하지 않고 endpoint·model·인증·credential을 비운 `Unconfigured`로
표시합니다.

## 운영 관측 지표 내보내기

```http
GET /api/operator/metrics
GET /api/operator/metrics/export
```

두 로컬 운영자 endpoint는 `metrics.read` 서비스 token scope가 필요합니다.
동일한 bounded JSON 스냅샷을 반환하며, `/export`는 다운로드 응답입니다. 스냅샷에는
저카디널리티 분류 provider 요청 시간·결과, 민감 접근 요청·결정 처리량, 안전 검색 후보
압력, 요청 시간·결과·cache 상태·결과 수·0건 결과 수, helpful/unhelpful feedback의
집계와 누적 지연 bucket(`10`, `50`, `100`, `500`, `1000`, `5000`, `60000` ms)만
포함됩니다. 지표는 메모리에만 있고 API process 재시작 시 초기화됩니다. query 텍스트, memory/source 식별자, actor 식별값, prompt,
원문, 경로, token은 포함하지 않고 외부 공개 작업도 만들지 않습니다.

MCP cache·timeout 결과는 `POST /api/agent/search-telemetry/observations`, 명시적
feedback은 `POST /api/agent/search-telemetry/feedback`으로 보고하며 둘 다
`metrics.write`가 필요합니다. observation은 allowlist surface/outcome/cache 상태,
시간·결과 수와 선택적 opaque `retrievalId`를 받으며, 생략하면 나중 feedback에 쓸
ID를 응답합니다. feedback은 opaque ID와 `helpful|unhelpful`만 받습니다. 로컬
aggregate snapshot과 database에는 사건 row, query, tag, 투영 내용을 저장하지
않습니다. Host는 `Luthn.Host.Api`라는 vendor-neutral `ActivitySource`에서
`retrieval.completed`, `retrieval.observed`, `retrieval.feedback` 사건도 내보냅니다.
기본 exporter는 없으며 OpenTelemetry host 연동 시 bounded 값과 opaque retrieval
correlation만 수집할 수 있습니다.

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

`sha256:` digest를 계산해 원문 대신 저장합니다. `content`, `title`, `safeSummary`, 모든 `coreTags` 항목을 하나의 전체 투영으로 분류한 뒤 정책을 실행하고, 정규화된 결과와 provider 호출·수집 결정에 대한 메타데이터 감사 사건을 기록합니다. 정책이 허용하면 `title`, `safeSummary`, `coreTags`로 위키 후보를 만들고, 어느 필드든 민감하면 민감 참조만 만들며 에이전트 표시 위키 후보는 만들지 않습니다. 민감 자료의 요청자 제공 `safeSummary`를 승인 출력으로 저장하지 않으며, 승인 결정자가 검토한 가림 출력만 붙일 수 있습니다.

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
{ "query": "release runbook", "coreTags": ["runbook"], "maxItems": 20, "projectKey": "luthn", "taskKey": "release", "topicTags": ["delivery"] }
```

`query`는 선택 사항이며 설정된 안전 검색 backend로 순위를 정합니다. 공개·에이전트 허용 위키 후보와 공유 기억만 반환합니다. `projectKey`가 있으면 같은 프로젝트와 전역 기록만 후보가 되고 다른 프로젝트 기록은 순위 계산 전에 제외됩니다. 정확한 작업·주제 일치와 최근 안전 투영에는 제한된 점수를 주며, 항목은 선택 메타데이터와 `projectionTimestamp`를 반환합니다. 응답의 opaque `retrievalId`는 명시적 집계 feedback에만 사용합니다. MCP `get_context_pack`은 `maxTokens`, `timeoutMs`, `cacheKey`, `cacheTtlSeconds`, `failOpen`도 받습니다. 이는 MCP process 안에서 이미 안전한 응답의 크기·시간·cache를 제한할 뿐 조회 범위를 넓히지 않으며 프로젝트·작업·주제는 cache identity에 포함됩니다.

## 에이전트 안전 검색

```http
POST /api/agent/search
```

```json
{
  "query": "release runbook",
  "coreTags": ["runbook"],
  "maxItems": 20,
  "projectKey": "luthn",
  "taskKey": "release",
  "topicTags": ["delivery"]
}
```

공개·에이전트 허용 위키 후보와 공유 기억의 `title`, `safeSummary`, `coreTags`, 안전 회상 메타데이터만 검색합니다. 기본은 결정적 process 내부 순위이며 첫 계획 vector provider는 `pgvector`입니다. 원본 Vault/source는 검색·반환하지 않습니다. 외부 기억 adapter도 `public-agent-allowed-safe-projections`, `metadata-only`, `safe-projection-only` 경계를 따릅니다.

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

메타데이터 전용 공유 기억을 저장하며 원문은 받지 않습니다. 선택적 `projectKey`, `taskKey`, `topicTags`는 정규화한 뒤 전체 투영 분류에 포함하며 원본 경로나 민감 식별자를 넣으면 안 됩니다. 읽기·조회는 공개·미만료·에이전트 허용 투영만 반환합니다. 쓰기는 저장 전에 `title`, `safeSummary`, 모든 `coreTags`와 회상 메타데이터를 함께 분류하며 어느 필드든 민감하다고 판단되면 비공개 기억 경계 뒤에 둡니다.

민감하거나 그 밖의 이유로 agent에 보이지 않는 사용자 필드는 별도
`sensitive_memory_payloads` table의 인증된 보호 payload로만 저장합니다. 일반 행,
쓰기 응답, search index에는 `[protected-memory]` / `[protected-payload]`
placeholder와 빈 tag·회상 metadata만 남습니다. 공개 API는 암호문을 반환하거나 이
payload를 복호화하지 않습니다. `/readyz`는 `sensitive-memory-protection` 상태를
보고하며 key ring 또는 기존 암호문을 검증할 수 없으면 보호 API route가 `503`을
반환합니다.

`/api/memory/items`, `/api/sources`, `/api/agent/turn-summaries` 쓰기는 같은
선택적 구조화 `provenance` 객체를 받습니다.

```json
{
  "provenance": {
    "userId": "owner.one",
    "agentId": "codex",
    "applicationId": "codex.desktop",
    "pluginId": "luthn.hook",
    "connectorId": "luthn.codex.connector",
    "connectorVersion": "2",
    "collectedAt": "2026-07-19T00:00:00Z"
  }
}
```

이 값은 길이와 문자가 제한된 호출자 주장입니다. 식별자는 소문자로 정규화하고,
원본 경로와 자유형 source metadata는 받지 않으며, server 수신 시각보다 5분을 넘게
앞선 수집 시각은 거부합니다. 인증된 service-token actor, 인증 owner user,
`receivedAt`은 항상 server가 정하며 호출자가 덮어쓸 수 없습니다.

## 수집 출처 정보

```http
GET /api/provenance/source-events/{sourceEventId}
GET /api/provenance/memory-items/{memoryItemId}
```

두 route는 `audit.read`가 필요하며 MCP agent 도구와 agent 전용 connector interface에는
노출하지 않습니다. source event와 memory item마다 versioned 불변 provenance 행이 정확히
하나 있고, turn summary는 source event와 memory item 양쪽에 연결된 한 행을 사용합니다.
`actorTrust`는 `service-token`, `local-runtime`, `legacy-unknown`, `claimsTrust`는
`caller-supplied`, `no-claims`, `legacy-unknown` 중 하나입니다. 기존 행은 migration에서
주장을 알 수 없는 결정적 version-1 기록을 받습니다.
`authenticatedUserId`는 server가 정한 신뢰 가능한 owner identity이고,
`claimedUserId`는 호출자가 보고한 수집 맥락일 뿐입니다. 비운영자 `audit.read` token은
자기 owner의 provenance만 읽을 수 있습니다.

provenance는 수집 기원의 상태를, audit event는 시간에 따른 행위와 결정을 기록합니다.
provenance를 audit payload, agent recall, search index, metric, 암호화 사용자 payload,
safe sync, 외부 publication으로 복사하지 않습니다.

## 위키 안전 후보

```http
GET /api/wiki/proposals/{id}
```

안전 요약과 가려진 원본 참조에서 만든 Markdown만 반환합니다.

## 민감 접근 요청

```http
GET  /api/access-requests?status=Pending&limit=25
GET  /api/access-requests/policy
PUT  /api/access-requests/policy
POST /api/access-requests
POST /api/access-requests/resolve
GET  /api/access-requests/{id}
GET  /api/access-requests/{id}/operator-detail
GET  /api/access-requests/{id}/result
POST /api/access-requests/{id}/approve
POST /api/access-requests/{id}/deny
```

기존 민감 참조에 대한 metadata-only 요청을 만들고 결정하며, server 재분류를 통과한 제한된 redacted output을 선택적으로 반환합니다. 원본 Vault/source
payload는 반환하지 않습니다. 요청자는 server가 정한 자기 owner의 요청만 생성·조회할
수 있습니다. 목록·운영자 상세에는 `access.review`, 승인·반려에는 별도의 신뢰된
`access.decide`가 필요합니다. 기존 client의 하위 호환을 위해 `access.decide`는 조회도
포함합니다. 명시적 운영자는
metadata-only audit를 남기면서 다른 owner 요청을 제한적으로 관리할 수 있습니다.
생성·조회에는 `access.request` scope가 필요합니다. MCP는 생성·상태·결과만 제공하며
승인·거절 도구를 노출하지 않습니다.

`POST /api/access-requests/resolve`는 공개 안전 memory 항목에서 기존 확인 요청
lifecycle로 연결하는 에이전트 안전 경계입니다.

```json
{
  "memoryItemId": "memory-item-...",
  "reason": "사용자가 요청한 보호된 세부 정보를 확인합니다."
}
```

선택적인 사유는 길이가 제한되며 사용자의 원문 질문이나 민감한 값을 포함하면 안 됩니다.
server는 인증된 owner와 workspace 안에서만 관련 보호 정보를 해석하고, 사람이 이해할 수
있는 `message`와 함께 `requested`, `not-found`, `expired` 중 하나를 반환합니다.
`requestId`는 `requested`일 때만 포함하고 보호 정보 참조나 내용은 반환하지 않습니다.
`requested`는 새 요청 생성뿐 아니라 기존 pending/active 요청의 안전한 재사용도 의미합니다.

### 권한과 정책 계약

로컬/self-hosted 운영자 기능은 검토, 결정, 정책 설정 권한을 분리합니다.

| Scope | 허용되는 민감 접근 작업 |
| --- | --- |
| `access.request` | owner 범위 요청 생성과 현재 상태 또는 제한된 결과의 동기 조회 |
| `access.review` | 인증된 workspace 안의 요청 목록과 운영자 상세 조회 |
| `access.decide` | 만료되지 않은 Pending 요청 승인·반려. 하위 호환을 위해 review를 포함하지만 정책 설정 권한은 포함하지 않음 |
| `access.configure` | workspace 공통 승인 대기시간, 승인 결과 노출시간, 최대 성공 조회 횟수 조회·개정. 검토·결정 권한은 포함하지 않음 |

새 service token은 두 정책 route 모두 `access.configure`를 사용합니다. 기존 로컬 console
session의 `config.write`는 호환성 연결로만 허용하며 review나 decide 권한을 부여하지
않습니다.

기본 정책은 승인 대기시간 600초, 이와 분리된 승인 결과 grant 600초, 성공 결과 조회
1회입니다. 두 시간값의 허용범위는 60–3600초이고 최대 성공 조회 횟수는 1–10회입니다.
무제한 값은 없습니다. 잘못되거나 권한 없는 정책 변경은 fail-closed 처리합니다. 각
revision은 server가 관리하고 이후 수명주기에만 적용되며 기존 request나 grant를
연장하거나 복원하지 않습니다.

정책 변경 요청:

```json
{
  "requestTimeoutSeconds": 600,
  "grantDurationSeconds": 600,
  "maximumSuccessfulReads": 1
}
```

정책 응답:

```json
{
  "revision": 3,
  "requestTimeoutSeconds": 600,
  "grantDurationSeconds": 600,
  "maximumSuccessfulReads": 1,
  "createdAt": "2026-07-04T00:00:00Z"
}
```

`GET /api/access-requests/policy`와 `PUT /api/access-requests/policy`는 workspace 범위를
강제하고 `Cache-Control: no-store`로 응답하며 제한된 정책 metadata만 노출합니다. 변경이
성공하면 기존 revision을 덮어쓰지 않고 새 revision을 생성합니다.

요청 생성 시 활성 승인 대기시간과 정책 revision을 snapshot하고 계산된 만료시각을
`requestExpiresAt`으로 노출합니다. 승인 시점에는 당시의 grant 노출시간, 최대 성공 조회
횟수, 정책 revision을 별도의 `grantExpiresAt`과 조회 제한 상태에 snapshot합니다. 두
만료시각의 의미는 다릅니다.
request expiry는 결정 가능 시간을 끝내고 grant expiry는 승인 결과의 사용 가능 시간을
끝냅니다. 백그라운드 만료 materialization이 아직 실행되지 않았어도 모든 결정과 결과
조회에서 현재 server 시각과 원자적 조회 counter를 다시 검사합니다. 상태 조회와 결과가
없는 응답은 성공 조회를 소비하지 않고, 승인된 제한 결과를 실제 반환할 때만 1회를
소비합니다.

`expiresInSeconds`는 기존 create JSON 형식의 호환성을 위해 계속 받고 범위도 검사하지만,
현재 요청 수명을 caller가 정하는 권한은 아닙니다. 활성 server 정책이 snapshot될 실제
request expiry를 결정합니다. `sessionId`를 생략하면 기존과 같이 server가 `legacy-...`
식별자를 생성합니다.

### 로컬 동기 수명주기 조회

`status`는 저장된 요청 결정 상태(`Pending`, `Approved`, `Denied`, `Expired`)이고,
추가 필드 `statusCode`는 현재 server 시각의 request/grant/read 수명주기를 나타냅니다.

| `statusCode` | 의미와 결과 동작 |
| --- | --- |
| `request-created` | 새 Pending 요청을 생성함 |
| `request-pending` | 같은 owner/workspace/reference의 Pending 요청이 유효하여 기존 요청을 재사용함 |
| `request-denied` | 요청이 반려되어 결과를 반환하지 않음 |
| `request-expired` | 결정 가능 시간이 만료되어 결과를 반환하지 않음 |
| `grant-active` | 승인 grant가 만료되지 않았고 조회 횟수가 남아 있어 결과 endpoint가 검토된 제한 결과를 반환할 수 있음 |
| `grant-expired` | 승인 결과 grant가 만료되어 결과를 반환하지 않음 |
| `grant-consumed` | 성공 조회 횟수를 모두 사용하여 결과를 반환하지 않음 |
| `result-returned` | 이번 결과 호출이 승인된 제한 결과를 반환하고 성공 조회 1회를 원자적으로 소비함 |

목록, request, operator detail, result 응답에는 적용 가능한 경우 `requestExpiresAt`,
`grantExpiresAt`, `remainingReads`, `maxReads`, `usedReads`가 추가됩니다. `usedReads`는
최대 횟수와 남은 횟수의 차이로 server가 계산한 제한된 값입니다. server가 정한 같은
workspace, owner, 민감 reference로 create를 반복하면 중복 요청을 만들지 않고 기존
Pending 요청이나 active grant를 반환합니다. terminal 상태는 새 요청을 조용히 만들지
않고 그대로 반환하며, 이후의 별도 명시적 create 요청에서 새 Pending 수명주기를 시작할
수 있습니다.

이는 로컬 동기 재조회 계약입니다. Agent는 기존 create/status/result 작업을 다시 호출할
때만 승인, 반려, 만료, 소진 상태를 확인합니다. SignalR, SSE, WebSocket, webhook, email,
Slack, mobile push 또는 Agent의 선제 메시지는 보내지 않습니다. `grant-active`를 받은
Agent는 같은 사용자 turn에서 기존 result 작업을 이어서 호출할 수 있습니다. 승인 결과는
Cloud, Cloud safe-projection sync 또는 외부 공개 outbox로 전송하지 않습니다. Cloud 결과
relay와 Cloud 관리자 route는 이 계약의 일부가 아닙니다.

### Workflow, 감사, 우회 차단 경계

`SensitiveAccessWorkflow`만 request resolution, 결정, 정책 revision, grant, 만료, 조회
counter를 조회하거나 변경할 수 있는 application 경계입니다. 승인 결과 조회에는 이
Workflow가 발급한 직렬화 불가능한 일회성 내부 permit도 필요합니다. permit은 HTTP, SDK,
MCP, 로그, 감사, cache, Cloud 계약에 노출하지 않습니다. Agent용 API/MCP에는 approve,
deny, 정책/grant 변경, permit, 원본 Vault/source 조회 작업이 없습니다.

백그라운드 만료 materializer도 Workflow 소유 system operation을 호출하며 request나 grant
row를 직접 변경하지 않습니다. materialization은 멱등이고 수명주기 근거를 기록하지만,
동기 조회와 결정이 항상 현재 server 시각과 counter를 다시 검사하므로 authorization
경계로 사용하지 않습니다.

직접 payload 조회, 민감 상태 직접 변경, 잘못된 상태 전이, scope 불일치, 만료 grant,
소진된 조회 제한, 잘못되거나 재사용된 permit은 결과와 비인가 상태 변경 없이 fail-closed
처리합니다. 요청 재사용, 결정, 정책 revision, grant 생성·만료·소진, 결과 조회, 만료
materialization, 우회 차단은 제한된 metadata-only audit와 저카디널리티 metric을
남깁니다. prompt·reason 본문, reference label, redacted output 본문, credential, secret,
owner path, workspace/owner 화면 식별자, 민감 원문은 포함하지 않습니다.

`GET /api/access-requests/{id}/operator-detail`은 로컬 또는 self-hosted Hub
콘솔을 위한 별도 `access.review` 계약입니다. 요청·결정 사유와 민감 참조에 이미
저장된 label, source metadata, redacted summary만 반환합니다. 응답은
`operator-sensitive-metadata`, `local-operator-only`로 표시되며 Agent-safe 데이터가
아니므로 Cloud safe-projection sync, 로그, metric, 일반 감사 payload에 넣으면 안 됩니다.
항상 인증된 workspace를 강제하고, 비운영자 decider는 server가 정한 자기 owner로도
제한합니다. 명시적 operator도 같은 workspace 안에서만 다른 owner를 검토할 수
있습니다. 성공한 조회는 내용 없는 metadata-only
`sensitive_access.operator_detail_read` 감사 사건을 남깁니다. 원본 source/Vault,
protected payload, credential, workspace id, owner id는 응답하지 않습니다.

새 호출자는 `sessionId`를 보내야 합니다. `expiresInSeconds`는 버전 없는 JSON 형식의
호환성을 위해 유지하고 60–3600초 밖의 값은 거절하지만, 실제 request 만료는 활성 server
정책이 결정합니다. 승인 시 선택적 `redactedSummary`를 받을 수 있으며 4000자 제한,
재분류, 공개 에이전트 안전 조건을 모두 만족해야 저장합니다. turn-summary reference는
이 값을 생략하면 저장된 공개 안전 투영이 있을 때 server가 다시 검증해 사용합니다.
reference 만료는 요청 생성, 결정, permit/grant 사용, 결과 조회에서 모두 현재 server
시각으로 재검사하며 항상 출력 없이 거절합니다. 거부된 승인 요약은
metadata-only 감사 사건만 만듭니다. `/result`는 명시적 출력 정책 계약이며
`pending-approval`, `expired-no-output`, `denied-no-output`,
`approved-redacted-output-available`, `approved-redacted-output-unavailable` 중 하나를
사용하고 원문은 반환하지 않습니다. request와 grant 시간은 server 정책으로 각각
60–3600초 범위에 제한됩니다. 만료와 결과 조회 감사에는 결과 본문을 복사하지 않습니다.

만료되는 민감 turn-summary reference가 보존 정리 시점에 도달하면 암호화 payload,
live reference, 연결된 memory/source graph, request, decision, grant를 하나의 원자 작업으로
제거합니다. 이후 상태·운영자 상세·결과 조회와 `Expired` 목록은 다음과 같은 content-free
tombstone만 반환합니다.

```json
{
  "id": "access-...",
  "status": "Expired",
  "outputPolicy": "expired-no-output"
}
```

tombstone에는 reference, actor/session, 요청·결정 사유, summary, payload, ciphertext,
result 속성이 없습니다. 기존 감사 이력은 불변으로 보존하며, cleanup은 제거된 요청마다
결정적 metadata-only `sensitive_access.content_pruned` 사건 하나만 추가합니다. 운영자
콘솔은 tombstone의 content·결정 control을 숨기고 metadata-only 감사 링크만 유지합니다.
Agent나 operator가 호출할 수 있는 cleanup mutation API는 추가하지 않습니다. SDK/connector
상태·결과 조회는 `SensitiveAccessReadDto` live-or-tombstone 계약을 반환하고, MCP는
결정 tool을 추가하지 않은 채 실제 content-free tombstone 타입을 전달합니다.
목록 응답은 기존 호환성을 위해 live 항목을 `requests`에 유지하고, 제거된 항목은 별도의
강타입 `tombstones` 배열로 제공합니다.

## Cloud-neutral 동기화 계약

`Luthn.Sdk`는 installation enrollment, capability negotiation, 안전 투영 batch,
receipt, checkpoint, 제한된 오류, metadata-only 감사 page를 위한 additive v2 DTO를
제공합니다. 이는 transport-neutral 계약일 뿐입니다. OSS runtime은 계속 기본적으로
비활성 sync transport를 등록하며 이 DTO만으로 Cloud endpoint나 credential 저장소가
활성화되지 않습니다.

v2 투영 payload에는 Organization, Workspace, Installation identity를 넣지 않습니다.
수신자는 caller가 선택한 tenancy 필드가 아니라 인증된 Installation authority에서
tenant 범위를 결정합니다. 각 batch item은 opaque `operationId`를 포함하고 수신자는
이를 receipt에 그대로 반환하므로 승인과 checkpoint 갱신이 tenant identity나 content
필드에 의존하지 않습니다. 엄격한 입력 계약은 알 수 없는 필드와 raw/Vault content,
encrypted payload, credential, prompt, transcript, working directory, local path를
거절합니다. 기존 `SafeProjectionSyncEnvelopeDto` v1 JSON 형식은 하위 호환을 위해
그대로 유지합니다.

## 운영 콘솔 profile

```http
GET /api/operator/console-profile
```

read-only profile은 같은 OSS console에 미등록 `SingleOwner`를 `Local`,
`MultiUser` 또는 등록 완료 설치를 `Hub` mode로 알려 줍니다. 또한 `cloudTransport: disabled`,
`sensitiveAuthority: oss-console`, `tenancySource: authenticated-request` 경계를
고정해 반환합니다. 요청 body나 호출자가 선택한 tenant/mode identity를 받지 않으며
workspace, organization, installation, owner, credential 필드를 반환하지 않습니다.

browser는 정적 label에 allowlist된 `en`, `ko` 언어 preference만 사용합니다. 언어
선택은 authorization, identity, audit, transport 상태를 바꾸지 않습니다. 민감 접근
승인과 외부 공개 승인은 별도 API·console section으로 유지되며 DB가 아니라 Host API만
사용합니다.

## 콘솔 세션과 Cloud 수명주기 경계

```http
GET  /api/operator/session
POST /api/operator/session/local/arm
POST /api/operator/session/local
POST /api/operator/session/logout
GET  /api/operator/enrollment
POST /api/operator/enrollment/start
POST /api/operator/enrollment/verify
GET  /api/operator/cloud-login
POST /api/operator/cloud-login
GET  /api/operator/lifecycle
POST /api/operator/lifecycle/reconnect
POST /api/operator/lifecycle/reclaim
```

브라우저는 먼저 권한 없는 HttpOnly 후보 cookie를 받습니다. 설치된 CLI는 운영체제에서
보호하는 운영자 bearer로 `/local/arm`을 호출해 활성 후보가 정확히 하나일 때만 승인합니다.
후보가 없거나 둘 이상이면 차단하며 bearer나 raw bootstrap 값은 브라우저·URL·API 본문에
전달하지 않습니다.
세션 cookie는 불투명한 서버측 식별자이며 유휴·절대 만료, HttpOnly, host-only,
SameSite를 적용합니다. Cookie 인증 변경 요청에는 same-origin `X-Luthn-CSRF` proof가
필요합니다. LocalAuto는 명시적 local-only·loopback·미등록 `SingleOwner`로 제한하며,
enrollment 활성화와 Local 회수는 기존 권한을 먼저 철회합니다. Enrollment, login,
lifecycle, recovery provider의 기본값은 disabled입니다. Fake provider는 outbound가 없는
결정적 시험 adapter일 뿐 production Cloud endpoint가 아닙니다.

Cloud 로그인은 forwarded header가 비활성인 직접 local-only loopback 요청에 한해서만
일반 HTTP를 허용합니다. 원격 또는 forwarded 배포는 반드시 HTTPS를 사용해야 하며 Cloud
세션 cookie는 두 경우 모두 `Secure`를 유지합니다.

JSON 계약은 제한된 상태·capability·만료·작업·server-derived label만 노출합니다.
Service credential, recovery proof 값, caller-selected tenant identity, raw/Vault content,
prompt, transcript, local path는 받거나 반환하지 않습니다. 기존 bearer API client는
독립적으로 하위 호환을 유지합니다.

## 감사 사건

```http
GET /api/audit-events?subjectId=access-...&limit=50&scope=workspace
GET /api/audit-events?category=Access&actionPrefix=sensitive_access.&outcome=approved&from=2026-08-06T00%3A00%3A00Z&to=2026-08-06T23%3A59%3A59Z
GET /api/audit-events/export?category=Access&subjectId=access-...
```

`subjectId`, `action`, `outcome`, `subjectType`, `actorKind`, `correlationId`는
정확히 일치하는 메타데이터 필터입니다. `from`, `to`는 양 끝을 포함하는 UTC
시각입니다. `actionPrefix`는 알려진 사건 계열인 `sensitive_access.`,
`operator.classification_provider.`, `classification.provider.`, `source.intake.`,
`turn_summary.`, `memory.`, `retrieval.`, `processing.`, `transport.`만 허용합니다.
`audit.`도 retention 사건 조회에 허용됩니다. `category`는 `Access`, `Security`,
`Configuration`, `Publication`, `Ingestion`, `Retention` 중 하나입니다.
필터는 인증된 workspace 또는 installation 범위를 넓히지 않습니다. 잘못된 UTC,
과도한 길이, 허용되지 않은 접두사는 database 조회 전에 `400`으로 거절합니다.

현재 `hub.ingress.*` 사건은 제한된 `Security` category를 사용합니다. 별도 Hub
action-prefix는 아직 허용하지 않으므로 `category=Security`와 subject, correlation,
UTC filter를 함께 사용해 조회합니다.

page는 `occurredAt` 내림차순, `id` 오름차순입니다. `nextCursor`가 null이 아니면
같은 filter와 함께 다음 요청에 전달합니다. opaque cursor에는 내용이나 credential이
없으며 변조된 cursor나 다른 filter에 재사용한 cursor는 `400`으로 거절합니다.

```json
{
  "events": [{
    "id": "audit-...",
    "scopeKind": "Workspace",
    "workspaceId": "default",
    "actor": "agent-service",
    "actorUserId": "local-owner",
    "actorKind": "agent",
    "action": "sensitive_access.requested",
    "subjectId": "access-...",
    "subjectType": "sensitive_access_request",
    "outcome": "requested",
    "correlationId": null,
    "payloadVersion": 1,
    "payloadClass": "metadata-only",
    "redactionState": "sensitive-boundary-only",
    "category": "Access",
    "retentionClass": "access-365d",
    "retainedUntil": "2027-08-06T08:30:00Z"
  }],
  "nextCursor": null
}
```

`GET /api/audit-events/export`는 같은 authorization과 제한된 filter를 재사용해
최대 1000개의 사건을 JSON attachment로 반환합니다. export에는 workspace,
actor user, owner 식별자가 없고 `metadata-only-no-protected-content` 경계를 명시합니다.
원본 source, Vault·암호화 payload, credential, prompt, transcript, local path는
내보내지 않습니다.

현재 `payloadVersion`은 `1`입니다. 미래의 알 수 없는 version도 메타데이터로 보존해야 하며 원본을 포함한다고 가정하면 안 됩니다.

감사 메타데이터는 다음과 같이 목적을 정한 뒤 사용합니다.

- 민감 접근 승인·반려 전후에는 요청 `subjectId` 또는 `sensitive_access.` 계열로
  요청, 검토, 결정, 결과 조회 순서를 확인합니다.
- 분류 실패 조사에는 `outcome=failed`로 시작한 뒤 `correlationId`와 UTC 시각
  범위로 좁힙니다. Provider 실패 감사 사건은 metadata-only이며 분류 대상 내용이나
  provider 오류 본문을 포함하지 않습니다.
- 분류 동작 변경 조사에는 installation 범위와
  `operator.classification_provider.` 계열로 provider 설정 변경·시험을 확인합니다.
  installation 범위는 계속 명시적 운영자만 조회할 수 있습니다.

감사 기록은 책임 추적과 운영 조사 수단이며 내용 복구 수단이 아닙니다. prompt,
transcript, credential, 원본 source, Vault payload, 보호 memory를 감사 기록에 넣거나
감사 API로 조회하지 않습니다.

## 운영 인증 경계

운영·직접 호스팅 환경은 외부 설정의 token SHA-256 digest와 scope로 보호 API에 bearer token을 요구할 수 있습니다. token 값, 실제 digest, 로컬 환경 파일을 커밋하지 않습니다. `X-Luthn-Operator`는 권한을 주지 않는 감사 actor 메타데이터입니다.

```bash
dotnet run --project src/Luthn.Tools -- token-digest --stdin
```

지원 scope: `agent.read`, `agent.write.summary`, `agent.connection.read`, `agent.connection.write`, `classification.preview`, `config.write`, `source.write`, `memory.write`, `memory.read`, `external-publication.read`, `external-publication.write`, `access.request`, `access.review`, `access.decide`, `access.configure`, `audit.read`, `metrics.read`, `hub.ingress.write`, `hub.ingress.operate`, `*`.

## 중앙 OSS Hub ingress (선택 활성화)

공개 runtime에는 선택적으로 켜는 Hub data-plane 기반이 있습니다. 기본값은
비활성이며 Cloud HTTP transport는 구현하지 않습니다. Hub ingress token은 server
설정에서 `HubOrganizationId`, `WorkspaceId`, `UserId`,
`HubAgentConnectionId`, `HubAgentId`, `HubSessionId`를 바인딩해야 합니다.
요청 body는 이 identity를 선택하거나 덮어쓸 수 없습니다.

```http
POST /api/hub/ingress/capsules
Authorization: Bearer <hub-ingress-token>
```

```json
{
  "idempotencyKey": "turn-event-42",
  "contentDigest": "sha256:<64-lowercase-hex>",
  "capsule": "bounded agent lifecycle capsule"
}
```

server는 digest와 byte 제한을 확인하고 OSS Data Protection key ring으로 capsule을
보호한 뒤 queue row와 metadata-only audit를 원자적으로 저장하고 `202 Accepted`를
반환합니다. receipt에는 `receiptId`, state, duplicate 여부, 수신 시각,
`payloadClass=metadata-only`만 있습니다. 같은 digest 재전송은 같은 receipt를
반환하고 다른 digest의 key 재사용은 `409`입니다. scope 용량·rate 포화는
acknowledge하거나 버리지 않고 안정된 `code`, `retryAfterSeconds`, `Retry-After`가
있는 `429`를 반환합니다.

로컬 worker는 Workspace 공정성이 있는 제한 batch, lease, retry/backoff,
dead-letter, 현재 정책 재적용 replay를 사용합니다. `hub.ingress.operate` scope와
같은 Workspace의 운영자만 다음 API를 사용할 수 있습니다.

```http
POST /api/hub/ingress/dead-letter/{receiptId}/replay
GET /api/hub/status
```

Hub status는 aggregate metadata-only입니다. admission outcome, 보호 queue
byte/depth/oldest age, processing/retry/dead-letter, safe-projection outbox
age/checkpoint, 제한된 worker duration과 relay state만 반환합니다. Workspace,
구성원, Agent, session identity와 capsule 내용, credential, prompt, transcript,
local path는 반환하지 않습니다.

## Vault 경계

원본 Vault 조회는 제공하지 않습니다. 구현된 제한 접근 흐름은 위에서 설명한 제한된
server 검증 redacted output을 반환하기 전에 운영자 승인과 감사 기록을 요구하며,
승인도 보호된 Vault payload 자체를 반환하지 않습니다.
