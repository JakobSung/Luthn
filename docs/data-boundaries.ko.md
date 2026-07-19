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
설정된 API 평가는 corpus가 API의 설정된 분류기로 전달될 수 있으므로
`--provider configured-api`와 `--allow-external-provider`를 모두 명시해야 합니다.
선택적 bearer token은 `--token-env`로 지정한 환경 변수에서만 읽고 평가 결과에는
token 값을 출력하지 않습니다.

## Codex 수집과 회상 경계

macOS, Linux, Windows에서 신뢰된 Codex Stop hook은 제한된 host 사건을 받아 최종 assistant 응답만으로 capsule을 만듭니다. 전체 대화 기록, 사용자 prompt, 작업 폴더 경로, 대화 기록 경로는 읽거나 올리지 않습니다. session·turn 식별자는 전송 전에 hash 처리되고, 요약은 API 제한보다 짧게 제한되며, 알려진 자격 증명 모양이 발견되면 capsule 전체를 로컬에서 버립니다.

서비스 token은 Luthn의 보호된 설정에 남으며 Codex hook 설정, MCP 등록, connector 상태, 자동 회상 지침으로 복사되지 않습니다. hook 전송은 비동기·실패 허용 방식이므로 Luthn을 사용할 수 없어도 Codex turn 완료를 막지 않습니다.

기본 자동 회상은 새 작업이나 중요한 주제 변경 때 범위가 제한된 MCP를 통해 작은 에이전트 안전 context pack 하나만 요청합니다. 자동 회상과 명시적 MCP 조회에는 같은 분류·정책·안전 투영 규칙이 적용됩니다.

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
