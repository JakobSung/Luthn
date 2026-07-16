# 프로젝트 구조 참고

[English](project-structure.md)

표준 프로젝트 경계와 의존 방향은 [프로젝트 맥락](project-context.ko.md)에 있습니다. 이 문서에는 현재 solution 구조와 이름 규칙만 기록합니다.

## 현재 구조

```text
Luthn.sln

src/
  Luthn.Core/
  Luthn.Core.Persistence/
  Luthn.Host.Api/
  Luthn.Host.Worker/
  Luthn.Tools/
  Luthn.Sdk/
  Luthn.AgentConnector.Http/
  Luthn.McpServer/

tests/
  Luthn.Core.Tests/
  Luthn.Core.Persistence.Tests/
  Luthn.Host.Api.Tests/
  Luthn.Sdk.Tests/
  Luthn.AgentConnector.Tests/
  Luthn.McpServer.Tests/
  Luthn.Tools.Tests/
```

## 이름 규칙

구현에서는 `CoreEntity`, `CoreRelationship`, `CoreTags`, `coreTags`, `CoreGraphModelTests`를 선호합니다. 구현 원본/API 계약에는 예약된 호스팅 제품 이름을 사용하지 않습니다.
