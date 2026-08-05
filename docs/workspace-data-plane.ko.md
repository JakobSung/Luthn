# Workspace-aware data plane 구현 계획

상태: 구현 완료
결정일: 2026-08-04

## 결정

Luthn 오픈소스는 Tenant 관리 플랫폼을 구현하지 않는다. 대신 모든 제품 데이터를
`WorkspaceId`로 격리하는 data plane을 제공한다. Tenant, 조직 멤버십, 초대, 과금,
quota, SSO와 프로비저닝은 별도의 Cloud control plane 책임으로 둔다.

로컬과 셀프호스트는 별도 control plane 없이도 동작한다. 인증된 서비스 토큰은 하나의
Workspace에 바인딩되고, 토큰이 없는 SingleOwner 개발 환경은 서버가 기본 Workspace를
결정한다.

## 보안 불변조건

1. 제품 데이터의 Workspace는 요청 본문이나 임의 헤더로 결정하지 않는다.
2. 서버가 검증한 인증 설정만으로 `LuthnRequestPrincipal.WorkspaceId`를 생성한다.
3. 모든 제품 조회, 검색, 쓰기, 캐시, 멱등성, outbox는 Workspace를 파티션 키로 사용한다.
4. 운영자 여부는 Workspace 데이터 경계를 자동으로 우회하지 않는다.
5. `OwnerUserId`는 호환성과 작성자 추적에만 남기고 데이터 격리 키로 사용하지 않는다.
6. 기존 MultiUser 데이터는 사용자별 personal Workspace로 이전해 기존 격리를 보존한다.

## 실행 컨텍스트

요청 경계에서 다음 컨텍스트를 서버가 만든다.

```text
LuthnRequestPrincipal
- WorkspaceId
- UserId
- ActorKind: User | Agent | Service | System
- ActorId
- IsOperator
```

`WorkspaceId`는 서비스 토큰의 서버 설정값을 우선 사용한다. 설정되지 않은 기존 토큰은
다음 호환 규칙을 사용한다.

- `local-owner`는 `default`
- 그 밖의 사용자는 `personal:{userId}`

Cloud는 서명된 자격증명에 Workspace를 바인딩하고, Tenant와 Workspace의 관계는
control plane에서 관리한다.

Cloud 연결 시 OSS data plane이 신뢰하는 입력은 검증된 credential에서 해석한
`WorkspaceId`, `UserId`, `ActorKind`, `ActorId`와 scope뿐이다. Tenant ID, 요금제,
membership, entitlement는 data plane의 API body나 저장 모델에 전달하지 않는다. Cloud
gateway가 발급한 credential을 service-token 검증 구현으로 교체할 때도 이 실행 컨텍스트
계약을 유지한다.

## 데이터 모델

다음 root 레코드가 `WorkspaceId`를 직접 가진다.

- SourceEvent
- WikiProposal
- SharedMemoryItem
- SensitiveRecordReference
- SensitiveAccessRequest
- CollectionProvenance
- AgentConnectionChannel
- SafeProjectionSyncOutbox
- SafeProjectionSyncCheckpoint

ClassificationResult, SensitiveAccessDecision, SensitiveMemoryPayload는 전역적으로 유일한
부모 ID를 통해 Workspace가 결정되므로 이번 단계에서는 중복 컬럼을 추가하지 않는다.
감사와 telemetry의 스키마 개선은 Workspace 기반이 안정화된 뒤 별도 작업으로 진행한다.

## 구현 순서

1. Workspace ID 정규화와 서버 신뢰 실행 컨텍스트를 추가한다.
2. 핵심 persistence 모델과 EF Core 매핑에 WorkspaceId를 추가한다.
3. 기존 데이터 backfill migration을 추가한다.
4. API와 retrieval query를 Workspace 경계로 전환한다.
5. 멱등성, agent connection, outbox, checkpoint, retention cleanup을 전환한다.
6. MCP 캐시를 endpoint, credential, Workspace 기준으로 파티션한다.
7. 교차 Workspace 격리와 기존 설치 호환 테스트를 추가한다.

## 마이그레이션 전략

마이그레이션은 기존 레코드의 서버 신뢰 `OwnerUserId`를 사용해 Workspace를 backfill한다.

```text
OwnerUserId == local-owner  -> default
그 외 OwnerUserId          -> personal:{OwnerUserId}
```

CollectionProvenance는 `AuthenticatedUserId`에 같은 규칙을 적용한다. 신규 쓰기는
WorkspaceId와 기존 작성자 필드를 함께 기록한다. 모든 읽기가 Workspace로 전환되고 격리
테스트가 통과한 뒤에만 OwnerUserId 제거를 별도 migration으로 검토한다.

## 완료 기준

- 같은 Workspace의 사용자와 에이전트는 공유 가능한 메모리를 함께 검색할 수 있다.
- 다른 Workspace에서는 ID를 알아도 데이터와 검색 결과를 조회할 수 없다.
- 동일한 멱등성 키와 AgentId가 다른 Workspace에서 충돌하지 않는다.
- SingleOwner와 기존 MultiUser 데이터의 접근 범위가 업그레이드 후 유지된다.
- 로컬 API와 MCP 기본 사용법은 Workspace 설정 없이 계속 동작한다.
- Cloud control plane은 Luthn OSS를 수정하지 않고 Workspace-bound credential로 연결할 수 있다.

## 명시적 비범위

- Tenant 및 조직 테이블
- 회원 초대와 조직 RBAC
- 과금, quota, entitlement
- SSO 및 Cloud 관리 콘솔
- 감사 데이터 v2와 외부 telemetry 계약 개선

## 계획된 중앙 Hub 후속 작업

팀 Cloud 구조는 구현 완료된 서버 신뢰 Workspace 경계를 중앙 OSS Hub 하나에서 그대로
사용합니다. Cloud는 계속 Organization과 membership의 source of truth입니다. Hub는
인증된 `HubInstallation`, `Membership`, `AgentConnection`, `AgentSession`, turn 귀속을
추가로 기록해야 하지만, 어떤 값도 caller가 선택하는 request field가 되어서는 안 됩니다.

durable Hub ingress, classification worker, scope별 rate limit, backpressure,
dead-letter 복구, Cloud relay와 용량 검증은 별도 예정 작업입니다. 자세한 내용은
[중앙 팀 Hub data plane 계획](cloud-hub-data-plane.ko.md)에 있습니다.
