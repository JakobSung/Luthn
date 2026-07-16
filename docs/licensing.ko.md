# 라이선스 모형

[English](licensing.md)

## 저장소 라이선스 범위

Luthn은 구성 요소별 라이선스 모형을 사용하며, 이 저장소에 포함된 원본 코드만 허가합니다.

## 구성 요소 라이선스

| 구성 요소 | 라이선스 | 설명 |
|---|---|---|
| Luthn.Core | AGPL-3.0-only | 핵심 엔진과 안전 지식 모형 |
| Luthn.Core.Persistence | AGPL-3.0-only | 영속화와 PostgreSQL 연결 |
| Luthn.Host.Api | AGPL-3.0-only | 직접 호스팅 API와 운영자 화면 |
| Luthn.Host.Worker | AGPL-3.0-only | 백그라운드 host 뼈대 |
| Luthn.Tools | AGPL-3.0-only | 범위가 제한된 관리·진단 명령 |
| Luthn.Sdk | Apache-2.0 | 공개 DTO와 client 계약 |
| Luthn.AgentConnector.Http | Apache-2.0 | 외부 에이전트용 HTTP connector |
| 공개 plugin 틀 | Apache-2.0 | 외부 서비스용 틀 |
| 공개 에이전트 절차/skill(가능한 경우) | Apache-2.0 | 폭넓은 재사용을 위한 연동 자료 |

## AGPL 경계

Core와 직접 호스팅 runtime은 지식 모형, 정책 논리, 영속화, 서비스 경계를 포함하며 AGPL-3.0-only입니다.

## Apache 경계

SDK, HTTP connector, 공개 plugin 틀은 얇은 통신 규약/client 경계로 유지해야 합니다. 라이선스 경계를 검토하지 않았다면 Luthn.Core에 직접 의존하면 안 됩니다.

## MCP 경계

Luthn.McpServer는 에이전트 안전 API 위의 HTTP adapter로 유지합니다. 라이선스 경계를 검토하지 않고 Core에 직접 연결하거나 Apache-2.0이라고 표시하지 않습니다.

## 범위 밖: Luthn Ontology

Luthn Ontology는 별도 상용 서비스이며 이 저장소에 포함되지 않습니다. 비공개 원본 코드, 관리형 서비스 기반 시설, 결제·tenant 관리 체계, 비공개 자체 서비스, 공식 호스팅 운영 코드에 대한 권리를 이 저장소가 부여하지 않습니다.

## 상표

Luthn 이름, logo, wordmark, service mark에는 별도의 프로젝트 상표 정책이 적용됩니다.

## 기여자 안내

AGPL 구성 요소에 대한 기여는 AGPL-3.0-only, Apache-2.0 구성 요소에 대한 기여는 Apache-2.0으로 허가됩니다. 실제 CLA 파일과 검토 절차가 추가되기 전에는 CLA 조건을 추가하지 않습니다.

## 제3자 참고 정책

- 제3자 원본 코드, 예제, 그림, README 문구, 명령문, 상표 자산, 홍보 문구를 복사하지 않습니다.
- 라이선스를 검토하고 기록하지 않은 의존성을 가져오지 않습니다.
- 다른 제품의 상표, 용어, 독점적 위치를 중심으로 Luthn의 정체성을 만들지 않습니다.
- 일반적인 방식은 참고할 수 있지만 구현과 문서는 독창적이어야 합니다.
