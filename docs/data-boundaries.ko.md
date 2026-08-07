# 데이터 경계 참고

[English](data-boundaries.md)

Luthn은 비공개 원본 데이터와 에이전트가 조회할 수 있는 안전한 투영을 분리합니다. 에이전트에게 보인다는 사실이 원본 접근이나 외부 공개 권한을 뜻하지는 않습니다. 표준 저장소 규칙은 [프로젝트 맥락](project-context.ko.md)에 있으며, 이 문서는 운영자 관점의 모형과 분류 예시를 설명합니다.

## 경계 모형

```text
원본 수집
  -> 분류 및 정책 적용
  -> 비공개·민감 기록은 저장 경계 안에 유지
  -> 승인된 안전 투영만 에이전트 API와 MCP에 제공
  -> 별도로 공개 승인을 받은 투영만 공개 outbox에 추가
```

- 설정한 분류기가 원본 수집 자료를 검사할 수 있으므로 provider 선택도 데이터 경계의 일부입니다.
- 저장 분류와 에이전트 가시성은 별개입니다. 저장된 기록이 자동으로 에이전트에 공개되지는 않습니다.
- 에이전트 조회는 제한 없는 원문 대신 안전한 투영을 반환합니다.
- 이미 에이전트에 보이는 투영도 외부 공개에는 별도의 명시적 승인이 필요합니다.
- 취소 또는 만료 시 비공개 원본을 감사·동기화 기록에 복사하지 않고 자격만 제거합니다.

## 분류 계약

정책으로 경로를 결정하기 전에 Luthn은 wiki, shared memory, 검색, MCP,
agent context에 나중에 나타날 수 있는 전체 투영을 분류합니다. 대상은
`content`, `title`, `safeSummary`, 모든 `coreTags` 항목입니다. 어느 한
필드에서라도 민감 신호가 나오면 결합된 전체 투영을 민감하게 취급합니다.

민감도 단계의 제한된 의미는 다음과 같습니다.

- `Public`: 알려진 민감 신호가 없고 공개 또는 팀 공유를 의도한 자료입니다.
  agent context와 wiki 검토 대상이 될 수 있습니다.
- `Internal`: 민감하지 않지만 기본적으로 공개를 의도하지 않은 운영
  지식입니다. 검토가 필요하며 자동으로 agent에 보이지 않습니다.
- `Confidential`: 개인정보, 고객, 계약, 재무, 회계, 비공개 통신
  자료입니다. 민감 경계 안에 유지합니다.
- `Restricted`: 자격 증명, 접근 키·개인 키, 고객 원문입니다. 민감 경계
  안에 유지하고 사람의 검토가 필요합니다.

분류 category taxonomy 버전 `1`은 다음의 안정된 표준 이름을 사용합니다.

- Restricted: `credential`, `private key`, `access key`, `customer original`
- Confidential: `contract`, `invoice`, `payment`, `tax`, `customer`, `email`,
  `personal identifier`, `finance`, `accounting`, `private message`,
  `incident log`. `finance`에는 금액·매출·연봉·급여·가격·비용·수익·예산·
  수수료와 이에 대응하는 영어 표현이 포함됩니다.

로컬 mock은 이 taxonomy에 대응하는 제한된 한국어·영어 표지를 인식합니다.
이는 시험·실험용 동작이며 운영 품질을 보장하지 않습니다.

모든 운영용 provider 결과에는 로컬 민감데이터 guard 버전 `1`을 결합합니다.
guard는 신뢰도가 높은 private key, access token, 값이 할당된 secret, email,
한국 전화번호·주민등록번호 형태, Luhn 검증을 통과한 결제카드와 금액 형태를
제한적으로 탐지합니다. 금전 문맥도 `finance` 민감도 하한을 올리지만 사람 이름
단독을 민감정보로 처리하지 않습니다. 결과에는 표준 category만 포함하며 일치한 값이나 일부 문장을 분류
결과, log, metric, audit, persistence metadata에 넣지 않습니다. provider 오류는
기존처럼 저장 전에 실패하고 detector 단독 허용으로 대체하지 않습니다.

`ExternalHttp`는 self-hosted 연결이 가능한 분류기 경계입니다. 로컬 또는 private
network의 AI service를 연결할 수 있고, 로컬 밖 전송은 운영자가 명시적으로
설정해야 합니다. 로컬 guard는 정상 provider 응답 뒤에 항상 적용되며 저장 경로를
더 제한적으로만 바꿀 수 있습니다.

provider 결과는 정책 평가 전에 정규화합니다. 민감 category는 taxonomy의
최소 민감도까지 올리고, `containsSensitiveMaterial`이 참이면 최소
`Confidential`로 올립니다. `Confidential` 또는 `Restricted`는 항상
`containsSensitiveMaterial`을 참으로 만듭니다. 따라서 필드가 서로 모순돼도
더 공개적인 경로가 아니라 더 제한적인 경로만 선택됩니다.

## 분류 Golden 평가

버전이 지정된 합성 corpus `data/classification/golden-v1.json`이 품질 평가의
기준 계약입니다. 한국어 사례가 과반이며 영어·혼합 언어, 민감·비민감 예시,
`title`, `safeSummary`, `coreTags`에만 신호가 있는 사례를 포함합니다. 운영·고객
자료나 실제 자격 증명은 포함하지 않습니다.

평가기는 분류기를 실행하기 전에 dataset·taxonomy 버전, 중복되지 않는 제한된
case 식별자, 표준 category 이름, 민감도와 저장 경로 기대값의 일관성을
검증합니다. JSON 결과에는 case 식별자, 기대·실제 분류, 저장 경로,
false-negative·false-positive·불일치 합계만 들어가며 corpus 원문은 복사하지
않습니다.

기본 평가는 로컬의 결정적 mock만 사용하고 network 요청을 하지 않습니다.
`--provider guarded-mock`은 API client를 만들지 않고 같은 hybrid guard 경로를
평가합니다.
설정된 API 평가는 corpus가 API의 설정된 분류기로 전달될 수 있으므로
`--provider configured-api`와 `--allow-external-provider`를 모두 명시해야 합니다.
선택적 bearer token은 `--token-env`로 지정한 환경 변수에서만 읽고 평가 결과에는
token 값을 출력하지 않습니다.

## MEMORY.md와 Luthn

`MEMORY.md`와 Luthn은 장기 에이전트 작업의 서로 다른 부분을 해결하며, 어느 한쪽이
다른 쪽을 대체하지 않습니다. `MEMORY.md`는 사람이 선별해 관리하는 운영 참고 자료로,
에이전트가 로컬 환경에서 읽을 수 있는 안정된 프로젝트 규칙, 결정, 반복 절차를 기록할
수 있습니다. Luthn은 직접 호스팅하는 runtime memory 서비스로, 제한된 에이전트 출력을
수집하고 분류·정책을 적용한 뒤 자동 회상 또는 MCP를 통해 허용된 안전 투영만 돌려줍니다.

검토된 지속적 작업 지침에는 `MEMORY.md`를 사용하고, 같은 설치에 연결된 Codex와
Claude Code가 안전하게 다시 쓸 수 있는 제한된 작업 범위 맥락에는 Luthn을 사용합니다.
`MEMORY.md`를 Luthn의 자동 내보내기로, 또는 Luthn을 지침 파일의 대체물로 취급하면 안
됩니다. 둘은 자동으로 동기화되지 않습니다.

이 경계는 의도된 것입니다.

- 연결된 Stop hook은 길이가 제한된 최종 assistant 응답 capsule만 보내며, MCP의 조회와
  명시적 쓰기는 계속 정책 통제를 받습니다.
- 저장 후보가 에이전트에 보이는 안전 투영이 될지는 분류와 정책이 결정합니다. 저장만으로
  에이전트 접근 권한이 생기지 않습니다.
- `MEMORY.md`에도 검토된 운영 지침만 두고, 원문 대화 기록, 자격 증명, 고객 원본 등
  비공개 원본 자료는 넣지 않아야 합니다.

다음 절은 Luthn connector가 적용하는 정확한 수집·회상 제한을 설명합니다.

## Codex 수집과 회상 경계

macOS, Linux, Windows에서 신뢰된 Codex Stop hook은 제한된 host 사건을 받아 최종 assistant 응답만으로 capsule을 만듭니다. 전체 대화 기록, 사용자 prompt, 작업 폴더 경로, 대화 기록 경로는 읽거나 올리지 않습니다. session·turn 식별자는 전송 전에 hash 처리되고, 요약은 API 제한보다 짧게 제한되며, 알려진 자격 증명 모양이 발견되면 capsule 전체를 로컬에서 버립니다.

서비스 token은 Luthn의 보호된 설정에 남으며 Codex hook 설정, MCP 등록, connector 상태, 자동 회상 지침으로 복사되지 않습니다. macOS와 Linux의 hook 전송은 비동기입니다. Windows에서는 host가 분리된 업로더를 종료하지 못하도록 10초 훅 제한 안에서 동기 전송합니다. 모든 플랫폼에서 실패 허용 동작을 유지하지만, Luthn을 사용할 수 없으면 Windows turn은 제한된 요청이 실패할 때까지 지연될 수 있습니다.

새로 수집한 자동 turn capsule은 server 수신 시각을 기준으로 만료되는 `Ephemeral`
memory가 됩니다. 기본값은 30일이며 운영자는 1일부터 365일까지 설정할 수 있습니다.
만료 시각부터 recall, 검색, sync, publication 후보에서 제외합니다. 기본 API 정리
loop는 provenance를 통해 `turn-summary` source와 계속 연결되고 outbox 이력이 없는
local-only 만료 capsule만 물리적으로 삭제합니다. memory, 암호화 payload,
provenance, classification, source event를 한 transaction에서 지우며, 기존 audit
이력은 남기고 metadata-only 정리 event 하나를 추가합니다. 기존 Durable 행,
명시적으로 만든 memory, 다른 source type, 외부 공개 승인·취소 record, outbox
연결 record는 자동 정리하지 않습니다. 사람이 명시적으로 선별한 memory는 별도로
요청한 `Durable`, `Session`, `Ephemeral` 수명주기를 유지합니다.

기본 자동 회상은 새 작업이나 중요한 주제 변경 때 범위가 제한된 MCP를 통해 작은 에이전트 안전 context pack 하나만 요청합니다. 자동 회상과 명시적 MCP 조회에는 같은 분류·정책·안전 투영 규칙이 적용됩니다. 선택적 `projectKey`, `taskKey`, `topicTags`는 정규화·길이 제한 후 전체 안전 투영과 함께 분류하며 비민감 식별자만 허용합니다. 원본 작업 폴더와 대화 기록 경로는 회상 메타데이터도, 저장되는 capture 필드도 아닙니다. 검색 품질 지표는 기본적으로 메모리 내 집계이며 allowlist surface, 결과, cache 상태, 결과 수, 시간, feedback 판단만 사용합니다. query, tag, 프로젝트·작업·주제 키, cache key, 제목, 요약, 결과 식별자, 원시 오류, 자유형 feedback은 aggregate snapshot과 database에서 제외합니다. Host는 vendor-neutral `ActivitySource("Luthn.Host.Api")`로 제한된 retrieval 사건을 추가 투영할 수 있지만 기본 exporter는 없으며, OpenTelemetry listener를 켜도 opaque retrieval correlation만 전달합니다.

## 공개해도 안전한 지식

- 팀 또는 공개 용도의 제품 이름
- 민감하지 않은 구현 메모
- 안전한 요약과 가려진 원본 참조
- 운영 절차서
- 에이전트 맥락에 사용해도 된다고 정책이 승인한 프로젝트 메타데이터

## 민감 저장소에 두어야 하는 자료

- 고객 원문
- 계약, 견적, 결제, 세금, 재무, 회계 원문
- 비공개 email·message 원문
- 자격 증명이 포함된 운영 자료
- 비공개 운영 자료가 가려지지 않은 장애 기록
- 정책이 비공개 전용으로 지정한 모든 기록

## 민감 Shared Memory 암호화

민감하거나 agent context에 허용되지 않은 shared memory 내용은 별도
`sensitive_memory_payloads` table에 저장합니다. 제목, 요약, tag,
project/task/topic metadata, source-session 연계값을 versioned payload로 직렬화하고,
memory record ID에 purpose-bound된 ASP.NET Core Data Protection으로 인증 암호화합니다.
결정적 로컬 guard가 인식한 민감값을 모두 제거하고 남은 제목·요약·tag·회상 metadata가
재분류를 통과하면, 일반 `shared_memory_items` 행에는 공개 가능한 마스킹 투영을 남기고
원본은 암호화 payload에만 보존할 수 있습니다. 제거가 불완전하거나 의미 있는 요약이
남지 않거나 재분류에 실패하면 일반 행에는 고정된 비활성 placeholder만 남고 search
필드에는 사용자 원문이 들어가지 않습니다. 암호문도 agent API, recall, sync,
publication, audit, log, metric으로 복사하지 않습니다.

Data Protection key ring은 PostgreSQL이 아니라 별도 `luthn-operator` volume에
있습니다. 따라서 database dump 또는 PostgreSQL volume만 유출된 경우를 방어하지만,
host 관리자/root 권한이나 PostgreSQL data와 operator key volume을 함께 탈취한 경우는
방어하지 않습니다. 기존 비공개·민감 행은 product traffic을 받기 전에 transaction으로
전환합니다. key ring 누락, 다른 purpose, 지원하지 않는 payload version, 손상된
암호문은 data를 덮어쓰지 않고 `/readyz`를 실패시키며, `/healthz`만 유지한 채 product
route를 차단합니다.

복구에는 database와 일치하는 operator key volume이 반드시 필요합니다. PostgreSQL과
`luthn-operator` key 자료를 하나의 복구 세트로 함께 backup·restore해야 합니다. key
XML을 commit·출력하거나 암호화되지 않은 저장소와 일반 log에 복사하면 안 됩니다.
key ring을 잃으면 암호화 memory는 복구할 수 없고 새 key 생성으로 기존 payload를
복호화할 수 없습니다.

## 서버 신뢰 Workspace 경계

인가 workspace는 server-side 속성이며 수집 metadata가 아닙니다. `SingleOwner`는 기존
anonymous 동작을 `default` workspace와 정규화된 local owner로 연결합니다. `MultiUser`는
모든 비운영자 product token에 제한된 user identity가 설정되지 않으면 fail-closed합니다.
호출자가 보낸 `provenance.userId`, request JSON, header, agent·app 이름, connector
metadata는 workspace나 owner를 선택하거나 바꾸지 못합니다.

source event, shared memory, wiki proposal, 민감 reference·request, provenance, safe-sync
outbox와 agent-connection 상태에는 서버가 정한 `WorkspaceId`와 작성자 귀속을 같은 write
transaction에서 기록합니다. 모든 agent-safe read와 ranking은 후보 선택 전에 workspace를
거릅니다. agent-connection upsert와 상태 묶음은 workspace+agent+channel을 사용합니다.
operator도 product data의 cross-workspace 우회 권한이 없고 관리 대상 workspace에
명시적으로 연결됩니다. turn-summary idempotency와 safe-sync idempotency에는 workspace
partition을 넣고 MCP context-pack cache key에는 endpoint·workspace·역으로 token을 알아낼
수 없는 credential partition을 넣습니다. user identity, bearer digest, provenance claim은
안전 투영이나 cache 상태 출력에 들어가지 않습니다. 민감 접근 상태 polling도 같은
partition을 사용하는 1초짜리 제한 cache라서 상태 변경이 그보다 오래 stale하지 않습니다.
운영자 권한은 호출자 header가 아니라 명시적 token 설정입니다.

## 수집 출처 경계

모든 source event와 shared-memory item은 versioned 불변 `collection_provenance` 행을
하나 가집니다. server가 정한 인증 ingest actor·owner user·수신 시각과, 호출자가 주장한 선택적
user·agent·application·plugin·connector·connector version·client 수집 시각을 분리해
저장합니다. 호출자 주장은 인증이나 tenant 권한 증거가 아닙니다. 기존 행은 명시적인
`legacy-unknown` trust와 비어 있는 origin claim을 사용합니다.

provenance 식별자는 길이와 문자가 제한되고 정규화됩니다. 원본 workspace·transcript
경로, device fingerprint, prompt, query, 자격 증명, 자유형 source metadata, source
원문은 제외합니다. provenance는 연결된 source 또는 memory의 retention 수명주기를
따르며 수정·삭제 API가 없습니다. `audit.read`로 보호된 operator route에서만 읽을 수
있고 agent recall, search index, 암호화 사용자 payload, safe sync, publication, audit
payload, log, metric에는 포함하지 않습니다. audit는 행위·결정의 사건 이력이고,
provenance는 불변 수집 기원 기록입니다.

## 민감 접근 검토 경계

민감 접근은 구조화된 목적, session correlation과 60~3600초 만료를 가진 제한된
요청으로 시작합니다. 요청 생성·상태·결과 조회는 server가 정한 owner로 제한하고,
목록과 결정에는 별도 `access.decide` scope가 필요합니다. 운영자 상세 투영은 agent
화면이 아닙니다. 기존의 안전한 label, source metadata와 redacted summary만 포함할 수
있으며 원본 Vault/source, protected payload, credential, workspace·owner identity는
포함하지 않습니다.

운영자는 상세를 확인한 뒤 명시적 승인·반려 사유를 기록해야 합니다. 승인은 server가
다시 분류해 공개·agent-safe라고 확인한 제한된 `redactedSummary`만 보존할 수 있습니다.
결과 계약은 검토된 summary 또는 pending·expired·denied·unavailable에 대한 명시적
무출력 정책을 반환합니다. 만료, 상세 조회, 결정과 결과 조회는 metadata-only 감사 사건을
남깁니다. 민감 접근 승인이 외부 공개 승인을 의미하지는 않습니다.

## 감사 사용과 보존 경계

감사 사건은 `Access`, `Security`, `Configuration`, `Publication`, `Ingestion`,
`Retention`으로 분류합니다. 요청 결정 timeline, 분류·provider 실패 조사, 설정 변경
검토, Hub ingress·worker·publication 결과, 보존 정리 확인에 사용합니다. subject,
action 계열, outcome, correlation과 UTC 시간 범위로 필터하고 opaque cursor로 다음
page를 조회하며 정책상 별도 검토 기록이 필요할 때만 metadata-only export를 사용합니다.

감사 응답·export에는 원본 source, Vault, 암호화 payload, credential, prompt, transcript,
local path가 없습니다. 감사는 책임 추적·조사 trail이지 backup이나 원문 복구 수단이
아닙니다. 보존 기간은 Access/Security/Publication 365일, Configuration/Retention
730일, Ingestion 90일이며 물리 정리는 기본 비활성입니다. 활성화하면 정리 pass마다
metadata-only retention 사건 하나를 남깁니다.

## Provider 경계

- 새 배포 설치는 로컬 `mock` 분류기를 사용하므로 별도 provider 설정 없이 분류가 동작합니다.
- mock 분류기는 로컬에서 자격 증명 없이 동작합니다. 설치 기본값이 `Provider=mock`과 `AllowMock=true`를 함께 설정하며, provider 기반 분류가 필요하면 운영자 설정으로 교체합니다.
- 운영자가 설정한 provider 비밀 값은 server에만 두고, 화면/API에는 key 보유 여부만 표시합니다.
- 외부 분류는 명시적으로 선택해야 합니다.
- `ChatGPT API`, `Claude API`, `Google AI API`, `OpenRouter API`는 Luthn이 민감도를 정하기 전에 분류 prompt로 원문을 받습니다. 운영자가 이 외부 전송을 허용할 때만 사용하고, 원본을 직접 호스팅 경계 안에 두어야 하면 통제 가능한 `External HTTP` provider를 사용합니다.
- 직접 연결하는 제3자 LLM endpoint는 API key header를 보내기 전에 예상 provider host의 HTTPS URL인지 확인합니다.
- provider 호출에는 payload 분류와 가림 상태를 포함하고, 감사 기록에는 경계 메타데이터와 저장 결정만 남기며 원문은 남기지 않습니다.
- 일반 자동 시험은 운영자가 연동 시험을 명시적으로 켜지 않는 한 mock 또는 fake provider를 사용합니다.

## 외부 공개 경계

- 에이전트 가시성은 외부 공개 권한이 아닙니다.
- 명시적으로 승인된 공개·미만료·에이전트 표시 가능 안전 투영만 외부 공개 outbox에 들어갈 수 있습니다.
- 취소 기록에는 식별자, revision, 작업, 제한된 정책 메타데이터만 두며 투영 본문을 반복하지 않습니다.
- 원본/Vault 데이터, 비공개 기억, 자격 증명, prompt, 대화 기록, 로컬 경로, 민감 접근 결과는 동기화 계약에 들어가면 안 됩니다.
- 공개 직접 호스팅 build에는 활성 cloud 전송이 없으며 기본적으로 외부 동기화를 수행하지 않습니다.
