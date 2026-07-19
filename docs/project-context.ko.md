# Luthn 프로젝트 맥락

[English](project-context.md)

기여자와 검토자를 위한 최소한의 구조·안전 맥락입니다. 공개해도 안전하고 오래 유지되는 내용만 둡니다. 개발 상태, 비공개 계획, 분석 메모, 실행 증거, PR 메타데이터, 내부 순서는 기록하지 않습니다.

## 문서 정책

- `README.md`와 `README.ko.md`는 제품 철학, 구조, 설정, 사용법을 설명합니다.
- `docs/project-context.md`와 이 문서는 커밋되는 안전·참고 색인입니다.
- 자동 생성된 `plan`, `architecture`, `analysis`, `review`, `report`, `handoff`, `evidence` Markdown은 기본적으로 비공개이며, 관리자가 명시적으로 커밋을 요청하지 않는 한 무시되는 로컬 경로에 둡니다.
- 커밋되는 문서는 개발 과정이 아닌 제품 또는 오래 유지되는 기술 계약을 설명해야 합니다.

## 제품 경계

Luthn은 AI 에이전트용 직접 호스팅 공유 기억 계층입니다. 민감한 데이터를 분류하고 정책이 허용한 기억과 맥락만 여러 에이전트가 공유하게 합니다.

- 에이전트는 기본적으로 Core로 걸러진 공유 기억, context pack, 위키 안전 Markdown을 읽습니다.
- 원본·비공개 기록은 Vault, 정책, 통제된 접근, 감사 경계 뒤에 둡니다.
- 위키 Markdown은 Core가 관리하는 지식의 투영이며 원본 사실 저장소가 아닙니다.
- 로컬/PostgreSQL이 기본 직접 호스팅 기억 경로이고 외부 기억 서비스는 Luthn 정책 뒤의 선택적 adapter입니다.
- 로컬 전용 동작은 불변 조건입니다. 외부 공개에는 운영자 동작이 필요하며, 버전이 지정된 공개 안전 투영만 로컬 durable outbox를 거칩니다. 공개 저장소에는 활성 cloud client가 없습니다.
- 로컬 직접 호스팅 확인 흐름은 provider 자격 증명 없이 실행할 수 있어야 합니다.
- 저장소에는 자격 증명, 비공개 원본, 고객 원문, 로컬 에이전트 자료, 계획 상태, 실행 증거를 두지 않습니다.

## Runtime 형태

```text
원본·비공개 자료
  -> 수집
  -> 분류 + 정책
  -> Vault / Core graph / 공유 기억 / 위키 투영 / 무시 / 검토 필요
  -> 에이전트 API는 Core로 걸러진 위키 안전 기억과 맥락 반환
```

선택적인 미래 팀 공유 경계:

```text
승인된 공유 기억
  -> 명시적 외부 공개 승인
  -> 로컬 durable outbox의 버전 지정 안전 투영
  -> 비활성 전송 경계
  -> 이 저장소 밖의 미래 상용 cloud adapter
```

runtime 프로젝트는 `Luthn.Core`, `Luthn.Core.Persistence`, `Luthn.Host.Api`, `Luthn.Host.Worker`, `Luthn.Tools`, `Luthn.Sdk`, `Luthn.AgentConnector.Http`, `Luthn.McpServer`입니다.

## 필수 규칙

- 구현된 지식 모형에는 `Core`, 맥락 선택에는 `coreTags`를 사용합니다.
- 원본 Vault/source 조회 route, connector method, MCP tool을 기본으로 추가하지 않습니다.
- 미래의 명시적 계획이 제한된 가림 출력을 구현하기 전까지 민감 접근·감사 응답은 메타데이터만 반환합니다.
- 민감하거나 agent에 보이지 않는 shared-memory 사용자 필드는 인증된 보호 payload 저장소에 두고 key ring은 PostgreSQL 밖에 둡니다. 암호문을 agent, sync, publication, audit, log, metric 계약으로 노출하지 않습니다.
- 모든 새 source event 또는 shared-memory item과 함께 버전이 지정된 불변 수집 출처 레코드 하나를 원자적으로 저장합니다. 호출자 주장은 서버가 확인한 actor identity와 구분하고, 출처정보는 권한 있는 운영자에게만 제공합니다.
- `Luthn.McpServer`는 HTTP를 이용하는 connector 쪽에 두고 Core에 직접 연결하지 않습니다.
- 일회성 console app을 추가하지 말고 API endpoint, hosted service, MCP tool, SDK/client library, 제한된 `Luthn.Tools` 하위 명령을 사용합니다.

## 집중 검토 대상

인증·권한·서비스 token scope, 민감 접근·감사·원본·Vault·분류 정책, 영속화·EF Core migration, MCP·에이전트 경계, 운영자 화면 token 처리, 생성 문서·로컬 자료 가시성 변경은 고위험으로 검토합니다.

## 검증 명령

```bash
dotnet build Luthn.sln --no-restore
dotnet test Luthn.sln --no-restore
docker compose config
git diff --check
```

## 참고 문서

- [제품 개요](../README.ko.md)
- [API](api.ko.md)
- [로컬 개발](local-development.ko.md)
- [운영](operations.ko.md)
- [Codex 연결](agent-quickstart.ko.md)
- [라이선스](licensing.ko.md)
- [구조](architecture.ko.md)
- [프로젝트 구조](project-structure.ko.md)
- [데이터 경계](data-boundaries.ko.md)
- [원본 참조](source-references.ko.md)
