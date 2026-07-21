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
  `incident log`

로컬 mock은 이 taxonomy에 대응하는 제한된 한국어·영어 표지를 인식합니다.
이는 시험·실험용 동작이며 운영 품질을 보장하지 않습니다.

모든 운영용 provider 결과에는 로컬 민감데이터 guard 버전 `1`을 결합합니다.
guard는 신뢰도가 높은 private key, access token, 값이 할당된 secret, email,
한국 전화번호·주민등록번호 형태, Luhn 검증을 통과한 결제카드 형태를 제한적으로
탐지합니다. 결과에는 표준 category만 포함하며 일치한 값이나 일부 문장을 분류
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

## Codex 수집과 회상 경계

macOS, Linux, Windows에서 신뢰된 Codex Stop hook은 제한된 host 사건을 받아 최종 assistant 응답만으로 capsule을 만듭니다. 전체 대화 기록, 사용자 prompt, 작업 폴더 경로, 대화 기록 경로는 읽거나 올리지 않습니다. session·turn 식별자는 전송 전에 hash 처리되고, 요약은 API 제한보다 짧게 제한되며, 알려진 자격 증명 모양이 발견되면 capsule 전체를 로컬에서 버립니다.

서비스 token은 Luthn의 보호된 설정에 남으며 Codex hook 설정, MCP 등록, connector 상태, 자동 회상 지침으로 복사되지 않습니다. macOS와 Linux의 hook 전송은 비동기입니다. Windows에서는 host가 분리된 업로더를 종료하지 못하도록 10초 훅 제한 안에서 동기 전송합니다. 모든 플랫폼에서 실패 허용 동작을 유지하지만, Luthn을 사용할 수 없으면 Windows turn은 제한된 요청이 실패할 때까지 지연될 수 있습니다.

기본 자동 회상은 새 작업이나 중요한 주제 변경 때 범위가 제한된 MCP를 통해 작은 에이전트 안전 context pack 하나만 요청합니다. 자동 회상과 명시적 MCP 조회에는 같은 분류·정책·안전 투영 규칙이 적용됩니다. 선택적 `projectKey`, `taskKey`, `topicTags`는 정규화·길이 제한 후 전체 안전 투영과 함께 분류하며 비민감 식별자만 허용합니다. 원본 작업 폴더와 대화 기록 경로는 회상 메타데이터도, 저장되는 capture 필드도 아닙니다. 검색 품질 지표는 메모리 내 집계이며 allowlist surface, 결과, cache 상태, 결과 수, 시간, feedback 판단만 사용합니다. query, tag, 프로젝트·작업·주제 키, cache key, 제목, 요약, 결과 식별자, 원시 오류, 자유형 feedback은 제외합니다.

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
일반 `shared_memory_items` 행에는 고정된 비활성 placeholder와 routing metadata만
남으며 search 필드에는 사용자 원문이 들어가지 않습니다. 암호문도 agent API,
recall, sync, publication, audit, log, metric으로 복사하지 않습니다.

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

## 서버 신뢰 소유권 경계

인가 owner는 server-side 속성이며 수집 metadata가 아닙니다. `SingleOwner`는 기존
anonymous와 service-token 동작을 정규화된 하나의 local owner로 연결합니다.
`MultiUser`는 모든 비운영자 product token에 제한된 user identity가 설정되지 않으면
fail-closed합니다. 호출자가 보낸 `provenance.userId`, request JSON, header, agent·app 이름,
connector metadata는 owner를 선택하거나 바꾸지 못합니다.

source event, shared memory, wiki proposal, 민감 reference·request, provenance, safe-sync
outbox와 agent-connection 상태의 owner는 해당 write transaction에서 기록합니다. 모든
agent-safe read와 ranking은 후보 선택 전에 owner를 거릅니다. agent-connection upsert와
상태 묶음은 owner+agent+channel을 사용하며 명시적 운영자만 제한된 owner 표시와 함께
전체 owner를 조회합니다. turn-summary idempotency에는 owner partition을 넣고 MCP
context-pack cache key에는 역으로 token을 알아낼 수 없는 credential partition을 넣습니다.
user identity, bearer digest, provenance claim은 안전 투영이나 cache 상태 출력에 들어가지
않습니다. 민감 접근 상태 polling도 같은 partition을 사용하는 1초짜리 제한 cache라서
상태 변경이 그보다 오래 stale하지 않습니다. 운영자 권한은 호출자 header가 아니라 명시적 token 설정이며, 교차-owner 작업은
관리 route와 metadata-only audit record로 제한합니다.

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

## Provider 경계

- 새 배포 설치는 명시적인 `unconfigured` 상태를 사용합니다. 분류는 원문을 저장하거나 투영하기 전에 제한된 provider-unavailable 응답으로 안전하게 실패합니다.
- mock 분류기는 로컬에서 자격 증명 없이 동작하며 시험·로컬 실험에만 사용합니다. `Provider=mock`과 `AllowMock=true`가 모두 필요하며, 업그레이드 전에 저장된 mock 선택도 이 명시적 opt-in 없이는 차단됩니다.
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
