# 구조 참고

[English](architecture.md)

표준 프로젝트 방향, runtime 경계, 기본 에이전트 표면, 검토 지점은 [프로젝트 맥락](project-context.ko.md)에 있습니다. 이 문서는 더 구체적인 구조만 설명합니다.

## Core 모형

초기 entity 종류는 `Product`, `Customer`, `Inquiry`, `Job`, `Approval`, `Event`, `Agent`, `Runner`, `VaultRecord`, `KnowledgeItem`, `Decision`, `ImplementationResult`, `WikiDocument`입니다.

초기 관계 종류는 `SubmittedBy`, `BelongsToProduct`, `CreatesJob`, `RequiresApproval`, `TriggersRunner`, `ProducesResult`, `SummarizedAs`, `References`, `DerivedFrom`, `UpdatesKnowledge`입니다.

```text
Customer -> Inquiry 제출 -> Job 생성 -> Approval 요구 -> Runner 실행
Runner -> ImplementationResult 생성 -> KnowledgeItem 갱신
VaultRecord -> KnowledgeItem로 요약 -> WikiDocument로 투영
```

## 공유 기억 모형

공유 기억은 에이전트를 위한 Core 관리 안전 투영 계층입니다.

- `MemoryActor`: 기억 session에 참여하는 사용자, 에이전트, peer
- `MemorySession`: owner와 선택적 에이전트/peer가 있는 제한된 상호 작용
- `MemoryMessage`: 원문이 아닌 안전한 message text와 원본 참조
- `SharedMemoryItem`: 안전 요약, 민감도, Core tag, 가시성, 보존 정책, 선택적 원본 session을 가진 정책 승인 기억
- `MemoryConclusion`: 기억 항목과 선택적 원본 message id에 연결된 파생 문장
- `MemoryVisibility`: owner 전용, 참여자 공유, 에이전트 공유, 공개 안전 가시성
- `MemoryRetentionPolicy`: 임시/session 기억은 만료해야 하고 durable 기억에는 만료를 두지 않음

제한된 기억은 기본적으로 owner에게만 보입니다. 더 넓은 공유는 명시적 정책 절차여야 합니다. 안전 기억 API는 메타데이터 기록만 로컬 또는 PostgreSQL에 저장하고, 공개·미만료·에이전트 허용 투영만 반환합니다. 선택적인 정규화 프로젝트·작업·주제 메타데이터는 wiki와 공유 기억 안전 투영에 저장합니다. 회상은 순위 계산 전에 프로젝트 범위를 제한하고 최신 기록부터 후보 수를 제한하며 `CreatedAt` 또는 `UpdatedAt`으로 결정적이고 제한된 최신성 점수를 계산합니다.

## 운영자 승인과 감사 모형

민감 접근은 에이전트 기능이 아니라 통제된 운영자 흐름입니다. 요청에는 제한된 목적,
session과 만료가 들어갑니다. Local/Hub 운영자 화면은 기존 안전 참조 metadata와
redacted summary만 가진 별도 상세 투영을 읽고, 명시적 승인·반려 사유를 기록합니다.
기존 승인 결과는 server가 재검증한 redacted summary만 포함합니다. 추가된 protected-memory
모드는 요청자 전용 opaque capability를 발급하고, 승인 뒤 제한된 시간과 횟수 동안 저장된
원본 title·summary만 복호화해 요청자에게 반환합니다. 운영자 화면에는 이 내용이 표시되지
않습니다. 자격증명·access key·private key는 항상 차단하며 제한 없는 Vault/source 조회
route는 만들지 않습니다.

외부 공개 승인은 민감 접근 승인과 별도입니다. 감사 사건은 요청·결정·결과 조회,
분류·provider 결과, 설정 변경, 수집·처리와 보존 정리를 metadata-only로 기록합니다.
cursor 조회와 metadata-only export는 조사를 위한 것이며 감사는 원문 저장소나 복구
backup이 아닙니다.

## Cloud 준비형 Local-First 기반

모든 공유 기억은 `LocalOnly`로 시작합니다. 에이전트 가시성과 외부 공개는 별도 결정입니다. 운영자가 `ApprovedForExternal`로 바꾸기 전에는 외부 공개 대기열에 넣지 않습니다. `Revoked`는 새 revision과 본문 없는 tombstone을 만들어 미래 remote adapter가 이전 투영을 삭제할 수 있게 합니다.

안전 동기화 envelope는 `originInstanceId`, 로컬 record id, revision, operation으로 식별하며 이 tuple이 중복 방지 경계입니다. 최초 upsert는 독립적으로 분류된 안전 요약, 제한된 정책 메타데이터, 시간만 내보내고 provenance 필드와 digest는 제외합니다. `title`과 `coreTags`는 별도 안전 분류 경로가 생길 때까지 비워 둡니다. 원본, 비공개/Vault 내용, 자격 증명, prompt, 대화 기록, 로컬 경로를 위한 필드는 계약에 없습니다.

공개 상태·감사 메타데이터·durable outbox row는 함께 커밋됩니다. Worker는 lease로 준비된 row를 가져와 backoff 재시도하고, 전송되지 않은 이전 revision을 `Superseded`로 만들며, acknowledgement/checkpoint와 취소 tombstone을 관리합니다. 이 저장소의 유일한 transport는 `disabled`이고 네트워크 요청을 하지 않습니다. 실제 cloud adapter와 tenant/auth, billing, 팀 data plane은 별도 상용 저장소 범위입니다.

### 중앙 OSS Hub runtime

승인된 팀 구조는 현재 safe-projection outbox를 유지하면서 Agent 수집을 Organization의
중앙 OSS Hub 하나로 모읍니다. 구성원 PC는 전체 runtime을 실행하지 않습니다. 공개
runtime에는 선택 활성화 방식의 기반이 구현되어 있습니다. Hub ingress는 암호화 capsule과
metadata-only 감사 사건을 저장한 뒤 `202`를 반환하고, 제한된 Workspace 공정 worker가
lease/retry/dead-letter와 명시적 replay를 처리하며 `/api/hub/status`는 내용 없는
aggregate 상태를 반환합니다. relay 경계는 disabled/fake이므로 OSS build가 Cloud 요청을
보내지 않습니다. 공개 Host에는 provider-neutral disabled/fake enrollment·사람 login·
회원/종료 준비·명시적 Local 회수 경계도 있습니다. 이는 2단계 활성화와 revoke-first를
검증하지만 실제 Cloud identity를 발급하지 않습니다. Production Cloud identity, billing,
실제 outbound transport는 [중앙 팀 Hub data plane](cloud-hub-data-plane.ko.md)에 정의된
미래 경계입니다.

## Plugin 수집 계약

Plugin 수집은 새 저장 경로가 아닌 메타데이터 계약입니다. source, 동의 근거, digest, payload 분류, 재시도 상태, 수신 시각, Core tag를 식별한 뒤 source intake로 넘깁니다. 지원 source는 email, messenger, document, local file, agent chat입니다.

재시도 상태는 처리 가능 시점과 소진 여부를 나타내며, 순서 처리는 메타데이터 전용 partition key와 증가 sequence로 표현합니다. dead-letter에는 이유, 시각, 오류 분류, 진단 code만 둡니다. 계약은 digest 중심이며 원문을 공개 기록에 저장하거나 분류·정책·Vault·원본 참조·감사를 우회하지 않습니다.

## 남은 설계 질문

- 법무 검토 뒤 `Luthn.McpServer`에 허용적 라이선스를 적용할 수 있는지
- 첫 운영 검색/index 개선 경로
- API/MCP 범위가 성숙한 뒤에도 `Luthn.Tools`를 유지할지
- 별도 상용 서비스인 Luthn Ontology의 hosted service 경계
