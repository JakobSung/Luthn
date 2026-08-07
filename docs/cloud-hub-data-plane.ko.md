# 중앙 팀 Hub Data Plane

[English](cloud-hub-data-plane.md)

상태: 공개 runtime은 선택 활성화 방식의 암호화 durable ingress, 제한된 로컬 worker,
disabled/fake relay 경계와 결정적 복구 harness를 구현했습니다. Cloud enrollment,
identity control plane과 실제 outbound transport는 아직 구현하지 않았습니다.

## 배포 경계

개인 셀프호스팅은 현재 방식을 유지합니다. 한 사용자가 Luthn을 설치하고 로컬 Agent를
연결합니다. 팀 Cloud 모드에서는 조직 관리자가 항상 켜진 서버 또는 PC에 중앙 OSS Hub
하나를 운영합니다. 다른 구성원 PC에는 Docker, 전체 OSS runtime, Luthn 네이티브
클라이언트를 설치하지 않습니다.

구성원은 Cloud Organization과 Workspace에 가입하고, 사용자·기기·Agent별 연결로
Codex 또는 Claude Code를 연결합니다. OAuth를 사용하는 remote MCP는 회상과 도구를
제공합니다. 안정적인 turn 자동 수집에는 Agent의 Stop hook, plugin 또는 관리형 설정도
필요합니다. MCP 등록만으로 모든 Agent lifecycle event 수집이 보장되지는 않습니다.

공개 저장소는 Hub data plane과 로컬 metadata-only 감사 trail을 소유합니다. 비공개 Luthn
Cloud는 Organization control plane, 구성원 identity, Agent 연결 enrollment, relay,
관리형 공유 기억, Cloud 측 감사 집계, 구독과 관리형 운영을 소유합니다.

## 신뢰 identity 계층

```text
Organization                 Cloud control plane
  -> Workspace               Cloud binding, OSS 인가 경계
     -> HubInstallation      고객 운영 OSS runtime
     -> Membership           사람 접근
        -> AgentConnection   구성원 + 기기 + Agent
           -> AgentSession
              -> Turn
```

Cloud credential은 Organization, Workspace, Membership, AgentConnection을
바인딩합니다. Hub 요청 경계의 신뢰 middleware가 `WorkspaceId`, `UserId`,
`ActorKind`, `ActorId`, `AgentConnectionId`, `SessionId`와 turn 멱등 identity를
해석하고 기록합니다. request body, SDK field, connector field, MCP argument 또는 임의
header는 이 값을 덮어쓸 수 없습니다.

OSS persistence는 Organization membership, billing, entitlement 또는 Cloud role의
source of truth가 되지 않습니다. Hub data-plane 작업의 인가, partition, 작성자 귀속,
중복 방지와 감사를 위해 필요한 제한된 identity만 저장합니다.

## 목표 수집 흐름

```text
Agent lifecycle connector
  -> Cloud relay를 통한 인증된 Hub ingress
  -> schema, 크기, 신뢰 identity 검증
  -> 멱등성 확인
  -> 암호화 raw capsule + ingress queue row 원자 저장
  -> 202 Accepted

Classification worker
  -> ingress item lease
  -> 제한된 provider 분류 + deterministic guard
  -> 정책 결정
     -> 민감/비공개: Hub 암호화 payload store
     -> 안전/공유 가능: version 지정 safe projection
     -> 불확실: 검토 필요

Safe publication worker
  -> 현재 durable safe-projection outbox
  -> Cloud acknowledgement/checkpoint
  -> 지연 revision보다 body-free revoke 우선
```

Agent가 접속하는 공개 endpoint와 인증서는 Cloud가 관리합니다. Hub는 outbound 인증
relay 연결을 유지하므로 관리자가 inbound port를 열거나 공개 인증서를 관리하지 않습니다.
raw capsule은 relay 전에 Hub용으로 암호화합니다. Cloud는 제한된 TTL 동안 Hub 암호문
envelope만 임시 보관할 수 있고 plaintext는 Hub 경계 안에 남습니다.

## Queue 계약

### Ingress queue

- admission 단계에서 인증, 크기/schema 제한, 멱등성을 검증합니다.
- 암호화 raw capsule, 신뢰 귀속정보, queue row를 PostgreSQL transaction 하나로
  commit합니다.
- `202 Accepted`는 durable owner가 event를 인수했다는 뜻이며 분류나 Cloud sync 완료를
  뜻하지 않습니다.
- 중복 전송은 성공 no-op이며 기존 opaque receipt를 반환합니다.
- 대기 작업은 Hub 재시작 후 복구되고, 인수한 event를 조용히 버리지 않습니다.

### Classification queue

- worker는 lease, 전체 제한 concurrency, Workspace별 제한 concurrency를 사용합니다.
- provider timeout, retry, exponential backoff가 ingress 요청을 붙잡지 않습니다.
- 재시도 소진 작업은 metadata-only dead-letter 상태로 이동합니다. replay는 명시적이고
  감사 가능하며 멱등적이고 현재 정책을 다시 통과합니다.
- provider 실패나 불확실성을 safe projection으로 하향하지 않습니다.

### Safe-projection outbox

- 승인된 version 지정 safe projection 또는 body-free revoke만 queue에 넣습니다.
- origin, local record, revision, operation, Workspace가 순서·멱등 경계입니다.
- acknowledgement가 durable checkpoint를 전진시킵니다.
- 재연결과 복구에서는 지연 revision보다 revoke tombstone을 먼저 적용해 삭제 기억의
  부활을 막습니다.

## Backpressure와 공정성

모든 구성원이 Cloud relay IP 하나를 통해 들어올 수 있으므로 remote-IP limiter만으로는
팀 경계가 충분하지 않습니다. OSS Hub 기준선은 이미 Organization, Workspace, Membership,
AgentConnection별 요청·byte budget을 적용합니다. 미래 Cloud relay admission도 이 scope
limit와 outstanding queue limit, 전체/Workspace별 worker concurrency를 유지해야 합니다.

hard limit에 도달하면 retry 가능한 명시적 상태, 안정된 error code와 `Retry-After`를
반환합니다. 먼저 승인하고 나중에 event를 버리면 안 됩니다. 한 Workspace의 과부하가
다른 Workspace를 굶기지 않아야 합니다.

## 관측성

내용을 포함하지 않는 metric과 상태에는 최소한 다음이 필요합니다.

- ingress accepted, duplicate, rejected, backpressured count
- ingress queue depth, byte, oldest pending age
- classification active work, provider latency, retry/exhausted count,
  dead-letter depth
- safe-projection outbox depth, oldest pending age, acknowledgement rate,
  Cloud sync lag
- relay heartbeat와 connected, stale, disconnected, revoked 상태
- prompt, transcript, summary, credential, local path, 민감 값을 제외한
  Workspace별 saturation

## 구현된 OSS 기준선

공개 runtime에는 첫 Hub data-plane 기반이 구현되어 있습니다. 기본값은 비활성이며
Cloud 요청을 보내지 않습니다. 현재 기본값은 다음과 같습니다.

- capsule 최대 크기 `16384` bytes
- pending limit: Organization `5000`, Workspace `1000`, Member `500`, Agent `250`
- 분당 admission limit: Organization `6000`, Workspace `1200`, Member `600`, Agent `300`
- worker batch `20`, Workspace별 batch `5`, poll `5`초, lease `120`초, 최대 시도 `5`,
  기본 retry 지연 `2`초

Ingress는 신뢰된 server token binding에서 Hub organization, Workspace, member, Agent,
session identity를 정합니다. 로컬 Data Protection key ring으로 capsule을 보호하고
queue row와 metadata-only 감사 사건을 원자적으로 저장한 뒤 내용 없는 `202` receipt를
반환하며 caller identity override를 받지 않습니다. Worker는 만료 lease를 복구하고
provider 실패를 제한적으로 재시도하며 metadata-only dead-letter를 만들고 같은
Workspace의 운영자만 명시적 replay를 실행할 수 있습니다. `/api/hub/status`는 identity,
capsule, prompt, transcript, credential, local path 없이 admission·queue·worker·outbox·
relay·provider latency의 aggregate 상태만 반환합니다.

결정적 시험 harness는 정상 사용자 10명, 각 1개 작업의 사용자 50명, 명시적 backpressure
합계를 확인하는 50개 burst, provider 지연, lease 복구, dead-letter replay, zero-outbound,
relay 재연결·revoke-first를 검증합니다. 이는 정확성·복구 baseline이며 production 용량·
지연 SLO가 아닙니다.

## 남아 있는 Cloud 경계

다음 경계는 현재 OSS runtime 밖에 있습니다. versioned enrollment·capability 교환,
Cloud가 발급하는 connection authority, 인증 relay transport, remote MCP/OAuth lifecycle
capture와 Organization 운영입니다. 미래 adapter도 인증된 installation에서 tenant 범위를
정하고 metadata-only 감사, safe-projection-only payload, revoke-first 순서와 개인
self-host disabled-by-default를 유지해야 합니다.

## 미래 용량·복구 evidence

다음 자동화 evidence가 필요합니다.

| 시나리오 | 필수 결과 |
| --- | --- |
| 일반 사용자 10명 | 유실 없는 안정된 ingress와 분류 |
| 사용자 50명, 분당 turn 1개 | 지속 처리량과 Workspace 공정성 |
| 동시 완료 50건 | 명시적 admission/backpressure와 제한된 drain 시간 |
| provider 5초·30초 지연 | ingress 응답 유지와 queue age 관측 |
| provider 오류/rate limit | 제한 retry, dead-letter, 감사 replay |
| Cloud 장애·복구 | 순서 보장 outbox replay와 revoke-first |
| 중복·순서 역전 event | 성공 no-op과 결정적 최종 상태 |
| 대기 작업 중 Hub 재시작 | queue, lease, checkpoint 복구 |
| noisy Workspace | 다른 Workspace의 목표 유지 |

ingress p95, queue age나 디자인 파트너 수치를 아직 제품 SLO로 취급하지 않습니다. 용량
목표를 정하기 전에 hardware, PostgreSQL 설정, concurrency, provider, throughput,
p50/p95/p99, CPU/memory, 실패, retry, queue/sync lag를 기록하고 승인 데이터 유실 0건과
멱등 retry 중복 0건을 유지해야 합니다.

## 현재 비범위

- 현재 개인 셀프호스트 흐름의 변경 또는 제거
- 모든 구성원 PC에 전체 runtime, Docker 또는 네이티브 Luthn client 설치
- OSS Hub를 Organization membership 또는 billing의 source of truth로 만들기
- caller가 tenant, Workspace, member, Agent, session scope를 선택하게 하기
- raw 또는 민감 plaintext를 safe-projection 계약으로 보내기
- durable owner가 복구를 보장할 수 없는데도 조용히 성공 처리하기
- multi-Hub HA, multi-region data plane, SAML, SCIM, custom enterprise role
